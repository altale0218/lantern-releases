using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lantern.AppLogic;
using Lantern.Core;

namespace Lantern.Windows;

// Win32 service registration may omit the A record for an SRV target. Publish the complete
// IPv4 service on each LAN interface. Windows DNS-SD still handles browsing and resolving peers.
internal sealed class WindowsBonjourPublication : IDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint Destination = new(Group, 5353);
    private static readonly IPEndPoint Sender = new(IPAddress.Any, 0);
    private const int AddressRecord = 1, PointerRecord = 12, TextRecord = 16, ServiceRecord = 33, AnyRecord = 255;
    private readonly Socket socket;
    private readonly uint interfaceIndex;
    private readonly string name;
    private readonly string host;
    private readonly byte[] response, goodbye;
    private readonly Task worker;
    private readonly object sendGate = new();
    private int disposed;

    public IPAddress Address { get; }
    public bool IsRunning => !worker.IsCompleted;

    private WindowsBonjourPublication(Socket socket, uint index, IPAddress address, DeviceIdentity identity, int port)
    {
        this.socket = socket;
        interfaceIndex = index;
        Address = address;
        name = LocalServiceIdentity.InstanceName(identity) + "." + LocalServiceIdentity.QualifiedType;
        host = "lantern-" + identity.Id + ".local";
        response = MakeResponse(port, 120);
        goodbye = MakeResponse(port, 0);
        worker = Task.Run(ListenAsync);
    }

    public static WindowsBonjourPublication? TryStart(uint index, IPAddress address, DeviceIdentity identity, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Destination.Port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            return new(socket, index, address, identity, port);
        }
        catch (SocketException) { socket.Dispose(); return null; }
    }

    private async Task ListenAsync()
    {
        var buffer = new byte[9000];
        try
        {
            Send(response);
            long lastResponse = Environment.TickCount64;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            var tick = timer.WaitForNextTickAsync().AsTask();
            var receive = Receive();
            while (Volatile.Read(ref disposed) == 0)
            {
                if (await Task.WhenAny(receive, tick) == tick)
                {
                    Send(response);
                    lastResponse = Environment.TickCount64;
                    tick = timer.WaitForNextTickAsync().AsTask();
                    continue;
                }
                var packet = await receive;
                long now = Environment.TickCount64;
                if (now - lastResponse >= 250 && packet.PacketInformation.Interface == interfaceIndex &&
                    packet.RemoteEndPoint is IPEndPoint source && source.Port == Destination.Port &&
                    LocalServiceIdentity.IsEndpoint(source.Address, source.Port) && WantsService(buffer.AsSpan(0, packet.ReceivedBytes)))
                {
                    Send(response);
                    lastResponse = now;
                }
                receive = Receive();
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }

        Task<SocketReceiveMessageFromResult> Receive() =>
            socket.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, Sender, CancellationToken.None).AsTask();
    }

    private bool WantsService(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || (packet[2] & 0xf8) != 0) return false;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        if (questions is < 1 or > 24) return false;
        int offset = 12;
        for (int i = 0; i < questions; i++)
        {
            string? question = ReadName(packet, ref offset);
            if (question is null || packet.Length - offset < 4) return false;
            int type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            int recordClass = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]) & 0x7fff;
            offset += 4;
            if (recordClass != 1) continue;
            if (type is PointerRecord or AnyRecord && question.Equals(LocalServiceIdentity.QualifiedType, StringComparison.OrdinalIgnoreCase) ||
                type is ServiceRecord or TextRecord or AnyRecord && question.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                type is AddressRecord or AnyRecord && question.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        Span<char> name = stackalloc char[253];
        int end = 0, current = offset, resume = -1;
        for (int parts = 0; parts < 128 && current < packet.Length; parts++)
        {
            int length = packet[current++];
            if (length == 0) { offset = resume < 0 ? current : resume; return new string(name[..end]); }
            if ((length & 0xc0) == 0xc0)
            {
                if (current >= packet.Length) return null;
                int next = ((length & 0x3f) << 8) | packet[current++];
                if (next < 12 || next >= current - 2) return null;
                if (resume < 0) resume = current;
                current = next;
                continue;
            }
            if (length > 63 || length > packet.Length - current || length + end + (end > 0 ? 1 : 0) > name.Length) return null;
            if (end > 0) name[end++] = '.';
            foreach (byte value in packet.Slice(current, length))
            {
                if (value is < 33 or > 126 || value == '.') return null;
                name[end++] = (char)value;
            }
            current += length;
        }
        return null;
    }

    private byte[] MakeResponse(int port, byte ttl)
    {
        var bytes = new List<byte>([0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0]);
        byte[] HostName(string value) => value.Split('.').SelectMany(part =>
            new[] { (byte)part.Length }.Concat(Encoding.ASCII.GetBytes(part))).Append((byte)0).ToArray();
        void WriteRecord(string label, byte type, byte options, IEnumerable<byte> data)
        {
            byte[] value = data.ToArray();
            bytes.AddRange(HostName(label));
            bytes.AddRange([0, type, options, 1, 0, 0, 0, ttl, (byte)(value.Length >> 8), (byte)value.Length]);
            bytes.AddRange(value);
        }
        WriteRecord(LocalServiceIdentity.QualifiedType, PointerRecord, 0, HostName(name));
        WriteRecord(name, ServiceRecord, 0x80, new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }.Concat(HostName(host)));
        WriteRecord(name, TextRecord, 0x80, [0]);
        WriteRecord(host, AddressRecord, 0x80, Address.GetAddressBytes());
        return bytes.ToArray();
    }

    private void Send(byte[] packet, bool withdrawing = false)
    {
        lock (sendGate)
        {
            if (!withdrawing && Volatile.Read(ref disposed) != 0) return;
            try { socket.SendTo(packet, SocketFlags.None, Destination); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Send(goodbye, withdrawing: true);
        socket.Dispose();
    }
}
