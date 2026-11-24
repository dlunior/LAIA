using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Laia.Collector;

/// <summary>
/// The tracks database (one SQLite file): which adsb.lol days have been collected, and for each airport and day its
/// tracks (gzipped, see TrackStore) and how many landings faced which way. Looked up by airport and day.
/// </summary>
public sealed class TrackDb
{
    private readonly string _path;
    private readonly string _cs;
    private readonly object _write = new();

    public sealed record AirportDay(string Day, int Tracks, Dictionary<int, int> Landings);
    public sealed record Row(string Icao, int Tracks, Dictionary<int, int> Landings, byte[] Data);

    public TrackDb(string path)
    {
        _path = path;
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA auto_vacuum=INCREMENTAL;");   // only takes effect on a new file: space freed by old days is given back
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS days(day TEXT PRIMARY KEY, done_utc TEXT NOT NULL, aircraft INTEGER NOT NULL, seconds REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS airport_days(icao TEXT NOT NULL, day TEXT NOT NULL, tracks INTEGER NOT NULL, landings TEXT NOT NULL, bytes INTEGER NOT NULL, PRIMARY KEY(icao, day));
            CREATE INDEX IF NOT EXISTS airport_days_day ON airport_days(day);
            CREATE TABLE IF NOT EXISTS tracks(icao TEXT NOT NULL, day TEXT NOT NULL, data BLOB NOT NULL, PRIMARY KEY(icao, day));
            CREATE INDEX IF NOT EXISTS tracks_day ON tracks(day);
            CREATE TABLE IF NOT EXISTS legs(day TEXT PRIMARY KEY, flights INTEGER NOT NULL, data BLOB NOT NULL);
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }
    private static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    /// <summary>The days collected, newest first.</summary>
    public List<string> Days()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT day FROM days ORDER BY day DESC";
        using var r = cmd.ExecuteReader();
        var res = new List<string>();
        while (r.Read()) res.Add(r.GetString(0));
        return res;
    }

    public int AirportCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT icao) FROM airport_days";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public long SizeBytes()
    {
        long n = 0;
        foreach (var f in new[] { _path, _path + "-wal" }) if (File.Exists(f)) n += new FileInfo(f).Length;
        return n;
    }

    /// <summary>An airport's days, newest first.</summary>
    public List<AirportDay> AirportDays(string icao)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT day, tracks, landings FROM airport_days WHERE icao = $i ORDER BY day DESC";
        cmd.Parameters.AddWithValue("$i", icao);
        using var r = cmd.ExecuteReader();
        var res = new List<AirportDay>();
        while (r.Read())
        {
            Dictionary<int, int> land;
            try { land = JsonSerializer.Deserialize<Dictionary<int, int>>(r.GetString(2)) ?? new(); } catch { land = new(); }
            res.Add(new AirportDay(r.GetString(0), r.GetInt32(1), land));
        }
        return res;
    }

    public byte[]? Tracks(string icao, string day)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM tracks WHERE icao = $i AND day = $d";
        cmd.Parameters.AddWithValue("$i", icao);
        cmd.Parameters.AddWithValue("$d", day);
        return cmd.ExecuteScalar() as byte[];
    }

    /// <summary>Saves a collected day in one go (replacing it if it was collected before).</summary>
    public void SaveDay(string day, IReadOnlyList<Row> rows, long aircraft, double seconds)
    {
        lock (_write)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var t in new[] { "airport_days", "tracks" })
            {
                using var del = c.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {t} WHERE day = $d";
                del.Parameters.AddWithValue("$d", day);
                del.ExecuteNonQuery();
            }
            using var a = c.CreateCommand();
            a.Transaction = tx;
            a.CommandText = "INSERT INTO airport_days(icao, day, tracks, landings, bytes) VALUES($i, $d, $n, $l, $b)";
            var ai = a.Parameters.Add("$i", SqliteType.Text); var ad = a.Parameters.Add("$d", SqliteType.Text);
            var an = a.Parameters.Add("$n", SqliteType.Integer); var al = a.Parameters.Add("$l", SqliteType.Text); var ab = a.Parameters.Add("$b", SqliteType.Integer);
            using var t2 = c.CreateCommand();
            t2.Transaction = tx;
            t2.CommandText = "INSERT INTO tracks(icao, day, data) VALUES($i, $d, $x)";
            var ti = t2.Parameters.Add("$i", SqliteType.Text); var td = t2.Parameters.Add("$d", SqliteType.Text); var tx2 = t2.Parameters.Add("$x", SqliteType.Blob);
            foreach (var r in rows)
            {
                ai.Value = r.Icao; ad.Value = day; an.Value = r.Tracks; al.Value = JsonSerializer.Serialize(r.Landings); ab.Value = r.Data.Length;
                a.ExecuteNonQuery();
                ti.Value = r.Icao; td.Value = day; tx2.Value = r.Data;
                t2.ExecuteNonQuery();
            }
            using var d = c.CreateCommand();
            d.Transaction = tx;
            d.CommandText = "INSERT OR REPLACE INTO days(day, done_utc, aircraft, seconds) VALUES($d, $u, $a, $s)";
            d.Parameters.AddWithValue("$d", day);
            d.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
            d.Parameters.AddWithValue("$a", aircraft);
            d.Parameters.AddWithValue("$s", seconds);
            d.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <summary>A day's real flights (TraceLegs, gzipped JSON), replacing any kept before.</summary>
    public void SaveLegs(string day, int flights, byte[] data)
    {
        lock (_write)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO legs(day, flights, data) VALUES($d, $n, $x)";
            cmd.Parameters.AddWithValue("$d", day); cmd.Parameters.AddWithValue("$n", flights); cmd.Parameters.AddWithValue("$x", data);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The days whose flights are kept, newest first, with how many.</summary>
    public List<(string day, int flights)> LegDays()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT day, flights FROM legs ORDER BY day DESC";
        using var r = cmd.ExecuteReader();
        var res = new List<(string, int)>();
        while (r.Read()) res.Add((r.GetString(0), r.GetInt32(1)));
        return res;
    }

    public byte[]? Legs(string day)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM legs WHERE day = $d";
        cmd.Parameters.AddWithValue("$d", day);
        return cmd.ExecuteScalar() as byte[];
    }

    /// <summary>Removes days older than this many days (day names are yyyy.MM.dd); returns how many.</summary>
    public int Prune(int keepDays)
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-keepDays).ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        lock (_write)
        {
            using var c = Open();
            int n;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM days WHERE day < $c";
                cmd.Parameters.AddWithValue("$c", cutoff);
                n = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            if (n == 0) return 0;
            foreach (var t in new[] { "days", "airport_days", "tracks", "legs" })
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"DELETE FROM {t} WHERE day < $c";
                cmd.Parameters.AddWithValue("$c", cutoff);
                cmd.ExecuteNonQuery();
            }
            Exec(c, "PRAGMA incremental_vacuum;");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            return n;
        }
    }
}
