using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Laia.Common;

/// <summary>Network ports LAIA uses. The server installer opens these in Windows Firewall (local network only).</summary>
public static class LaiaPorts
{
    public const int Discovery = 47800;   // UDP: "is there a LAIA server here?"
    public const int Http = 47801;        // TCP: radio audio, status, control page
    public const int SpeechWorker = 47811; // TCP, localhost only: speech engine inside the server
}

/// <summary>A LAIA server found on the local network.</summary>
public sealed record ServerInfo(string Name, string Machine, string Address, int Port, string Version)
{
    public string BaseUrl => $"http://{Address}:{Port}";
}

/// <summary>The JSON a server sends back when asked "are you there?".</summary>
public sealed class DiscoveryReply
{
    public string App { get; set; } = "LAIA";
    public string Name { get; set; } = "LAIA Server";
    public string Machine { get; set; } = "";
    public int Port { get; set; } = LaiaPorts.Http;
    public string Version { get; set; } = "";
}

/// <summary>
/// Finds LAIA servers automatically. The sim side shouts a short question to the whole
/// local network; every LAIA server answers with its name and port. No IP addresses needed.
/// </summary>
public static class Discovery
{
    public const string Query = "LAIA_DISCOVER_V1";

    public static async Task<List<ServerInfo>> FindServersAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var results = new Dictionary<string, ServerInfo>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var payload = Encoding.UTF8.GetBytes(Query);

        foreach (var target in GetTargets())
        {
            try { await udp.SendAsync(payload, payload.Length, new IPEndPoint(target, LaiaPorts.Discovery)); }
            catch { /* some adapters refuse broadcast; ignore */ }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var reply = JsonSerializer.Deserialize<DiscoveryReply>(r.Buffer);
                if (reply is null || reply.App != "LAIA") continue;
                var address = r.RemoteEndPoint.Address.ToString();
                var key = string.IsNullOrEmpty(reply.Machine) ? address : reply.Machine;
                // Prefer a real LAN address over loopback when the same machine answers twice.
                if (results.TryGetValue(key, out var existing) && !IPAddress.IsLoopback(IPAddress.Parse(existing.Address)))
                    continue;
                results[key] = new ServerInfo(reply.Name, reply.Machine, address, reply.Port, reply.Version);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { /* ICMP noise on Windows; keep listening */ }
            catch (JsonException) { }
        }
        return results.Values.ToList();
    }

    private static IEnumerable<IPAddress> GetTargets()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    set.Add(new IPAddress(b));
                }
            }
        }
        catch { }
        return set;
    }
}
