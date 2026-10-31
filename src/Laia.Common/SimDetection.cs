using Microsoft.Win32;

namespace Laia.Common;

/// <summary>One installed copy of Microsoft Flight Simulator.</summary>
public sealed record SimInstall(string Name, string Store, string UserCfgPath, string? PackagesPath)
{
    public string? CommunityPath => PackagesPath is null ? null : Path.Combine(PackagesPath, "Community");
    public string DisplayName => $"{Name} ({Store})";
}

/// <summary>Finds MSFS 2024 / 2020 (Microsoft Store or Steam) by reading each version's UserCfg.opt.</summary>
public static class MsfsDetector
{
    public static List<SimInstall> Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new (string name, string store, string cfg)[]
        {
            ("MSFS 2024", "Microsoft Store", Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt")),
            ("MSFS 2024", "Steam", Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt")),
            ("MSFS 2020", "Microsoft Store", Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt")),
            ("MSFS 2020", "Steam", Path.Combine(roaming, "Microsoft Flight Simulator", "UserCfg.opt")),
        };
        var found = new List<SimInstall>();
        foreach (var (name, store, cfg) in candidates)
        {
            if (!File.Exists(cfg)) continue;
            found.Add(new SimInstall(name, store, cfg, ReadPackagesPath(cfg)));
        }
        return found;
    }

    public static string? ReadPackagesPath(string userCfg)
    {
        try
        {
            foreach (var raw in File.ReadLines(userCfg))
            {
                var line = raw.Trim();
                if (!line.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
                var q1 = line.IndexOf('"');
                var q2 = line.LastIndexOf('"');
                if (q1 >= 0 && q2 > q1) return line.Substring(q1 + 1, q2 - q1 - 1);
            }
        }
        catch { }
        return null;
    }
}

/// <summary>AIG (Alpha India Group) AI Manager and its aircraft models.</summary>
public static class AigDetector
{
    public static bool AiManagerInstalled()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIGTech");
            if (Directory.Exists(dir) && Directory.GetDirectories(dir, "AIGTech_-_AI_Manager*").Length > 0) return true;
        }
        catch { }
        return Uninstall.FindDisplayNames().Any(n => n.Contains("AI Manager", StringComparison.OrdinalIgnoreCase) && n.Contains("AIG", StringComparison.OrdinalIgnoreCase));
    }

    public static int CountAigPackages(IEnumerable<string?> communityFolders)
    {
        var count = 0;
        foreach (var c in communityFolders)
        {
            if (string.IsNullOrEmpty(c) || !Directory.Exists(c)) continue;
            try { count += Directory.GetDirectories(c, "aig-*").Length; } catch { }
        }
        return count;
    }
}

/// <summary>Other traffic / ATC programs that would fight with LAIA if run at the same time.</summary>
public static class ConflictDetector
{
    private static readonly (string match, string friendly)[] Known =
    {
        ("Beyond ATC", "Beyond ATC"),
        ("SayIntentions", "SayIntentions.AI"),
        ("FSLTL", "FSLTL traffic injector"),
        ("AIG Traffic Controller", "AIG Traffic Controller"),
    };

    public static List<string> Detect(IEnumerable<string?> communityFolders)
    {
        var names = Uninstall.FindDisplayNames();
        var hits = new SortedSet<string>();
        foreach (var (match, friendly) in Known)
            if (names.Any(n => n.Contains(match, StringComparison.OrdinalIgnoreCase))) hits.Add(friendly);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        void Probe(string path, string friendly) { try { if (Directory.Exists(path)) hits.Add(friendly); } catch { } }
        Probe(Path.Combine(local, "SayIntentionsAI"), "SayIntentions.AI");
        Probe(Path.Combine(roaming, "SayIntentionsAI"), "SayIntentions.AI");
        Probe(Path.Combine(local, "BeyondATC"), "Beyond ATC");
        Probe(Path.Combine(roaming, "BeyondATC"), "Beyond ATC");

        foreach (var c in communityFolders)
        {
            if (string.IsNullOrEmpty(c) || !Directory.Exists(c)) continue;
            try { if (Directory.GetDirectories(c, "fsltl-*").Length > 0) hits.Add("FSLTL traffic injector"); } catch { }
        }
        return hits.ToList();
    }
}

internal static class Uninstall
{
    public static List<string> FindDisplayNames()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        var roots = new (RegistryKey hive, string path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var s = key.OpenSubKey(sub);
                    if (s?.GetValue("DisplayName") is string n && n.Length > 0) list.Add(n);
                }
            }
            catch { }
        }
        return list;
    }
}
