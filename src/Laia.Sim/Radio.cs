using System.Net.Http.Headers;
using System.Text.Json;
using Laia.Common;
using NAudio.Utils;
using NAudio.Wave;

namespace Laia.Sim;

/// <summary>Records the microphone while push-to-talk is held.</summary>
internal sealed class Recorder
{
    private WaveInEvent? _wave;
    private MemoryStream? _ms;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<byte[]>? _done;
    private DateTime _started;
    public bool IsRecording => _wave is not null;

    public void Start()
    {
        if (_wave is not null) return;
        _ms = new MemoryStream();
        _done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wave = new WaveInEvent { DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 40 };
        _writer = new WaveFileWriter(new IgnoreDisposeStream(_ms), wave.WaveFormat);
        wave.DataAvailable += (_, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        wave.RecordingStopped += (_, e) =>
        {
            _writer?.Dispose();
            _writer = null;
            if (e.Exception is not null) _done?.TrySetException(e.Exception);
            else _done?.TrySetResult(_ms?.ToArray() ?? Array.Empty<byte>());
        };
        _wave = wave;
        _started = DateTime.UtcNow;
        wave.StartRecording();
    }

    /// <summary>Stops and returns the WAV bytes plus how long the transmission was.</summary>
    public async Task<(byte[] wav, TimeSpan length)> StopAsync()
    {
        var wave = _wave;
        if (wave is null || _done is null) return (Array.Empty<byte>(), TimeSpan.Zero);
        var length = DateTime.UtcNow - _started;
        // Keep a short tail so the last word isn't clipped.
        await Task.Delay(150);
        wave.StopRecording();
        byte[] bytes;
        try { bytes = await _done.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { wave.Dispose(); _wave = null; }
        return (bytes, length);
    }
}

internal static class Player
{
    public static Task PlayAsync(byte[] wav)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var reader = new WaveFileReader(new MemoryStream(wav));
            var output = new WaveOutEvent { DeviceNumber = -1 };
            output.Init(reader);
            output.PlaybackStopped += (_, _) =>
            {
                output.Dispose();
                reader.Dispose();
                tcs.TrySetResult();
            };
            output.Play();
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
        return tcs.Task;
    }

    /// <summary>A short radio-style click, generated on the fly.</summary>
    public static byte[] Click(bool keyUp)
    {
        const int sr = 16000;
        var n = sr * (keyUp ? 90 : 45) / 1000;
        var rng = new Random(keyUp ? 2 : 1);
        using var ms = new MemoryStream();
        using (var w = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(sr, 16, 1)))
        {
            for (var i = 0; i < n; i++)
            {
                var env = 1.0 - (double)i / n;
                var s = (rng.NextDouble() * 2 - 1) * 0.18 * env * env;
                w.WriteSample((float)s);
            }
        }
        return ms.ToArray();
    }
}

internal sealed record RadioResult(string Heard, string Reply, byte[] Audio);

internal sealed class RadioException(string message) : Exception(message);

/// <summary>Talks HTTP to the LAIA Server on the ATC PC.</summary>
internal sealed class ServerLink
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<RadioResult> TransmitAsync(ServerInfo server, byte[] wav)
    {
        using var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var req = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl + "/api/radio") { Content = content };
        req.Headers.Add("X-LAIA-Client", Environment.MachineName);
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            var msg = "The server couldn't process that call.";
            try { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("error", out var e)) msg = e.GetString() ?? msg; } catch { }
            throw new RadioException(msg);
        }
        var audio = await resp.Content.ReadAsByteArrayAsync();
        return new RadioResult(Header(resp, "X-LAIA-Heard"), Header(resp, "X-LAIA-Reply"), audio);
    }

    public async Task<(bool ok, string speech, string? detail)> StatusAsync(ServerInfo server)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var json = await _http.GetStringAsync(server.BaseUrl + "/api/status", cts.Token);
            using var doc = JsonDocument.Parse(json);
            var sp = doc.RootElement.GetProperty("speech");
            var detail = sp.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return (true, sp.GetProperty("status").GetString() ?? "unknown", detail);
        }
        catch { return (false, "unreachable", null); }
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? Uri.UnescapeDataString(v.FirstOrDefault() ?? "") : "";
}
