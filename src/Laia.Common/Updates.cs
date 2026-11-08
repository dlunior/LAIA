using System.Text.Json;

namespace Laia.Common;

/// <summary>A LAIA release on GitHub with the download links for both installers.</summary>
public sealed record ReleaseInfo(string Version, string? ServerUrl, long ServerSize, string? SimUrl, long SimSize);

/// <summary>Finds the newest LAIA release (GitHub releases of the LAIA repository) and downloads installers.</summary>
public static class Updates
{
    private const string Api = "https://api.github.com/repos/dlunior/LAIA/releases?per_page=5";

    public static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The newest published release, or null if GitHub can't be reached.</summary>
    public static async Task<ReleaseInfo?> LatestAsync(HttpClient http, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(Api, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub answered {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        ReleaseInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var version = tag.TrimStart('v', 'V');
            string? serverUrl = null, simUrl = null;
            long serverSize = 0, simSize = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                var size = a.GetProperty("size").GetInt64();
                if (name.Equals("LAIA-Server-Setup.exe", StringComparison.OrdinalIgnoreCase)) { serverUrl = url; serverSize = size; }
                if (name.Equals("LAIA-Sim-Setup.exe", StringComparison.OrdinalIgnoreCase)) { simUrl = url; simSize = size; }
            }
            if (serverUrl is null && simUrl is null) continue;
            var info = new ReleaseInfo(version, serverUrl, serverSize, simUrl, simSize);
            if (best is null || IsNewer(info.Version, best.Version)) best = info;
        }
        return best;
    }

    /// <summary>True if version a is newer than version b ("0.1.30" vs "0.1.29").</summary>
    public static bool IsNewer(string a, string b)
    {
        static int[] Parts(string v) => v.Split('+', '-')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var x = Parts(a); var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var p = i < x.Length ? x[i] : 0; var q = i < y.Length ? y[i] : 0;
            if (p != q) return p > q;
        }
        return false;
    }

    /// <summary>Downloads to a temporary name, checks the size, then moves it into place.</summary>
    public static async Task DownloadAsync(HttpClient http, string url, string path, long expectedSize, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".part";
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct);
        }
        var got = new FileInfo(tmp).Length;
        if (expectedSize > 0 && got != expectedSize)
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"Download was incomplete ({got:N0} of {expectedSize:N0} bytes)");
        }
        File.Move(tmp, path, true);
    }
}
