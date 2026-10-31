using System.Net.Http.Headers;
using System.Text.Json;
using Laia.Common;
using NAudio.CoreAudioApi;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Laia.Sim;

/// <summary>Microphones and speakers, by Windows' own device names. Null id = the Windows default.</summary>
internal static class AudioDevices
{
    public sealed record Device(string? Id, string Name);

    public static List<Device> List(DataFlow flow)
    {
        var list = new List<Device> { new(null, "Windows default") };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                list.Add(new Device(d.ID, d.FriendlyName));
                d.Dispose();
            }
        }
        catch (Exception ex) { Log.Write("Device list failed: " + ex.Message); }
        return list;
    }

    public static MMDevice Open(DataFlow flow, string? id)
    {
        var en = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var d = en.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
            }
            catch { }
            Log.Write($"Saved {flow} device not available; using Windows default");
        }
        return en.GetDefaultAudioEndpoint(flow, Role.Console);
    }
}

/// <summary>Turns whatever the microphone delivers (usually 48 kHz float stereo) into samples.</summary>
internal static class AudioConvert
{
    public static float[] ToMonoFloat(byte[] raw, int count, WaveFormat f)
    {
        var ch = Math.Max(1, f.Channels);
        var isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (f is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        var bytesPer = f.BitsPerSample / 8;
        var frames = count / (bytesPer * ch);
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            double sum = 0;
            for (var c = 0; c < ch; c++)
            {
                var o = (i * ch + c) * bytesPer;
                sum += isFloat && bytesPer == 4 ? BitConverter.ToSingle(raw, o)
                     : bytesPer == 2 ? BitConverter.ToInt16(raw, o) / 32768.0
                     : bytesPer == 3 ? ((raw[o] | raw[o + 1] << 8 | (sbyte)raw[o + 2] << 16)) / 8388608.0
                     : bytesPer == 4 ? BitConverter.ToInt32(raw, o) / 2147483648.0
                     : 0;
            }
            mono[i] = (float)(sum / ch);
        }
        return mono;
    }

    public static float Peak(byte[] raw, int count, WaveFormat f)
    {
        var s = ToMonoFloat(raw, count, f);
        float p = 0;
        foreach (var v in s) p = Math.Max(p, Math.Abs(v));
        return p;
    }

    /// <summary>16 kHz mono 16-bit WAV, which is what the speech engine wants.</summary>
    public static byte[] ToWav16k(float[] mono, int sampleRate)
    {
        var n = sampleRate == 16000 ? mono.Length : (int)((long)mono.Length * 16000 / sampleRate);
        using var ms = new MemoryStream();
        using (var w = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(16000, 16, 1)))
        {
            for (var i = 0; i < n; i++)
            {
                float v;
                if (sampleRate == 16000) v = mono[i];
                else
                {
                    var pos = (double)i * sampleRate / 16000;
                    var a = (int)pos;
                    var b = Math.Min(a + 1, mono.Length - 1);
                    v = (float)(mono[a] + (mono[b] - mono[a]) * (pos - a));
                }
                w.WriteSample(Math.Clamp(v, -1f, 1f));
            }
        }
        return ms.ToArray();
    }
}

/// <summary>Records the chosen microphone while push-to-talk is held.</summary>
internal sealed class Recorder
{
    private WasapiCapture? _cap;
    private MemoryStream? _raw;
    private TaskCompletionSource<bool>? _done;
    private DateTime _started;
    public string? DeviceId { get; set; }
    public bool IsRecording => _cap is not null;

    public void Start()
    {
        if (_cap is not null) return;
        var dev = AudioDevices.Open(DataFlow.Capture, DeviceId);
        var cap = new WasapiCapture(dev) { ShareMode = AudioClientShareMode.Shared };
        _raw = new MemoryStream();
        _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cap.DataAvailable += (_, e) => _raw?.Write(e.Buffer, 0, e.BytesRecorded);
        cap.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) _done?.TrySetException(e.Exception);
            else _done?.TrySetResult(true);
        };
        _cap = cap;
        _started = DateTime.UtcNow;
        cap.StartRecording();
    }

    /// <summary>Stops and returns a 16 kHz WAV plus how long the transmission was.</summary>
    public async Task<(byte[] wav, TimeSpan length)> StopAsync()
    {
        var cap = _cap;
        if (cap is null || _done is null || _raw is null) return (Array.Empty<byte>(), TimeSpan.Zero);
        var length = DateTime.UtcNow - _started;
        await Task.Delay(150); // keep a short tail so the last word isn't clipped
        cap.StopRecording();
        try { await _done.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { _cap = null; }
        var fmt = cap.WaveFormat;
        var raw = _raw.ToArray();
        cap.Dispose();
        var mono = AudioConvert.ToMonoFloat(raw, raw.Length, fmt);
        return (AudioConvert.ToWav16k(mono, fmt.SampleRate), length);
    }
}

/// <summary>Live microphone level for the settings window.</summary>
internal sealed class MicMonitor : IDisposable
{
    private WasapiCapture? _cap;
    public float Level { get; private set; }

    public void Start(string? deviceId)
    {
        Stop();
        try
        {
            var cap = new WasapiCapture(AudioDevices.Open(DataFlow.Capture, deviceId)) { ShareMode = AudioClientShareMode.Shared };
            cap.DataAvailable += (_, e) => Level = Math.Max(AudioConvert.Peak(e.Buffer, e.BytesRecorded, cap.WaveFormat), Level * 0.85f);
            cap.StartRecording();
            _cap = cap;
        }
        catch (Exception ex) { Log.Write("Mic monitor failed: " + ex.Message); }
    }

    public void Stop()
    {
        try { _cap?.StopRecording(); _cap?.Dispose(); } catch { }
        _cap = null;
        Level = 0;
    }

    public void Dispose() => Stop();
}

internal static class Player
{
    public static string? DeviceId { get; set; }
    public static float Volume { get; set; } = 0.9f;

    public static Task PlayAsync(byte[] wav)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var reader = new WaveFileReader(new MemoryStream(wav));
            var vol = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = Volume };
            var output = new WasapiOut(AudioDevices.Open(DataFlow.Render, DeviceId), AudioClientShareMode.Shared, true, 80);
            output.Init(new SampleToWaveProvider(vol));
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

    /// <summary>Returns null when the server heard nothing real (a stray key-up).</summary>
    public async Task<RadioResult?> TransmitAsync(ServerInfo server, byte[] wav)
    {
        using var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var req = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl + "/api/radio") { Content = content };
        req.Headers.Add("X-LAIA-Client", Environment.MachineName);
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
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

    public async Task<JsonDocument?> GetSettingsAsync(ServerInfo server)
    {
        try { return JsonDocument.Parse(await _http.GetStringAsync(server.BaseUrl + "/api/settings")); }
        catch (Exception ex) { Log.Write("Get settings failed: " + ex.Message); return null; }
    }

    public async Task<bool> PutSettingsAsync(ServerInfo server, Dictionary<string, string> changes)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(changes), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(server.BaseUrl + "/api/settings", content);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log.Write("Save settings failed: " + ex.Message); return false; }
    }

    public async Task<(bool ok, string message)> TestServiceAsync(ServerInfo server, string service, string? value)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { value = value ?? "" }), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{server.BaseUrl}/api/settings/test/{service}", content);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return (doc.RootElement.GetProperty("ok").GetBoolean(), doc.RootElement.GetProperty("message").GetString() ?? "");
        }
        catch (Exception ex) { return (false, "Couldn't reach LAIA Server: " + ex.Message); }
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? Uri.UnescapeDataString(v.FirstOrDefault() ?? "") : "";
}
