using Laia.Common.SimConnect;

namespace Laia.Server;

/// <summary>
/// Build step 3: the server flies AI aircraft and LAIA Sim draws them. For now one test departure or
/// arrival at a time, started from the control page, on the real runway best suited to the wind.
/// </summary>
public sealed class TrafficEngine(RunwayDb runways, FrequencyDb freqs, ServerState state, ILogger<TrafficEngine> log) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<Flight> _flights = new();
    private readonly Random _rng = new();
    private static readonly (string airline, string name, string type)[] Liveries =
    {
        ("DAL", "Delta", "B739"), ("DAL", "Delta", "A321"), ("DAL", "Delta", "B752"), ("UAL", "United", "B738"), ("UAL", "United", "A320"),
        ("AAL", "American", "A321"), ("SWA", "Southwest", "B737"), ("JBU", "JetBlue", "A320"), ("ACA", "Air Canada", "A220"),
    };

    private sealed class Flight
    {
        public required TrafficAircraft Ac;
        public required RunwayEnd Rwy;
        public required string Kind; // departure | arrival
        public double Elapsed;
        public double Hold;
        public double DistanceFromThreshold; // nm along the runway direction (negative = before threshold)
        public double AglFt;
        public bool Done;
    }

    public List<TrafficAircraft> Snapshot()
    {
        lock (_gate) return _flights.Where(f => !f.Done).Select(f => Clone(f.Ac)).ToList();
    }

    private static TrafficAircraft Clone(TrafficAircraft a) => new()
    {
        Id = a.Id, Callsign = a.Callsign, Airline = a.Airline, Type = a.Type, Lat = a.Lat, Lon = a.Lon, AltFt = a.AltFt, OnGround = a.OnGround,
        Heading = a.Heading, Pitch = a.Pitch, Bank = a.Bank, GroundSpeed = a.GroundSpeed, VerticalSpeed = a.VerticalSpeed, GearDown = a.GearDown, Phase = a.Phase,
        Lights = a.Lights, AglFt = a.AglFt, TimeMs = a.TimeMs,
    };

    public void Clear() { lock (_gate) _flights.Clear(); }

    /// <summary>Starts a test flight at the airport nearest the user's aircraft. Returns a message for the control page.</summary>
    public (bool ok, string message) StartTest(string kind)
    {
        var sim = state.Sim;
        if (sim is null || !sim.Connected || DateTime.UtcNow - state.SimUpdatedUtc > TimeSpan.FromSeconds(15))
            return (false, "Start MSFS and load a flight first, so LAIA knows where you are.");
        if (!runways.Loaded) return (false, "Runway data is still downloading. Try again in a minute.");
        var near = freqs.NearestAirport(sim.Lat, sim.Lon);
        if (near is null || near.Value.nm > 30) return (false, "You're not near a towered US airport (within 30 nm).");
        var rwy = runways.Active(near.Value.ident, sim.WindDirection, sim.WindSpeed);
        if (rwy is null) return (false, $"No runway data for {near.Value.ident}.");

        var (airline, name, type) = Liveries[_rng.Next(Liveries.Length)];
        var number = _rng.Next(300, 2999);
        var ac = new TrafficAircraft
        {
            Id = Guid.NewGuid().ToString("N")[..8], Callsign = $"{airline}{number}", Airline = airline, Type = type,
            Heading = rwy.TrueHeading, Phase = kind,
        };
        var f = new Flight { Ac = ac, Rwy = rwy, Kind = kind };
        if (kind == "departure")
        {
            f.Hold = 25; // lined up and waiting
            f.DistanceFromThreshold = 0.05;
            ac.OnGround = true;
            ac.GearDown = true;
        }
        else
        {
            f.DistanceFromThreshold = -10; // 10 nm final
            f.AglFt = 10 * 318.4;          // 3 degree glidepath
            ac.GroundSpeed = 145;
            ac.GearDown = true;
        }
        Place(f);
        lock (_gate)
        {
            _flights.RemoveAll(x => x.Done);
            if (_flights.Count >= 4) _flights.RemoveAt(0);
            _flights.Add(f);
        }
        log.LogInformation("Test {kind}: {cs} {type} runway {rwy} at {apt}", kind, ac.Callsign, type, rwy.Id, rwy.Airport);
        var what = kind == "departure" ? $"lined up on runway {rwy.Id}, departing in about 25 seconds" : $"on a 10-mile final for runway {rwy.Id}";
        return (true, $"{name} {number} ({type}) is {what} at {rwy.Airport}.");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        const double dt = 0.25;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(dt));
        while (await timer.WaitForNextTickAsync(ct))
        {
            lock (_gate)
            {
                foreach (var f in _flights.Where(f => !f.Done)) Step(f, dt);
                _flights.RemoveAll(f => f.Done && f.Elapsed > 5);
            }
        }
    }

    private static void Step(Flight f, double dt)
    {
        var a = f.Ac;
        f.Elapsed += dt;
        if (f.Kind == "departure")
        {
            if (f.Hold > 0) { f.Hold -= dt; a.Phase = "lined up"; Place(f); return; }
            if (a.OnGround)
            {
                a.Phase = "takeoff roll";
                a.GroundSpeed = Math.Min(a.GroundSpeed + 4.5 * dt, 165); // ~36 s to rotation, about 5,000 ft of runway
                if (a.GroundSpeed > 145) a.Pitch = Math.Min(a.Pitch + 3 * dt, 9); // rotate
                if (a.GroundSpeed >= 160) { a.OnGround = false; a.Phase = "climbing"; }
            }
            else
            {
                a.GroundSpeed = Math.Min(a.GroundSpeed + 1.5 * dt, 250);
                a.VerticalSpeed = Math.Min(a.VerticalSpeed + 400 * dt, 2500);
                a.Pitch = Math.Clamp(a.Pitch + 1 * dt, 9, 12);
                f.AglFt += a.VerticalSpeed / 60 * dt;
                if (f.AglFt > 400) a.GearDown = false;
            }
            f.DistanceFromThreshold += a.GroundSpeed / 3600 * dt;
            if (f.DistanceFromThreshold > 15) f.Done = true;
        }
        else
        {
            if (!a.OnGround)
            {
                a.Phase = "final approach";
                f.DistanceFromThreshold += a.GroundSpeed / 3600 * dt;
                var glide = Math.Max(0, -f.DistanceFromThreshold * 318.4 + 50); // cross the threshold at ~50 ft
                if (f.AglFt > 40) { f.AglFt = glide; a.Pitch = 2.5; a.VerticalSpeed = -a.GroundSpeed * 101.3 * Math.Tan(3 * Math.PI / 180); }
                else
                {
                    a.Phase = "flare";
                    a.Pitch = Math.Min(a.Pitch + 1.5 * dt, 5);
                    a.VerticalSpeed = -180;
                    f.AglFt = Math.Max(0, f.AglFt - 3 * dt);
                    a.GroundSpeed = Math.Max(a.GroundSpeed - 1 * dt, 130);
                    if (f.AglFt <= 0) { a.OnGround = true; a.VerticalSpeed = 0; }
                }
            }
            else
            {
                a.Phase = "landing roll";
                a.Pitch = Math.Max(a.Pitch - 2 * dt, 0);
                a.GroundSpeed = Math.Max(a.GroundSpeed - 4 * dt, 0);
                f.DistanceFromThreshold += a.GroundSpeed / 3600 * dt;
                if (a.GroundSpeed < 25) { a.Phase = "vacating"; f.Done = true; }
            }
        }
        Place(f);
    }

    private const int Nav = 1, Beacon = 2, Strobes = 4, Landing = 8, Taxi = 16, Logo = 32, Wing = 64;

    /// <summary>Lights the way airline crews use them: nav/beacon/logo whenever active; strobes and
    /// landing lights on the runway and below 10,000 ft; taxi lights on the ground before takeoff.</summary>
    private static int LightsFor(Flight f)
    {
        var a = f.Ac;
        var l = Nav | Beacon | Logo;
        var belowTenThousand = f.AglFt + f.Rwy.ElevationFt < 10000;
        if (f.Kind == "departure")
        {
            if (a.OnGround) l |= Strobes | Landing | Taxi | Wing;
            else { l |= Strobes; if (belowTenThousand) l |= Landing; }
        }
        else
        {
            l |= Strobes;
            if (belowTenThousand) l |= Landing;
            if (a.OnGround && a.GroundSpeed < 60) l = (l & ~Landing & ~Strobes) | Taxi; // vacating
        }
        return l;
    }

    private static void Place(Flight f)
    {
        f.Ac.Lights = LightsFor(f);
        f.Ac.AglFt = f.AglFt;
        f.Ac.TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (lat, lon) = Geo.Offset(f.Rwy.Lat, f.Rwy.Lon, f.Rwy.TrueHeading, f.DistanceFromThreshold);
        f.Ac.Lat = lat;
        f.Ac.Lon = lon;
        f.Ac.Heading = f.Rwy.TrueHeading;
        f.Ac.AltFt = f.Rwy.ElevationFt + f.AglFt;
    }
}
