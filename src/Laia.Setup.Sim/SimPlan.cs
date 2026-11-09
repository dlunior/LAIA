using System.Diagnostics;
using System.Text.Json;
using Laia.Common;
using Laia.Setup.Common;
using Microsoft.Win32;

namespace Laia.Setup.Sim;

/// <summary>What the sim-side installer detects on its own, and the install / uninstall steps.</summary>
internal sealed class SimPlan
{
    public const string ArpKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LAIA Sim";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string InstallDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LAIA Sim");
    public string Exe => Path.Combine(InstallDir, "LaiaSim.exe");
    public string SettingsDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LAIA");
    public string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LAIA Sim.lnk");
    public string Version { get; } = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";

    public List<SimInstall> Sims { get; private set; } = new();
    public bool AiManager { get; private set; }
    public int AigPackages { get; private set; }
    public List<string> Conflicts { get; private set; } = new();
    public List<ServerInfo> Servers { get; private set; } = new();

    public void DetectLocal()
    {
        Sims = MsfsDetector.Detect();
        var community = Sims.Select(s => s.CommunityPath).ToList();
        AiManager = AigDetector.AiManagerInstalled();
        AigPackages = AigDetector.CountAigPackages(community);
        Conflicts = ConflictDetector.Detect(community);
        SetupLog.Write($"Sims: {string.Join("; ", Sims.Select(s => $"{s.DisplayName} -> {s.CommunityPath}"))}");
        SetupLog.Write($"AI Manager: {AiManager}; AIG packages: {AigPackages}; conflicts: {string.Join(", ", Conflicts)}");
    }

    public async Task DiscoverAsync()
    {
        Servers = await Discovery.FindServersAsync(TimeSpan.FromSeconds(3));
        SetupLog.Write($"Servers: {string.Join(", ", Servers.Select(s => $"{s.Machine}@{s.Address}"))}");
    }

    public List<InstallStep> InstallSteps() => new()
    {
        new("Closing LAIA Sim if it's open", c => { Sys.KillByPath(InstallDir); return Task.CompletedTask; }, weight: 0.3),
        new("Copying LAIA Sim", c =>
        {
            if (!Payload.TryOpen(out var zip)) throw new InvalidOperationException("This setup file is damaged. Please download it again.");
            Sys.MoveAsideLocked(InstallDir);
            using (zip) Payload.Extract(zip!, InstallDir, (f, n) => c.Report(f, Path.GetFileName(n)));
            return Task.CompletedTask;
        }, weight: 3),
        new("Saving what we found on this PC", c =>
        {
            Directory.CreateDirectory(SettingsDir);
            var info = new
            {
                detectedAt = DateTime.Now,
                sims = Sims.Select(s => new { s.Name, s.Store, s.UserCfgPath, s.PackagesPath, s.CommunityPath }),
                aiManager = AiManager,
                aigPackages = AigPackages,
                conflicts = Conflicts,
            };
            File.WriteAllText(Path.Combine(SettingsDir, "sim-detected.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            c.Result = Sims.Count > 0 ? $"Using {Sims[0].DisplayName}" : null;
            return Task.CompletedTask;
        }, weight: 0.2),
        new("Adding to Start menu and starting with Windows", c =>
        {
            Sys.CreateShortcut(StartMenuLink, Exe, description: "LAIA Sim - push-to-talk radio for LAIA ATC");
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue("LAIA Sim", $"\"{Exe}\"");
            using (var k = Registry.CurrentUser.CreateSubKey(ArpKey))
            {
                var uninstaller = Path.Combine(InstallDir, "uninstall.exe");
                k.SetValue("DisplayName", "LAIA Sim");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "LAIA");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("DisplayIcon", Exe);
                k.SetValue("UninstallString", $"\"{uninstaller}\"");
                k.SetValue("QuietUninstallString", $"\"{uninstaller}\" /silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 75_000, RegistryValueKind.DWord);
            }
            try { File.Copy(SetupLog.FilePath, Path.Combine(SettingsDir, "setup.log"), true); } catch { }
            return Task.CompletedTask;
        }, weight: 0.5),
    };

    public void Launch()
    {
        try { Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = InstallDir }); }
        catch (Exception ex) { SetupLog.Write("Launch failed: " + ex.Message); }
    }

    public List<InstallStep> UninstallSteps(bool removeSettings) => new()
    {
        new("Closing LAIA Sim", c => { Sys.KillByPath(InstallDir); return Task.CompletedTask; }),
        new("Removing from Start menu and startup", c =>
        {
            try { File.Delete(StartMenuLink); } catch { }
            try { using var run = Registry.CurrentUser.OpenSubKey(RunKey, true); run?.DeleteValue("LAIA Sim", false); } catch { }
            return Task.CompletedTask;
        }),
        new("Deleting program files", c => { Sys.DeleteDirectory(InstallDir); return Task.CompletedTask; }),
        new("Deleting your LAIA Sim settings", c => { Sys.DeleteDirectory(SettingsDir); return Task.CompletedTask; }) { When = () => removeSettings },
        new("Removing from Settings > Apps", c =>
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(ArpKey, false); } catch { }
            return Task.CompletedTask;
        }),
    };
}
