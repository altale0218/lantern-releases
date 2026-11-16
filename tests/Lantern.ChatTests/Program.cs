using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;

void Assert(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS "+label);}
JsonElement Element(object obj)=>JsonSerializer.SerializeToElement(obj,AppController.Json);
async Task<JsonElement> Cmd(AppController c,object obj)=>Element(await c.DispatchAsync(Element(obj)));
LanService Service(AppController c)=>(LanService)typeof(AppController).GetField("service",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(c)!;
async Task<JsonElement[]> Records(AppController c)=>(await c.GetStateAsync()).Pipe(Element).GetProperty("tasks").EnumerateArray().ToArray();
async Task<string> Id(AppController c)=>Element(await c.GetStateAsync()).GetProperty("self").GetProperty("id").GetString()!;
async Task Pair(AppController a,AppController b)
{
    var code=(await Cmd(b,new{type="open-pairing"})).GetProperty("code").GetString();
    var originalA=Service(a).ConfirmPairing;var originalB=Service(b).ConfirmPairing;Service(a).ConfirmPairing=Service(b).ConfirmPairing=(_,_)=>Task.FromResult(true);
    var connection=await Service(a).ConnectAsync("127.0.0.1",Service(b).Port,code,null,CancellationToken.None);
    await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},CancellationToken.None);
    Service(a).ConfirmPairing=originalA;Service(b).ConfirmPairing=originalB;
    var aid=await Id(a);var bid=await Id(b);
    Service(a).Nearby[bid]=new(b.LocalIdentity,"127.0.0.1",Service(b).Port,DateTimeOffset.UtcNow);
    Service(b).Nearby[aid]=new(a.LocalIdentity,"127.0.0.1",Service(a).Port,DateTimeOffset.UtcNow);
}
async Task Wait(AppController c,string id)
{
    for(int i=0;i<250;i++){var t=(await Records(c)).FirstOrDefault(t=>t.GetProperty("id").GetString()==id);if(t.ValueKind!=JsonValueKind.Undefined&&t.GetProperty("state").GetString() is "completed" or "failed")return;await Task.Delay(30);}
    throw new Exception("Message timeout "+id);
}
if(args[0]=="--native-chat")
{
    var nativeHost=new Host(Path.GetFullPath(args[1]),"Android");
    await using var client=new AppController(nativeHost);await client.InitializeAsync(0);
    Service(client).ConfirmPairing=(_,_)=>Task.FromResult(true);
    var connection=await Service(client).ConnectAsync("127.0.0.1",LanService.DefaultPort,args[2],null,CancellationToken.None);
    string peer=connection.Peer.Id;
    await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},CancellationToken.None);
    var result=await Cmd(client,new{type="chat-send",peerId=peer,text="原生客户端加密聊天验证 🔐"});
    Assert(result.GetProperty("ok").GetBoolean(),"native peer text queued");await Wait(client,result.GetProperty("id").GetString()!);
    Console.WriteLine("NATIVE_CHAT_READY");
    for(int i=0;i<600;i++)
    {
        if((await Records(client)).Any(t=>t.GetProperty("direction").GetString()=="receive"&&t.GetProperty("chatText").GetString()=="Windows 原生回复已校验 🔐")){Console.WriteLine("NATIVE_CHAT_VERIFIED");return;}
        await Task.Delay(100);
    }
    throw new Exception("Native reply timeout");
}
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
bool apple=args.Contains("--apple"),android=args.Contains("--android");
var ah=new Host(Path.Combine(root,"A"),android?"Android":"Windows");
var bh=new Host(Path.Combine(root,"B"),apple?"iOS":"Android");
var ch=new Host(Path.Combine(root,"C"),apple?"iPadOS":"Android");
var a=new AppController(ah);var b=new AppController(bh);var c=new AppController(ch);
try
{
    await a.InitializeAsync(0);await b.InitializeAsync(0);await c.InitializeAsync(0);await Pair(a,b);await Pair(a,c);
    string aid=await Id(a),bid=await Id(b),cid=await Id(c);
    var sent=await Cmd(a,new{type="chat-send",peerId=bid,text="秘密消息 <img src=x onerror=alert(1)> 🔐\n第二行"});
    Assert(sent.GetProperty("ok").GetBoolean(),"authenticated text queued");string messageId=sent.GetProperty("id").GetString()!;await Wait(a,messageId);
    Assert((await Records(a)).Single(t=>t.GetProperty("id").GetString()==messageId).GetProperty("state").GetString()=="completed","sender waits for matching verified receipt");
    Assert((await Records(b)).Single().GetProperty("chatText").GetString()!.Contains("秘密消息"),"receiver decrypts exact Unicode text");
    var second=await Cmd(a,new{type="chat-send",peerId=cid,text="第三台设备"});await Wait(a,second.GetProperty("id").GetString()!);
    Assert((await Records(a)).Select(t=>t.GetProperty("conversationKey").GetString()).Distinct().Count()==2,"same device names remain separate by identity and key");
    var replies=await Task.WhenAll(Enumerable.Range(0,5).Select(i=>Cmd(b,new{type="chat-send",peerId=aid,text="并发回复 "+i})));
    foreach(var r in replies)await Wait(b,r.GetProperty("id").GetString()!);
    Assert((await Records(a)).Count(t=>t.GetProperty("direction").GetString()=="receive")==5,"bidirectional concurrent authenticated messages");
    Assert(!(await Cmd(a,new{type="chat-send",peerId=bid,text=new string('中',6000)})).GetProperty("ok").GetBoolean(),"UTF8 byte limit enforced");
    Assert(!(await Cmd(a,new{type="chat-send",peerId=bid,text="  \n"})).GetProperty("ok").GetBoolean(),"empty messages rejected");
    var received=(await Records(b)).Single(t=>t.GetProperty("id").GetString()==messageId);string text=received.GetProperty("chatText").GetString()!;
    async Task Raw(ChatEnvelope envelope,bool accepted)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection=await Service(a).ConnectAsync("127.0.0.1",Service(b).Port,null,bid,timeout.Token);
        await using(connection.Channel)
        {
            await connection.Channel.SendJsonAsync(envelope,timeout.Token);
            try{var receipt=await connection.Channel.ReceiveJsonAsync<ChatReceipt>(timeout.Token);if(!accepted||!receipt.Verified)throw new Exception("Unexpected receipt");}
            catch(Exception ex) when(!accepted&&ex is IOException or OperationCanceledException){}
        }
    }
    await Raw(new(1,messageId,text,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))),true);
    Assert((await Records(b)).Count(t=>t.GetProperty("id").GetString()==messageId)==1,"retry duplicate acknowledged without duplicating message");
    await Raw(new(1,Guid.NewGuid().ToString("N"),"恶意篡改",new string('0',64)),false);
    Assert(!(await Records(b)).Any(t=>t.GetProperty("chatText").ValueKind==JsonValueKind.String&&t.GetProperty("chatText").GetString()=="恶意篡改"),"bad content digest discarded before storage");
    await Raw(new(1,messageId,"冲突",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("冲突")))),false);
    Assert((await Records(b)).Single(t=>t.GetProperty("id").GetString()==messageId).GetProperty("chatText").GetString()==text,"conflicting message id cannot replace existing content");
    Assert(Element(await b.GetStateAsync()).GetProperty("settings").GetProperty("autoAccept").GetBoolean(),"authenticated image transfer uses the fresh-install auto-receive default");
    byte[] image=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
    ah.Selection=[new(new("图片.png",image.Length,"image/png",()=>Task.FromResult<Stream>(new MemoryStream(image))),"data:image/png;base64,"+Convert.ToBase64String(image))];
    await Cmd(a,new{type="pick"});await Cmd(a,new{type="send",peerId=bid});
    for(int i=0;i<200&&!(await Records(b)).Any(t=>t.GetProperty("kind").GetString()=="files"&&t.GetProperty("state").GetString()=="completed");i++)await Task.Delay(30);
    var file=(await Records(b)).Single(t=>t.GetProperty("kind").GetString()=="files");
    Assert(file.GetProperty("state").GetString()=="completed"&&File.ReadAllBytes(Directory.GetFiles(file.GetProperty("folder").GetString()!).Single()).SequenceEqual(image),"image uses existing encrypted SHA256 verified file transfer");
    Assert(file.GetProperty("conversationKey").GetString()==received.GetProperty("conversationKey").GetString(),"files and text share same authenticated conversation");
    await b.DisposeAsync();b=new AppController(bh);await b.InitializeAsync(0);
    Assert((await Records(b)).Any(t=>t.GetProperty("id").GetString()==messageId),"chat persists across restart");
    Assert((await Records(b)).Single(t=>t.GetProperty("kind").GetString()=="files").GetProperty("files")[0].GetProperty("preview").GetString() is not null,"encrypted image thumbnail persists across restart");
    var history=await File.ReadAllBytesAsync(Path.Combine(bh.DataDirectory,"conversations.bin"));
    Assert(!Encoding.UTF8.GetString(history).Contains("秘密消息")&&!File.Exists(Path.Combine(bh.DataDirectory,"history.json")),"disk history ciphertext contains no plaintext and legacy file removed");
    var key=Convert.FromBase64String((await bh.Secrets.GetAsync("lantern.history.key.v1"))!);history[^1]^=1;
    bool rejected=false;try{HistoryVault.Open(history,key);}catch(CryptographicException){rejected=true;}Assert(rejected,"modified encrypted history rejected by GCM tag");
    await Cmd(b,new{type="delete-record",id=messageId,confirmed=true});Assert(!(await Records(b)).Any(t=>t.GetProperty("id").GetString()==messageId),"individual chat record deletion");
    await Cmd(a,new{type="forget",id=cid});Assert(!(await Cmd(a,new{type="chat-send",peerId=cid,text="不能发送"})).GetProperty("ok").GetBoolean(),"revoked trust prevents text send");
    var oldKey=(await Records(a)).First(t=>t.GetProperty("peerId").GetString()==cid).GetProperty("conversationKey").GetString();
    await Pair(a,c);var newRecord=await Cmd(a,new{type="chat-send",peerId=cid,text="重新配对"});await Wait(a,newRecord.GetProperty("id").GetString()!);
    Assert((await Records(a)).Single(t=>t.GetProperty("id").GetString()==newRecord.GetProperty("id").GetString()).GetProperty("conversationKey").GetString()!=oldKey,"changed pairing key creates separate conversation");
    if (apple)
    {
        await Pair(b,c);
        var appleMessage=await Cmd(c,new{type="chat-send",peerId=bid,text="iPad 与 iPhone 加密消息"});
        await Wait(c,appleMessage.GetProperty("id").GetString()!);
        Assert((await Records(b)).Any(t=>t.GetProperty("id").GetString()==appleMessage.GetProperty("id").GetString()),"simulated iPad and iPhone exchange verified text without UDP discovery");
        await Pair(b,a);
        bh.Selection=ah.Selection;
        Assert((await Cmd(b,new{type="pick"})).GetProperty("ok").GetBoolean(),"simulated iPhone selects a file after restart");
        Assert((await Cmd(b,new{type="send",peerId=aid})).GetProperty("ok").GetBoolean(),"simulated iPhone queues authenticated file in the reverse direction");
        for(int i=0;i<200&&!(await Records(a)).Any(t=>t.GetProperty("kind").GetString()=="files"&&t.GetProperty("direction").GetString()=="receive"&&t.GetProperty("state").GetString()=="completed");i++)await Task.Delay(30);
        var reverse=(await Records(a)).Single(t=>t.GetProperty("kind").GetString()=="files"&&t.GetProperty("direction").GetString()=="receive");
        Assert(reverse.GetProperty("state").GetString()=="completed"&&File.ReadAllBytes(Directory.GetFiles(reverse.GetProperty("folder").GetString()!).Single()).SequenceEqual(image),"simulated iPhone sends the exact verified file bytes back to the other platform");
    }
}
finally{await a.DisposeAsync();await b.DisposeAsync();await c.DisposeAsync();}
var migrationHost=new Host(Path.Combine(root,"Migration"),"Windows");Directory.CreateDirectory(migrationHost.DataDirectory);
await File.WriteAllTextAsync(Path.Combine(migrationHost.DataDirectory,"history.json"),JsonSerializer.Serialize(Enumerable.Range(0,130).Select(i=>new UiTask{Id=Guid.NewGuid().ToString("N"),PeerId=Guid.NewGuid().ToString("N"),State="completed",Name="旧记录"+i}),AppController.Json));
await using(var migrated=new AppController(migrationHost))
{
    await migrated.InitializeAsync(0);Assert((await Records(migrated)).Length==130,"all 130 legacy records migrate without previous 100 record cap");
    await Cmd(migrated,new{type="setting",key="history",value=false});Assert(!File.Exists(Path.Combine(migrationHost.DataDirectory,"conversations.bin")),"history opt out removes persisted ciphertext");
}
Console.WriteLine("PASS all chat integration/security/migration scenarios");
static class Extensions{public static T Pipe<T>(this object o,Func<object,T> f)=>f(o);}
sealed class Host(string directory,string platform):IPlatformHost
{
    public bool UseUdpDiscovery=>platform is not ("iOS" or "iPadOS");
    public string Platform=>platform;public string DeviceName=>"同名设备";public string DataDirectory=>directory;public ISecretStore Secrets{get;}=new MemorySecrets();
    public IReadOnlyList<PickedFile> Selection=[];
    public Task<IReadOnlyList<PickedFile>> PickAsync()=>Task.FromResult(Selection);
    public Task OpenFolderAsync(string f)=>Task.CompletedTask;
    public Task<string?> ThumbnailAsync(string f,string m)=>Task.FromResult<string?>("data:image/png;base64,"+Convert.ToBase64String(File.ReadAllBytes(f)));
}
sealed class MemorySecrets:ISecretStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> values=new();
    public Task<string?> GetAsync(string k)=>Task.FromResult(values.GetValueOrDefault(k));
    public Task SetAsync(string k,string v){values[k]=v;return Task.CompletedTask;}
    public Task RemoveAsync(string k){values.TryRemove(k,out _);return Task.CompletedTask;}
}
