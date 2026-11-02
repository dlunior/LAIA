// LAIA SimConnect client.
// A small, dependency-free implementation of the SimConnect wire protocol, so LAIA does not need
// Microsoft's SimConnect DLLs (which differ between MSFS 2020 and 2024 and are not freely
// redistributable). Protocol details are ported from node-simconnect
// (https://github.com/EvenAR/node-simconnect, itself a port of jsimconnect).
//
// This file is licensed under the GNU Lesser General Public License v3.0, like node-simconnect.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace Laia.Common.SimConnect;

public enum SimDataType : uint { Int32 = 1, Int64 = 2, Float32 = 3, Float64 = 4, String8 = 5, String32 = 6, String64 = 7, String128 = 8, String256 = 9, String260 = 10 }
public enum SimPeriod : uint { Never = 0, Once = 1, VisualFrame = 2, SimFrame = 3, Second = 4 }

/// <summary>One SIMOBJECT_DATA message: the values of a data definition, packed in definition order.</summary>
public sealed class SimObjectData
{
    public uint RequestId { get; init; }
    public uint ObjectId { get; init; }
    public uint DefineId { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    private int _pos;

    public double ReadFloat64() { var v = BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan(_pos)); _pos += 8; return v; }
    public int ReadInt32() { var v = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(_pos)); _pos += 4; return v; }
    public string ReadString(int length)
    {
        var span = Data.AsSpan(_pos, Math.Min(length, Data.Length - _pos));
        _pos += length;
        var end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
    }
}

public sealed class SimConnectClient : IAsyncDisposable
{
    private const uint Unused = 0xFFFFFFFF;
    private const int ProtocolVersion = 4; // FSX SP2 protocol: accepted by MSFS 2020 and 2024 for everything LAIA uses
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private uint _sendId;

    public string Endpoint { get; }
    public string SimulatorName { get; private set; } = "";
    public event Action<SimObjectData>? Data;
    public event Action<uint, uint>? SimException; // exception code, sendId
    public event Action<uint, uint>? SimEvent;     // client event id, data
    public event Action? Quit;
    public event Action<uint, uint>? AssignedObjectId; // request id, object id
    public event Action? Frame;

    private SimConnectClient(Stream stream, string endpoint) { _stream = stream; Endpoint = endpoint; }

    /// <summary>
    /// Connects to the simulator on this PC. Tries MSFS's named pipes, then the local IPv4 port the
    /// simulator writes to the registry, then common default ports. Returns null if MSFS isn't reachable.
    /// </summary>
    public static async Task<SimConnectClient?> ConnectAsync(string appName, Action<string>? log = null, CancellationToken ct = default)
    {
        foreach (var pipe in new[] { @"Microsoft Flight Simulator\SimConnect", @"Microsoft Flight Simulator 2024\SimConnect" })
        {
            try
            {
                var p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await p.ConnectAsync(1500, ct);
                var c = new SimConnectClient(p, @"pipe \\.\pipe\" + pipe);
                if (await c.OpenAsync(appName, ct)) return c;
                await c.DisposeAsync();
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { log?.Invoke($"pipe {pipe}: {ex.Message}"); }
        }
        foreach (var port in CandidatePorts())
        {
            try
            {
                var tcp = new TcpClient { NoDelay = true };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(1500);
                await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
                var c = new SimConnectClient(tcp.GetStream(), $"tcp 127.0.0.1:{port}");
                if (await c.OpenAsync(appName, ct)) return c;
                await c.DisposeAsync();
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException) { log?.Invoke($"port {port}: {ex.Message}"); }
        }
        return null;
    }

    private static IEnumerable<int> CandidatePorts()
    {
        var seen = new HashSet<int>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"Software\Microsoft\Microsoft Games\Flight Simulator", @"Software\Microsoft\Microsoft Games\Flight Simulator 2024" })
            {
                int? port = null;
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(key);
                    if (k?.GetValue("SimConnect_Port_IPv4") is { } v && int.TryParse(v.ToString(), out var p)) port = p;
                }
                catch { }
                if (port is int pp && seen.Add(pp)) yield return pp;
            }
        }
        foreach (var p in new[] { 500, 5111, 2048 }) if (seen.Add(p)) yield return p;
    }

    // ------------------------------------------------------------------ sending

    private sealed class Packet
    {
        private readonly MemoryStream _ms = new();
        private readonly BinaryWriter _w;
        public Packet(uint type)
        {
            _w = new BinaryWriter(_ms, Encoding.Latin1);
            _w.Write(0);                         // size, filled in later
            _w.Write(ProtocolVersion);
            _w.Write(0xF0000000 | type);
            _w.Write(0);                         // send id, filled in later
        }
        public Packet U32(uint v) { _w.Write(v); return this; }
        public Packet F32(float v) { _w.Write(v); return this; }
        public Packet F64(double v) { _w.Write(v); return this; }
        public Packet Byte(byte v) { _w.Write(v); return this; }
        public Packet Bytes(byte[] v) { _w.Write(v); return this; }
        public Packet Str(string? s, int fixedLength)
        {
            var bytes = Encoding.Latin1.GetBytes(s ?? "");
            var n = Math.Min(bytes.Length, fixedLength);
            _w.Write(bytes, 0, n);
            for (var i = n; i < fixedLength; i++) _w.Write((byte)0);
            return this;
        }
        public byte[] Build(uint sendId)
        {
            _w.Flush();
            var b = _ms.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0), b.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), sendId);
            return b;
        }
    }

    private async Task<uint> SendAsync(Packet p)
    {
        await _writeLock.WaitAsync();
        try
        {
            var id = ++_sendId;
            var bytes = p.Build(id);
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
            return id;
        }
        finally { _writeLock.Release(); }
    }

    private async Task<bool> OpenAsync(string appName, CancellationToken ct)
    {
        // Open (0x01): app name, reserved, then the client version block for protocol 4 (FSX SP2).
        await SendAsync(new Packet(0x01).Str(appName, 256).U32(0).Byte(0).Str("XSF", 3).U32(10).U32(0).U32(61259).U32(0));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            while (true)
            {
                var (type, body) = await ReadMessageAsync(cts.Token);
                if (type == 2) // RECV_OPEN
                {
                    SimulatorName = ReadStr(body, 0, 256);
                    return true;
                }
                if (type == 1) return false; // exception during open
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException) { return false; }
    }

    public Task<uint> AddToDataDefinitionAsync(uint defineId, string datum, string? units, SimDataType type, float epsilon = 0) =>
        SendAsync(new Packet(0x0C).U32(defineId).Str(datum, 256).Str(units, 256).U32((uint)type).F32(epsilon).U32(Unused));

    public Task<uint> RequestDataOnSimObjectAsync(uint requestId, uint defineId, uint objectId, SimPeriod period, uint flags = 0) =>
        SendAsync(new Packet(0x0E).U32(requestId).U32(defineId).U32(objectId).U32((uint)period).U32(flags).U32(0).U32(0).U32(0));

    /// <summary>Places an AI aircraft that MSFS's own air traffic control ignores (LAIA flies it).</summary>
    public Task<uint> AICreateNonATCAircraftAsync(string title, string tail, double lat, double lon, double altFt,
        double pitch, double bank, double heading, bool onGround, int airspeedKt, uint requestId) =>
        SendAsync(new Packet(0x29).Str(title, 256).Str(tail, 12)
            .F64(lat).F64(lon).F64(altFt).F64(pitch).F64(bank).F64(heading).U32(onGround ? 1u : 0u).U32((uint)airspeedKt)
            .U32(requestId));

    public Task<uint> AIReleaseControlAsync(uint objectId, uint requestId) => SendAsync(new Packet(0x2B).U32(objectId).U32(requestId));
    public Task<uint> AIRemoveObjectAsync(uint objectId, uint requestId) => SendAsync(new Packet(0x2C).U32(objectId).U32(requestId));

    public Task<uint> MapClientEventToSimEventAsync(uint eventId, string name) => SendAsync(new Packet(0x04).U32(eventId).Str(name, 256));

    /// <summary>Sends a sim event to an object (highest priority group).</summary>
    public Task<uint> TransmitClientEventAsync(uint objectId, uint eventId, uint data) =>
        SendAsync(new Packet(0x05).U32(objectId).U32(eventId).U32(data).U32(1).U32(0x10));

    /// <summary>Writes the values of a data definition to an object (untagged, one entry).</summary>
    public Task<uint> SetDataOnSimObjectAsync(uint defineId, uint objectId, byte[] data)
    {
        var p = new Packet(0x10).U32(defineId).U32(objectId).U32(0).U32(1).U32((uint)data.Length);
        p.Bytes(data);
        return SendAsync(p);
    }

    public Task<uint> SubscribeToSystemEventAsync(uint eventId, string name) =>
        SendAsync(new Packet(0x17).U32(eventId).Str(name, 256));

    // ------------------------------------------------------------------ receiving

    private async Task<(uint type, byte[] body)> ReadMessageAsync(CancellationToken ct)
    {
        var head = new byte[4];
        await _stream.ReadExactlyAsync(head, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (size < 12 || size > 16 * 1024 * 1024) throw new IOException($"bad SimConnect message size {size}");
        var rest = new byte[size - 4];
        await _stream.ReadExactlyAsync(rest, ct);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(4));
        return (type, rest.AsSpan(8).ToArray());
    }

    private static string ReadStr(byte[] b, int offset, int len)
    {
        var span = b.AsSpan(offset, Math.Min(len, b.Length - offset));
        var end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
    }

    /// <summary>Reads messages until the simulator closes or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (type, body) = await ReadMessageAsync(ct);
            switch (type)
            {
                case 1: // EXCEPTION
                    SimException?.Invoke(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
                    break;
                case 3: // QUIT
                    Quit?.Invoke();
                    return;
                case 4: // EVENT
                    SimEvent?.Invoke(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
                    break;
                case 7: // EVENT_FRAME
                    Frame?.Invoke();
                    break;
                case 12: // ASSIGNED_OBJECT_ID
                    AssignedObjectId?.Invoke(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
                    break;
                case 8: // SIMOBJECT_DATA
                case 9: // SIMOBJECT_DATA_BYTYPE
                    Data?.Invoke(new SimObjectData
                    {
                        RequestId = BinaryPrimitives.ReadUInt32LittleEndian(body),
                        ObjectId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)),
                        DefineId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)),
                        Data = body.AsSpan(28).ToArray(),
                    });
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.DisposeAsync(); } catch { }
        _writeLock.Dispose();
    }
}
