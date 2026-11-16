using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;

var root=Path.GetFullPath(args[0]);int minutes=int.Parse(args[1]);
string thirdPlatform=args.Length>2?args[2]:"Android";
var hosts=new[]{"Windows","Android",thirdPlatform}.Select((platform,i)=>new Host(Path.Combine(root,i.ToString()),platform)).ToArray();
var clients=hosts.Select(h=>new AppController(h)).ToArray();
JsonElement Json(object o)=>JsonSerializer.SerializeToElement(o,AppController.Json);
async Task<JsonElement> State(int i)=>Json(await clients[i].GetStateAsync());
async Task<JsonElement> Cmd(int i,object o)=>Json(await clients[i].DispatchAsync(Json(o)));
LanService Service(int i)=>(LanService)typeof(AppController).GetField("service",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(clients[i])!;
void Check(bool ok,string text){if(!ok)throw new Exception(text);}
int cycles=0,transfers=0,messages=0;long verifiedBytes=0;
var timer=Stopwatch.StartNew();long peak=0,baseline=0;var report=Path.Combine(root,"soak-result.json");
Directory.CreateDirectory(root);
try
{
    for(int i=0;i<3;i++)await clients[i].InitializeAsync(0);
    var identities=new DeviceIdentity[3];for(int i=0;i<3;i++)identities[i]=(await State(i)).GetProperty("self").Deserialize<DeviceIdentity>(AppController.Json)!;
    for(int i=0;i<3;i++)for(int j=i+1;j<3;j++)
    {
        string key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await new TrustStore(hosts[i].Secrets).SaveAsync(new(identities[j],key,DateTimeOffset.UtcNow));await new TrustStore(hosts[j].Secrets).SaveAsync(new(identities[i],key,DateTimeOffset.UtcNow));
    }
    for(int i=0;i<3;i++){await Cmd(i,new{type="setting",key="autoAccept",value=true});await Cmd(i,new{type="release-seen"});}
    void Routes()
    {
        for(int i=0;i<3;i++)for(int j=0;j<3;j++)if(i!=j)
        {
            var entry=new NearbyDevice(identities[j],"127.0.0.1",Service(j).Port,DateTimeOffset.UtcNow);
            Service(i).Nearby[identities[j].Id]=entry;
            ((ConcurrentDictionary<string,NearbyDevice>)typeof(LanService).GetField("candidates",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Service(i))!)[identities[j].Id]=entry;
        }
    }
    async Task Wait(int sender,string[] ids)
    {
        for(int n=0;n<200;n++)
        {
            var tasks=(await State(sender)).GetProperty("tasks").EnumerateArray().Where(t=>ids.Contains(t.GetProperty("id").GetString())).ToArray();
            if(tasks.Length==ids.Length&&tasks.All(t=>t.GetProperty("state").GetString()=="completed"))return;
            if(tasks.Any(t=>t.GetProperty("state").GetString() is "failed" or "cancelled"))throw new Exception("Soak task failed: "+string.Join(",",tasks.Select(t=>t.GetProperty("message").ToString())));
            await Task.Delay(25);
        }
        throw new Exception("Soak task completion timeout");
    }
    while(timer.Elapsed<TimeSpan.FromMinutes(minutes))
    {
        Routes();int sender=cycles%3;var targets=Enumerable.Range(0,3).Where(i=>i!=sender).ToArray();
        byte[] payload=RandomNumberGenerator.GetBytes(128*1024+cycles%8192);
        var pick=Json(clients[sender].SelectNativeFiles([new(new("soak.bin",payload.Length,"application/octet-stream",()=>Task.FromResult<Stream>(new MemoryStream(payload))),null)]));
        var sent=await Cmd(sender,new{type="send",peerIds=targets.Select(i=>identities[i].Id).ToArray(),selectionId=pick.GetProperty("selectionId").GetString()});Check(sent.GetProperty("ok").GetBoolean(),"Soak batch not queued");
        var ids=sent.GetProperty("ids").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        var textResults=await Task.WhenAll(targets.Select(i=>Cmd(i,new{type="chat-send",peerId=identities[sender].Id,text="soak "+cycles+" 🔐"})));
        await Wait(sender,ids);
        for(int j=0;j<targets.Length;j++)
        {
            Check(textResults[j].GetProperty("ok").GetBoolean(),"Soak chat not queued");await Wait(targets[j],[textResults[j].GetProperty("id").GetString()!]);
            var received=(await State(targets[j])).GetProperty("tasks").EnumerateArray().Single(t=>ids.Contains(t.GetProperty("id").GetString()));
            var saved=Directory.GetFiles(received.GetProperty("folder").GetString()!);Check(saved.Length==1&&File.ReadAllBytes(saved[0]).SequenceEqual(payload),"Soak bytes differ");
            await Cmd(targets[j],new{type="delete-received",id=received.GetProperty("id").GetString(),confirmed=true});
        }
        transfers+=2;messages+=2;verifiedBytes+=2L*payload.Length;cycles++;
        if(cycles%10==0)
        {
            for(int i=0;i<3;i++)await Cmd(i,new{type="clear-history"});
            long memory=GC.GetTotalMemory(true);if(baseline==0)baseline=memory;peak=Math.Max(peak,memory);
            var state=new{version=AppRelease.Version,elapsedSeconds=(int)timer.Elapsed.TotalSeconds,cycles,transfers,messages,verifiedBytes,managedBytes=memory,baselineManagedBytes=baseline,peakManagedBytes=peak,success=true,complete=false};
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(state));Console.WriteLine(JsonSerializer.Serialize(state));
            Check(memory<baseline+64L*1024*1024,"Managed memory grew without bound after history cleanup");
        }
        await Task.Delay(1500);
    }
    await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{version=AppRelease.Version,elapsedSeconds=(int)timer.Elapsed.TotalSeconds,cycles,transfers,messages,verifiedBytes,baselineManagedBytes=baseline,peakManagedBytes=peak,success=true,complete=true}));
    Console.WriteLine("PASS bounded multi-device encryption/verification soak completed");
}
finally{foreach(var client in clients)await client.DisposeAsync();}

sealed class Host(string directory,string platform):IPlatformHost
{
    public string DataDirectory=>directory;public string Platform=>platform;public string DeviceName=>"Isolated soak";public ISecretStore Secrets{get;}=new Secrets();public Func<IReadOnlyList<LanInterface>>? NetworkInterfaces=>()=>[];
    public bool UseUdpDiscovery=>platform is not ("iOS" or "iPadOS");
    public Task<IReadOnlyList<PickedFile>> PickAsync()=>Task.FromResult<IReadOnlyList<PickedFile>>([]);public Task OpenFolderAsync(string directory)=>Task.CompletedTask;public Task<string?> ThumbnailAsync(string file,string mime)=>Task.FromResult<string?>(null);
    public Task DeleteReceivedAsync(string folder){Directory.Delete(folder,true);return Task.CompletedTask;}
}
sealed class Secrets:ISecretStore
{
    private readonly ConcurrentDictionary<string,string> values=new();public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}public Task RemoveAsync(string key){values.TryRemove(key,out _);return Task.CompletedTask;}
}
