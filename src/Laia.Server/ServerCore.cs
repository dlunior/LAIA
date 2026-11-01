using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Laia.Common;

namespace Laia.Server;

public static class Paths
{
    public static string DataDir => Environment.GetEnvironmentVariable("LAIA_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LAIA");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string InstallDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static string Python => Environment.GetEnvironmentVariable("LAIA_PYTHON") ?? Path.Combine(InstallDir, "python", "python.exe");
    public static string SpeechScript => Environment.GetEnvironmentVariable("LAIA_SPEECH") ?? Path.Combine(InstallDir, "speech", "worker.py");
}

public sealed record RadioExchange(DateTime Time, string Heard, string Reply, double Ms);

/// <summary>Everything the control page and the sim app need to know about the server right now.</summary>
public sealed class ServerState
{
    private readonly object _gate = new();
    private readonly List<RadioExchange> _log = new();
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public string Version { get; } = typeof(ServerState).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string SpeechStatus { get; set; } = "starting"; // starting | loading | ready | error
    public string? SpeechDetail { get; set; }
    public JsonElement? SpeechHealth { get; set; }
    public Laia.Common.SimConnect.AircraftState? Sim { get; private set; }
    public DateTime SimUpdatedUtc { get; private set; } = DateTime.MinValue;
    public string? LastRadioContext { get; set; }

    /// <summary>Returns true when the sim just (re)connected.</summary>
    public bool SetSim(Laia.Common.SimConnect.AircraftState s)
    {
        var wasConnected = Sim is { Connected: true } && DateTime.UtcNow - SimUpdatedUtc < TimeSpan.FromSeconds(30);
        Sim = s;
        SimUpdatedUtc = DateTime.UtcNow;
        return s.Connected && !wasConnected;
    }

    public string? SimAddress { get; private set; }
    public string? SimName { get; private set; }
    public DateTime? SimSeenUtc { get; private set; }

    public void TouchSim(string? address, string? name)
    {
        SimAddress = address;
        if (!string.IsNullOrWhiteSpace(name)) SimName = name;
        SimSeenUtc = DateTime.UtcNow;
    }

    public void AddExchange(string heard, string reply, double ms)
    {
        lock (_gate)
        {
            _log.Add(new RadioExchange(DateTime.UtcNow, heard, reply, ms));
            if (_log.Count > 200) _log.RemoveAt(0);
        }
    }

    public List<RadioExchange> Recent(int n = 50)
    {
        lock (_gate) return _log.TakeLast(n).Reverse().ToList();
    }

    public object Snapshot(object? flight = null) => new
    {
        flight,
        app = "LAIA Server",
        version = Version,
        machine = Environment.MachineName,
        uptimeSeconds = (int)(DateTime.UtcNow - StartedUtc).TotalSeconds,
        speech = new { status = SpeechStatus, detail = SpeechDetail, health = SpeechHealth },
        sim = SimSeenUtc is null ? null : new
        {
            address = SimAddress,
            name = SimName,
            lastSeenSeconds = (int)(DateTime.UtcNow - SimSeenUtc.Value).TotalSeconds,
        },
        transcript = Recent(20),
    };
}

/// <summary>Answers "is there a LAIA server here?" questions from the sim PC.</summary>
public sealed class DiscoveryResponder(ServerState state, ILogger<DiscoveryResponder> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        while (udp is null && !ct.IsCancellationRequested)
        {
            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, LaiaPorts.Discovery));
                udp.EnableBroadcast = true;
                log.LogInformation("Listening for LAIA Sim on UDP {port}", LaiaPorts.Discovery);
            }
            catch (Exception ex)
            {
                log.LogWarning("Discovery port busy ({msg}); retrying", ex.Message);
                udp?.Dispose();
                udp = null;
                await Task.Delay(5000, ct);
            }
        }
        if (udp is null) return;
        using var _ = udp;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            if (Encoding.UTF8.GetString(r.Buffer) != Discovery.Query) continue;
            var reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryReply
            {
                Machine = Environment.MachineName,
                Name = $"LAIA Server on {Environment.MachineName}",
                Port = LaiaPorts.Http,
                Version = state.Version,
            });
            try { await udp.SendAsync(reply, r.RemoteEndPoint, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("Reply failed: {msg}", ex.Message); }
        }
    }
}

/// <summary>Runs the Python speech engine (Whisper + Kokoro) and restarts it if it ever stops.</summary>
public sealed class SpeechWorker(ServerState state, IHttpClientFactory http, ILogger<SpeechWorker> log) : BackgroundService
{
    private Process? _proc;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = Task.Run(() => HealthLoop(ct), ct);
        var delay = 2;
        while (!ct.IsCancellationRequested)
        {
            if (!File.Exists(Paths.Python) || !File.Exists(Paths.SpeechScript))
            {
                state.SpeechStatus = "error";
                state.SpeechDetail = "Speech engine files are missing. Run LAIA Server setup again.";
                log.LogError("Missing {py} or {script}", Paths.Python, Paths.SpeechScript);
                try { await Task.Delay(15000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            var psi = new ProcessStartInfo(Paths.Python)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Paths.SpeechScript)!,
            };
            foreach (var a in new[] { "-u", Paths.SpeechScript, "--config", Paths.ConfigPath, "--port", LaiaPorts.SpeechWorker.ToString(), "--parent-pid", Environment.ProcessId.ToString() })
                psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
            psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            psi.Environment["LAIA_DATA"] = Paths.DataDir;

            var started = DateTime.UtcNow;
            try
            {
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log.LogInformation("[speech] {line}", e.Data); };
                _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.LogWarning("[speech] {line}", e.Data); };
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                state.SpeechStatus = "loading";
                state.SpeechDetail = "Starting the speech engine…";
                await _proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not start speech engine");
            }

            state.SpeechStatus = "error";
            state.SpeechDetail = $"Speech engine stopped (code {SafeExit(_proc)}). Restarting…";
            delay = DateTime.UtcNow - started > TimeSpan.FromMinutes(3) ? 2 : Math.Min(delay * 2, 60);
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static string SafeExit(Process? p) { try { return p?.ExitCode.ToString() ?? "?"; } catch { return "?"; } }

    private async Task HealthLoop(CancellationToken ct)
    {
        var client = http.CreateClient("speech");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                var json = await client.GetStringAsync("health", cts.Token);
                using var doc = JsonDocument.Parse(json);
                var h = doc.RootElement.Clone();
                state.SpeechHealth = h;
                var ready = h.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
                var failed = h.TryGetProperty("failed", out var fl) && fl.ValueKind == JsonValueKind.True;
                state.SpeechStatus = ready ? "ready" : failed ? "error" : "loading";
                state.SpeechDetail = h.TryGetProperty("detail", out var d) ? d.GetString() : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch
            {
                if (state.SpeechStatus == "ready") { state.SpeechStatus = "loading"; state.SpeechDetail = "Reconnecting to the speech engine…"; }
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        await base.StopAsync(ct);
    }
}

/// <summary>Writes a plain text log to ProgramData\LAIA\logs so problems can be diagnosed later.</summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly object _gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 5_000_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var cat = category.Split('.').Last();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {logLevel.ToString()[..4].ToUpper()} {cat}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Write(line);
        }
    }
}
