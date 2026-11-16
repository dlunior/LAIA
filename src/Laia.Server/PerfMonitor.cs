using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Laia.Server;

/// <summary>
/// How the server PC is coping (user, 16 Nov: "hows the performance of the server PC? is it handling the load
/// adequately?"): once a minute a line in the log — the whole PC's processor and memory use, LAIA's own, how long the
/// traffic updates took (they run four times a second), .NET's pauses, and the graphics card (speech) — and the same
/// on the status page. A traffic update over a second is logged at once with where the time went (16 Nov, 12:50:30:
/// one 3.8 s gap in the updates to the sim).
/// </summary>
public sealed class PerfMonitor(ILogger<PerfMonitor> log) : BackgroundService
{
    private readonly object _gate = new();
    private int _ticks, _slow;
    private double _sumMs, _maxMs, _maxLockMs;
    private string _maxWhere = "";

    /// <summary>The latest minute, for the status page.</summary>
    public object? Latest { get; private set; }

    /// <summary>One traffic update: how long it took, how long it waited for the lock, and its parts (name → ms).</summary>
    public void Tick(double ms, double lockMs, IReadOnlyList<(string part, double ms)> parts)
    {
        lock (_gate)
        {
            _ticks++; _sumMs += ms;
            if (ms > 250) _slow++;
            _maxLockMs = Math.Max(_maxLockMs, lockMs);
            if (ms > _maxMs)
            {
                _maxMs = ms;
                _maxWhere = string.Join(", ", parts.Where(p => p.ms >= 1).OrderByDescending(p => p.ms).Take(4).Select(p => $"{p.part} {p.ms:0} ms"));
            }
        }
        if (ms + lockMs > 1000)
            log.LogWarning("Traffic update took {ms:N0} ms{lk}: {parts}", ms, lockMs > 50 ? $" (after waiting {lockMs:N0} ms for the traffic lock)" : "",
                string.Join(", ", parts.Where(p => p.ms >= 1).OrderByDescending(p => p.ms).Select(p => $"{p.part} {p.ms:N0} ms")));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemStatus m);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var proc = Process.GetCurrentProcess();
        long idle0 = 0, kern0 = 0, user0 = 0;
        try { GetSystemTimes(out idle0, out kern0, out user0); } catch { }
        var cpu0 = proc.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var pause0 = GC.GetTotalPauseDuration();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
            try
            {
                // The whole PC's processor: busy share of all cores since the last minute.
                double? pcCpu = null;
                try
                {
                    GetSystemTimes(out var idle, out var kern, out var user);
                    var total = (kern - kern0) + (user - user0);   // kernel time includes idle
                    if (total > 0) pcCpu = 100.0 * (total - (idle - idle0)) / total;
                    idle0 = idle; kern0 = kern; user0 = user;
                }
                catch { }
                proc.Refresh();
                var cpu = proc.TotalProcessorTime;
                var secs = wall.Elapsed.TotalSeconds; wall.Restart();
                var laiaCpu = 100.0 * (cpu - cpu0).TotalSeconds / Math.Max(1, secs) / Environment.ProcessorCount;
                cpu0 = cpu;
                var pause = GC.GetTotalPauseDuration();
                var pauseMs = (pause - pause0).TotalMilliseconds; pause0 = pause;
                double? memPct = null, memGb = null;
                try { var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() }; if (GlobalMemoryStatusEx(ref m)) { memPct = m.Load; memGb = m.TotalPhys / 1073741824.0; } } catch { }
                var laiaMb = proc.WorkingSet64 / 1048576.0;
                var gpu = await GpuAsync(ct);
                int ticks, slow; double avg, max, maxLock; string where;
                lock (_gate)
                {
                    ticks = _ticks; slow = _slow; avg = _ticks > 0 ? _sumMs / _ticks : 0; max = _maxMs; maxLock = _maxLockMs; where = _maxWhere;
                    _ticks = 0; _slow = 0; _sumMs = 0; _maxMs = 0; _maxLockMs = 0; _maxWhere = "";
                }
                var line = $"PC processor {(pcCpu is { } pc ? $"{pc:0}%" : "?")} (LAIA {laiaCpu:0.0}%), memory {(memPct is { } mp ? $"{mp:0}% of {memGb:0} GB" : "?")} (LAIA {laiaMb:N0} MB); "
                    + $"traffic updates {ticks} (average {avg:0.0} ms, longest {max:0} ms{(where.Length > 0 && max >= 50 ? $": {where}" : "")}, {slow} over 250 ms{(maxLock > 50 ? $", longest wait for the lock {maxLock:0} ms" : "")}); "
                    + $".NET pauses {pauseMs:0} ms; graphics card {gpu?.text ?? "not read"}";
                log.LogInformation("Performance, last minute: {line}", line);
                Latest = new
                {
                    at = DateTime.UtcNow,
                    pcCpu = pcCpu is { } p1 ? Math.Round(p1) : (double?)null,
                    laiaCpu = Math.Round(laiaCpu, 1),
                    memPct, memGb = memGb is { } g ? Math.Round(g) : (double?)null,
                    laiaMb = Math.Round(laiaMb),
                    ticks, avgMs = Math.Round(avg, 1), maxMs = Math.Round(max), slow, pauseMs = Math.Round(pauseMs),
                    gpu = gpu?.text,
                    summary = line,
                };
            }
            catch (Exception ex) { log.LogInformation("Performance reading failed: {msg}", ex.Message); }
        }
    }

    /// <summary>The NVIDIA card's use, memory and temperature (nvidia-smi, installed with its driver).</summary>
    private static async Task<(string text, double util)?> GpuAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outText = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var f = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',').Select(x => x.Trim()).ToArray();
            if (f is not { Length: >= 4 }) return null;
            double.TryParse(f[0], System.Globalization.CultureInfo.InvariantCulture, out var util);
            double.TryParse(f[1], System.Globalization.CultureInfo.InvariantCulture, out var used);
            double.TryParse(f[2], System.Globalization.CultureInfo.InvariantCulture, out var tot);
            return ($"{util:0}% busy, {used / 1024:0.0} of {tot / 1024:0} GB memory, {f[3]}°C", util);
        }
        catch { return null; }
    }
}
