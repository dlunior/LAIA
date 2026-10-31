using System.Diagnostics;
using Microsoft.Win32;

namespace Laia.Common;

public sealed record GpuInfo(string Name, bool IsNvidia, int VramMb, string? Driver)
{
    public string VramText => VramMb > 0 ? $"{VramMb / 1024.0:0.#} GB" : "unknown memory";
}

/// <summary>Plain-language facts about this PC, used to pick sensible settings automatically.</summary>
public static class HardwareDetector
{
    public static GpuInfo? DetectGpu()
    {
        var smi = FindNvidiaSmi();
        if (smi is not null)
        {
            try
            {
                var psi = new ProcessStartInfo(smi, "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (first is not null)
                {
                    var parts = first.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 3 && int.TryParse(parts[1], out var mb))
                        return new GpuInfo(parts[0], true, mb, parts[2]);
                }
            }
            catch { }
        }
        return DetectGpuFromRegistry();
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static GpuInfo? DetectGpuFromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        GpuInfo? best = null;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return null;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!int.TryParse(sub, out _)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string name) continue;
                long bytes = 0;
                var q = k.GetValue("HardwareInformation.qwMemorySize");
                if (q is long l) bytes = l;
                else if (q is byte[] qb && qb.Length >= 8) bytes = BitConverter.ToInt64(qb, 0);
                if (bytes == 0)
                {
                    var m = k.GetValue("HardwareInformation.MemorySize");
                    if (m is int i) bytes = (uint)i;
                    else if (m is byte[] mb && mb.Length >= 4) bytes = BitConverter.ToUInt32(mb, 0);
                }
                var info = new GpuInfo(name, name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase), (int)(bytes / (1024 * 1024)), k.GetValue("DriverVersion") as string);
                if (best is null || info.VramMb > best.VramMb) best = info;
            }
        }
        catch { }
        return best;
    }

    public static string CpuName()
    {
        if (!OperatingSystem.IsWindows()) return Environment.ProcessorCount + " cores";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k?.GetValue("ProcessorNameString") is string s) return s.Trim();
        }
        catch { }
        return Environment.ProcessorCount + " cores";
    }

    public static double RamGb() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;

    public static string WindowsName()
    {
        if (!OperatingSystem.IsWindows()) return Environment.OSVersion.ToString();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = k?.GetValue("ProductName") as string ?? "Windows";
            var build = k?.GetValue("CurrentBuild") as string;
            var display = k?.GetValue("DisplayVersion") as string;
            if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return display is null ? product : $"{product} {display}";
        }
        catch { return "Windows"; }
    }

    public static double FreeDiskGb(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace / 1024.0 / 1024 / 1024; }
        catch { return -1; }
    }
}
