using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Lantern.AppLogic;
using Lantern.Core;

namespace Lantern.Windows;

internal sealed class WindowsServiceDiscovery(AppController controller) : IDisposable
{
    private const uint Pending = 9506;
    private const ushort AddressRecord = 1, Ptr = 12, ServiceRecord = 33;
    private static readonly string Suffix = "." + LocalServiceIdentity.QualifiedType;
    private static readonly ConcurrentDictionary<nint, Action<uint, nint>> Callbacks = new();
    private static long lastToken;
    private static readonly Completion BrowseCompletion = CompleteBrowse;
    private static readonly Completion InstanceCompletion = CompleteInstance;

    private readonly ConcurrentQueue<Action> updates = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, WindowsBonjourPublication> publications = new();
    private Operation? browse;
    private long retryAt, observeAt, generation;
    private int queued;

    private sealed class Operation(Action<uint, nint> callback)
    {
        public readonly nint Token = AddCallback(callback);
        public nint Cancel;
        public void Close() => Callbacks.TryRemove(Token, out _);
    }

    private sealed class Entry(DeviceIdentity identity)
    {
        public DeviceIdentity Identity { get; } = identity;
        public IPAddress? Address;
        public int Port;
        public long ProbeAt, ExpiresAt, ResolveDeadline;
        public Operation? Resolve;
    }

    public void Refresh()
    {
        while (updates.TryDequeue(out var update)) { Interlocked.Decrement(ref queued); update(); }
        if (!controller.IsDiscoverable) { Stop(); return; }
        long now = Environment.TickCount64;
        if (now >= retryAt)
        {
            retryAt = now + 12_000;
            if (browse is null) StartBrowse();
            UpdatePublications();
        }
        if (now < observeAt) return;
        observeAt = now + 2_000;
        int resolving = entries.Values.Count(entry => entry.Resolve is not null);
        foreach (var (name, entry) in entries.ToArray())
        {
            if (now >= entry.ExpiresAt) { Remove(name); continue; }
            if (entry.Resolve is not null && now >= entry.ResolveDeadline) { CancelResolve(entry); resolving--; }
            if (entry.Address is not null) controller.ObserveLocalService(entry.Identity, entry.Address.ToString(), entry.Port);
            if (resolving < 8 && entry.Resolve is null && now >= entry.ProbeAt)
            {
                StartResolve(name, entry, now);
                if (entry.Resolve is not null) resolving++;
            }
        }
    }

    private void StartBrowse()
    {
        long stamp = generation;
        browse = new((status, records) =>
        {
            if (status != 0 || records == 0) return;
            var pointers = new List<(string Name, DeviceIdentity Identity, uint Ttl)>();
            var targets = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);
            var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            for (int length = 0; records != 0 && length < 100; length++)
            {
                var row = Marshal.PtrToStructure<DnsRecord>(records);
                string? owner = Marshal.PtrToStringUni(row.Name)?.TrimEnd('.');
                if (owner is not null)
                {
                    if (row.Type == Ptr && row.DataLength >= IntPtr.Size &&
                        owner.Equals(LocalServiceIdentity.QualifiedType, StringComparison.OrdinalIgnoreCase) &&
                        Marshal.PtrToStringUni(row.Data) is { } target && TryName(target, out string name) is { } identity)
                        pointers.Add((name, identity, row.Ttl));
                    if (row.Type == ServiceRecord && row.Ttl > 0 && row.DataLength >= IntPtr.Size + 6 &&
                        TryName(owner, out _) is not null && Marshal.PtrToStringUni(row.Data) is { } host)
                    {
                        nint data = records + Marshal.OffsetOf<DnsRecord>(nameof(DnsRecord.Data)).ToInt32();
                        targets[owner] = (host.TrimEnd('.'), (ushort)Marshal.ReadInt16(data, IntPtr.Size + 4));
                    }
                    if (row.Type == AddressRecord && row.Ttl > 0 && row.DataLength == 4)
                        addresses[owner] = new IPAddress((uint)row.Data);
                }
                records = row.Next;
            }
            foreach (var (name, identity, ttl) in pointers)
            {
                IPAddress? address = null;
                int port = 0;
                if (targets.TryGetValue(name, out var target) && addresses.TryGetValue(target.Host, out var ip) &&
                    LocalServiceIdentity.IsEndpoint(ip, target.Port)) { address = ip; port = target.Port; }
                Post(stamp, () => ObserveRecord(name, identity, ttl, address, port));
            }
        });
        var request = new QueryRequest(LocalServiceIdentity.QualifiedType, BrowseCompletion, browse.Token);
        if (DnsServiceBrowse(ref request, ref browse.Cancel) == Pending) return;
        browse.Close();
        browse = null;
    }

    private void ObserveRecord(string name, DeviceIdentity identity, uint ttl, IPAddress? address, int port)
    {
        if (identity.Id == controller.LocalIdentity.Id) return;
        if (ttl == 0) { Remove(name); return; }
        if (!entries.TryGetValue(name, out var entry))
        {
            if (entries.Count >= 100) return;
            entries.Add(name, entry = new(identity));
        }
        entry.ExpiresAt = Environment.TickCount64 + Math.Clamp(ttl, 1, 120) * 1000L;
        if (address is null) return;
        entry.Address = address;
        entry.Port = port;
        entry.ProbeAt = Environment.TickCount64 + 25_000;
        controller.ObserveLocalService(identity, address.ToString(), port);
    }

    private void StartResolve(string name, Entry entry, long now)
    {
        entry.ProbeAt = now + (entry.Address is null ? 6_000 : 25_000);
        entry.ResolveDeadline = now + 8_000;
        long stamp = generation;
        Operation? pending = null;
        pending = new((status, instance) =>
        {
            IPAddress? address = null;
            int port = 0;
            if (status == 0 && instance != 0)
            {
                var item = Marshal.PtrToStructure<DnsInstance>(instance);
                if (item.IPv4 != 0)
                {
                    var bytes = new byte[4];
                    Marshal.Copy(item.IPv4, bytes, 0, bytes.Length);
                    var ip = new IPAddress(bytes);
                    if (LocalServiceIdentity.IsEndpoint(ip, item.Port)) { address = ip; port = item.Port; }
                }
            }
            Post(stamp, () =>
            {
                if (!entries.TryGetValue(name, out var current) || current != entry || entry.Resolve is not { } active || active != pending) return;
                active.Close();
                entry.Resolve = null;
                if (address is null) return;
                entry.Address = address;
                entry.Port = port;
                controller.ObserveLocalService(entry.Identity, address.ToString(), port);
            });
        });
        entry.Resolve = pending;
        var request = new QueryRequest(name, InstanceCompletion, entry.Resolve.Token);
        if (DnsServiceResolve(ref request, ref entry.Resolve.Cancel) == Pending) return;
        entry.Resolve.Close();
        entry.Resolve = null;
    }

    private void UpdatePublications()
    {
        var current = new Dictionary<uint, IPAddress>();
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return; }
        foreach (var adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast || !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var address = properties.UnicastAddresses.Select(item => item.Address)
                    .FirstOrDefault(ip => LocalServiceIdentity.IsEndpoint(ip, controller.ListeningPort));
                if (address is not null && properties.GetIPv4Properties() is { Index: > 0 } ipv4)
                    current[(uint)ipv4.Index] = address;
            }
            catch (NetworkInformationException) { }
        }

        foreach (var (index, previous) in publications.ToArray())
        {
            if (current.TryGetValue(index, out var address) && address.Equals(previous.Address)) continue;
            previous.Dispose();
            publications.Remove(index);
        }
        foreach (var (index, address) in current)
        {
            if (publications.TryGetValue(index, out var present) && present.IsRunning) continue;
            present?.Dispose();
            publications.Remove(index);
            if (WindowsBonjourPublication.TryStart(index, address, controller.LocalIdentity, controller.ListeningPort) is { } publication)
                publications.Add(index, publication);
        }
    }

    private void Post(long stamp, Action apply)
    {
        if (Interlocked.Increment(ref queued) > 512) { Interlocked.Decrement(ref queued); return; }
        updates.Enqueue(() => { if (generation == stamp) apply(); });
    }

    private static DeviceIdentity? TryName(string target, out string name)
    {
        name = target.TrimEnd('.');
        return name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? LocalServiceIdentity.Parse(name[..^Suffix.Length]) : null;
    }

    private void Remove(string name)
    {
        if (entries.Remove(name, out var entry)) CancelResolve(entry);
    }

    private static void CancelResolve(Entry entry)
    {
        if (entry.Resolve is not { } resolve) return;
        entry.Resolve = null;
        resolve.Close();
        DnsServiceResolveCancel(ref resolve.Cancel);
    }

    private void Stop()
    {
        if (browse is null && publications.Count == 0 && entries.Count == 0) return;
        generation++;
        if (browse is { } watch) { watch.Close(); DnsServiceBrowseCancel(ref watch.Cancel); browse = null; }
        foreach (var publication in publications.Values) publication.Dispose();
        publications.Clear();
        foreach (string name in entries.Keys.ToArray()) Remove(name);
        retryAt = observeAt = 0;
    }

    public void Dispose() => Stop();

    private static nint AddCallback(Action<uint, nint> callback)
    {
        var token = (nint)Interlocked.Increment(ref lastToken);
        Callbacks.TryAdd(token, callback);
        return token;
    }

    private static void CompleteBrowse(uint status, nint token, nint records)
    {
        try { if (Callbacks.TryGetValue(token, out var callback)) callback(status, records); }
        finally { if (records != 0) DnsRecordListFree(records, 1); }
    }

    private static void CompleteInstance(uint status, nint token, nint instance)
    {
        try { if (Callbacks.TryGetValue(token, out var callback)) callback(status, instance); }
        finally { if (instance != 0) DnsServiceFreeInstance(instance); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Completion(uint status, nint context, nint data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QueryRequest(string name, Completion callback, nint context)
    {
        public uint Version = 1, InterfaceIndex = 0;
        public string Name = name;
        public Completion Callback = callback;
        public nint Context = context;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsRecord
    {
        public nint Next, Name;
        public ushort Type, DataLength;
        public uint Flags, Ttl, Reserved;
        public nint Data;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsInstance
    {
        public nint Name, Host, IPv4, IPv6;
        public ushort Port;
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)] private static extern void DnsServiceFreeInstance(nint instance);
    [DllImport("dnsapi.dll", ExactSpelling = true)] private static extern uint DnsServiceBrowse(ref QueryRequest request, ref nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)] private static extern uint DnsServiceBrowseCancel(ref nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)] private static extern uint DnsServiceResolve(ref QueryRequest request, ref nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)] private static extern uint DnsServiceResolveCancel(ref nint cancel);
    [DllImport("dnsapi.dll", ExactSpelling = true)] private static extern void DnsRecordListFree(nint records, int freeType);
}
