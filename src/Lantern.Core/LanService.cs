using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace Lantern.Core;

public static class LocalNetwork
{
    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254);
    }
    public static IPAddress Parse(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || !IsPrivate(ip)) throw new InvalidDataException("只允许局域网 IPv4 地址");
        return ip;
    }
}
public sealed class LanService(DeviceIdentity me,ITrustStore trust,Func<IReadOnlyList<LanInterface>>? interfaces=null) : IAsyncDisposable
{
    public const int DiscoveryPort=53318,DefaultPort=53319;
    public static readonly TimeSpan DiscoveryLifetime=TimeSpan.FromSeconds(6);
    public PairingWindow Pairing {get;}=new();
    public ConcurrentDictionary<string,NearbyDevice> Nearby {get;}=new();
    public event Action? Changed;
    public event Action<string>? Error;
    public event Action<string>? PeerUnavailable;
    public Func<AuthenticatedConnection,CancellationToken,Task>? Incoming {get;set;}
    public Func<PairingChallenge,CancellationToken,Task<bool>>? ConfirmPairing {get;set;}
    public event Action<DeviceIdentity>? Paired;
    private readonly CancellationTokenSource stop=new();
    // Reserve capacity for control/heartbeat sessions even while four file tasks run.
    private readonly SemaphoreSlim admissions=new(12),transfers=new(4),messages=new(4),speedTests=new(1),probeSlots=new(8),probePulse=new(0,1);
    private readonly ConcurrentDictionary<int,Task> handlers=new();
    private readonly ConcurrentDictionary<string,NearbyDevice> candidates=new();
    private sealed record Health(bool Online,DateTimeOffset CheckedAt,int Failures=0);
    private readonly ConcurrentDictionary<string,Health> health=new();
    private int handlerId,disposed;
    private TcpListener? listener;
    private LanDiscovery? discovery;
    private Task? acceptLoop,discoveryLoop,presenceLoop;
    public bool Discoverable {get;set;}=true;
    public bool UseUdpDiscovery {get;init;}=true;
    public bool DiscoveryAvailable=>discovery?.Available==true;
    public int Port {get;private set;}
    public void Start(int port=DefaultPort)
    {
        listener=new TcpListener(IPAddress.Any,port);listener.Start(32);Port=((IPEndPoint)listener.LocalEndpoint).Port;
        if(UseUdpDiscovery)
        {
            discovery=new LanDiscovery(me,()=>Port,()=>Discoverable,Observe,interfaces);
            discoveryLoop=Task.Run(()=>discovery.RunAsync(stop.Token));
        }
        acceptLoop=Task.Run(()=>AcceptLoopAsync(stop.Token));presenceLoop=Task.Run(()=>PresenceAsync(stop.Token));
    }
    public void Observe(NearbyDevice device)
    {
        if(device.Identity.Id==me.Id||!Guid.TryParseExact(device.Identity.Id,"N",out _)||device.Port is <1024 or >65535||
            !IPAddress.TryParse(device.Address,out var ip)||!LocalNetwork.IsPrivate(ip))return;
        if(candidates.Count>=100&&!candidates.ContainsKey(device.Identity.Id))return;
        // A broadcast cannot redirect a recently authenticated live endpoint.
        if(candidates.TryGetValue(device.Identity.Id,out var previous)&&health.TryGetValue(device.Identity.Id,out var h)&&h.Online&&DateTimeOffset.UtcNow-h.CheckedAt<DiscoveryLifetime&&
            (previous.Address!=device.Address||previous.Port!=device.Port))return;
        candidates[device.Identity.Id]=device;Nearby[device.Identity.Id]=device;Changed?.Invoke();
    }
    private void Authenticated(DeviceIdentity peer,string address,int port)
    {
        var entry=new NearbyDevice(peer,address,port,DateTimeOffset.UtcNow);candidates[peer.Id]=entry;Nearby[peer.Id]=entry;health[peer.Id]=new(true,entry.SeenAt);Changed?.Invoke();
    }
    public string Presence(string id)
    {
        if(health.TryGetValue(id,out var h))
        {
            if(!h.Online)return "offline";
            if(DateTimeOffset.UtcNow-h.CheckedAt<TimeSpan.FromSeconds(8))return "online";
        }
        return Nearby.TryGetValue(id,out var d)&&DateTimeOffset.UtcNow-d.SeenAt<DiscoveryLifetime?"discovered":"offline";
    }
    public void RefreshDiscovery()
    {
        discovery?.Refresh();if(probePulse.CurrentCount==0)try{probePulse.Release();}catch(SemaphoreFullException){}
    }
    private static void Configure(TcpClient client)
    {
        client.NoDelay=true;
        try{client.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.KeepAlive,true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp,SocketOptionName.TcpKeepAliveTime,3);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp,SocketOptionName.TcpKeepAliveInterval,1);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp,SocketOptionName.TcpKeepAliveRetryCount,3);
        }catch(Exception ex) when(ex is SocketException or PlatformNotSupportedException){}
    }
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try{while(!ct.IsCancellationRequested)
        {
            var client=await listener!.AcceptTcpClientAsync(ct);
            if(!LocalNetwork.IsPrivate(((IPEndPoint)client.Client.RemoteEndPoint!).Address)||!admissions.Wait(0)){client.Dispose();continue;}
            int id=Interlocked.Increment(ref handlerId);var task=HandleAsync(client,ct);handlers[id]=task;
            _=task.ContinueWith(_=>handlers.TryRemove(id,out var ignored),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }}catch(Exception ex) when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    private async Task HandleAsync(TcpClient client,CancellationToken ct)
    {
        bool entered=false,messageEntered=false,speedEntered=false,authenticated=false;
        try
        {
            Configure(client);using var handshakeTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(25));
            async Task<bool> ConfirmAsync(PairingChallenge challenge,CancellationToken token)
            {handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(95));return await ConfirmPairing!(challenge,token);}
            var connection=await Handshake.AcceptAsync(client.GetStream(),me,trust,Pairing,handshakeTimeout.Token,ConfirmPairing is null?null:ConfirmAsync);authenticated=true;
            if(connection.NewlyPaired)Paired?.Invoke(connection.Peer);
            var ip=((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            var current=Nearby.GetValueOrDefault(connection.Peer.Id);
            int peerPort=current?.Address==ip?current.Port:candidates.GetValueOrDefault(connection.Peer.Id)?.Port??DefaultPort;
            Authenticated(connection.Peer,ip,peerPort);
            await using(connection.Channel)
            {
                using var firstTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);firstTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var first=await connection.Channel.ReceiveJsonAsync<JsonElement>(firstTimeout.Token);
                // Compatible with existing pairing-only clients; heartbeats use the same encrypted control message.
                if(first.TryGetProperty("PairOnly",out var pairOnly)&&pairOnly.ValueKind==JsonValueKind.True)return;
                if(first.TryGetProperty("SpeedTest",out _))
                {if(!speedTests.Wait(0)){await connection.Channel.SendJsonAsync(new SpeedTestReply(1,SpeedTests.RequestId(first),false,"对方正在测速，请稍后重试"),firstTimeout.Token);return;}speedEntered=true;}
                else if(first.TryGetProperty("Chat",out _))
                {if(!messages.Wait(0))return;messageEntered=true;}
                else {if(!transfers.Wait(0)){await connection.Channel.SendJsonAsync(new Decision(false),ct);return;}entered=true;}
                if(Incoming is not null)await Incoming(connection with{FirstMessage=first},ct);
            }
        }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {if(!ct.IsCancellationRequested&&authenticated)Error?.Invoke("连接已关闭：传输中断或设备已离线");}
        finally{client.Dispose();if(entered)transfers.Release();if(messageEntered)messages.Release();if(speedEntered)speedTests.Release();admissions.Release();}
    }
    public Task<AuthenticatedConnection> ConnectAsync(string address,int port,string? code,string? peerId,CancellationToken ct)=>
        ConnectAsync(address,port,code,peerId,ct,observe:true);

    private async Task<AuthenticatedConnection> ConnectAsync(string address,int port,string? code,string? peerId,CancellationToken ct,bool observe)
    {
        var ip=LocalNetwork.Parse(address);if(port is <1024 or >65535)throw new InvalidDataException("Invalid port");var client=new TcpClient();Configure(client);
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(25));
            async Task<bool> ConfirmAsync(PairingChallenge challenge,CancellationToken token)
            {timeout.CancelAfter(TimeSpan.FromSeconds(95));return await ConfirmPairing!(challenge,token);}
            await client.ConnectAsync(ip,port,timeout.Token);var connection=await Handshake.ConnectAsync(client.GetStream(),me,trust,code,peerId,timeout.Token,ConfirmPairing is null?null:ConfirmAsync);
            if(connection.NewlyPaired)Paired?.Invoke(connection.Peer);
            if(observe)Authenticated(connection.Peer,address,port);return connection;
        }catch{client.Dispose();throw;}
    }
    private async Task ProbeAsync(NearbyDevice device,CancellationToken ct)
    {
        var id=device.Identity.Id;
        await probeSlots.WaitAsync(ct);try
        {
            var before=health.GetValueOrDefault(id);
            if(before is {Online:true,Failures:0}&&DateTimeOffset.UtcNow-before.CheckedAt<TimeSpan.FromSeconds(2))return;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var connection=await ConnectAsync(device.Address,device.Port,null,id,deadline.Token,observe:false);
                await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},deadline.Token);
                Authenticated(connection.Peer,device.Address,device.Port);
            }
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {
                if(ct.IsCancellationRequested||!candidates.TryGetValue(id,out var route)||route.Address!=device.Address||route.Port!=device.Port)return;
                var failed=new Health(before is {Online:true,Failures:0},DateTimeOffset.UtcNow,(before?.Failures??0)+1);
                // A slow probe may finish after a transfer authenticated the same device.
                if(!(before is null?health.TryAdd(id,failed):health.TryUpdate(id,failed,before)))return;
                if(!failed.Online&&Nearby.TryGetValue(id,out var nearby)&&nearby.SeenAt<=failed.CheckedAt)
                    Nearby.TryRemove(new KeyValuePair<string,NearbyDevice>(id,nearby));
                if(failed.Failures==2)PeerUnavailable?.Invoke(id);
                Changed?.Invoke();
            }
        }
        finally{probeSlots.Release();}
    }
    private async Task PresenceAsync(CancellationToken ct)
    {
        try{while(!ct.IsCancellationRequested)
        {
            try
            {
            var now=DateTimeOffset.UtcNow;
            foreach(var pair in Nearby)if(now-pair.Value.SeenAt>=DiscoveryLifetime)Nearby.TryRemove(pair);
            var trusted=await trust.ListAsync();
            var targets=trusted.Select(t=>candidates.GetValueOrDefault(t.Identity.Id)).OfType<NearbyDevice>().ToArray();
            await Task.WhenAll(targets.Select(d=>ProbeAsync(d,ct)));
            foreach(var pair in candidates)if(now-pair.Value.SeenAt>TimeSpan.FromMinutes(5)&&Presence(pair.Key)=="offline"){candidates.TryRemove(pair.Key,out _);health.TryRemove(pair.Key,out _);}
            Changed?.Invoke();
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex) when(ex is not OutOfMemoryException){Error?.Invoke("设备状态检查暂不可用，将自动重试");System.Diagnostics.Trace.TraceWarning("Presence retry: {0}",ex.Message);}
            await probePulse.WaitAsync(TimeSpan.FromSeconds(2),ct);
        }}catch(OperationCanceledException){}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stop.Cancel();listener?.Stop();
        foreach(var task in new[]{acceptLoop,discoveryLoop,presenceLoop}.OfType<Task>())try{await task;}catch(OperationCanceledException){}
        await Task.WhenAll(handlers.Values);Pairing.Dispose();stop.Dispose();admissions.Dispose();transfers.Dispose();messages.Dispose();speedTests.Dispose();probeSlots.Dispose();probePulse.Dispose();
    }
}
