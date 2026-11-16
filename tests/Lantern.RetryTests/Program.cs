using Lantern.AppLogic;
using Lantern.Core;
using System.Text.Json;
void Assert(bool ok,string text){if(!ok)throw new Exception(text);}
foreach(string platform in new[]{"Windows","Android"})
{
    string directory=Path.Combine(Path.GetFullPath(args[0]),platform);Directory.CreateDirectory(directory);
    string source=Path.Combine(directory,"sample.bin");File.WriteAllBytes(source,[1,2,3]);
    var host=new Host(platform,directory,source);
    var peer=new DeviceIdentity(Guid.NewGuid().ToString("N"),"Peer","Windows");
    var store=new TrustStore(host.Secrets);await store.SaveAsync(new(peer,Convert.ToBase64String(new byte[32]),DateTimeOffset.Now));
    UiTask[] history=[new(){Id="failed",PeerId=peer.Id,Direction="send",State="failed",Files=[new(){Name="sample.bin",Size=3,LocalPath=platform=="Windows"?source:null}]},new(){Id="active",PeerId=peer.Id,Direction="send",State="transferring"},new(){Id="receive",PeerId=peer.Id,Direction="receive",State="failed"}];
    await File.WriteAllTextAsync(Path.Combine(directory,"history.json"),JsonSerializer.Serialize(history,AppController.Json));
    await using var controller=new AppController(host);await controller.InitializeAsync(0);
    async Task<JsonElement> Retry(string id){using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new{type="prepare-retry",id}));return JsonSerializer.SerializeToElement(await controller.DispatchAsync(doc.RootElement),AppController.Json);}
    var result=await Retry("failed");Assert(result.GetProperty("ok").GetBoolean(),"Prepare retry");Assert(result.GetProperty("peerId").GetString()==peer.Id&&result.GetProperty("selection").GetArrayLength()==1,"Target and files retained");
    var state=JsonSerializer.SerializeToElement(await controller.GetStateAsync(),AppController.Json);Assert(state.GetProperty("tasks").GetArrayLength()==3,"Preparation never sends or removes old task");Assert(!state.ToString().Contains(source),"Native source paths never enter UI state");
    // Startup recovery intentionally marks interrupted persisted tasks failed.
    // Exercise the live-state guard after recovery instead of asserting against an old snapshot.
    var live=(System.Collections.Concurrent.ConcurrentDictionary<string,UiTask>)typeof(AppController).GetField("tasks",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(controller)!;
    Assert(live["active"].State=="failed","Interrupted persisted task becomes retryable after restart");live["active"].State="transferring";
    Assert(!(await Retry("active")).GetProperty("ok").GetBoolean(),"Active task retry rejected");Assert(!(await Retry("receive")).GetProperty("ok").GetBoolean(),"Receiver cannot read remote sender sources");
    File.WriteAllBytes(source,[1,2,3,4]);Assert(!(await Retry("failed")).GetProperty("ok").GetBoolean(),"Changed sources rejected");File.Delete(source);Assert(!(await Retry("failed")).GetProperty("ok").GetBoolean(),"Missing sources rejected");
    await store.ForgetAsync(peer.Id);Assert(!(await Retry("failed")).GetProperty("ok").GetBoolean(),"Revoked trust rejected");
    Console.WriteLine($"PASS {platform}: failed-task retry preparation, source reopening, file/target confirmation, no implicit send, active/receive rejection, changed/missing files and revoked trust protection");
}
sealed class Host(string platform,string directory,string source):IPlatformHost
{
    public string Platform=>platform;public string DataDirectory=>directory;public string DeviceName=>"Retry test";public ISecretStore Secrets{get;}=new Secrets();
    public Task<IReadOnlyList<PickedFile>> PickAsync()=>RestoreSourcesAsync([source]);
    public Task<IReadOnlyList<PickedFile>> RestoreSourcesAsync(IReadOnlyList<string> paths)=>Task.FromResult<IReadOnlyList<PickedFile>>(paths.Select(path=>new PickedFile(new(Path.GetFileName(path),new FileInfo(path).Length,"application/octet-stream",()=>Task.FromResult<Stream>(File.OpenRead(path))),null,platform=="Windows"?path:null)).ToArray());
    public Task OpenFolderAsync(string folder)=>Task.CompletedTask;public Task<string?> ThumbnailAsync(string file,string mime)=>Task.FromResult<string?>(null);
}
sealed class Secrets:ISecretStore
{
    readonly Dictionary<string,string> values=[];public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}public Task RemoveAsync(string key){values.Remove(key);return Task.CompletedTask;}
}
