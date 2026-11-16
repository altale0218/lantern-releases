using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Lantern.Core;

if(args.Length==2&&args[0]=="--native-peer")return await NativePeer.RunAsync(args[1]);

var tests = new List<(string, Func<Task>)>();
void Test(string name, Func<Task> action) => tests.Add((name, action));
void Assert(bool ok, string message = "Assertion failed") { if (!ok) throw new Exception(message); }
async Task Reject(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected rejection"); }
async Task<(TcpClient A, TcpClient B)> Sockets()
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var a = new TcpClient(); var incoming = listener.AcceptTcpClientAsync();
    await a.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
    var b = await incoming; listener.Stop(); return (a, b);
}
var alice = new DeviceIdentity(Guid.NewGuid().ToString("N"), "Alice", "Windows");
var bob = new DeviceIdentity(Guid.NewGuid().ToString("N"), "Bob", "Android");
ITrustStore aliceTrust = new TrustStore(new MemorySecrets()), bobTrust = new TrustStore(new MemorySecrets());
Test("J-PAKE mutual authentication and trust storage", async () =>
{
    var (a, b) = await Sockets(); using var ca = a; using var cb = b;
    var window = new PairingWindow(); var code = window.Open().Code;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var client = Handshake.ConnectAsync(a.GetStream(), alice, aliceTrust, code, null, timeout.Token);
    var server = Handshake.AcceptAsync(b.GetStream(), bob, bobTrust, window, timeout.Token);
    await Task.WhenAll(client, server);
    await using var x = client.Result.Channel; await using var y = server.Result.Channel;
    Assert((await aliceTrust.FindAsync(bob.Id))?.Secret == (await bobTrust.FindAsync(alice.Id))?.Secret);
    await x.SendAsync("private filename.jpg"u8.ToArray(), timeout.Token);
    Assert((await y.ReceiveAsync(timeout.Token)).AsSpan().SequenceEqual("private filename.jpg"u8));
    await Reject(() => Task.Run(() => window.Reserve()));
});
Test("Wrong pairing codes fail mutual key confirmation", async () =>
{
    var (a, b) = await Sockets(); using var ca = a; using var cb = b;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var x = Handshake.PairKeyAsync(a.GetStream(), "12345678", true, timeout.Token);
    var y = Handshake.PairKeyAsync(b.GetStream(), "87654321", false, timeout.Token);
    await Reject(async () => await Task.WhenAll(x, y)); Assert(x.IsFaulted && y.IsFaulted);
});
Test("Closing matching code cancels a reserved handshake before identity approval",async()=>
{
    var (a,b)=await Sockets();using var client=a;using var server=b;using var window=new PairingWindow();window.Open();
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));int confirmations=0;
    var accept=Handshake.AcceptAsync(b.GetStream(),bob,new TrustStore(new MemorySecrets()),window,timeout.Token,(_,_)=>{confirmations++;return Task.FromResult(true);});
    await Wire.JsonAsync(a.GetStream(),new Hello(1,"pair-confirm","","",""),timeout.Token);
    await Wire.JsonAsync<Pake1>(a.GetStream(),timeout.Token); // Receiver reserved the current code.
    window.Close();await Reject(async()=>await accept);Assert(confirmations==0,"Cancelled exchange must never reach approval or trust storage");
});
Test("Trusted reconnect plus exact binary/zero-byte/duplicate-name transfer", async () =>
{
    var (a, b) = await Sockets(); using var ca = a; using var cb = b;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var x = Handshake.ConnectAsync(a.GetStream(), alice, aliceTrust, null, bob.Id, timeout.Token);
    var y = Handshake.AcceptAsync(b.GetStream(), bob, bobTrust, new PairingWindow(), timeout.Token);
    await Task.WhenAll(x, y); await using var cx = x.Result.Channel; await using var cy = y.Result.Channel;
    byte[] bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 319);
    string root = Path.Combine(Path.GetTempPath(), "lantern-test-" + Guid.NewGuid().ToString("N"));
    SourceFile[] files = [new("test.bin", bytes.Length, "application/octet-stream", () => Task.FromResult<Stream>(new MemoryStream(bytes))), new("test.bin", 0, "application/octet-stream", () => Task.FromResult<Stream>(new MemoryStream()))];
    try
    {
        var receiver = Transfers.ReceiveAsync(cy, alice, root, _ => Task.FromResult(true), new(), _ => { }, timeout.Token);
        await Transfers.SendAsync(cx, alice, files, Guid.NewGuid().ToString("N"), new(), new(), _ => { }, timeout.Token);
        string directory = await receiver;
        var saved = Directory.GetFiles(directory);
        Assert(saved.Length == 2);
        Assert((await File.ReadAllBytesAsync(saved.Single(p => new FileInfo(p).Length > 0))).AsSpan().SequenceEqual(bytes));
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
});
Test("AES-GCM tampering fails closed", async () =>
{
    byte[] key = RandomNumberGenerator.GetBytes(32); var memory = new MemoryStream();
    await using var writer = new SecureChannel(memory, key, true);
    await writer.SendAsync("secret"u8.ToArray(), default);
    byte[] frame = memory.ToArray(); frame[^1] ^= 1;
    await using var reader = new SecureChannel(new MemoryStream(frame), key, false);
    await Reject(async () => await reader.ReceiveAsync(default));
    await Reject(async () => await reader.ReceiveAsync(default));
});
Test("Replay and reflected direction rejected", async () =>
{
    byte[] key = RandomNumberGenerator.GetBytes(32); var memory = new MemoryStream();
    await using var writer = new SecureChannel(memory, key, true);
    await writer.SendAsync("secret"u8.ToArray(), default);
    byte[] frame = memory.ToArray();
    await using var reader = new SecureChannel(new MemoryStream(frame.Concat(frame).ToArray()), key, false);
    await reader.ReceiveAsync(default); await Reject(async () => await reader.ReceiveAsync(default));
    await using var reflected = new SecureChannel(new MemoryStream(frame), key, true);
    await Reject(async () => await reflected.ReceiveAsync(default));
});
Test("Bounded frame allocation", async () =>
{
    await Reject(async () => await Wire.ReadAsync(new MemoryStream([0x7f, 0xff, 0xff, 0xff]), default));
});
Test("Traversal, reserved names, alternate data streams, quotas rejected", async () =>
{
    foreach (string name in new[] { "../secret", "..\\secret", "C:\\Windows", "a:b", "CON.txt", "NUL", "a.", "a ", "\u0000.jpg" })
        await Reject(() => Task.Run(() => Transfers.ValidateName(name)));
    Transfers.ValidateName("旅行照片 你好.jpg");
    await Reject(() => Task.Run(() => Transfers.Validate(new(Guid.NewGuid().ToString("N"), alice, [new(Guid.NewGuid().ToString("N"), "huge.bin", long.MaxValue, "bin")]))));
});
Test("Pairing lockout, exclusivity and invalid IP", async () =>
{
    var w = new PairingWindow(); w.Open(); w.Reserve();
    await Reject(() => Task.Run(() => w.Reserve())); w.Complete(false);
    for (int i = 1; i < 5; i++) { w.Reserve(); w.Complete(false); }
    await Reject(() => Task.Run(() => w.Reserve()));
    await Reject(() => Task.Run(() => LocalNetwork.Parse("8.8.8.8")));
    await Reject(() => Task.Run(() => LocalNetwork.Parse("example.com")));
    Assert(LocalNetwork.IsPrivate(IPAddress.Parse("192.168.1.99")));
});
Test("Expired pairing window rejects without waiting", async () =>
{
    var clock = new TestClock(); var window = new PairingWindow(clock); window.Open();
    clock.Now = clock.Now.AddSeconds(91);
    await Reject(() => Task.Run(() => window.Reserve()));
});
Test("Authenticated wrong checksum removes partial task directory", async () =>
{
    var (a,b) = await Sockets(); using var ca=a; using var cb=b;
    byte[] key=RandomNumberGenerator.GetBytes(32);
    await using var sender=new SecureChannel(a.GetStream(),key,true); await using var receiver=new SecureChannel(b.GetStream(),key,false);
    string root=Path.Combine(Path.GetTempPath(),"lantern-integrity-"+Guid.NewGuid().ToString("N"));
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    string fileId=Guid.NewGuid().ToString("N"); var offer=new TransferOffer(Guid.NewGuid().ToString("N"),alice,[new(fileId,"bad.bin",3,"application/octet-stream")]);
    try
    {
        var operation=Transfers.ReceiveAsync(receiver,alice,root,_=>Task.FromResult(true),new(),_=>{},timeout.Token);
        await sender.SendJsonAsync(offer,timeout.Token); Assert((await sender.ReceiveJsonAsync<Decision>(timeout.Token)).Accept);
        await sender.SendAsync(new byte[]{1,2,3},timeout.Token);
        await sender.SendJsonAsync(new FileDigest(fileId,new string('0',64)),timeout.Token);
        await Reject(async()=>await operation);
        Assert(Directory.GetDirectories(root).Length==0,"Partial task was not cleaned up");
    }
    finally{if(Directory.Exists(root))Directory.Delete(root,true);}
});
Test("Receiver decline happens before file data", async () =>
{
    var (a,b)=await Sockets();using var ca=a;using var cb=b;
    byte[] key=RandomNumberGenerator.GetBytes(32);await using var sender=new SecureChannel(a.GetStream(),key,true);await using var receiver=new SecureChannel(b.GetStream(),key,false);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));string root=Path.Combine(Path.GetTempPath(),"lantern-decline-"+Guid.NewGuid().ToString("N"));
    var operation=Transfers.ReceiveAsync(receiver,alice,root,_=>Task.FromResult(false),new(),_=>{},timeout.Token);
    await sender.SendJsonAsync(new TransferOffer(Guid.NewGuid().ToString("N"),alice,[new(Guid.NewGuid().ToString("N"),"private.bin",123,"bin")]),timeout.Token);
    Assert(!(await sender.ReceiveJsonAsync<Decision>(timeout.Token)).Accept);await Reject(async()=>await operation);Assert(!Directory.Exists(root));
});
Test("Trusted device impersonation with wrong secret fails", async () =>
{
    var fake=new TrustStore(new MemorySecrets());await fake.SaveAsync(new(bob,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),DateTimeOffset.UtcNow));
    var(a,b)=await Sockets();using var ca=a;using var cb=b;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var client=Handshake.ConnectAsync(a.GetStream(),alice,fake,null,bob.Id,timeout.Token);var server=Handshake.AcceptAsync(b.GetStream(),bob,bobTrust,new(),timeout.Token);
    await Reject(async()=>await Task.WhenAll(client,server));Assert(client.IsFaulted&&server.IsFaulted);
});
Test("Revoked trust rejects reconnect", async () =>
{
    await aliceTrust.ForgetAsync(bob.Id);
    var (a, b) = await Sockets(); using var ca = a; using var cb = b;
    await Reject(async () => await Handshake.ConnectAsync(a.GetStream(), alice, aliceTrust, null, bob.Id, default));
});
Test("Final receipt waits for destination commit; failure never reports success",async()=>
{
    foreach(bool fail in new[]{false,true})
    {
        var(a,b)=await Sockets();using var ca=a;using var cb=b;
        byte[] key=RandomNumberGenerator.GetBytes(32);await using var sender=new SecureChannel(a.GetStream(),key,true);await using var receiver=new SecureChannel(b.GetStream(),key,false);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string root=Path.Combine(Path.GetTempPath(),"lantern-commit-"+Guid.NewGuid().ToString("N"));bool completed=false;
        try{
            var receive=Transfers.ReceiveAsync(receiver,alice,root,_=>Task.FromResult(true),new(),p=>{if(p.State=="completed")completed=true;},timeout.Token,finalize:async(folder,ct)=>
            {
                Assert(Directory.GetFiles(folder).Length==1);Assert((await File.ReadAllBytesAsync(Directory.GetFiles(folder)[0],ct)).SequenceEqual(new byte[]{1,2,3}));
                entered.SetResult();await release.Task.WaitAsync(ct);if(fail)throw new IOException("Destination full");
            });
            var send=Transfers.SendAsync(sender,alice,[new("test.bin",3,"application/octet-stream",()=>Task.FromResult<Stream>(new MemoryStream(new byte[]{1,2,3})))],Guid.NewGuid().ToString("N"),new(),new(),_=>{},timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);Assert(!send.IsCompleted&&!completed,"Premature success before public save");release.SetResult();
            if(fail){await Reject(async()=>await receive);await receiver.DisposeAsync();await Reject(async()=>await send);Assert(!completed);Assert(Directory.GetDirectories(root).Length==0);}
            else{await Task.WhenAll(send,receive);Assert(completed);}
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
});
Test("Five peers: four transfers plus authenticated presence, offline expiry and reconnect",async()=>
{
    var hubIdentity=new DeviceIdentity(Guid.NewGuid().ToString("N"),"Multi hub","Windows");var hubTrust=new TrustStore(new MemorySecrets());
    var identities=Enumerable.Range(0,5).Select(i=>new DeviceIdentity(Guid.NewGuid().ToString("N"),"Peer "+i,"Android")).ToArray();
    var peerTrusts=identities.Select(_=>new TrustStore(new MemorySecrets())).ToArray();
    for(int i=0;i<5;i++){var key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));await hubTrust.SaveAsync(new(identities[i],key,DateTimeOffset.UtcNow));await peerTrusts[i].SaveAsync(new(hubIdentity,key,DateTimeOffset.UtcNow));}
    var hub=new LanService(hubIdentity,hubTrust);var peers=identities.Select((id,i)=>new LanService(id,peerTrusts[i])).ToArray();
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var full=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int waiting=0;var received=new System.Collections.Concurrent.ConcurrentBag<string>();
    string root=Path.Combine(Path.GetTempPath(),"lantern-multi-"+Guid.NewGuid().ToString("N"));Task[] sends=[];
    async Task Until(Func<bool> condition,int seconds=8){using var limit=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);limit.CancelAfter(TimeSpan.FromSeconds(seconds));while(!condition())await Task.Delay(100,limit.Token);}
    try
    {
        hub.Incoming=async(connection,ct)=>
        {
            string folder=await Transfers.ReceiveAsync(connection.Channel,connection.Peer,root,_=>Task.FromResult(true),new(),_=>{},ct,
                finalize:async(_,token)=>{if(Interlocked.Increment(ref waiting)==4)full.TrySetResult();await release.Task.WaitAsync(token);},initialMessage:connection.FirstMessage);
            received.Add(folder);
        };
        hub.Start(0);foreach(var peer in peers)peer.Start(0);
        // Seed endpoints via real authentication so this test also works with multicast blocked.
        await Task.WhenAll(peers.Select(async(peer)=>{var c=await hub.ConnectAsync("127.0.0.1",peer.Port,null,identities[Array.IndexOf(peers,peer)].Id,timeout.Token);await using(c.Channel)await c.Channel.SendJsonAsync(new{PairOnly=true},timeout.Token);}));
        byte[] payload=RandomNumberGenerator.GetBytes(1024*1024+53);
        sends=peers.Take(4).Select(async(peer)=>{var c=await peer.ConnectAsync("127.0.0.1",hub.Port,null,hubIdentity.Id,timeout.Token);await using(c.Channel)await Transfers.SendAsync(c.Channel,identities[Array.IndexOf(peers,peer)],[new("multi.bin",payload.Length,"application/octet-stream",()=>Task.FromResult<Stream>(new MemoryStream(payload)))],Guid.NewGuid().ToString("N"),new(),new(),_=>{},timeout.Token);}).ToArray();
        await full.Task.WaitAsync(timeout.Token);
        var fifth=await peers[4].ConnectAsync("127.0.0.1",hub.Port,null,hubIdentity.Id,timeout.Token);await using(fifth.Channel)await fifth.Channel.SendJsonAsync(new{PairOnly=true},timeout.Token);
        // Hold all data slots across multiple heartbeat cycles; control sessions must still work.
        await Task.Delay(4500,timeout.Token);Assert(identities.All(id=>hub.Presence(id.Id)=="online"),"Busy data slots prevented presence checks");
        Assert(sends.All(t=>!t.IsCompleted),"Transfers did not remain independently active");release.SetResult();await Task.WhenAll(sends);await Until(()=>received.Count==4);
        foreach(var folder in received)Assert((await File.ReadAllBytesAsync(Directory.GetFiles(folder).Single(),timeout.Token)).SequenceEqual(payload));
        var unavailable=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);hub.PeerUnavailable+=id=>{if(id==identities[4].Id)unavailable.TrySetResult();};
        int oldPort=peers[4].Port;await peers[4].DisposeAsync();await Until(()=>hub.Presence(identities[4].Id)=="offline");await unavailable.Task.WaitAsync(timeout.Token);
        hub.Nearby[identities[4].Id]=new(identities[4],"127.0.0.1",oldPort,DateTimeOffset.UtcNow);
        Assert(hub.Presence(identities[4].Id)=="offline","Unauthenticated beacon overrode failed probe");
        Assert(identities.Take(4).All(id=>hub.Presence(id.Id)=="online"),"One offline peer affected other peers");
        peers[4]=new LanService(identities[4],peerTrusts[4]);peers[4].Start(oldPort);hub.RefreshDiscovery();await Until(()=>hub.Presence(identities[4].Id)=="online");
        Assert(hub.Nearby.Keys.Count(id=>id==identities[4].Id)==1,"Reconnect duplicated peer");
    }
    finally
    {
        release.TrySetResult();await hub.DisposeAsync();foreach(var peer in peers)await peer.DisposeAsync();
        try{await Task.WhenAll(sends);}catch{}if(Directory.Exists(root))Directory.Delete(root,true);
    }
});
Test("Stale discovery is offline without waiting for a UI refresh",async()=>
{
    await using var service=new LanService(alice,new TrustStore(new MemorySecrets()));
    service.Start(0);service.Nearby[bob.Id]=new(bob,"127.0.0.1",53319,DateTimeOffset.UtcNow-TimeSpan.FromSeconds(7));
    Assert(service.Presence(bob.Id)=="offline");
});
Test("Three independent discovery listeners receive multicast/broadcast peers",async()=>
{
    var ids=Enumerable.Range(0,3).Select(i=>new DeviceIdentity(Guid.NewGuid().ToString("N"),"Discovery "+i,"Windows")).ToArray();
    var services=ids.Select(id=>new LanService(id,new TrustStore(new MemorySecrets()))).ToArray();
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
    try
    {
        foreach(var service in services)service.Start(0);
        foreach(var service in services)service.RefreshDiscovery();
        while(!services.Select((service,i)=>ids.Where((_,j)=>i!=j).All(id=>service.Nearby.ContainsKey(id.Id))).All(x=>x))await Task.Delay(100,deadline.Token);
        Assert(services.All(s=>s.DiscoveryAvailable));
    }
    finally{foreach(var service in services)await service.DisposeAsync();}
});
Test("Discovery recovers after native interface provider fails during startup",async()=>
{
    int attempts=0;
    await using var service=new LanService(alice,new TrustStore(new MemorySecrets()),()=>
    {
        if(Interlocked.Increment(ref attempts)==1)throw new InvalidOperationException("Temporary native network failure");
        return new[]{new LanInterface(System.Net.IPAddress.Parse("127.0.0.1"),8)};
    });
    service.Start(0);
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while(Volatile.Read(ref attempts)<2)await Task.Delay(100,deadline.Token);
    Assert(service.Port>0,"Discovery error stopped TCP service");
});
int failed = 0;
foreach (var (name, action) in tests)
{
    try { await action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed"); return failed;
sealed class MemorySecrets : ISecretStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> values = new();
    public Task<string?> GetAsync(string key) => Task.FromResult(values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value) { values[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key) { values.TryRemove(key, out _); return Task.CompletedTask; }
}
sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now {get;set;}=DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow()=>Now;
}
