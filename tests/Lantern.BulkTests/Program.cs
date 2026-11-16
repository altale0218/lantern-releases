using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;

string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
var hosts=Enumerable.Range(0,3).Select(i=>new Host(Path.Combine(root,i.ToString()),i==0?"Windows":"Android")).ToArray();var clients=hosts.Select(h=>new AppController(h)).ToArray();
JsonElement Element(object o)=>JsonSerializer.SerializeToElement(o,AppController.Json);
async Task<JsonElement> Cmd(int i,object o)=>Element(await clients[i].DispatchAsync(Element(o)));
async Task<JsonElement> State(int i)=>Element(await clients[i].GetStateAsync());
LanService Service(int i)=>(LanService)typeof(AppController).GetField("service",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(clients[i])!;
void Check(bool ok,string text){if(!ok)throw new Exception(text);}
byte[] pattern=RandomNumberGenerator.GetBytes(64*1024);long[] sizes=[256L*1024*1024,16L*1024*1024,0];string[] names=["大文件流式校验.bin","照片资料备份.bin","空文件.txt"];
var digests=new string[sizes.Length];for(int i=0;i<sizes.Length;i++){await using var stream=new PatternStream(pattern,sizes[i]);digests[i]=Convert.ToHexString(await SHA256.HashDataAsync(stream));}
try
{
    for(int i=0;i<3;i++){await clients[i].InitializeAsync(0);await Cmd(i,new{type="setting",key="autoAccept",value=true});}
    var identities=new DeviceIdentity[3];for(int i=0;i<3;i++)identities[i]=(await State(i)).GetProperty("self").Deserialize<DeviceIdentity>(AppController.Json)!;
    for(int i=1;i<3;i++){string key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));await new TrustStore(hosts[0].Secrets).SaveAsync(new(identities[i],key,DateTimeOffset.UtcNow));await new TrustStore(hosts[i].Secrets).SaveAsync(new(identities[0],key,DateTimeOffset.UtcNow));}
    void Routes(){for(int i=0;i<3;i++)for(int j=0;j<3;j++)if(i!=j){var d=new NearbyDevice(identities[j],"127.0.0.1",Service(j).Port,DateTimeOffset.UtcNow);Service(i).Nearby[identities[j].Id]=d;((ConcurrentDictionary<string,NearbyDevice>)typeof(LanService).GetField("candidates",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Service(i))!)[identities[j].Id]=d;}}
    Routes();await Cmd(0,new{type="setting",key="limitMiB",value=30});
    var files=sizes.Select((length,i)=>new PickedFile(new(names[i],length,"application/octet-stream",()=>Task.FromResult<Stream>(new PatternStream(pattern,length))),null)).ToArray();
    var selection=Element(clients[0].SelectNativeFiles(files));long baseline=GC.GetTotalMemory(true),peak=baseline;var clock=Stopwatch.StartNew();
    var batch=await Cmd(0,new{type="send",peerIds=identities.Skip(1).Select(i=>i.Id).ToArray(),selectionId=selection.GetProperty("selectionId").GetString()});Check(batch.GetProperty("ok").GetBoolean(),"Bulk batch rejected");
    string[] ids=batch.GetProperty("ids").EnumerateArray().Select(x=>x.GetString()!).ToArray();bool paused=false,resumed=false,chatSent=false;long pauseAt=0,pausedBytes=0;string? pausedId=null,chatId=null;var previous=new Dictionary<string,long>();
    while(clock.Elapsed<TimeSpan.FromSeconds(45))
    {
        Routes();var state=await State(0);var tasks=state.GetProperty("tasks").EnumerateArray().Where(t=>ids.Contains(t.GetProperty("id").GetString())).ToArray();
        foreach(var t in tasks){string id=t.GetProperty("id").GetString()!;long done=t.GetProperty("completed").GetInt64();Check(done>=previous.GetValueOrDefault(id),"Progress regressed");previous[id]=done;Check(t.GetProperty("state").GetString() is not("failed" or "cancelled" or "rejected"),"Bulk transfer failed: "+t.GetProperty("message"));}
        if(!paused&&tasks.Any(t=>t.GetProperty("completed").GetInt64()>8*1024*1024))
        {var t=tasks.Single(t=>t.GetProperty("peerId").GetString()==identities[1].Id);pausedId=t.GetProperty("id").GetString();pausedBytes=t.GetProperty("completed").GetInt64();await Cmd(0,new{type="pause",id=pausedId});paused=true;pauseAt=clock.ElapsedMilliseconds;}
        if(paused&&!chatSent){var result=await Cmd(0,new{type="chat-send",peerId=identities[2].Id,text="大文件传输期间仍能及时聊天 🔐"});Check(result.GetProperty("ok").GetBoolean(),"Chat blocked by bulk files");chatId=result.GetProperty("id").GetString();chatSent=true;}
        if(paused&&!resumed&&clock.ElapsedMilliseconds-pauseAt>500)
        {var t=tasks.Single(t=>t.GetProperty("id").GetString()==pausedId);Check(t.GetProperty("state").GetString()=="paused"&&t.GetProperty("completed").GetInt64()<=pausedBytes+1024*1024,"Pause did not stop file reads");await Cmd(0,new{type="pause",id=pausedId});resumed=true;}
        peak=Math.Max(peak,GC.GetTotalMemory(false));
        if(tasks.Length==2&&tasks.All(t=>t.GetProperty("state").GetString()=="completed")){Check(paused&&resumed&&chatSent,"Bulk phases not exercised");Check(state.GetProperty("tasks").EnumerateArray().Any(t=>t.GetProperty("id").GetString()==chatId&&t.GetProperty("state").GetString()=="completed"),"Concurrent message not verified");break;}
        await Task.Delay(100);
    }
    var final=(await State(0)).GetProperty("tasks").EnumerateArray().Where(t=>ids.Contains(t.GetProperty("id").GetString())).ToArray();Check(final.Length==2&&final.All(t=>t.GetProperty("state").GetString()=="completed"),"Bulk completion timeout");
    foreach(var host in hosts.Skip(1))for(int i=0;i<names.Length;i++){var file=Directory.GetFiles(Path.Combine(host.DataDirectory,"Inbox"),"*-"+names[i],SearchOption.AllDirectories).Single();await using var stream=File.OpenRead(file);Check(stream.Length==sizes[i]&&Convert.ToHexString(await SHA256.HashDataAsync(stream))==digests[i],"Bulk bytes differ");}
    Check(peak<baseline+256L*1024*1024,"Memory scaled with full file payload");
    var report=new{version=AppRelease.Version,success=true,recipients=2,verifiedBytes=2*sizes.Sum(),elapsedSeconds=clock.Elapsed.TotalSeconds,managedBaseline=baseline,managedPeak=peak,pauseResume=true,concurrentChat=true,zeroByteFile=true};
    await File.WriteAllTextAsync(Path.Combine(root,"bulk-result.json"),JsonSerializer.Serialize(report));Console.WriteLine(JsonSerializer.Serialize(report));
    Console.WriteLine("PASS multi-recipient large streamed files: per-file SHA-256 and receipts, independent pause/resume, concurrent chat, zero-byte file and bounded managed memory. Loopback/30 MiB/s per-session test is not a Wi-Fi throughput claim.");
}
finally
{
    foreach(var client in clients)await client.DisposeAsync();
    foreach(var host in hosts){string inbox=Path.Combine(host.DataDirectory,"Inbox");if(Directory.Exists(inbox))Directory.Delete(inbox,true);}
}
sealed class PatternStream(byte[] pattern,long length):Stream
{
    long position;public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>length;public override long Position{get=>position;set=>throw new NotSupportedException();}
    public override int Read(byte[] buffer,int offset,int count){int size=(int)Math.Min(count,length-position),written=0;while(written<size){int index=(int)((position+written)%pattern.Length),copy=Math.Min(size-written,pattern.Length-index);pattern.AsSpan(index,copy).CopyTo(buffer.AsSpan(offset+written,copy));written+=copy;}position+=size;return size;}
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){ct.ThrowIfCancellationRequested();int size=(int)Math.Min(buffer.Length,length-position),written=0;while(written<size){int index=(int)((position+written)%pattern.Length),copy=Math.Min(size-written,pattern.Length-index);pattern.AsSpan(index,copy).CopyTo(buffer.Span.Slice(written,copy));written+=copy;}position+=size;return ValueTask.FromResult(size);}
    public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}
sealed class Host(string directory,string platform):IPlatformHost
{
    public string Platform=>platform;public string DeviceName=>"Bulk fixture";public string DataDirectory=>directory;public ISecretStore Secrets{get;}=new Secrets();public Func<IReadOnlyList<LanInterface>>? NetworkInterfaces=>()=>[];
    public Task<IReadOnlyList<PickedFile>> PickAsync()=>Task.FromResult<IReadOnlyList<PickedFile>>([]);public Task OpenFolderAsync(string folder)=>Task.CompletedTask;public Task<string?> ThumbnailAsync(string file,string mime)=>Task.FromResult<string?>(null);
}
sealed class Secrets:ISecretStore
{
    readonly ConcurrentDictionary<string,string> values=new();public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}public Task RemoveAsync(string key){values.TryRemove(key,out _);return Task.CompletedTask;}
}
