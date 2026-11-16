using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;

string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
var hosts=Enumerable.Range(0,18).Select(i=>new Host(Path.Combine(root,i.ToString()))).ToArray();var clients=hosts.Select(h=>new AppController(h)).ToArray();
var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int started=0;
JsonElement Element(object o)=>JsonSerializer.SerializeToElement(o,AppController.Json);
async Task<JsonElement> Cmd(int i,object o)=>Element(await clients[i].DispatchAsync(Element(o)));
async Task<JsonElement> State(int i)=>Element(await clients[i].GetStateAsync());
LanService Service(int i)=>(LanService)typeof(AppController).GetField("service",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(clients[i])!;
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
try
{
 for(int i=0;i<clients.Length;i++){await clients[i].InitializeAsync(0);await Cmd(i,new{type="setting",key="autoAccept",value=true});}
 var identities=new DeviceIdentity[clients.Length];for(int i=0;i<clients.Length;i++)identities[i]=(await State(i)).GetProperty("self").Deserialize<DeviceIdentity>(AppController.Json)!;
 for(int i=1;i<clients.Length;i++){string key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));await new TrustStore(hosts[0].Secrets).SaveAsync(new(identities[i],key,DateTimeOffset.UtcNow));await new TrustStore(hosts[i].Secrets).SaveAsync(new(identities[0],key,DateTimeOffset.UtcNow));}
 void Routes(){for(int i=1;i<clients.Length;i++)foreach(var (a,b) in new[]{(0,i),(i,0)}){var entry=new NearbyDevice(identities[b],"127.0.0.1",Service(b).Port,DateTimeOffset.UtcNow);Service(a).Nearby[identities[b].Id]=entry;((ConcurrentDictionary<string,NearbyDevice>)typeof(LanService).GetField("candidates",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Service(a))!)[identities[b].Id]=entry;}}
 Routes();byte[] payload=RandomNumberGenerator.GetBytes(2*1024*1024);
 var pick=Element(clients[0].SelectNativeFiles([new(new("并行传输.bin",payload.Length,"application/octet-stream",async()=>{Interlocked.Increment(ref started);await gate.Task;return new MemoryStream(payload);}),null)]));
 string revision=pick.GetProperty("selectionId").GetString()!;
 var rejected=await Cmd(0,new{type="send",peerIds=identities.Skip(1).Select(i=>i.Id).ToArray(),selectionId=revision});
 Check(!rejected.GetProperty("ok").GetBoolean(),"17 targets must be rejected before consuming selection");
 var batch=await Cmd(0,new{type="send",peerIds=identities.Skip(1).Take(16).Select(i=>i.Id).ToArray(),selectionId=revision});
 Check(batch.GetProperty("ok").GetBoolean()&&batch.GetProperty("recipients").GetInt32()==16,"16-target batch or preserved selection failed");
 string[] ids=batch.GetProperty("ids").EnumerateArray().Select(i=>i.GetString()!).ToArray();
 for(int n=0;n<200&&Volatile.Read(ref started)<4;n++){Routes();await Task.Delay(25);}
 Check(started==4,"Exactly four file lanes should be admitted");
 var initial=(await State(0)).GetProperty("tasks").EnumerateArray().Where(t=>ids.Contains(t.GetProperty("id").GetString())).ToArray();
 var queued=initial.Where(t=>t.GetProperty("state").GetString()=="queued").ToArray();Check(queued.Length==12,"Remaining recipients must queue");
 string cancelled=queued.Last().GetProperty("id").GetString()!;await Cmd(0,new{type="cancel",id=cancelled});
 var messageIds=new List<string>();foreach(var identity in identities.Skip(1).Take(16)){var result=await Cmd(0,new{type="chat-send",peerId=identity.Id,text="16 台设备排队传文件时，文字仍应及时送达 🔐"});Check(result.GetProperty("ok").GetBoolean(),"Message queue rejected");messageIds.Add(result.GetProperty("id").GetString()!);}
 bool messagesDone=false;
 for(int n=0;n<400;n++){Routes();var tasks=(await State(0)).GetProperty("tasks").EnumerateArray().ToArray();if(messageIds.All(id=>tasks.Any(t=>t.GetProperty("id").GetString()==id&&t.GetProperty("state").GetString()=="completed"))){messagesDone=true;break;}await Task.Delay(25);}
 Check(messagesDone&&started==4,"All 16 messages must arrive while all file lanes are blocked");gate.TrySetResult();
 bool filesDone=false;
 for(int n=0;n<800;n++){Routes();var tasks=(await State(0)).GetProperty("tasks").EnumerateArray().Where(t=>ids.Contains(t.GetProperty("id").GetString())).ToArray();if(tasks.Any(t=>t.GetProperty("state").GetString() is "failed" or "rejected"))throw new Exception("Fanout transfer failed");if(tasks.Length==16&&tasks.All(t=>t.GetProperty("state").GetString()==(t.GetProperty("id").GetString()==cancelled?"cancelled":"completed"))){filesDone=true;break;}await Task.Delay(25);}
 Check(filesDone,"Fanout completion/cancellation timeout");
 int verified=0;for(int i=1;i<=16;i++){var incoming=(await State(i)).GetProperty("tasks").EnumerateArray().Where(t=>ids.Contains(t.GetProperty("id").GetString())).ToArray();if(incoming.Length==0)continue;Check(incoming.Length==1,"Recipient received duplicate task");var files=Directory.GetFiles(incoming[0].GetProperty("folder").GetString()!);Check(files.Length==1&&File.ReadAllBytes(files[0]).SequenceEqual(payload),"Recipient bytes differ");verified++;}
 Check(verified==15,"Exactly 15 recipients complete after one queued cancellation");Check(!(await State(17)).GetProperty("tasks").EnumerateArray().Any(),"Rejected 17th target must receive nothing");
 var report=new{version=AppRelease.Version,success=true,selectedRecipients=16,verifiedRecipients=verified,verifiedBytes=(long)verified*payload.Length,messages=16,concurrentFiles=4,queuedFiles=12,queuedCancellation=true,seventeenthRejected=true,selectionPreserved=true};
 await File.WriteAllTextAsync(Path.Combine(root,"fanout-result.json"),JsonSerializer.Serialize(report));Console.WriteLine(JsonSerializer.Serialize(report));Console.WriteLine("PASS 16-recipient authenticated fanout: queue bounds, 16 verified chat receipts during blocked files, isolated queued cancellation, byte-exact recipients and no rejected-target disclosure");
}
finally{gate.TrySetResult();foreach(var client in clients)await client.DisposeAsync();foreach(var host in hosts){string inbox=Path.Combine(host.DataDirectory,"Inbox");if(Directory.Exists(inbox))Directory.Delete(inbox,true);}}
sealed class Host(string directory):IPlatformHost
{
 public string DataDirectory=>directory;public string Platform=>"Windows";public string DeviceName=>"Fanout fixture";public ISecretStore Secrets{get;}=new Secrets();public Func<IReadOnlyList<LanInterface>>? NetworkInterfaces=>()=>[];
 public Task<IReadOnlyList<PickedFile>> PickAsync()=>Task.FromResult<IReadOnlyList<PickedFile>>([]);public Task OpenFolderAsync(string directory)=>Task.CompletedTask;public Task<string?> ThumbnailAsync(string file,string mime)=>Task.FromResult<string?>(null);
}
sealed class Secrets:ISecretStore
{
 readonly ConcurrentDictionary<string,string> values=new();public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}public Task RemoveAsync(string key){values.TryRemove(key,out _);return Task.CompletedTask;}
}
