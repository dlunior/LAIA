using System.Text;
using System.Xml.Linq;
using Laia.Common.Airports;
using Laia.Server.TrafficCore;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Laia.Server;

/// <summary>
/// Every US airport's FAA "Attention All Users Pages" (AAUPs), straight from the FAA's digital Terminal Procedures
/// (d-TPP): each 28-day cycle the chart list (the d-TPP metafile) says which charts an airport has; the AAUP PDFs are
/// downloaded, their text read, and turned into runway rules (AirportRules.Parse). Kept on disk per cycle, so each
/// airport is fetched once a cycle. Airports with no AAUP simply have no rules.
/// </summary>
public sealed class AaupReader(ILogger log)
{
    private static readonly HttpClient Http = MakeClient();
    private readonly Dictionary<string, (DateTime at, string text, string charts)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private XDocument? _meta;
    private string _metaCycle = "";
    private readonly SemaphoreSlim _metaLock = new(1, 1);

    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        return h;
    }

    /// <summary>
    /// The d-TPP cycle in effect on a date: "YYNN", the NN-th 28-day cycle starting that year (29 OCT 2026 = 2611).
    /// </summary>
    public static (string code, DateTime start) Cycle(DateTime utc)
    {
        var refStart = new DateTime(2026, 10, 29);
        var n = (int)Math.Floor((utc.Date - refStart).TotalDays / 28);
        var start = refStart.AddDays(28 * n);
        var first = start;
        while (first.AddDays(-28).Year == start.Year) first = first.AddDays(-28);
        var num = (int)((start - first).TotalDays / 28) + 1;
        return ($"{start:yy}{num:00}", start);
    }

    /// <summary>The airport's AAUP text and chart names (empty until fetched, or when it has none); fetches in the background.</summary>
    public (string text, string charts)? Get(string icao)
    {
        if (icao.Length != 4 || icao[0] != 'K') return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(icao, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromHours(12)) return (c.text, c.charts);
            if (_busy.Add(icao)) _ = Task.Run(() => FetchAsync(icao));
            return _cache.TryGetValue(icao, out c) ? (c.text, c.charts) : null;
        }
    }

    private async Task FetchAsync(string icao)
    {
        var text = ""; var charts = "";
        try
        {
            var (cycle, _) = Cycle(DateTime.UtcNow);
            var dir = Path.Combine(Paths.DataDir, "aaup", cycle);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, icao + ".txt");
            if (File.Exists(file))
            {
                var lines = await File.ReadAllLinesAsync(file);
                charts = lines.FirstOrDefault() ?? "";
                text = string.Join("\n", lines.Skip(1));
            }
            else
            {
                var meta = await MetafileAsync(cycle, dir);
                var apt = meta?.Descendants("airport_name").FirstOrDefault(a => (string?)a.Attribute("icao_ident") == icao
                    || "K" + (string?)a.Attribute("apt_ident") == icao);
                var recs = apt?.Elements("record").Where(r => ((string?)r.Element("chart_name") ?? "").Contains("AAUP", StringComparison.OrdinalIgnoreCase)
                    || ((string?)r.Element("chart_name") ?? "").Contains("ATTENTION ALL USERS", StringComparison.OrdinalIgnoreCase)).ToList() ?? new();
                var sb = new StringBuilder();
                var names = new List<string>();
                foreach (var r in recs)
                {
                    var pdf = (string?)r.Element("pdf_name");
                    if (string.IsNullOrEmpty(pdf)) continue;
                    var name = (string?)r.Element("chart_name") ?? pdf;
                    var bytes = await Http.GetByteArrayAsync($"https://aeronav.faa.gov/d-tpp/{cycle}/{pdf}");
                    using var doc = PdfDocument.Open(bytes);
                    foreach (var page in doc.GetPages()) sb.AppendLine(ContentOrderTextExtractor.GetText(page));
                    if (!names.Contains(name)) names.Add(name);
                }
                charts = names.Count == 0 ? "" : string.Join(", ", names);
                text = sb.ToString();
                await File.WriteAllTextAsync(file, charts + "\n" + text);
            }
            log.LogInformation("{icao} AAUP (d-TPP {cycle}): {what}", icao, cycle, charts.Length == 0 ? "none published" : charts);
            // The lines that matter for runway rules, as the PDF reader gave them (to see why a rule was or wasn't read).
            var keep = text.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+", " ").Trim())
                .Where(l => l.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(l, @"RUNWAY|RWY|LOCAL|\d{4}\s*[-–]\s*\d{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Take(40).ToList();
            if (keep.Count > 0) log.LogInformation("{icao} AAUP text (runway lines): {lines}", icao, string.Join(" | ", keep));
        }
        catch (Exception ex) { log.LogInformation("{icao} AAUP not available: {msg}", icao, ex.Message); }
        lock (_cache) { _cache[icao] = (DateTime.UtcNow, text, charts); _busy.Remove(icao); }
    }

    /// <summary>The cycle's chart list (about 30 MB), downloaded once a cycle and kept.</summary>
    private async Task<XDocument?> MetafileAsync(string cycle, string dir)
    {
        await _metaLock.WaitAsync();
        try
        {
            if (_meta is not null && _metaCycle == cycle) return _meta;
            var file = Path.Combine(dir, "d-tpp_Metafile.xml");
            if (!File.Exists(file))
            {
                log.LogInformation("Downloading the FAA chart list for d-TPP cycle {cycle}", cycle);
                var bytes = await Http.GetByteArrayAsync($"https://aeronav.faa.gov/d-tpp/{cycle}/xml_data/d-tpp_Metafile.xml");
                await File.WriteAllBytesAsync(file + ".tmp", bytes);
                File.Move(file + ".tmp", file, true);
            }
            _meta = XDocument.Load(file);
            _metaCycle = cycle;
            return _meta;
        }
        finally { _metaLock.Release(); }
    }

    /// <summary>The rules for an airport, from its AAUP text and its own SIDs and STARs.</summary>
    public static AirportRules Rules(string text, string charts, AirportData data) =>
        AirportRules.Parse(text, data.Departures.Select(d => d.Name), data.Arrivals.Select(a => a.Name), charts.Length > 0 ? charts : "AAUP");
}
