using Laia.Common.Airports;

namespace Laia.Server.TrafficCore;

/// <summary>One end of a runway on the local map.</summary>
public sealed class RunwayEnd
{
    public required string Ident;
    public required int Runway;        // index of the physical runway (both ends share it)
    public required double Heading;    // true
    public required V2 Threshold;
    public required V2 FarEnd;
    public required double LengthM;
    public required double WidthM;
    public required double ElevationFt;
    public V2 Dir => V2.FromBearing(Heading);
    /// <summary>Distance along the runway from this threshold (negative before it).</summary>
    public double Along(V2 p) => V2.Dot(p - Threshold, Dir);
    public double Offset(V2 p) => V2.Cross(Dir, p - Threshold); // + left of centreline
    public bool Contains(V2 p, double margin = 5) =>
        Along(p) > -margin - 30 && Along(p) < LengthM + margin + 30 && Math.Abs(Offset(p)) < WidthM / 2 + margin;
}

public sealed class GraphEdge
{
    public required int A;
    public required int B;
    public required double Len;
    public required int Type;      // 1 taxi, 2 runway, 3 parking, 4 path
    public string Name = "";
}

/// <summary>A waypoint of an air route, with the procedure's constraints.</summary>
public sealed record AirPoint(V2 P, string Fix, double AltMinFt = 0, double AltMaxFt = 0, double SpeedKt = 0);

/// <summary>
/// The airport ready for traffic: everything on a flat local map in metres, plus a taxi network for route
/// finding. Built from the AirportData that LAIA Sim read out of MSFS.
/// </summary>
public sealed class AirportModel
{
    public string Icao { get; }
    public string Name { get; }
    public LocalMap Map { get; }
    public double ElevationFt { get; }
    public double MagVar { get; }          // east positive: true = magnetic + MagVar
    public List<RunwayEnd> Ends { get; } = new();
    public int RunwayCount { get; }
    public V2[] Nodes { get; private set; } = Array.Empty<V2>();
    public int TaxiPointCount { get; private set; }
    public int[] NodeRunway { get; private set; } = Array.Empty<int>();   // physical runway the node is on, or -1
    public bool[] NodeHoldShort { get; private set; } = Array.Empty<bool>();
    public List<GraphEdge>[] Adj { get; private set; } = Array.Empty<List<GraphEdge>>();
    public List<ParkingSpot> Spots { get; } = new();
    /// <summary>Vehicle service roads (the road behind the gates marks where a pushback may start turning).</summary>
    public List<(V2 a, V2 b)> Roads { get; } = new();
    /// <summary>All taxi lines (taxiways and apron lanes) as segments between graph nodes.</summary>
    public List<GraphEdge> TaxiLines { get; } = new();
    public AirportData Data { get; }
    public string Calibration { get; private set; } = "";
    public bool HasGround => Spots.Count > 0 && Nodes.Length > 0;

    public sealed class ParkingSpot
    {
        public required int Index;      // parking index in the data
        public required int Node;       // graph node of the spot itself
        public required V2 P;
        public required double Heading;
        public required double RadiusM;
        public required int Type;
        public required string Label;
        public int Access = -1;          // taxi node the spot connects to
        public List<string> Airlines = new(); // ICAO codes of the airlines that use this stand (from the scenery)
        /// <summary>Alternative stands drawn over this one (e.g. one large stand covering two small ones): only one can be used.</summary>
        public List<ParkingSpot> Overlaps = new();
        public bool IsGate => Type is 8 or 9 or 10 or 15;
        // By the stand's radius (half the largest wingspan it takes); the type names (small/medium/heavy) vary by scenery.
        public int Size => RadiusM >= 33 ? 2 : RadiusM >= 17 ? 1 : RadiusM > 0 ? 0 : Type switch { 10 or 15 => 2, 9 => 1, _ => 0 };
    }

    public AirportModel(AirportData d)
    {
        Data = d;
        Icao = d.Icao;
        // The sim's "no suffix" comes through as "0" (it showed as "ILS 36L 0").
        foreach (var ap in d.Approaches) if (ap.Suffix is "0" || ap.Suffix.Trim().Length == 0) ap.Suffix = "";
        Name = d.Name;
        Map = new LocalMap(d.Lat, d.Lon);
        ElevationFt = d.ElevationM * Geo2.FtPerM;
        for (var i = 0; i < d.Runways.Count; i++)
        {
            var r = d.Runways[i];
            var c = Map.ToXY(r.Lat, r.Lon);
            var dir = V2.FromBearing(r.Heading);
            var half = r.LengthM / 2;
            var elev = r.ElevationM * Geo2.FtPerM;
            if (r.PrimaryNumber is >= 1 and <= 36)
                Ends.Add(new RunwayEnd { Ident = AirportRunway.Ident(r.PrimaryNumber, r.PrimaryDesignator), Runway = i, Heading = Geo2.Norm(r.Heading), Threshold = c - dir * half, FarEnd = c + dir * half, LengthM = r.LengthM, WidthM = Math.Max(20, r.WidthM), ElevationFt = elev });
            if (r.SecondaryNumber is >= 1 and <= 36)
                Ends.Add(new RunwayEnd { Ident = AirportRunway.Ident(r.SecondaryNumber, r.SecondaryDesignator), Runway = i, Heading = Geo2.Norm(r.Heading + 180), Threshold = c + dir * half, FarEnd = c - dir * half, LengthM = r.LengthM, WidthM = Math.Max(20, r.WidthM), ElevationFt = elev });
        }
        RunwayCount = d.Runways.Count;
        MagVar = EstimateMagVar(d);
        BuildGround(d);
    }

    /// <summary>A runway-only airport (no layout data yet): used for the test buttons.</summary>
    public static AirportModel RunwayOnly(string icao, double lat, double lon, double elevFt, double headingTrue, double lengthFt, string ident, double thrLat, double thrLon)
    {
        var lengthM = lengthFt / Geo2.FtPerM;
        var map = new LocalMap(thrLat, thrLon);
        var dir = V2.FromBearing(headingTrue);
        var c = dir * (lengthM / 2);
        var (clat, clon) = map.ToLatLon(c);
        var num = int.TryParse(new string(ident.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 1;
        var des = ident.EndsWith('L') ? 1 : ident.EndsWith('R') ? 2 : ident.EndsWith('C') ? 3 : 0;
        var opp = (num + 18 - 1) % 36 + 1;
        var odes = des switch { 1 => 2, 2 => 1, _ => des };
        var d = new AirportData
        {
            Icao = icao, Name = icao, Lat = lat, Lon = lon, ElevationM = elevFt / Geo2.FtPerM,
            Runways = { new AirportRunway { Lat = clat, Lon = clon, ElevationM = elevFt / Geo2.FtPerM, Heading = headingTrue, LengthM = lengthM, WidthM = 45, PrimaryNumber = num, PrimaryDesignator = des, SecondaryNumber = opp, SecondaryDesignator = odes } },
        };
        return new AirportModel(d);
    }

    private static double EstimateMagVar(AirportData d)
    {
        // Runway numbers are magnetic headings / 10; compare them with the true headings.
        var diffs = new List<double>();
        foreach (var r in d.Runways)
            if (r.PrimaryNumber is >= 1 and <= 36) diffs.Add(Geo2.Diff(r.PrimaryNumber * 10, r.Heading));
        var est = diffs.Count > 0 ? diffs.Average() : 0;
        if (Math.Abs(d.MagVar) < 0.01) return est;
        var a = d.MagVar > 180 ? d.MagVar - 360 : d.MagVar;
        return Math.Abs(a - est) <= Math.Abs(-a - est) ? a : -a;
    }

    public V2 ToXY(double lat, double lon) => Map.ToXY(lat, lon);

    private void BuildGround(AirportData d)
    {
        var np = d.Points.Count;
        var nk = d.Parkings.Count;
        if (np == 0) return;
        // The offsets' axes: MSFS documents X as east and Z as north. Check against where the runway paths
        // land and pick the reading that puts them on the runways (guards against a sign or axis mix-up).
        var transforms = new (string name, Func<double, double, V2> f)[]
        {
            ("x east, z north", (x, z) => new V2(x, z)), ("x east, z south", (x, z) => new V2(x, -z)),
            ("x west, z north", (x, z) => new V2(-x, z)), ("x west, z south", (x, z) => new V2(-x, -z)),
            ("z east, x north", (x, z) => new V2(z, x)), ("z east, x south", (x, z) => new V2(z, -x)),
            ("z west, x north", (x, z) => new V2(-z, x)), ("z west, x south", (x, z) => new V2(-z, -x)),
        };
        var runwayPts = d.Paths.Where(p => p.Type == 2).SelectMany(p => new[] { p.Start, p.End }).Where(i => i >= 0 && i < np).Distinct().ToList();
        var sample = runwayPts.Count >= 4 ? runwayPts : Enumerable.Range(0, np).ToList();
        var bestScore = -1.0;
        var best = transforms[0];
        foreach (var tr in transforms)
        {
            double score;
            if (runwayPts.Count >= 4)
                score = sample.Count(i => Ends.Any(e => e.Contains(tr.f(d.Points[i].BiasX, d.Points[i].BiasZ), 20))) / (double)sample.Count;
            else
                score = sample.Count(i => Ends.Any(e => Math.Abs(e.Offset(tr.f(d.Points[i].BiasX, d.Points[i].BiasZ))) < 600)) / (double)sample.Count;
            if (score > bestScore + 1e-9) { bestScore = score; best = tr; }
        }
        Calibration = $"{best.name} ({bestScore:P0} of runway taxi points on a runway)";

        Nodes = new V2[np + nk];
        for (var i = 0; i < np; i++) Nodes[i] = best.f(d.Points[i].BiasX, d.Points[i].BiasZ);
        for (var k = 0; k < nk; k++) Nodes[np + k] = best.f(d.Parkings[k].BiasX, d.Parkings[k].BiasZ);
        TaxiPointCount = np;
        NodeRunway = new int[Nodes.Length];
        NodeHoldShort = new bool[Nodes.Length];
        for (var i = 0; i < Nodes.Length; i++)
        {
            NodeRunway[i] = -1;
            if (i < np)
            {
                NodeHoldShort[i] = d.Points[i].Type is 2 or 4 or 5 or 6;
                foreach (var e in Ends) if (e.Contains(Nodes[i], 3)) { NodeRunway[i] = e.Runway; break; }
            }
        }
        Adj = new List<GraphEdge>[Nodes.Length];
        for (var i = 0; i < Adj.Length; i++) Adj[i] = new List<GraphEdge>();
        foreach (var p in d.Paths)
        {
            if (p.Type is 6 or 7)
            {
                if (p.Start >= 0 && p.Start < np && p.End >= 0 && p.End < np) Roads.Add((Nodes[p.Start], Nodes[p.End]));
                continue;
            }
            if (p.Type is not (1 or 2 or 3 or 4)) continue;
            var a = p.Start;
            var b = p.Type == 3 ? np + p.End : p.End;
            if (a < 0 || a >= Nodes.Length || b < 0 || b >= Nodes.Length || a == b) continue;
            if (p.Type == 3 && (p.End < 0 || p.End >= nk)) continue;
            var len = (Nodes[a] - Nodes[b]).Length;
            if (len < 0.1 || len > 5000) continue;
            var name = p.NameIndex >= 0 && p.NameIndex < d.TaxiNames.Count ? d.TaxiNames[p.NameIndex] : "";
            Adj[a].Add(new GraphEdge { A = a, B = b, Len = len, Type = p.Type, Name = name });
            if (p.Type is 1 or 4) TaxiLines.Add(new GraphEdge { A = a, B = b, Len = len, Type = p.Type, Name = name });
            Adj[b].Add(new GraphEdge { A = b, B = a, Len = len, Type = p.Type, Name = name });
        }
        for (var k = 0; k < nk; k++)
        {
            var pk = d.Parkings[k];
            var node = np + k;
            var spot = new ParkingSpot { Index = k, Node = node, P = Nodes[node], Heading = Geo2.Norm(pk.Heading), RadiusM = pk.RadiusM, Type = pk.Type, Label = pk.Label(),
                Airlines = pk.Airlines.SelectMany(AirlineCodes.ToIcaoList).Distinct().ToList() };
            var link = Adj[node].Where(e => e.B < np).OrderBy(e => e.Len).FirstOrDefault();
            if (link is not null) spot.Access = link.B;
            else
            {
                // No parking path: connect to the nearest taxi point that is part of the network.
                var bestNode = -1; var bestD = double.MaxValue;
                for (var i = 0; i < np; i++)
                {
                    if (Adj[i].Count == 0 || NodeRunway[i] >= 0) continue;
                    var dd = (Nodes[i] - spot.P).Length;
                    if (dd < bestD) { bestD = dd; bestNode = i; }
                }
                if (bestNode >= 0 && bestD < 250)
                {
                    spot.Access = bestNode;
                    Adj[node].Add(new GraphEdge { A = node, B = bestNode, Len = bestD, Type = 3 });
                    Adj[bestNode].Add(new GraphEdge { A = bestNode, B = node, Len = bestD, Type = 3 });
                }
            }
            if (spot.Access >= 0 && pk.Type is not (6 or 7 or 11 or 12 or 13)) Spots.Add(spot);
        }
        LinkOverlappingStands();
    }

    private void LinkOverlappingStands()
    {
        foreach (var a in Spots)
            foreach (var b in Spots)
                if (a != b && (a.P - b.P).Length < Math.Max(6, (a.RadiusM + b.RadiusM) * 0.7)) a.Overlaps.Add(b);
    }

    /// <summary>
    /// Shortest taxi route between two nodes (A*). Parking spots are never passed through, runway edges
    /// are avoided (used only when nothing else works), and long runway backtaxis are heavily penalised.
    /// </summary>
    public List<int>? Route(int from, int to, IReadOnlySet<int>? avoidRunways = null, IReadOnlySet<int>? avoidNodes = null)
    {
        if (from < 0 || to < 0 || from >= Nodes.Length || to >= Nodes.Length) return null;
        var dist = new Dictionary<int, double> { [from] = 0 };
        var prev = new Dictionary<int, int>();
        var open = new PriorityQueue<int, double>();
        open.Enqueue(from, 0);
        var closed = new HashSet<int>();
        while (open.TryDequeue(out var u, out _))
        {
            if (u == to) break;
            if (!closed.Add(u)) continue;
            foreach (var e in Adj[u])
            {
                var v = e.B;
                if (v >= TaxiPointCount && v != to) continue; // don't route through other parking spots
                if (avoidNodes is not null && avoidNodes.Contains(v) && v != to) continue;
                var cost = e.Len;
                // Prefer named taxiways; apron alleys and lead-ins only to reach a gate.
                // Named taxiways (A, B, AA, D5…) are preferred; numbered or unnamed lines are usually apron lanes.
                cost *= e.Type switch { 2 => 8, 3 => 4, _ => IsTaxiway(e.Name) ? 1.0 : 2.5 };
                if (NodeRunway[v] >= 0 && avoidRunways is not null && avoidRunways.Contains(NodeRunway[v])) cost += 400; // crossing an active runway
                var nd = dist[u] + cost;
                if (dist.TryGetValue(v, out var old) && old <= nd) continue;
                dist[v] = nd;
                prev[v] = u;
                open.Enqueue(v, nd + (Nodes[v] - Nodes[to]).Length);
            }
        }
        if (!prev.ContainsKey(to) && from != to) return null;
        var path = new List<int> { to };
        while (path[^1] != from) path.Add(prev[path[^1]]);
        path.Reverse();
        return path;
    }

    public static bool IsTaxiway(string name) => name.Length > 0 && char.IsLetter(name[0]);

    /// <summary>Taxi nodes that sit on a runway's centreline area and connect off it: (runway node, off-runway node).</summary>
    public IEnumerable<(int on, int off)> RunwayLinks(int runway)
    {
        for (var i = 0; i < TaxiPointCount; i++)
        {
            if (NodeRunway[i] != runway) continue;
            foreach (var e in Adj[i])
                if (e.B < TaxiPointCount && NodeRunway[e.B] != runway && e.Type != 2) yield return (i, e.B);
        }
    }

    /// <summary>One line for the log: how much of the layout LAIA could use.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        for (var r = 0; r < RunwayCount; r++)
        {
            var ends = string.Join("/", Ends.Where(e => e.Runway == r).Select(e => e.Ident));
            var nodes = NodeRunway.Count(x => x == r);
            var links = RunwayLinks(r).Count();
            parts.Add($"{ends}: {nodes} taxi points on it, {links} taxiway connections");
        }
        var connected = Spots.Count(s => s.Access >= 0);
        return $"{Spots.Count} parking spots usable ({Spots.Count(s => s.IsGate)} gates); " + string.Join("; ", parts);
    }

    public int NearestNode(V2 p, bool offRunway = true)
    {
        var best = -1; var bestD = double.MaxValue;
        for (var i = 0; i < TaxiPointCount; i++)
        {
            if (Adj[i].Count == 0 || (offRunway && NodeRunway[i] >= 0)) continue;
            var d = (Nodes[i] - p).Length;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ------------------------------------------------------------------ procedures

    private double TrueCourse(ProcedureLeg l) => l.TrueCourse ? l.Course : Geo2.Norm(l.Course + MagVar);

    /// <summary>Converts procedure legs to points on the map. Vector legs end the list (ATC would vector from there).</summary>
    public List<AirPoint> LegsToPoints(IEnumerable<ProcedureLeg> legs, V2 start, double startAltFt, out bool endedInVectors, bool hybrid = false)
    {
        var pts = new List<AirPoint>();
        var cur = start;
        var alt = startAltFt;
        endedInVectors = false;
        var list = legs.ToList();
        for (var li = 0; li < list.Count; li++)
        {
            var l = list[li];
            double minA = 0, maxA = 0;
            switch (l.AltDesc)
            {
                case 1: minA = maxA = l.Alt1Ft; break;
                case 2: minA = l.Alt1Ft; break;
                case 3: maxA = l.Alt1Ft; break;
                case 4: maxA = l.Alt1Ft; minA = l.Alt2Ft; break;
            }
            var hasFix = Math.Abs(l.Lat) > 0.0001 || Math.Abs(l.Lon) > 0.0001;
            switch (l.Type)
            {
                case 2 or 8 or 19: // CA, FA, VA: climb on a course to an altitude
                {
                    var target = l.Alt1Ft > 0 ? l.Alt1Ft : alt + 1500;
                    var nm = Math.Clamp((target - alt) / 450, 1.0, 8);
                    cur += V2.FromBearing(TrueCourse(l), nm * Geo2.MPerNm);
                    alt = Math.Max(alt, target);
                    pts.Add(new AirPoint(cur, "", minA, maxA, l.SpeedKt));
                    break;
                }
                case 11 or 22: // FM, VM: heading until vectored
                {
                    // A hybrid procedure (MCO's RNAV SIDs: runway → KAAPE, then "expect radar vectors" to UKAYY, then
                    // RNAV again): ATC vectors off the heading after a few miles and clears it direct to the next fix,
                    // where pilot navigation resumes. Only when no fix follows does the route end in vectors.
                    var next = !hybrid ? null : list.Skip(li + 1).FirstOrDefault(n => Math.Abs(n.Lat) > 0.0001 || Math.Abs(n.Lon) > 0.0001);
                    if (next is not null)
                    {
                        var to = Map.ToXY(next.Lat, next.Lon);
                        var nm = Math.Clamp((to - cur).Length / Geo2.MPerNm * 0.4, 2, 6);
                        cur += V2.FromBearing(TrueCourse(l), nm * Geo2.MPerNm);
                        pts.Add(new AirPoint(cur, "", minA, maxA, l.SpeedKt));
                        break;
                    }
                    cur += V2.FromBearing(TrueCourse(l), 5 * Geo2.MPerNm);
                    pts.Add(new AirPoint(cur, "", minA, maxA, l.SpeedKt));
                    endedInVectors = true;
                    return pts;
                }
                case 5 or 21 or 3 or 10 or 20 or 6 or 23: // CI/VI/CD/FD/VD/CR/VR: short course leg
                    if (hasFix) goto default;
                    cur += V2.FromBearing(TrueCourse(l), 3 * Geo2.MPerNm);
                    pts.Add(new AirPoint(cur, "", minA, maxA, l.SpeedKt));
                    break;
                case 12 or 13 or 14 or 16: // holds and procedure turns: skip
                    break;
                default:
                    if (!hasFix) break;
                    cur = Map.ToXY(l.Lat, l.Lon);
                    if (maxA > 0) alt = Math.Min(Math.Max(alt, minA), maxA); else if (minA > 0) alt = Math.Max(alt, minA);
                    pts.Add(new AirPoint(cur, l.Fix, minA, maxA, l.SpeedKt));
                    break;
            }
        }
        return pts;
    }
}

/// <summary>Scenery lists stand airlines by ICAO (DAL) or IATA (DL) code; LAIA works in ICAO.</summary>
public static class AirlineCodes
{
    private static readonly Dictionary<string, string> Iata = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DL"] = "DAL", ["AA"] = "AAL", ["UA"] = "UAL", ["WN"] = "SWA", ["B6"] = "JBU", ["AS"] = "ASA", ["NK"] = "NKS", ["F9"] = "FFT",
        ["OO"] = "SKW", ["YX"] = "RPA", ["MQ"] = "ENY", ["9E"] = "EDV", ["OH"] = "JIA", ["PT"] = "PDT", ["G4"] = "AAY", ["SY"] = "SCX",
        ["HA"] = "HAL", ["MX"] = "MXY", ["QX"] = "QXE", ["G7"] = "GJS", ["YV"] = "ASH", ["C5"] = "UCA", ["ZW"] = "AWI", ["AC"] = "ACA",
        ["WS"] = "WJA", ["QK"] = "JZA", ["PD"] = "POE", ["TS"] = "TSC", ["WG"] = "SWG", ["F8"] = "FLE", ["XP"] = "VXP", ["BA"] = "BAW",
        ["LH"] = "DLH", ["AF"] = "AFR", ["KL"] = "KLM", ["IB"] = "IBE", ["VS"] = "VIR", ["EI"] = "EIN", ["LX"] = "SWR", ["TK"] = "THY",
        ["EK"] = "UAE", ["QR"] = "QTR", ["EY"] = "ETD", ["AM"] = "AMX", ["Y4"] = "VOI", ["CM"] = "CMP", ["AV"] = "AVA", ["FX"] = "FDX",
        ["5X"] = "UPS", ["U2"] = "EZY", ["FR"] = "RYR", ["SK"] = "SAS", ["AY"] = "FIN", ["OS"] = "AUA", ["TP"] = "TAP", ["AZ"] = "ITY",
        ["SN"] = "BEL", ["LO"] = "LOT", ["W6"] = "WZZ", ["NH"] = "ANA", ["JL"] = "JAL", ["CX"] = "CPA", ["SQ"] = "SIA", ["KE"] = "KAL",
        ["QF"] = "QFA", ["NZ"] = "ANZ", ["LA"] = "LAN", ["ET"] = "ETH",
    };

    // Scenery codes like "DALX" mean the airline's regional partners (Delta Connection etc.); EGF is American Eagle's old code.
    private static readonly Dictionary<string, string[]> Partners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DALX"] = new[] { "EDV", "SKW", "RPA" }, ["AALX"] = new[] { "ENY", "PDT", "JIA", "SKW", "RPA" }, ["EGF"] = new[] { "ENY" },
        ["UALX"] = new[] { "SKW", "RPA", "ASH", "GJS", "UCA" }, ["ACAX"] = new[] { "JZA", "SKV" }, ["ASAX"] = new[] { "QXE", "SKW" },
    };

    public static IEnumerable<string> ToIcaoList(string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (Partners.TryGetValue(code, out var list)) return list;
        var one = ToIcao(code);
        return one.Length == 3 ? new[] { one } : Array.Empty<string>();
    }

    public static string ToIcao(string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 3 && code.All(char.IsLetter)) return code;
        return Iata.TryGetValue(code, out var icao) ? icao : "";
    }
}
