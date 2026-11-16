using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
namespace Lantern.Core;

// Discovery is a hint only. Authentication remains in LanService / Handshake.
public sealed record LanInterface(IPAddress Address,int PrefixLength);

internal sealed class LanDiscovery(DeviceIdentity me,Func<int> port,Func<bool> enabled,Action<NearbyDevice> observe,Func<IReadOnlyList<LanInterface>>? interfaces=null)
{
    private static readonly IPAddress Group=IPAddress.Parse("239.77.77.77");
    private sealed record Beacon(string App,int Version,DeviceIdentity Identity,int Port,string Kind="hello");
    private sealed record Route(IPAddress Address,IPAddress Broadcast,UdpClient Sender);
    private readonly SemaphoreSlim pulse=new(0,1);
    private int refreshInterfaces;
    public bool Available {get;private set;}
    public void Refresh()
    {
        Interlocked.Exchange(ref refreshInterfaces,1);
        if(pulse.CurrentCount==0)try{pulse.Release();}catch(SemaphoreFullException){}
    }
    private (IPAddress Address,IPAddress Broadcast)[] Interfaces()
    {
        var found=new List<(IPAddress,IPAddress)>();
        if(interfaces is not null)
        {
            foreach(var item in interfaces())
            {
                if(item.Address.AddressFamily!=AddressFamily.InterNetwork||IPAddress.IsLoopback(item.Address)||!LocalNetwork.IsPrivate(item.Address)||item.PrefixLength is <1 or >30)continue;
                uint hostMask=uint.MaxValue>>item.PrefixLength;
                var bytes=item.Address.GetAddressBytes();
                for(int i=0;i<4;i++)bytes[i]|=(byte)(hostMask>>(24-i*8));
                found.Add((item.Address,new IPAddress(bytes)));
            }
            return found.Distinct().ToArray();
        }
        foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up))
        {
            try{foreach(var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if(address.Address.AddressFamily!=AddressFamily.InterNetwork||IPAddress.IsLoopback(address.Address)||!LocalNetwork.IsPrivate(address.Address))continue;
                var ip=address.Address.GetAddressBytes();var mask=address.IPv4Mask.GetAddressBytes();
                if(mask.Length!=4)continue;var broadcast=new IPAddress(ip.Select((b,i)=>(byte)(b|~mask[i])).ToArray());found.Add((address.Address,broadcast));
            }}catch(Exception ex) when(ex is NetworkInformationException or PlatformNotSupportedException or SocketException){}
        }
        return found.Distinct().ToArray();
    }
    public async Task RunAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            UdpClient? receiver=null;Route[] routes=[];using var cycle=CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? receive=null;
            try
            {
                receiver=new UdpClient(AddressFamily.InterNetwork);receiver.ExclusiveAddressUse=false;
                receiver.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
                receiver.Client.Bind(new IPEndPoint(IPAddress.Any,LanService.DiscoveryPort));
                receive=ReceiveAsync(receiver,cycle.Token);int iterations=0;
                while(!cycle.IsCancellationRequested)
                {
                    if(receive.IsCompleted)await receive;
                    // Re-read live adapters to recover from Wi-Fi, hotspot and VPN route changes.
                    bool refresh=Interlocked.Exchange(ref refreshInterfaces,0)!=0;
                    if(iterations++%3==0||refresh)
                    {
                        var current=Interfaces();
                        foreach(var old in routes.Where(r=>!current.Any(n=>n.Address.Equals(r.Address)))){try{receiver.Client.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.DropMembership,new MulticastOption(Group,old.Address));}catch(SocketException){}old.Sender.Dispose();}
                        var next=new List<Route>();foreach(var endpoint in current)
                        {
                            var old=routes.FirstOrDefault(r=>r.Address.Equals(endpoint.Address));if(old is not null){next.Add(old with{Broadcast=endpoint.Broadcast});continue;}
                            try{
                                var sender=new UdpClient(new IPEndPoint(endpoint.Address,0)){EnableBroadcast=true,MulticastLoopback=true,Ttl=1};
                                try{receiver.JoinMulticastGroup(Group,endpoint.Address);}catch(SocketException){}
                                next.Add(new(endpoint.Address,endpoint.Broadcast,sender));
                            }catch(SocketException){}
                        }
                        routes=next.ToArray();Available=routes.Length>0;
                    }
                    if(enabled())
                    {
                        var bytes=JsonSerializer.SerializeToUtf8Bytes(new Beacon("Lantern",1,me,port()));
                        foreach(var route in routes)foreach(var destination in new[]{Group,route.Broadcast})
                        {try{await route.Sender.SendAsync(bytes,new IPEndPoint(destination,LanService.DiscoveryPort),cycle.Token);}catch(SocketException){}}
                    }
                    var delay=pulse.WaitAsync(TimeSpan.FromSeconds(2),cycle.Token);
                    await Task.WhenAny(delay,receive);if(receive.IsCompleted){cycle.Cancel();await receive;}else await delay;
                }
            }
            catch(Exception ex) when(ex is not OutOfMemoryException){System.Diagnostics.Trace.TraceWarning("Discovery retry: {0}",ex.Message);}
            finally
            {
                Available=false;cycle.Cancel();receiver?.Dispose();foreach(var route in routes)route.Sender.Dispose();
                if(receive is not null)try{await receive;}catch(Exception ex) when(ex is not OutOfMemoryException){}
            }
            if(!ct.IsCancellationRequested)try{await Task.Delay(2000,ct);}catch(OperationCanceledException){}
        }
    }
    private async Task ReceiveAsync(UdpClient receiver,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            var packet=await receiver.ReceiveAsync(ct);
            if(!enabled()||packet.Buffer.Length>1024||!LocalNetwork.IsPrivate(packet.RemoteEndPoint.Address))continue;
            try{
                var b=JsonSerializer.Deserialize<Beacon>(packet.Buffer);
                if(b is null||b.App!="Lantern"||b.Version!=1||b.Identity is null||b.Identity.Id==me.Id||!Guid.TryParseExact(b.Identity.Id,"N",out _)||
                    b.Identity.Name is null||b.Identity.Name.Length is <1 or >80||b.Identity.Platform is null||b.Identity.Platform.Length>24||b.Port is <1024 or >65535)continue;
                observe(new(b.Identity,packet.RemoteEndPoint.Address.ToString(),b.Port,DateTimeOffset.UtcNow));
            }catch(JsonException){}
        }
    }
}
