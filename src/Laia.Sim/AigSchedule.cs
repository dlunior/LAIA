using System.IO.Compression;
using System.Net.Http.Json;
using System.Xml;
using System.Xml.Linq;
using Laia.Common.Airports;

namespace Laia.Sim;

/// <summary>
/// Reads the real-world schedules that AIG installs with its traffic (Community\aig-aitraffic-oci\Traffic Files\AIGFP).
/// Each airline file lists every aircraft by model/livery and registration, with all its flights in the week.
/// Only flights flown by a model that is actually installed are used.
/// </summary>
internal static class AigSchedules
{
    // Regional partners flying under a mainline airline's schedule: their own callsign comes from the livery title.
    private static readonly (string word, string icao)[] Operators =
    {
        ("Endeavor", "EDV"), ("SkyWest", "SKW"), ("Republic", "RPA"), ("Envoy", "ENY"), ("PSA", "JIA"), ("Piedmont", "PDT"),
        ("Mesa", "ASH"), ("Air Wisconsin", "AWI"), ("GoJet", "GJS"), ("CommuteAir", "UCA"), ("Horizon", "QXE"), ("Jazz", "JZA"),
        ("PAL Airlines", "PVL"), ("Compass", "CPZ"), ("Silver Airways", "SIL"),
    };

    public static string? Folder(string community)
    {
        var direct = Path.Combine(community, "aig-aitraffic-oci", "Traffic Files", "AIGFP");
        if (Directory.Exists(direct)) return direct;
        foreach (var pkg in Directory.GetDirectories(community, "aig-*"))
        {
            var p = Path.Combine(pkg, "Traffic Files", "AIGFP");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Reads the schedules of several airports in one pass over the flight plans.</summary>
    public static Dictionary<string, ScheduleData>? Read(string community, IReadOnlyCollection<string> airports, AigCatalog catalog)
    {
        var folder = Folder(community);
        if (folder is null) return null;
        var started = DateTime.UtcNow;
        var installed = catalog.Models.GroupBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Which airline each file belongs to (and skip private/business jet files).
        var airlineOf = new Dictionary<string, (string icao, string type)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var db = XDocument.Load(Path.Combine(folder, "FPDatabase.xml"));
            foreach (var a in db.Descendants("Airline"))
                airlineOf[(string?)a.Attribute("file") ?? ""] = (((string?)a.Element("ICAO") ?? "").Trim().ToUpperInvariant(), ((string?)a.Element("Type") ?? "").Trim().ToUpperInvariant());
        }
        catch (Exception ex) { Log.Write("AIG schedules: couldn't read FPDatabase.xml: " + ex.Message); }

        var result = airports.Select(a => a.Trim().ToUpperInvariant()).Distinct()
            .ToDictionary(a => a, a => new ScheduleData { Airport = a, Source = "AIG", ReadUtc = DateTime.UtcNow }, StringComparer.OrdinalIgnoreCase);
        int files = 0, notInstalled = 0;
        foreach (var file in Directory.GetFiles(folder, "*.aigfp"))
        {
            var name = Path.GetFileName(file);
            if (name.Contains("(GA", StringComparison.OrdinalIgnoreCase)) continue;
            airlineOf.TryGetValue(name, out var info);
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var entry = zip.GetEntry("Flightplan.aigxml");
                if (entry is null) continue;
                files++;
                using var stream = entry.Open();
                using var xr = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
                string title = "", reg = "";
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element) continue;
                    switch (xr.Name)
                    {
                        case "Aircraft": title = xr.GetAttribute("title") ?? ""; break;
                        case "Registration": reg = xr.GetAttribute("id") ?? ""; break;
                        case "Leg":
                        {
                            var dep = xr.GetAttribute("dep-apt") ?? "";
                            var arr = xr.GetAttribute("arr-apt") ?? "";
                            var hits = result.TryGetValue(dep, out var d1) ? d1 : null;
                            var hita = result.TryGetValue(arr, out var d2) ? d2 : null;
                            if (hits is null && hita is null) break;
                            if (!installed.TryGetValue(title, out var model)) { notInstalled++; break; }
                            if (!int.TryParse(xr.GetAttribute("dep-time"), out var dm) || !int.TryParse(xr.GetAttribute("arr-time"), out var am)) break;
                            var airline = info.icao.Length == 3 ? info.icao : model.Airline;
                            var op = Operators.FirstOrDefault(o => title.Contains(o.word, StringComparison.OrdinalIgnoreCase)).icao ?? airline;
                            var leg = new ScheduleLeg
                            {
                                Airline = airline, Operator = op, Title = title, Type = model.Type, Reg = reg,
                                Flight = xr.GetAttribute("flightnumber") ?? "", Dep = dep.ToUpperInvariant(), Arr = arr.ToUpperInvariant(),
                                DepMin = ((dm % 10080) + 10080) % 10080, ArrMin = ((am % 10080) + 10080) % 10080,
                            };
                            hits?.Legs.Add(leg);
                            if (hita is not null && hita != hits) hita.Legs.Add(leg);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write($"AIG schedules: couldn't read {name}: {ex.Message}"); }
        }
        foreach (var d in result.Values)
            Log.Write($"AIG schedules: {d.Airport} — {d.Legs.Count} flights a week from {files} airline files");
        Log.Write($"AIG schedules: {notInstalled} more flights need models that aren't installed; read in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
        return result;
    }
}

/// <summary>Sends the schedules the server asks for, one airport at a time.</summary>
internal sealed class ScheduleUploader
{
    private readonly Dictionary<string, DateTime> _tried = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private int _busy;

    public void Request(IEnumerable<string>? airports, string serverBase, AigCatalog catalog, CancellationToken ct)
    {
        if (airports is null || catalog.Community is null) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return; // reading already; the server asks again later
        List<string> todo;
        lock (_tried)
        {
            // The server asks until it has it; read again at most every 90 seconds per airport.
            todo = airports.Where(a => !string.IsNullOrWhiteSpace(a) && (!_tried.TryGetValue(a, out var t) || DateTime.UtcNow - t > TimeSpan.FromSeconds(90))).Distinct().ToList();
            foreach (var a in todo) _tried[a] = DateTime.UtcNow;
        }
        if (todo.Count == 0) { Interlocked.Exchange(ref _busy, 0); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                Log.Write("AIG schedules: reading " + string.Join(", ", todo));
                var data = AigSchedules.Read(catalog.Community, todo, catalog);
                if (data is null) { Log.Write("AIG schedules: no AIG flight plans found"); return; }
                foreach (var d in data.Values)
                {
                    using var resp = await _http.PostAsJsonAsync(serverBase + "/api/schedule", d, ct);
                    if (!resp.IsSuccessStatusCode) Log.Write($"AIG schedules: the ATC PC refused {d.Airport} ({(int)resp.StatusCode})");
                }
            }
            catch (Exception ex) { Log.Write("AIG schedules error: " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }, ct);
    }
}
