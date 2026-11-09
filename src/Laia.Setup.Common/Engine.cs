using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using System.Text;

namespace Laia.Setup.Common;

/// <summary>Setup log in %TEMP%; copied next to the installed app at the end.</summary>
public static class SetupLog
{
    private static readonly object Gate = new();
    public static string FilePath { get; set; } = Path.Combine(Path.GetTempPath(), "LAIA-Setup.log");
    public static event Action<string>? Line;

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {msg}";
        lock (Gate) { try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { } }
        Line?.Invoke(line);
    }
}

/// <summary>
/// The app files travel inside the setup .exe itself: a zip is appended to the end of the file,
/// followed by its length and a marker. The same .exe without the zip acts as the uninstaller.
/// </summary>
public static class Payload
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LAIAPAY1");

    public static bool TryOpen(out ZipArchive? zip)
    {
        zip = null;
        try
        {
            var path = Environment.ProcessPath!;
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 16) { fs.Dispose(); return false; }
            fs.Seek(-16, SeekOrigin.End);
            var footer = new byte[16];
            fs.ReadExactly(footer);
            if (!footer.AsSpan(8).SequenceEqual(Magic)) { fs.Dispose(); return false; }
            var len = BitConverter.ToInt64(footer, 0);
            var start = fs.Length - 16 - len;
            if (len <= 0 || start < 0) { fs.Dispose(); return false; }
            zip = new ZipArchive(new SubStream(fs, start, len), ZipArchiveMode.Read, leaveOpen: false);
            return true;
        }
        catch (Exception ex)
        {
            SetupLog.Write("Payload open failed: " + ex.Message);
            return false;
        }
    }

    public static bool Present() { if (!TryOpen(out var z)) return false; z!.Dispose(); return true; }

    /// <summary>Extracts every file; reports progress by bytes.</summary>
    public static void Extract(ZipArchive zip, string destination, Action<double, string> progress, Func<string, bool>? skip = null)
    {
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var total = Math.Max(1, entries.Sum(e => e.Length));
        long done = 0;
        var root = Path.GetFullPath(destination);
        foreach (var e in entries)
        {
            if (skip?.Invoke(e.FullName) == true) { done += e.Length; continue; }
            var target = Path.GetFullPath(Path.Combine(root, e.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (var attempt = 0; ; attempt++)
            {
                try { e.ExtractToFile(target, overwrite: true); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(800); }
            }
            done += e.Length;
            progress((double)done / total, e.FullName);
        }
    }

    private sealed class SubStream(Stream inner, long start, long length) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _pos;
            if (remaining <= 0) return 0;
            if (count > remaining) count = (int)remaining;
            inner.Position = start + _pos;
            var n = inner.Read(buffer, offset, count);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => length + offset };
            return _pos;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

public static class Sys
{
    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Runs a program hidden and waits. Every output line goes to the log and the optional callback.</summary>
    public static async Task<int> RunAsync(string file, string args, Action<string>? onLine = null, TimeSpan? timeout = null, string? workDir = null, IDictionary<string, string>? env = null)
    {
        SetupLog.Write($"> {Path.GetFileName(file)} {args}");
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        if (env is not null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = new Process { StartInfo = psi };
        void Handle(string? l) { if (string.IsNullOrWhiteSpace(l)) return; SetupLog.Write("  " + l); onLine?.Invoke(l); }
        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived += (_, e) => Handle(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(30));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            SetupLog.Write("  (timed out)");
            return -1;
        }
        SetupLog.Write($"  exit {p.ExitCode}");
        return p.ExitCode;
    }

    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static void CreateShortcut(string lnkPath, string target, string? args = null, string? icon = null, string? description = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var type = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        if (args is not null) lnk.Arguments = args;
        lnk.WorkingDirectory = Path.GetDirectoryName(target);
        if (icon is not null) lnk.IconLocation = icon;
        if (description is not null) lnk.Description = description;
        lnk.Save();
    }

    public static void CreateUrlShortcut(string path, string url, string? icon = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = $"[InternetShortcut]\r\nURL={url}\r\n" + (icon is null ? "" : $"IconFile={icon}\r\nIconIndex=0\r\n");
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// Program files that are still in use (an app that wouldn't close) can't be overwritten, but Windows does
    /// let them be renamed. Move them aside so the new files can be written; the old ones are cleaned up next time.
    /// </summary>
    public static void MoveAsideLocked(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var old in Directory.GetFiles(folder, "*.old-*")) { try { File.Delete(old); } catch { } }
        foreach (var f in Directory.GetFiles(folder).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            for (var i = 0; i < 6; i++)
            {
                try { File.Delete(f); break; }
                catch
                {
                    if (i < 5) { Thread.Sleep(500); continue; }
                    try { File.Move(f, $"{f}.old-{DateTime.Now:HHmmss}"); SetupLog.Write($"{Path.GetFileName(f)} was still in use; moved it aside"); }
                    catch (Exception ex) { SetupLog.Write($"Couldn't move {Path.GetFileName(f)} aside: {ex.Message}"); }
                }
            }
        }
    }

    public static void KillByPath(string folder)
    {
        // Also by name: if the app runs with different rights, its path can't be read but it can often still be closed.
        foreach (var p in Process.GetProcessesByName("LaiaSim"))
        {
            try { if (p.Id != Environment.ProcessId) { SetupLog.Write($"Closing LaiaSim (pid {p.Id})"); p.Kill(); p.WaitForExit(5000); } }
            catch (Exception ex) { SetupLog.Write($"Couldn't close LaiaSim (pid {p.Id}): {ex.Message}"); }
            finally { p.Dispose(); }
        }
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (file is not null && file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && p.Id != Environment.ProcessId)
                {
                    SetupLog.Write($"Closing {Path.GetFileName(file)} (pid {p.Id})");
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Copies this exe to %TEMP% and runs it from there, so the install folder can be deleted.</summary>
    public static bool RelaunchFromTempIfInside(string installDir, string extraArgs)
    {
        var me = Environment.ProcessPath!;
        if (!me.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase)) return false;
        var tmp = Path.Combine(Path.GetTempPath(), $"laia-uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(me, tmp, true);
        var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)) + " " + extraArgs;
        Process.Start(new ProcessStartInfo(tmp, args) { UseShellExecute = true });
        return true;
    }

    /// <summary>Deletes this exe a few seconds after it exits (used when running from %TEMP%).</summary>
    public static void ScheduleSelfDelete()
    {
        try
        {
            var me = Environment.ProcessPath!;
            if (!me.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & del /f /q \"{me}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
    }

    /// <summary>Removes a folder a few seconds after this process exits (silent uninstall run from inside it).</summary>
    public static void ScheduleDirectoryDelete(string dir)
    {
        try
        {
            if (!Environment.ProcessPath!.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{dir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
    }

    public static void DeleteDirectory(string dir)
    {
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return; }
            catch { Thread.Sleep(1000); }
        }
        SetupLog.Write("Could not fully remove " + dir);
    }
}

public static class Downloader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "LAIA-Setup/1.0" } },
    };

    /// <summary>Downloads to a .part file then renames; retries a few times; skips if already complete.</summary>
    public static async Task DownloadAsync(string url, string target, Action<long, long?> progress, long? expectedMinBytes = null)
    {
        if (File.Exists(target) && (expectedMinBytes is null || new FileInfo(target).Length >= expectedMinBytes))
        {
            SetupLog.Write($"Already have {Path.GetFileName(target)}");
            var l = new FileInfo(target).Length;
            progress(l, l);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                SetupLog.Write($"Downloading {url} (attempt {attempt})");
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                await using (var src = await resp.Content.ReadAsStreamAsync())
                await using (var dst = File.Create(part))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    var lastReport = DateTime.MinValue;
                    int n;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 120) { progress(done, total); lastReport = DateTime.UtcNow; }
                    }
                    progress(done, total);
                }
                File.Move(part, target, true);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                SetupLog.Write($"  download failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }
        throw new InvalidOperationException($"Couldn't download {Path.GetFileName(target)}. Check the internet connection and try again. ({last?.Message})");
    }

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
}

/// <summary>One line on the install page.</summary>
public sealed class InstallStep(string title, Func<StepContext, Task> run, bool optional = false, double weight = 1)
{
    public string Title { get; } = title;
    public Func<StepContext, Task> Run { get; } = run;
    public bool Optional { get; } = optional;
    public double Weight { get; } = weight;
    public Func<bool>? When { get; init; }
}

public sealed class StepContext(Action<double?, string> report)
{
    public void Report(double? fraction, string detail) => report(fraction, detail);
    public string? Warning { get; set; }
    public string? Result { get; set; }
}

public interface IInstallView
{
    void StepStarted(int index, InstallStep step);
    void StepProgress(int index, double overall, double? stepFraction, string detail);
    void StepFinished(int index, RowState state, string? detail);
}

/// <summary>Runs the steps in order. Optional steps can fail without stopping the install.</summary>
public static class InstallRunner
{
    public static async Task<(bool ok, string? error)> RunAsync(IReadOnlyList<InstallStep> steps, IInstallView view)
    {
        var active = steps.Select((s, i) => (s, i)).ToList();
        var totalWeight = Math.Max(0.001, steps.Sum(s => s.Weight));
        double before = 0;
        foreach (var (step, i) in active)
        {
            if (step.When is not null && !step.When())
            {
                view.StepFinished(i, RowState.Skipped, "Not needed on this PC");
                before += step.Weight;
                continue;
            }
            view.StepStarted(i, step);
            SetupLog.Write($"== {step.Title}");
            var w = step.Weight;
            var b = before;
            var ctx = new StepContext((f, d) => view.StepProgress(i, (b + w * (f ?? 0.5)) / totalWeight, f, d));
            try
            {
                await Task.Run(() => step.Run(ctx));
                view.StepFinished(i, ctx.Warning is null ? RowState.Ok : RowState.Warn, ctx.Warning ?? ctx.Result);
            }
            catch (Exception ex)
            {
                SetupLog.Write($"!! {step.Title} failed: {ex}");
                if (step.Optional)
                {
                    view.StepFinished(i, RowState.Warn, ex.Message);
                }
                else
                {
                    view.StepFinished(i, RowState.Fail, ex.Message);
                    return (false, $"{step.Title}: {ex.Message}");
                }
            }
            before += step.Weight;
            view.StepProgress(i, before / totalWeight, 1, "");
        }
        return (true, null);
    }
}

/// <summary>Headless view used for /silent installs (automated tests): writes everything to the log.</summary>
public sealed class LogInstallView : IInstallView
{
    private int _lastPct = -1;
    public void StepStarted(int index, InstallStep step) => SetupLog.Write($"[{index + 1}] {step.Title}…");
    public void StepProgress(int index, double overall, double? stepFraction, string detail)
    {
        var pct = (int)(overall * 100);
        if (pct / 10 != _lastPct / 10) { _lastPct = pct; SetupLog.Write($"   {pct}% {detail}"); }
    }
    public void StepFinished(int index, RowState state, string? detail) => SetupLog.Write($"[{index + 1}] {state} {detail}");
}
