using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;

if(args.Length==3&&args[0]=="--native-speed")return await Checks.NativePeerAsync(args[1],args[2]);
var cases=new List<(string,Func<Task>)>();
void Test(string title,Func<Task> test)=>cases.Add((title,test));
var request=new SpeedTestRequest(1,Guid.NewGuid().ToString("N"),250,1048576,65536);
Test("reject invalid protocol, identity, duration, byte and frame quotas",async()=>
{
    foreach(var bad in new[]{request with{SpeedTest=2},request with{Id="not-an-id"},request with{DurationMs=0},request with{DurationMs=5001},request with{MaxBytes=0},request with{MaxBytes=SpeedTests.MaxDirectionBytes+1},request with{ChunkBytes=1},request with{ChunkBytes=1048577}})
        await Checks.RejectAsync(()=>Task.Run(()=>SpeedTests.Validate(bad)));
});
Test("two independently measured directions, encrypted traffic and verified receipt",async()=>
{
    await using var pair=await ChannelPair.CreateAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var updates=new List<SpeedTestProgress>();var server=Task.Run(async()=>{var inbound=await pair.B.ReceiveJsonAsync<SpeedTestRequest>(timeout.Token);return await SpeedTests.RespondAsync(pair.B,inbound,_=>{},timeout.Token);});
    var result=await SpeedTests.RunAsync(pair.A,request,updates.Add,timeout.Token);await server;
    Checks.Assert(result.Upload.Verified&&result.Download.Verified&&result.Upload.Bytes==request.MaxBytes&&result.Download.Bytes==request.MaxBytes);
    Checks.Assert(result.Upload.BytesPerSecond>0&&result.Download.BytesPerSecond>0&&double.IsFinite(result.Upload.BytesPerSecond));
    Checks.Assert(updates.Any(p=>p.Direction=="upload"&&p.Result is not null)&&updates.Any(p=>p.Direction=="download"&&p.Result is not null));
    Checks.Assert(!System.Text.Encoding.UTF8.GetString(pair.Captured.ToArray()).Contains("SpeedTest"),"control messages must be encrypted on the wire");
});
Test("modified whole-burst digest is rejected",async()=>
{
    await using var pair=await ChannelPair.CreateAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var response=SpeedTests.RespondAsync(pair.B,request,_=>{},timeout.Token);await pair.A.ReceiveJsonAsync<SpeedTestReply>(timeout.Token);
    await pair.A.SendAsync(new byte[request.ChunkBytes+1].Select((b,i)=>i==0?(byte)1:b).ToArray(),timeout.Token);
    await Checks.EndAsync(pair.A,request,"upload",request.ChunkBytes,new string('0',64),timeout.Token);
    await Checks.RejectAsync(async()=>await response,typeof(CryptographicException));
});
Test("per-direction byte cap is enforced even for authenticated frames",async()=>
{
    await using var pair=await ChannelPair.CreateAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var response=SpeedTests.RespondAsync(pair.B,request,_=>{},timeout.Token);await pair.A.ReceiveJsonAsync<SpeedTestReply>(timeout.Token);
    var block=new byte[request.ChunkBytes+1];block[0]=1;
    for(int i=0;i<request.MaxBytes/request.ChunkBytes+1;i++)await pair.A.SendAsync(block,timeout.Token);
    await Checks.RejectAsync(async()=>await response,typeof(InvalidDataException));
});
Test("short data frames and unknown frame kinds cannot consume an unbounded stream",async()=>
{
    foreach(var block in new[]{new byte[]{1,2},new byte[]{7,0}})
    {
        await using var pair=await ChannelPair.CreateAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response=SpeedTests.RespondAsync(pair.B,request,_=>{},timeout.Token);await pair.A.ReceiveJsonAsync<SpeedTestReply>(timeout.Token);await pair.A.SendAsync(block,timeout.Token);
        await Checks.RejectAsync(async()=>await response,typeof(InvalidDataException));
    }
});
Test("a false or wrong-run receipt never completes upload",async()=>
{
    await using var pair=await ChannelPair.CreateAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var fake=Task.Run(async()=>
    {
        var req=await pair.B.ReceiveJsonAsync<SpeedTestRequest>(timeout.Token);await pair.B.SendJsonAsync(new SpeedTestReply(1,req.Id,true),timeout.Token);
        while(true){var block=await pair.B.ReceiveAsync(timeout.Token);if(block[0]!=2)continue;var end=JsonSerializer.Deserialize<JsonElement>(block.AsSpan(1));await pair.B.SendJsonAsync(new{Id=Guid.NewGuid().ToString("N"),Direction="upload",Bytes=end.GetProperty("Bytes").GetInt64(),Sha256=end.GetProperty("Sha256").GetString(),Verified=false},timeout.Token);break;}
    });
    await Checks.RejectAsync(()=>SpeedTests.RunAsync(pair.A,request,_=>{},timeout.Token),typeof(CryptographicException));await fake;
});
Test("old clients receive no fallback file transfer",async()=>
{
    await using var pair=await ChannelPair.CreateAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var legacy=Task.Run(async()=>{await pair.B.ReceiveJsonAsync<JsonElement>(timeout.Token);await pair.B.SendJsonAsync(new Decision(false),timeout.Token);});
    await Checks.RejectAsync(()=>SpeedTests.RunAsync(pair.A,request,_=>{},timeout.Token),typeof(SpeedTestUnsupportedException));await legacy;
});
Test("cancel while waiting for peer reply is prompt",async()=>
{
    await using var pair=await ChannelPair.CreateAsync();using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));var watch=Stopwatch.StartNew();
    await Checks.RejectAsync(()=>SpeedTests.RunAsync(pair.A,request,_=>{},cancel.Token),typeof(OperationCanceledException));Checks.Assert(watch.Elapsed<TimeSpan.FromSeconds(2));
});
Test("real controllers authenticate, preserve files/history and report both directions",async()=>
{
    await using var pair=await ControllerPair.CreateAsync();var before=Checks.Tree(pair.Root);
    var begin=await Checks.Command(pair.A,new{type="speed-test-start",peerId=pair.BId});Checks.Assert(begin.GetProperty("ok").GetBoolean());
    var result=await Checks.Completed(pair.A,begin.GetProperty("id").GetString()!);var response=(await Checks.State(pair.B)).GetProperty("speedTest");
    Checks.Assert(result.GetProperty("upload").GetProperty("verified").GetBoolean()&&result.GetProperty("download").GetProperty("verified").GetBoolean());
    Checks.Assert(!response.GetProperty("initiatedHere").GetBoolean()&&response.GetProperty("phase").GetString()=="completed");
    Checks.Assert((await Checks.State(pair.A)).GetProperty("tasks").GetArrayLength()==0&&(await Checks.State(pair.B)).GetProperty("tasks").GetArrayLength()==0);
    Checks.Assert(before.SequenceEqual(Checks.Tree(pair.Root)),"benchmark must not create files or rewrite encrypted history");
});
Test("discovery alone and wrong peer identity cannot authorize speed testing",async()=>
{
    await using var pair=await ControllerPair.CreateAsync();string unknown=Guid.NewGuid().ToString("N");
    Checks.Service(pair.A).Nearby[unknown]=new(new(unknown,"虚构未配对设备","Android"),"127.0.0.1",Checks.Service(pair.B).Port,DateTimeOffset.UtcNow);
    Checks.Assert(!(await Checks.Command(pair.A,new{type="speed-test-start",peerId=unknown})).GetProperty("ok").GetBoolean());
    await new TrustStore(pair.HostA.Secrets).SaveAsync(new(new(unknown,"虚构错误设备","Android"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),DateTimeOffset.UtcNow));
    var begin=await Checks.Command(pair.A,new{type="speed-test-start",peerId=unknown});Checks.Assert(begin.GetProperty("ok").GetBoolean());
    var result=await Checks.Terminal(pair.A,begin.GetProperty("id").GetString()!);Checks.Assert(result.GetProperty("phase").GetString()=="failed");
    Checks.Assert((await Checks.State(pair.B)).GetProperty("speedTest").GetProperty("phase").GetString()=="idle");
});
Test("duplicate tests and stale cancellation ids cannot affect the active run",async()=>
{
    await using var pair=await ControllerPair.CreateAsync();var begin=await Checks.Command(pair.A,new{type="speed-test-start",peerId=pair.BId});string id=begin.GetProperty("id").GetString()!;
    Checks.Assert(!(await Checks.Command(pair.A,new{type="speed-test-start",peerId=pair.BId})).GetProperty("ok").GetBoolean());
    Checks.Assert(!(await Checks.Command(pair.A,new{type="speed-test-cancel",id=Guid.NewGuid().ToString("N")})).GetProperty("ok").GetBoolean());
    Checks.Assert((await Checks.Command(pair.A,new{type="speed-test-cancel",id})).GetProperty("ok").GetBoolean());
    Checks.Assert((await Checks.Terminal(pair.A,id)).GetProperty("phase").GetString()=="cancelled");
});
Test("forgetting the paired key stops an active speed test",async()=>
{
    await using var pair=await ControllerPair.CreateAsync();var begin=await Checks.Command(pair.A,new{type="speed-test-start",peerId=pair.BId});
    await Checks.Command(pair.A,new{type="forget",id=pair.BId});var result=await Checks.Terminal(pair.A,begin.GetProperty("id").GetString()!);
    Checks.Assert(result.GetProperty("phase").GetString() is "cancelled" or "failed");Checks.Assert(await new TrustStore(pair.HostA.Secrets).FindAsync(pair.BId) is null);
});
Test("receiver admits one test and sends a bounded busy response to another",async()=>
{
    await using var pair=await ControllerPair.CreateAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var held=await Checks.Service(pair.A).ConnectAsync("127.0.0.1",Checks.Service(pair.B).Port,null,pair.BId,timeout.Token);
    await using(held.Channel){await held.Channel.SendJsonAsync(request,timeout.Token);Checks.Assert((await held.Channel.ReceiveJsonAsync<SpeedTestReply>(timeout.Token)).Accept);
        var begin=await Checks.Command(pair.A,new{type="speed-test-start",peerId=pair.BId});var result=await Checks.Terminal(pair.A,begin.GetProperty("id").GetString()!);
        Checks.Assert(result.GetProperty("phase").GetString()=="failed"&&result.GetProperty("message").GetString()!.Contains("正在测速"));}
});
Test("starting a real file transfer stops speed testing and the file still verifies",async()=>
{
    await using var pair=await ControllerPair.CreateAsync();await Checks.Command(pair.B,new{type="setting",key="thumbnails",value=false});
    var begin=await Checks.Command(pair.A,new{type="speed-test-start",peerId=pair.BId});byte[] payload=RandomNumberGenerator.GetBytes(512*1024);
    var pick=JsonSerializer.SerializeToElement(pair.A.SelectNativeFiles(new[]{new PickedFile(new("demo.txt",payload.Length,"text/plain",()=>Task.FromResult<Stream>(new MemoryStream(payload))),null)}),AppController.Json);
    var send=await Checks.Command(pair.A,new{type="send",peerIds=new[]{pair.BId},selectionId=pick.GetProperty("selectionId").GetString()});Checks.Assert(send.GetProperty("ok").GetBoolean());
    Checks.Assert((await Checks.Terminal(pair.A,begin.GetProperty("id").GetString()!)).GetProperty("phase").GetString()=="cancelled");
    string id=send.GetProperty("ids")[0].GetString()!;bool done=false;for(int i=0;i<200;i++){var t=(await Checks.State(pair.A)).GetProperty("tasks").EnumerateArray().Single(t=>t.GetProperty("id").GetString()==id);if(t.GetProperty("state").GetString()=="completed"){done=true;break;}await Task.Delay(25);}Checks.Assert(done,"file transfer must take priority and complete");
    var received=Directory.GetFiles(Path.Combine(pair.HostB.DataDirectory,"Inbox"),"*-demo.txt",SearchOption.AllDirectories).Single();Checks.Assert(SHA256.HashData(File.ReadAllBytes(received)).SequenceEqual(SHA256.HashData(payload)));
});
Test("peer shutdown cannot leave a successful or stuck run",async()=>
{
    await using var pair=await ControllerPair.CreateAsync();var begin=await Checks.Command(pair.A,new{type="speed-test-start",peerId=pair.BId});await pair.B.DisposeAsync();
    Checks.Assert((await Checks.Terminal(pair.A,begin.GetProperty("id").GetString()!)).GetProperty("phase").GetString() is "failed" or "cancelled");
});
int failed=0;foreach(var test in cases){try{await test.Item2();Console.WriteLine("PASS "+test.Item1);}catch(Exception e){failed++;Console.WriteLine("FAIL "+test.Item1+": "+e.GetType().Name+" "+e.Message);}}
Console.WriteLine($"{cases.Count-failed}/{cases.Count} speed tests passed");return failed==0?0:1;

static class Checks
{
    public static void Assert(bool value,string message="Assertion failed"){if(!value)throw new Exception(message);}
    public static async Task RejectAsync(Func<Task> action,Type? type=null){try{await action();}catch(Exception e){if(type is null||type.IsAssignableFrom(e.GetType()))return;throw;}throw new Exception("Expected rejection");}
    public static async Task EndAsync(SecureChannel channel,SpeedTestRequest req,string direction,long bytes,string sha,CancellationToken ct){var json=JsonSerializer.SerializeToUtf8Bytes(new{req.Id,Direction=direction,Bytes=bytes,Sha256=sha});byte[] end=new byte[json.Length+1];end[0]=2;json.CopyTo(end,1);await channel.SendAsync(end,ct);}
    public static LanService Service(AppController app)=>(LanService)typeof(AppController).GetField("service",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
    public static async Task<JsonElement> State(AppController app)=>JsonSerializer.SerializeToElement(await app.GetStateAsync(),AppController.Json);
    public static async Task<JsonElement> Command(AppController app,object value)=>JsonSerializer.SerializeToElement(await app.DispatchAsync(JsonSerializer.SerializeToElement(value)),AppController.Json);
    public static async Task<JsonElement> Terminal(AppController app,string id){for(int i=0;i<400;i++){var s=(await State(app)).GetProperty("speedTest");if(s.GetProperty("id").GetString()==id&&s.GetProperty("phase").GetString() is "completed" or "failed" or "cancelled")return s;await Task.Delay(25);}throw new TimeoutException("speed job did not stop");}
    public static async Task<JsonElement> Completed(AppController app,string id){var s=await Terminal(app,id);Assert(s.GetProperty("phase").GetString()=="completed",s.GetProperty("message").GetString()??"speed failed");return s;}
    public static string[] Tree(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order().Select(f=>Path.GetRelativePath(root,f)+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).ToArray();
    public static async Task<int> NativePeerAsync(string root,string code)
    {
        try{
            var host=new TestHost(root,"Android");await using var app=new AppController(host);await app.InitializeAsync(0);var service=Service(app);service.ConfirmPairing=(_,_)=>Task.FromResult(true);
            var me=(await State(app)).GetProperty("self").Deserialize<DeviceIdentity>(AppController.Json)!;
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)){EnableBroadcast=true};var beacon=JsonSerializer.SerializeToUtf8Bytes(new{App="Lantern",Version=1,Identity=me,Port=service.Port});
            // A loopback broadcast reaches both local discovery sockets; unicast with reuse can reach only this peer.
            for(int i=0;i<3;i++){await udp.SendAsync(beacon,new IPEndPoint(IPAddress.Parse("127.255.255.255"),LanService.DiscoveryPort));await Task.Delay(100);}
            var connected=await service.ConnectAsync("127.0.0.1",LanService.DefaultPort,code,null,CancellationToken.None);await using(connected.Channel)await connected.Channel.SendJsonAsync(new{PairOnly=true},CancellationToken.None);
            Console.WriteLine("SPEED_PEER_READY "+me.Id);Console.Out.Flush();
            for(int i=0;i<1200;i++){var s=(await State(app)).GetProperty("speedTest");if(s.GetProperty("phase").GetString()=="completed"){Assert(s.GetProperty("upload").GetProperty("verified").GetBoolean()&&s.GetProperty("download").GetProperty("verified").GetBoolean());Assert((await State(app)).GetProperty("tasks").GetArrayLength()==0);Console.WriteLine("NATIVE_SPEED_VERIFIED");Console.Out.Flush();await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));return 0;}await Task.Delay(50);}throw new TimeoutException();
        }catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);return 1;}
    }
}
sealed class MemorySecrets : ISecretStore
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> values=new();
    public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));
    public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}
    public Task RemoveAsync(string key){values.TryRemove(key,out _);return Task.CompletedTask;}
}
sealed class TestHost(string root,string platform) : IPlatformHost
{
    public string Platform=>platform;public string DataDirectory=>root;public string DeviceName=>"虚构测速设备 "+platform;public ISecretStore Secrets{get;}=new MemorySecrets();
    public Func<IReadOnlyList<LanInterface>> NetworkInterfaces=>()=>Array.Empty<LanInterface>();
    public Task<IReadOnlyList<PickedFile>> PickAsync()=>throw new Exception("Speed tests must not open a file picker");
    public Task OpenFolderAsync(string folder)=>throw new Exception("Speed tests must not open files");
    public Task<string?> ThumbnailAsync(string file,string mime)=>throw new Exception("Speed tests must not generate previews");
}
sealed class ControllerPair : IAsyncDisposable
{
    public string Root=Path.Combine(Path.GetTempPath(),"Lantern-Speed-"+Guid.NewGuid().ToString("N"));public TestHost HostA=null!,HostB=null!;public AppController A=null!,B=null!;public string BId="";
    public static async Task<ControllerPair> CreateAsync()
    {
        var p=new ControllerPair();p.HostA=new(Path.Combine(p.Root,"a"),"Windows");p.HostB=new(Path.Combine(p.Root,"b"),"TestPeer");
        string a=Guid.NewGuid().ToString("N"),b=Guid.NewGuid().ToString("N"),secret=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));p.BId=b;
        await p.HostA.Secrets.SetAsync("lantern.identity",a);await p.HostB.Secrets.SetAsync("lantern.identity",b);
        await new TrustStore(p.HostA.Secrets).SaveAsync(new(new(b,"虚构对端","TestPeer"),secret,DateTimeOffset.UtcNow));await new TrustStore(p.HostB.Secrets).SaveAsync(new(new(a,"虚构本机","Windows"),secret,DateTimeOffset.UtcNow));
        p.A=new(p.HostA);p.B=new(p.HostB);await p.A.InitializeAsync(0);await p.B.InitializeAsync(0);
        var connection=await Checks.Service(p.A).ConnectAsync("127.0.0.1",Checks.Service(p.B).Port,null,b,CancellationToken.None);await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},CancellationToken.None);
        return p;
    }
    public async ValueTask DisposeAsync(){if(A is not null)await A.DisposeAsync();if(B is not null)await B.DisposeAsync();if(Directory.Exists(Root)){string full=Path.GetFullPath(Root),temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!full.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("Lantern-Speed-",StringComparison.Ordinal)||Directory.EnumerateFileSystemEntries(full,"*",SearchOption.AllDirectories).Any(f=>(File.GetAttributes(f)&FileAttributes.ReparsePoint)!=0))throw new IOException("Unsafe test cleanup target");Directory.Delete(full,true);}}
}
sealed class TapStream(Stream inner,MemoryStream capture) : Stream
{
    public override bool CanRead=>inner.CanRead;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override void Flush()=>inner.Flush();public override Task FlushAsync(CancellationToken ct)=>inner.FlushAsync(ct);
    public override int Read(byte[] b,int o,int c)=>inner.Read(b,o,c);public override ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default)=>inner.ReadAsync(b,ct);
    public override void Write(byte[] b,int o,int c){capture.Write(b,o,c);inner.Write(b,o,c);}public override ValueTask WriteAsync(ReadOnlyMemory<byte> b,CancellationToken ct=default){capture.Write(b.Span);return inner.WriteAsync(b,ct);}
    public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override ValueTask DisposeAsync()=>inner.DisposeAsync();
}
sealed class ChannelPair : IAsyncDisposable
{
    readonly TcpClient client,server;public readonly MemoryStream Captured=new();public SecureChannel A{get;}public SecureChannel B{get;}
    ChannelPair(TcpClient a,TcpClient b){client=a;server=b;byte[] key=RandomNumberGenerator.GetBytes(32);A=new(new TapStream(a.GetStream(),Captured),key,true);B=new(b.GetStream(),key,false);CryptographicOperations.ZeroMemory(key);}
    public static async Task<ChannelPair> CreateAsync(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();try{var a=new TcpClient(){NoDelay=true};var incoming=listener.AcceptTcpClientAsync();await a.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);var b=await incoming;b.NoDelay=true;return new(a,b);}finally{listener.Stop();}}
    public async ValueTask DisposeAsync(){await A.DisposeAsync();await B.DisposeAsync();client.Dispose();server.Dispose();Captured.Dispose();}
}
