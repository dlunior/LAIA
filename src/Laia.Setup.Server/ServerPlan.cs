using System.Text.Json;
using System.Text.Json.Nodes;
using Laia.Common;
using Laia.Setup.Common;
using Microsoft.Win32;

namespace Laia.Setup.Server;

/// <summary>Everything the server installer works out on its own, plus the install / uninstall steps.</summary>
internal sealed class ServerPlan
{
    public const string ServiceName = "LAIA";
    public const string ArpKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LAIA Server";
    private const string KokoroBase = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";

    public string InstallDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LAIA Server");
    public string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LAIA");
    public string ModelsDir => Path.Combine(DataDir, "models");
    public string ConfigPath => Path.Combine(DataDir, "config.json");
    public string PythonExe => Path.Combine(InstallDir, "python", "python.exe");
    public string WorkerScript => Path.Combine(InstallDir, "speech", "worker.py");
    public string ServerExe => Path.Combine(InstallDir, "app", "Laia.Server.exe");
    public string Version { get; } = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";

    public GpuInfo? Gpu { get; private set; }
    public bool UseGpu => Gpu is { IsNvidia: true, VramMb: >= 3500 };
    public string WhisperModel { get; private set; } = "base.en";
    public string CpuName { get; private set; } = "";
    public double RamGb { get; private set; }
    public string WindowsName { get; private set; } = "";
    public double FreeGb { get; private set; }
    public bool Internet { get; private set; }

    public string WhisperSize => WhisperModel switch
    {
        "tiny.en" => "75 MB", "base.en" => "145 MB", "small.en" => "480 MB", "medium.en" => "1.5 GB", _ => "1-3 GB",
    };

    public async Task DetectAsync()
    {
        WindowsName = HardwareDetector.WindowsName();
        CpuName = HardwareDetector.CpuName();
        RamGb = HardwareDetector.RamGb();
        FreeGb = HardwareDetector.FreeDiskGb(InstallDir);
        Gpu = await Task.Run(HardwareDetector.DetectGpu);
        WhisperModel = Environment.GetEnvironmentVariable("LAIA_WHISPER_MODEL") ?? (UseGpu ? (Gpu!.VramMb >= 6000 ? "medium.en" : "small.en") : "base.en");
        Internet = await CheckInternetAsync();
        SetupLog.Write($"Detected: {WindowsName}; {CpuName}; {RamGb:0} GB RAM; GPU {Gpu?.Name} {Gpu?.VramMb} MB driver {Gpu?.Driver}; free {FreeGb:0} GB; internet {Internet}; whisper {WhisperModel}");
    }

    private static async Task<bool> CheckInternetAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var r = await http.GetAsync("https://huggingface.co/api/models/Systran/faster-whisper-base.en", HttpCompletionOption.ResponseHeadersRead);
            return (int)r.StatusCode < 500;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ install

    public List<InstallStep> InstallSteps() => new()
    {
        new("Getting ready", PrepareAsync, weight: 0.5),
        new("Copying LAIA files", CopyFilesAsync, weight: 3),
        new("Installing Microsoft runtime", VcRuntimeAsync, optional: true, weight: 1),
        new("Saving settings", WriteConfigAsync, weight: 0.2),
        new("Downloading the controller voice", DownloadVoiceAsync, optional: true, weight: 3),
        new("Setting up your graphics card for speech", GpuLibsAsync, optional: true, weight: 5) { When = () => UseGpu },
        new("Downloading speech recognition", WhisperAsync, weight: 4),
        new("Configuring Windows", ConfigureWindowsAsync, weight: 0.8),
        new("Starting LAIA", StartAsync, optional: true, weight: 2),
    };

    private async Task PrepareAsync(StepContext c)
    {
        c.Report(null, "Stopping any older version of LAIA");
        await StopServiceAsync();
        Sys.KillByPath(InstallDir);
        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
    }

    private Task CopyFilesAsync(StepContext c)
    {
        if (!Payload.TryOpen(out var zip)) throw new InvalidOperationException("This setup file is damaged. Please download it again.");
        using (zip)
        {
            // Old python packages could clash with new ones; start the runtime folders fresh.
            foreach (var d in new[] { "python", "speech", "app" }) Sys.DeleteDirectory(Path.Combine(InstallDir, d));
            Payload.Extract(zip!, InstallDir, (f, name) => c.Report(f, Path.GetFileName(name)));
        }
        return Task.CompletedTask;
    }

    private async Task VcRuntimeAsync(StepContext c)
    {
        var redist = Path.Combine(InstallDir, "redist", "vc_redist.x64.exe");
        if (!File.Exists(redist)) { c.Result = "Already included"; return; }
        var bundled = System.Diagnostics.FileVersionInfo.GetVersionInfo(redist);
        var installed = InstalledVcRuntime();
        if (installed is not null && System.Version.TryParse(bundled.ProductVersion, out var want) && installed >= new System.Version(want.Major, want.Minor, Math.Max(0, want.Build)))
        {
            c.Result = $"Already installed ({installed})";
            return;
        }
        c.Report(null, "Microsoft Visual C++ runtime");
        var code = await Sys.RunAsync(redist, "/install /quiet /norestart", timeout: TimeSpan.FromMinutes(10));
        if (code is not (0 or 1638 or 3010 or 1641)) throw new InvalidOperationException($"Visual C++ runtime installer returned {code}");
        c.Result = code == 1638 ? "Already up to date" : "Installed";
    }

    private static System.Version? InstalledVcRuntime()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (k?.GetValue("Installed") is int i && i == 1 &&
                k.GetValue("Major") is int major && k.GetValue("Minor") is int minor && k.GetValue("Bld") is int bld)
                return new System.Version(major, minor, bld);
        }
        catch { }
        return null;
    }

    private Task WriteConfigAsync(StepContext c)
    {
        Directory.CreateDirectory(DataDir);
        JsonObject cfg;
        try { cfg = File.Exists(ConfigPath) ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new() : new(); }
        catch { cfg = new(); }
        void Default(string key, JsonNode value) { if (cfg[key] is null) cfg[key] = value; }
        // Hardware-driven choices are refreshed every install; user preferences are kept.
        cfg["whisper_model"] = WhisperModel;
        cfg["whisper_device"] = UseGpu ? "auto" : "cpu";
        cfg["models_dir"] = ModelsDir;
        cfg["cuda_dir"] = CudaDir;
        cfg["kokoro_model"] = Path.Combine(ModelsDir, "kokoro", "kokoro-v1.0.onnx");
        cfg["kokoro_voices"] = Path.Combine(ModelsDir, "kokoro", "voices-v1.0.bin");
        Default("voice", "am_michael");
        Default("speed", 1.08);
        Default("fallback_tts_url", "https://voice.sageks.dev/v1/audio/speech");
        Default("fallback_tts_voice", "am_michael");
        File.WriteAllText(ConfigPath, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        c.Result = $"Speech recognition: Whisper {WhisperModel} on {(UseGpu ? "graphics card" : "processor")}";
        return Task.CompletedTask;
    }

    private async Task DownloadVoiceAsync(StepContext c)
    {
        var dir = Path.Combine(ModelsDir, "kokoro");
        var files = new (string name, long min, long approx)[]
        {
            ("voices-v1.0.bin", 20_000_000, 28_000_000),
            ("kokoro-v1.0.onnx", 300_000_000, 325_000_000),
        };
        long totalApprox = files.Sum(f => f.approx), before = 0;
        foreach (var (name, min, approx) in files)
        {
            await Downloader.DownloadAsync(KokoroBase + name, Path.Combine(dir, name), (done, total) =>
                c.Report((before + done) / (double)totalApprox, $"{Downloader.Size(before + done)} of about {Downloader.Size(totalApprox)}"), min);
            before += approx;
        }
        c.Result = "Kokoro voice ready";
    }

    /// <summary>NVIDIA libraries live in ProgramData so updates don't delete and re-download them.</summary>
    public string CudaDir => Path.Combine(DataDir, "cuda");
    private const string CudaSpec = "nvidia-cublas-cu12 nvidia-cudnn-cu12>=9,<10 nvidia-cuda-runtime-cu12";

    private async Task GpuLibsAsync(StepContext c)
    {
        var marker = Path.Combine(CudaDir, "laia-installed.txt");
        var have = File.Exists(marker) && File.ReadAllText(marker).Trim() == CudaSpec &&
                   Directory.Exists(Path.Combine(CudaDir, "nvidia", "cublas", "bin")) && Directory.Exists(Path.Combine(CudaDir, "nvidia", "cudnn", "bin"));
        if (have) { c.Result = $"{Gpu!.Name} ({Gpu.VramText}) ready (already downloaded)"; return; }

        c.Report(null, "Downloading NVIDIA speech libraries (about 1.2 GB, only once)…");
        Sys.DeleteDirectory(CudaDir);
        Directory.CreateDirectory(CudaDir);
        var code = await Sys.RunAsync(PythonExe,
            $"-m pip install --no-warn-script-location --disable-pip-version-check --progress-bar off --target \"{CudaDir}\" " +
            string.Join(" ", CudaSpec.Split(' ').Select(x => $"\"{x}\"")),
            line =>
            {
                if (line.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Collecting", StringComparison.OrdinalIgnoreCase))
                    c.Report(null, line.Length > 90 ? line[..90] + "…" : line);
                else if (line.StartsWith("Installing", StringComparison.OrdinalIgnoreCase))
                    c.Report(null, "Installing NVIDIA libraries…");
            },
            timeout: TimeSpan.FromMinutes(60), env: PyEnv());
        if (code != 0)
        {
            c.Warning = "Couldn't set up the graphics card, so speech will run on the processor (a little slower).";
            return;
        }
        File.WriteAllText(marker, CudaSpec);
        c.Result = $"{Gpu!.Name} ({Gpu.VramText}) ready";
    }

    private async Task WhisperAsync(StepContext c)
    {
        c.Report(null, $"Whisper {WhisperModel} (about {WhisperSize})");
        var code = await Sys.RunAsync(PythonExe, $"-u \"{WorkerScript}\" --config \"{ConfigPath}\" --prefetch-whisper {WhisperModel}",
            line => { if (line.Contains('%')) c.Report(null, $"Whisper {WhisperModel} (about {WhisperSize}) {ExtractPercent(line)}"); },
            timeout: TimeSpan.FromMinutes(60), env: PyEnv());
        if (code != 0) throw new InvalidOperationException("Couldn't download the speech recognition model. Check the internet connection and run setup again.");
        c.Result = $"Whisper {WhisperModel} ready";
    }

    private static string ExtractPercent(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})%");
        return m.Success ? m.Groups[1].Value + "%" : "";
    }

    private Dictionary<string, string> PyEnv() => new()
    {
        ["PYTHONIOENCODING"] = "utf-8",
        ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
        ["HF_HUB_DISABLE_TELEMETRY"] = "1",
        ["LAIA_DATA"] = DataDir,
        ["PIP_CACHE_DIR"] = Path.Combine(Path.GetTempPath(), "laia-pip-cache"),
    };

    private async Task ConfigureWindowsAsync(StepContext c)
    {
        c.Report(0.1, "Registering the LAIA background service");
        var sc = Sys.System32("sc.exe");
        var bin = $"binPath= \"\\\"{ServerExe}\\\"\" start= delayed-auto DisplayName= \"LAIA Server\"";
        var code = await Sys.RunAsync(sc, $"create {ServiceName} {bin}");
        if (code == 1073) code = await Sys.RunAsync(sc, $"config {ServiceName} {bin}");
        if (code != 0) throw new InvalidOperationException($"Couldn't register the LAIA service (code {code}).");
        await Sys.RunAsync(sc, $"description {ServiceName} \"LAIA - Local AI ATC: AI traffic and air traffic control for your flight simulator.\"");
        await Sys.RunAsync(sc, $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000");

        c.Report(0.4, "Allowing your sim PC to reach this PC (local network only)");
        var netsh = Sys.System32("netsh.exe");
        await Sys.RunAsync(netsh, "advfirewall firewall delete rule name=\"LAIA Server\"");
        await Sys.RunAsync(netsh, $"advfirewall firewall add rule name=\"LAIA Server\" dir=in action=allow protocol=UDP localport={LaiaPorts.Discovery} remoteip=localsubnet profile=any");
        await Sys.RunAsync(netsh, $"advfirewall firewall add rule name=\"LAIA Server\" dir=in action=allow protocol=TCP localport={LaiaPorts.Http} remoteip=localsubnet profile=any");

        c.Report(0.6, "Keeping this PC awake while plugged in");
        var powercfg = Sys.System32("powercfg.exe");
        await Sys.RunAsync(powercfg, "/change standby-timeout-ac 0");
        await Sys.RunAsync(powercfg, "/change hibernate-timeout-ac 0");

        c.Report(0.8, "Adding LAIA to Settings > Apps");
        var uninstaller = Path.Combine(InstallDir, "uninstall.exe");
        using (var k = Registry.LocalMachine.CreateSubKey(ArpKey))
        {
            k.SetValue("DisplayName", "LAIA Server");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "LAIA");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("DisplayIcon", ServerExe);
            k.SetValue("UninstallString", $"\"{uninstaller}\"");
            k.SetValue("QuietUninstallString", $"\"{uninstaller}\" /silent");
            k.SetValue("URLInfoAbout", $"http://localhost:{LaiaPorts.Http}");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(DirSizeKb(InstallDir)), RegistryValueKind.DWord);
        }
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LAIA");
        Sys.CreateUrlShortcut(Path.Combine(startMenu, "LAIA Server control page.url"), $"http://localhost:{LaiaPorts.Http}", ServerExe);
        try { File.Copy(SetupLog.FilePath, Path.Combine(DataDir, "logs", "setup.log"), true); } catch { }
    }

    private static long DirSizeKb(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; }
        catch { return 0; }
    }

    private async Task StartAsync(StepContext c)
    {
        c.Report(null, "Starting the LAIA service");
        await Sys.RunAsync(Sys.System32("sc.exe"), $"start {ServiceName}");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var deadline = DateTime.UtcNow.AddMinutes(4);
        string status = "starting", detail = "";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{LaiaPorts.Http}/api/status");
                using var doc = JsonDocument.Parse(json);
                var sp = doc.RootElement.GetProperty("speech");
                status = sp.GetProperty("status").GetString() ?? "";
                detail = sp.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                if (status == "ready") { c.Result = "Running and ready for your sim PC"; return; }
                c.Report(null, string.IsNullOrEmpty(detail) ? "Warming up the speech engine…" : detail);
            }
            catch { c.Report(null, "Waiting for the service to start…"); }
            await Task.Delay(2000);
        }
        c.Warning = status == "error"
            ? $"LAIA started but the speech engine reported a problem: {detail}"
            : "LAIA is running and still warming up. It will be ready in a minute or two.";
    }

    private static async Task StopServiceAsync()
    {
        var sc = Sys.System32("sc.exe");
        if (await Sys.RunAsync(sc, $"query {ServiceName}") != 0) return; // not installed
        await Sys.RunAsync(sc, $"stop {ServiceName}");
        for (var i = 0; i < 30; i++)
        {
            var stopped = false;
            await Sys.RunAsync(sc, $"query {ServiceName}", l => { if (l.Contains("STOPPED")) stopped = true; });
            if (stopped) return;
            await Task.Delay(1000);
        }
    }

    // ------------------------------------------------------------------ uninstall

    public List<InstallStep> UninstallSteps(bool removeData) => new()
    {
        new("Stopping LAIA", async c => { await StopServiceAsync(); Sys.KillByPath(InstallDir); }),
        new("Removing the background service", async c =>
        {
            await Sys.RunAsync(Sys.System32("sc.exe"), $"delete {ServiceName}");
            await Sys.RunAsync(Sys.System32("netsh.exe"), "advfirewall firewall delete rule name=\"LAIA Server\"");
        }),
        new("Deleting program files", c =>
        {
            Sys.DeleteDirectory(InstallDir);
            Sys.DeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LAIA"));
            return Task.CompletedTask;
        }),
        new("Deleting downloaded speech models and settings", c => { Sys.DeleteDirectory(DataDir); return Task.CompletedTask; }) { When = () => removeData },
        new("Removing from Settings > Apps", c =>
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(ArpKey, false); } catch { }
            return Task.CompletedTask;
        }),
    };

    public double DataSizeGb()
    {
        try { return Directory.Exists(DataDir) ? DirSizeKb(DataDir) / 1024.0 / 1024 : 0; } catch { return 0; }
    }
}
