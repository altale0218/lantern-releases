using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;

int passed=0;
void Assert(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
JsonElement Element(object obj)=>JsonSerializer.SerializeToElement(obj,AppController.Json);
async Task<JsonElement> Cmd(AppController c,object cmd)=>Element(await c.DispatchAsync(Element(cmd)));
async Task<JsonElement> State(AppController c)=>Element(await c.GetStateAsync());
LanService Service(AppController c)=>(LanService)typeof(AppController).GetField("service",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(c)!;
async Task Until(Func<Task<bool>> test,string message,int milliseconds=8000){var start=Environment.TickCount64;while(Environment.TickCount64-start<milliseconds){if(await test())return;await Task.Delay(20);}throw new Exception("Timeout: "+message);}
async Task<JsonElement[]> Requests(AppController c)=>(await State(c)).GetProperty("pairingRequests").EnumerateArray().ToArray();
async Task<bool> Trusted(AppController c,string id)=>(await State(c)).GetProperty("devices").EnumerateArray().Any(d=>d.GetProperty("id").GetString()==id&&d.GetProperty("trusted").GetBoolean());
async Task<string> Id(AppController c)=>(await State(c)).GetProperty("self").GetProperty("id").GetString()!;
async Task Begin(AppController a,AppController b){var code=(await Cmd(b,new{type="open-pairing"})).GetProperty("code").GetString();var r=await Cmd(a,new{type="pair",address="127.0.0.1",port=Service(b).Port,code});Assert(r.GetProperty("ok").GetBoolean()&&r.GetProperty("started").GetBoolean(),"pairing starts without blocking bridge");await Until(async()=> (await Requests(a)).Length==1&&(await Requests(b)).Length==1,"both approval dialogs");}
async Task Decide(AppController c,bool accept){var request=(await Requests(c)).Single();var r=await Cmd(c,new{type="pair-decision",id=request.GetProperty("id").GetString(),accept});Assert(r.GetProperty("ok").GetBoolean(),accept?"local confirmation accepted":"local rejection accepted");}
async Task Complete(AppController a,AppController b){await Begin(a,b);await Decide(a,true);await Decide(b,true);var aid=await Id(a);var bid=await Id(b);await Until(async()=>await Trusted(a,bid)&&await Trusted(b,aid),"mutual pairing commit");await Until(async()=>!(await State(a)).GetProperty("pairingBusy").GetBoolean(),"pairing task completion");}
if(args.Length>0&&args[0]=="--native-ux")
{
    string profile=Path.GetFullPath(args[1]);var host=new Host(profile,"Android");await using var peer=new AppController(host);await peer.InitializeAsync(0);
    Service(peer).ConfirmPairing=(_,_)=>Task.FromResult(true);
    var connection=await Service(peer).ConnectAsync("127.0.0.1",LanService.DefaultPort,args[2],null,CancellationToken.None);
    string windowsId=connection.Peer.Id;
    await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},CancellationToken.None);
    await Cmd(peer,new{type="setting",key="autoAccept",value=true});
    await Cmd(peer,new{type="chat-send",peerId=windowsId,text="原生双端确认与群发测试 🔐"});
    Console.WriteLine("NATIVE_UX_READY "+await Id(peer));
    long start=Environment.TickCount64;
    while(Environment.TickCount64-start<180000&&!File.Exists(Path.Combine(profile,"stop")))
    {
        var state=await State(peer);await File.WriteAllTextAsync(Path.Combine(profile,"peer-state.json"),state.GetRawText());await Task.Delay(100);
    }
    return;
}
if(args.Length>1&&args[0]=="--approval-timeout")
{
    string profile=Path.GetFullPath(args[1]);Directory.CreateDirectory(profile);
    var firstHost=new Host(Path.Combine(profile,"A"),"Windows");var secondHost=new Host(Path.Combine(profile,"B"),"Android");
    await using var expiryA=new AppController(firstHost);
    await using var expiryB=new AppController(secondHost);
    await expiryA.InitializeAsync(0);await expiryB.InitializeAsync(0);
    await Begin(expiryA,expiryB);var stale=(await Requests(expiryB)).Single().GetProperty("id").GetString();
    await Decide(expiryA,true);string firstId=await Id(expiryA),secondId=await Id(expiryB);var timer=System.Diagnostics.Stopwatch.StartNew();
    while(timer.Elapsed<TimeSpan.FromSeconds(105))
    {
        if(await new TrustStore(firstHost.Secrets).FindAsync(secondId) is not null||await new TrustStore(secondHost.Secrets).FindAsync(firstId) is not null)throw new Exception("Timeout test established one-sided trust");
        if((await Requests(expiryA)).Length==0&&(await Requests(expiryB)).Length==0&&!(await State(expiryA)).GetProperty("pairingBusy").GetBoolean())break;
        await Task.Delay(250);
    }
    Assert(timer.Elapsed.TotalSeconds is >=88 and <=103,"real 90-second approval timeout releases initiator");
    Assert((await Requests(expiryA)).Length==0&&(await Requests(expiryB)).Length==0,"real timeout clears both approval prompts");
    Assert(!await Trusted(expiryA,secondId)&&!await Trusted(expiryB,firstId),"timeout after one approval leaves no trust on either endpoint");
    Assert(!(await Cmd(expiryB,new{type="pair-decision",id=stale,accept=true})).GetProperty("ok").GetBoolean(),"expired approval ID cannot establish trust");
    await Complete(expiryA,expiryB);
    await File.WriteAllTextAsync(Path.Combine(profile,"timeout-result.json"),JsonSerializer.Serialize(new{version=AppRelease.Version,success=true,elapsedSeconds=timer.Elapsed.TotalSeconds,oneSidedTrustPrevented=true,bothPromptsCleared=true,staleDecisionRejected=true,rePairSucceeded=true}));
    Console.WriteLine("PASS real-time approval expiry and fresh mutual pairing recovery");return;
}
var root=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(Path.GetTempPath(),"lantern-pairing-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
Host ah=new(Path.Combine(root,"A"),"Windows"),bh=new(Path.Combine(root,"B"),"Android"),ch=new(Path.Combine(root,"C"),"iPadOS");
await using var a=new AppController(ah);await using var b=new AppController(bh);await using var c=new AppController(ch);
await a.InitializeAsync(0);await b.InitializeAsync(0);await c.InitializeAsync(0);
string aid=await Id(a),bid=await Id(b),cid=await Id(c);
await Begin(a,b);
var ar=(await Requests(a)).Single();var br=(await Requests(b)).Single();
Assert(ar.GetProperty("verificationCode").GetString()==br.GetProperty("verificationCode").GetString(),"both PAKE-authenticated sessions show identical verification code");
Assert(!await Trusted(a,bid)&&!await Trusted(b,aid),"no trust persisted before user confirmation");
Assert(ar.GetProperty("outgoing").GetBoolean()&&!br.GetProperty("outgoing").GetBoolean(),"initiator and receiver both receive approval prompts");
await Decide(a,true);await Task.Delay(120);
Assert(!await Trusted(a,bid)&&!await Trusted(b,aid),"one-sided approval never establishes trust");
Assert((await Requests(a)).Single().GetProperty("accepted").GetBoolean(),"local approval remains visible while waiting for peer");
await Decide(b,false);
await Until(async()=> (await Requests(a)).Length==0&&(await Requests(b)).Length==0&&!(await State(a)).GetProperty("pairingBusy").GetBoolean(),"rejection clears both requests");
Assert(!await Trusted(a,bid)&&!await Trusted(b,aid),"rejected pairing leaves no trust on either endpoint");
await Begin(a,b);await Decide(a,true);await Cmd(b,new{type="close-pairing"});
await Until(async()=> (await Requests(a)).Length==0&&(await Requests(b)).Length==0&&!(await State(a)).GetProperty("pairingBusy").GetBoolean(),"cancel after local approval");
Assert(!await Trusted(a,bid)&&!await Trusted(b,aid),"receiver can cancel while waiting without persisting trust");
Assert(!(await Cmd(a,new{type="pair-decision",id=ar.GetProperty("id").GetString(),accept=true})).GetProperty("ok").GetBoolean(),"stale approval commands rejected");
await Complete(a,b);
Assert(await Trusted(a,bid)&&await Trusted(b,aid),"trust saved only after both approvals");
var oldSecret=await ah.Secrets.GetAsync("lantern.trust."+bid);
await Begin(a,b);await Decide(a,false);
await Until(async()=> (await Requests(a)).Length==0&&!(await State(a)).GetProperty("pairingBusy").GetBoolean(),"re-pair rejection");
Assert(await ah.Secrets.GetAsync("lantern.trust."+bid)==oldSecret,"failed re-pair preserves existing device key");
await Complete(a,c);
Assert(await Trusted(a,bid)&&await Trusted(a,cid),"multiple remembered devices retain separate authenticated identities");
Assert(!Service(c).DiscoveryAvailable,"Apple profile supports pairing without raw UDP discovery");
var wrongRoute=await Cmd(a,new{type="reconnect-device",id=cid,address="127.0.0.1",port=Service(b).Port});
Assert(!wrongRoute.GetProperty("ok").GetBoolean(),"manual reconnect rejects a different trusted device at the same address");
var correctRoute=await Cmd(a,new{type="reconnect-device",id=cid,address="127.0.0.1",port=Service(c).Port});
Assert(correctRoute.GetProperty("ok").GetBoolean()&&Service(a).Presence(cid)=="online","manual reconnect verifies the remembered key before advertising online");
var unpairedRoute=await Cmd(c,new{type="reconnect-device",id=bid,address="127.0.0.1",port=Service(b).Port});
Assert(!unpairedRoute.GetProperty("ok").GetBoolean(),"manual reconnect cannot add an unpaired device");
using(var socket=new TcpClient())
{
    var code=(await Cmd(b,new{type="open-pairing"})).GetProperty("code").GetString();await socket.ConnectAsync("127.0.0.1",Service(b).Port);
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));bool rejected=false;
    try{var legacy=await Handshake.ConnectAsync(socket.GetStream(),new(Guid.NewGuid().ToString("N"),"Legacy","Android"),new TrustStore(new Secrets()),code,null,deadline.Token);await legacy.Channel.DisposeAsync();}catch(Exception ex) when(ex is not OutOfMemoryException){rejected=true;}
    Assert(rejected&&(await Requests(b)).Length==0,"legacy pairing cannot bypass required bilateral approval");await Cmd(b,new{type="close-pairing"});
}
await Cmd(b,new{type="setting",key="autoAccept",value=true});await Cmd(c,new{type="setting",key="autoAccept",value=true});
// Ephemeral listener ports are deterministic known endpoints in this isolated test.
Service(a).Nearby[bid]=new(new(bid,"B","Android"),"127.0.0.1",Service(b).Port,DateTimeOffset.UtcNow);
Service(a).Nearby[cid]=new(new(cid,"C","iPadOS"),"127.0.0.1",Service(c).Port,DateTimeOffset.UtcNow);
byte[] first=RandomNumberGenerator.GetBytes(1024*1024+79),second=Encoding.UTF8.GetBytes("duplicate-name second file 🔐");
PickedFile[] files=[new(new("same.bin",first.Length,"application/octet-stream",()=>Task.FromResult<Stream>(new MemoryStream(first))),null),new(new("same.bin",second.Length,"application/octet-stream",()=>Task.FromResult<Stream>(new MemoryStream(second))),null)];
var selected=Element(a.SelectNativeFiles(files));Assert(selected.GetProperty("selection").GetArrayLength()==2,"same-name Android source files retained independently");
var sent=await Cmd(a,new{type="send",peerIds=new[]{bid,cid,bid},selectionId=selected.GetProperty("selectionId").GetString()});
Assert(sent.GetProperty("ok").GetBoolean()&&sent.GetProperty("recipients").GetInt32()==2,"multi-send creates one task per distinct selected trusted device");
string[] ids=sent.GetProperty("ids").EnumerateArray().Select(x=>x.GetString()!).ToArray();
await Until(async()=>{var tasks=(await State(a)).GetProperty("tasks").EnumerateArray().Where(t=>ids.Contains(t.GetProperty("id").GetString())).ToArray();return tasks.Length==2&&tasks.All(t=>t.GetProperty("state").GetString() is "completed" or "failed");},"both recipient transfers",15000);
Assert((await State(a)).GetProperty("tasks").EnumerateArray().Where(t=>ids.Contains(t.GetProperty("id").GetString())).All(t=>t.GetProperty("state").GetString()=="completed"),"both recipients independently verify and acknowledge complete file contents");
foreach(var host in new[]{bh,ch}){var received=Directory.GetFiles(Path.Combine(host.DataDirectory,"Inbox"),"*",SearchOption.AllDirectories).Select(File.ReadAllBytes).ToArray();Assert(received.Length==2&&received.Any(x=>x.SequenceEqual(first))&&received.Any(x=>x.SequenceEqual(second)),"recipient receives both same-name files without loss or collision");}
selected=Element(a.SelectNativeFiles(files));var replacement=Element(a.SelectNativeFiles(files[..1]));
Assert(!(await Cmd(a,new{type="send",peerIds=new[]{bid},selectionId=selected.GetProperty("selectionId").GetString()})).GetProperty("ok").GetBoolean(),"stale file selection token prevents unintended file sends");
var invalid=await Cmd(a,new{type="send",peerIds=new[]{bid,Guid.NewGuid().ToString("N")},selectionId=replacement.GetProperty("selectionId").GetString()});
Assert(!invalid.GetProperty("ok").GetBoolean(),"untrusted recipient rejects batch before consuming chosen files");
var valid=await Cmd(a,new{type="send",peerIds=new[]{bid},selectionId=replacement.GetProperty("selectionId").GetString()});Assert(valid.GetProperty("ok").GetBoolean(),"file selection remains usable after failed batch validation");
await Until(async()=>{var state=await State(a);return state.GetProperty("tasks").EnumerateArray().Where(t=>t.GetProperty("direction").GetString()=="send").All(t=>t.GetProperty("state").GetString()=="completed");},"prior files completed");
var releaseFiles=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var openedStreams=0;
PickedFile blocked=new(new("fairness.bin",128,"application/octet-stream",()=>{Interlocked.Increment(ref openedStreams);return Task.FromResult<Stream>(new GateStream(new byte[128],releaseFiles.Task));}),null);
try
{
    for(int i=0;i<4;i++){var pick=Element(a.SelectNativeFiles([blocked]));Assert((await Cmd(a,new{type="send",peerIds=new[]{bid},selectionId=pick.GetProperty("selectionId").GetString()})).GetProperty("ok").GetBoolean(),"file lane queued "+i);}
    await Until(()=>Task.FromResult(Volatile.Read(ref openedStreams)==4),"four file streams blocked");
    var chat=await Cmd(a,new{type="chat-send",peerId=bid,text="大文件传输时消息仍可送达"});Assert(chat.GetProperty("ok").GetBoolean(),"chat queued while all four file lanes busy");
    string chatId=chat.GetProperty("id").GetString()!;
    await Until(async()=>(await State(a)).GetProperty("tasks").EnumerateArray().Any(t=>t.GetProperty("id").GetString()==chatId&&t.GetProperty("state").GetString()=="completed"),"chat bypasses four blocked file lanes",3000);
    Assert(!releaseFiles.Task.IsCompleted,"message delivered before any blocked file lane released");
    Assert(a.GetTransferNotificationSnapshot().Tasks.All(t=>t.Id!=chatId),"text messages do not trigger file progress notification flashes");
}
finally{releaseFiles.TrySetResult(true);}
await Until(async()=>(await State(a)).GetProperty("tasks").EnumerateArray().Where(t=>t.GetProperty("name").GetString()=="fairness.bin").All(t=>t.GetProperty("state").GetString()=="completed"),"released files complete",8000);
var isolatedGate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);int isolatedOpened=0;
var isolatedPick=Element(a.SelectNativeFiles([new(new("isolation.bin",first.Length,"application/octet-stream",()=>{Interlocked.Increment(ref isolatedOpened);return Task.FromResult<Stream>(new GateStream(first,isolatedGate.Task));}),null)]));
var isolatedBatch=await Cmd(a,new{type="send",peerIds=new[]{bid,cid},selectionId=isolatedPick.GetProperty("selectionId").GetString()});
Assert(isolatedBatch.GetProperty("ok").GetBoolean(),"cancellation isolation batch created");
var isolatedIds=isolatedBatch.GetProperty("ids").EnumerateArray().Select(x=>x.GetString()!).ToArray();
try
{
    await Until(()=>Task.FromResult(Volatile.Read(ref isolatedOpened)==2),"both isolated streams opened");
    var cancelled=(await State(a)).GetProperty("tasks").EnumerateArray().Single(t=>isolatedIds.Contains(t.GetProperty("id").GetString())&&t.GetProperty("peerId").GetString()==cid).GetProperty("id").GetString();
    Assert((await Cmd(a,new{type="cancel",id=cancelled})).GetProperty("ok").GetBoolean(),"one recipient can be cancelled independently");
    await Until(async()=>(await State(a)).GetProperty("tasks").EnumerateArray().Any(t=>t.GetProperty("id").GetString()==cancelled&&t.GetProperty("state").GetString()=="cancelled"),"cancelled recipient finishes");
}
finally{isolatedGate.TrySetResult(true);}
await Until(async()=>(await State(a)).GetProperty("tasks").EnumerateArray().Any(t=>isolatedIds.Contains(t.GetProperty("id").GetString())&&t.GetProperty("peerId").GetString()==bid&&t.GetProperty("state").GetString()=="completed"),"other recipient completes after cancellation",8000);
Assert(Directory.GetFiles(Path.Combine(bh.DataDirectory,"Inbox"),"*isolation.bin",SearchOption.AllDirectories).Any(f=>File.ReadAllBytes(f).SequenceEqual(first)),"cancelling another recipient never corrupts successful verified contents");
var release=(await State(a)).GetProperty("releaseNotes");Assert(release.GetProperty("showOnStartup").GetBoolean(),"new version advertises release notes on first launch");await Cmd(a,new{type="release-seen"});Assert(!(await State(a)).GetProperty("releaseNotes").GetProperty("showOnStartup").GetBoolean(),"acknowledged release notes no longer auto-display");
Assert(await ah.Secrets.GetAsync("lantern.release.seen")==AppRelease.Version,"release acknowledgment persisted in local secure store");
Console.WriteLine($"PASS {passed} pairing, multi-device transfer, source-selection and release-note assertions");

sealed class Host(string directory,string platform):IPlatformHost
{
    public string Platform=>platform;public string DeviceName=>platform+" 同名设备";public string DataDirectory=>directory;public ISecretStore Secrets{get;}=new Secrets();
    public bool UseUdpDiscovery=>platform!="iPadOS";
    public Task<IReadOnlyList<PickedFile>> PickAsync()=>Task.FromResult<IReadOnlyList<PickedFile>>([]);
    public Task OpenFolderAsync(string folder)=>Task.CompletedTask;public Task<string?> ThumbnailAsync(string file,string mime)=>Task.FromResult<string?>(null);
}
sealed class Secrets:ISecretStore
{
    private readonly ConcurrentDictionary<string,string> values=new();
    public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}public Task RemoveAsync(string key){values.TryRemove(key,out _);return Task.CompletedTask;}
}
sealed class GateStream(byte[] bytes,Task gate):MemoryStream(bytes)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){await gate.WaitAsync(ct);return await base.ReadAsync(buffer,ct);}
}
