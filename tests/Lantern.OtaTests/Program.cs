using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;
using Lantern.Windows;

void Assert(bool value,string text){if(!value)throw new Exception(text);}
if(args[0]=="--preferences"){await PreferenceChecks.RunAsync(Path.GetFullPath(args[1]));return;}
if(args[0]=="--startup"){await StartupChecks.RunAsync(Path.GetFullPath(args[1]));return;}
if(args[0]=="--native-ota")
{
    var host=new Host("Android",Path.GetFullPath(args[1]));
    await using var client=new AppController(host);await client.InitializeAsync(0);
    var service=(LanService)typeof(AppController).GetField("service",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(client)!;
    service.ConfirmPairing=(_,_)=>Task.FromResult(true);
    var connection=await service.ConnectAsync("127.0.0.1",LanService.DefaultPort,args[2],null,CancellationToken.None);
    string id=connection.Peer.Id;
    await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},CancellationToken.None);
    async Task<JsonElement> Cmd(object o)=>JsonSerializer.SerializeToElement(await client.DispatchAsync(JsonSerializer.SerializeToElement(o,AppController.Json)),AppController.Json);
    Assert((await Cmd(new{type="ota-check",peerId=id})).GetProperty("ok").GetBoolean(),"Native Windows OTA check");
    var state=JsonSerializer.SerializeToElement(await client.GetStateAsync(),AppController.Json);
    Assert(state.GetProperty("ota").GetProperty("phase").GetString()=="available","Native hosted release offered");
    await Cmd(new{type="ota-download"});
    for(int i=0;i<600;i++)
    {
        state=JsonSerializer.SerializeToElement(await client.GetStateAsync(),AppController.Json);
        var phase=state.GetProperty("ota").GetProperty("phase").GetString();
        if(phase=="ready"){Console.WriteLine("NATIVE_OTA_VERIFIED "+new FileInfo(Path.Combine(host.DataDirectory,"OtaCache","update.apk")).Length);return;}
        if(phase=="failed")throw new Exception(state.GetProperty("ota").GetProperty("message").GetString());
        await Task.Delay(100);
    }
    throw new Exception("Native OTA download timeout");
}
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
var windows=new Host("Windows",Path.Combine(root,"Windows"));var android=new Host("Android",Path.Combine(root,"Android"));
await using var pc=new AppController(windows);await pc.InitializeAsync(0);
await using var phone=new AppController(android);await phone.InitializeAsync(0);
LanService Service(AppController c)=>(LanService)typeof(AppController).GetField("service",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(c)!;
JsonElement Element(object o)=>JsonSerializer.SerializeToElement(o,AppController.Json);
async Task<JsonElement> Command(AppController c,object o)=>Element(await c.DispatchAsync(Element(o)));
async Task<JsonElement> State()=>Element(await phone.GetStateAsync());
async Task<string> Phase()=> (await State()).GetProperty("ota").GetProperty("phase").GetString()!;
async Task WaitFinished(){for(int i=0;i<200;i++){if(await Phase() is not("downloading" or "verifying")){return;}await Task.Delay(30);}throw new Exception("OTA timeout");}
async Task Pair()
{
    var code=(await Command(pc,new{type="open-pairing"})).GetProperty("code").GetString();
    var oldPc=Service(pc).ConfirmPairing;var oldPhone=Service(phone).ConfirmPairing;Service(pc).ConfirmPairing=Service(phone).ConfirmPairing=(_,_)=>Task.FromResult(true);
    var connected=await Service(phone).ConnectAsync("127.0.0.1",Service(pc).Port,code,null,CancellationToken.None);
    await using(connected.Channel)await connected.Channel.SendJsonAsync(new{PairOnly=true},CancellationToken.None);
    Service(pc).ConfirmPairing=oldPc;Service(phone).ConfirmPairing=oldPhone;
}
await Pair();var pcId=Element(await pc.GetStateAsync()).GetProperty("self").GetProperty("id").GetString()!;
var check=new{type="ota-check",peerId=pcId};
Assert((await Command(phone,check)).GetProperty("ok").GetBoolean()&&await Phase()=="idle","No package check");
string apk=Path.Combine(root,"payload.apk");await File.WriteAllBytesAsync(apk,RandomNumberGenerator.GetBytes(3*1024*1024));
var manifest=new OtaManifest(1,AppRelease.PackageId,AppRelease.Version,AppRelease.AndroidCode,new FileInfo(apk).Length,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(apk))),"更新日志：测试加密 OTA");
windows.Package=new(manifest,apk);
Assert((await Command(phone,check)).GetProperty("ok").GetBoolean()&&await Phase()=="available","New authenticated version");
Assert(!(await Command(phone,new{type="ota-install",path=apk})).GetProperty("ok").GetBoolean(),"No installation before verified download");
windows.Package=new(manifest with{Notes="Changed"},apk);
await Command(phone,new{type="ota-download"});await WaitFinished();Assert(await Phase()=="failed","Manifest changed since check rejected");
windows.Package=new(manifest,apk);await Command(phone,check);
await Command(phone,new{type="ota-download",path="C:/Windows/notepad.exe"});await WaitFinished();
Assert(await Phase()=="ready"&&android.Validations>0,"Verified download becomes installable");
string local=Path.Combine(android.DataDirectory,"OtaCache","update.apk");Assert(File.ReadAllBytes(apk).SequenceEqual(File.ReadAllBytes(local)),"Downloaded APK SHA256 matches");
android.CanInstallUpdates=false;
Assert((await Command(phone,new{type="ota-install-permission"})).GetProperty("ok").GetBoolean()&&android.PermissionRequests==1,"Guide opens Android installation permission");
Assert((await Command(phone,new{type="ota-install"})).GetProperty("ok").GetBoolean()&&android.PermissionRequests==2&&android.Installs==0&&File.Exists(local),"Denied installation permission retains verified package");
android.CanInstallUpdates=true;
Assert((await Command(phone,new{type="ota-install"})).GetProperty("ok").GetBoolean()&&android.Installs==1,"Explicit user installation command");
Assert(!(await State()).ToString().Contains(local),"Private update path not exposed in bridge");
var restartedHost=new Host("Android",Path.Combine(root,"AndroidRestart"));
var restartedCache=Directory.CreateDirectory(Path.Combine(restartedHost.DataDirectory,"OtaCache")).FullName;
File.Copy(local,Path.Combine(restartedCache,"update.apk"),true);
File.Copy(Path.Combine(Path.GetDirectoryName(local)!,"ready.json"),Path.Combine(restartedCache,"ready.json"),true);
await using(var restarted=new AppController(restartedHost))
{
    await restarted.InitializeAsync(0);
    var restored=Element(await restarted.GetStateAsync()).GetProperty("ota");
    Assert(restored.GetProperty("phase").GetString()=="ready"&&restartedHost.Validations>0,"Verified cache is restored without connected PC");
    Assert((await Command(restarted,new{type="ota-install"})).GetProperty("ok").GetBoolean()&&restartedHost.Installs==1,"Cached package installs after restart");
}
Assert(File.Exists(Path.Combine(restartedCache,"update.apk")),"Closing controller retains verified APK");
await Command(phone,new{type="ota-clear"});Assert(!File.Exists(local)&&await Phase()=="idle","Delete cached package");
android.RejectSignature=true;await Command(phone,check);await Command(phone,new{type="ota-download"});await WaitFinished();
Assert(await Phase()=="failed"&&!File.Exists(local),"Platform signature rejection never installs or retains package");android.RejectSignature=false;
windows.Package=new(manifest with{Sha256=new string('0',64)},apk);await Command(phone,check);await Command(phone,new{type="ota-download"});await WaitFinished();
Assert(await Phase()=="failed"&&!File.Exists(local),"Advertised hash mismatch rejected");
windows.Package=new(manifest with{VersionCode=18},apk);await Command(phone,check);Assert(await Phase()=="current","Same/older code not offered");
windows.Package=new(manifest,apk);await Command(phone,check);await Command(phone,new{type="forget",id=pcId});
await Command(phone,new{type="ota-download"});await WaitFinished();Assert(await Phase()=="failed","Revoked trust blocks download");
foreach(var bad in new[]{manifest with{PackageId="other.app"},manifest with{Size=OtaManifest.MaxBytes+1},manifest with{Sha256="invalid"},manifest with{Protocol=2}})
{bool rejected=false;try{bad.Validate();}catch(InvalidDataException){rejected=true;}Assert(rejected,"Bad manifest rejected");}
var reducer=new TaskbarProgressReducer();TransferNotificationSnapshot Snapshot(params TransferNotificationTask[] t)=>new(false,false,false,t);
var active=new TransferNotificationTask("one","transferring","send",100,25,0);var second=active with{Id="two",Total=300,Completed=75};
Assert(reducer.Update(Snapshot(active,second),true,0)==new TaskbarFrame(TaskbarState.Normal,2500,10000),"Weighted multiple tasks, independent of notifications switch");
Assert(reducer.Update(Snapshot(active with{State="paused"}),true,1).State==TaskbarState.Paused,"Paused yellow");
Assert(reducer.Update(Snapshot(active with{State="saving",Completed=100}),true,2).State==TaskbarState.Indeterminate,"Saving never premature success");
Assert(reducer.Update(Snapshot(active with{State="failed"}),true,3).State==TaskbarState.Error,"Failure red");
Assert(reducer.Update(Snapshot(active with{State="failed"}),true,5004).State==TaskbarState.None,"Error clears after timeout");
Assert(reducer.Update(Snapshot(active),false,5005).State==TaskbarState.None,"Opt out clears");
Assert(reducer.Update(Snapshot(active with{State="completed"}),true,5006).State==TaskbarState.None,"Completion clears");
Console.WriteLine("PASS OTA real TCP/PAKE: no package, encrypted check/download, changed manifest, byte integrity, platform signature gate, explicit installation, private path isolation, cache deletion, downgrade/malformed manifest and revoked trust rejection. PASS taskbar multi-task/paused/saving/error/timeout/opt-out/completion policy.");

sealed class Host(string platform,string directory):IPlatformHost
{
    public string Platform=>platform;public string DeviceName=>"OTA test";public string DataDirectory=>directory;public ISecretStore Secrets{get;}=new Secrets();
    public int Code=18;public int ApplicationVersionCode=>Code;public OtaPackage? Package;public int Validations,Installs,PermissionRequests;public bool RejectSignature;
    public bool CanInstallUpdates {get;set;}=true;
    public Task OpenUpdatePermissionAsync(){PermissionRequests++;return Task.CompletedTask;}
    public Task<OtaPackage?> GetHostedUpdateAsync(CancellationToken ct)=>Task.FromResult(Package);
    public Task ValidateUpdateAsync(string file,OtaManifest m){Validations++;if(RejectSignature)throw new IOException("Test platform: signature mismatch");return Task.CompletedTask;}
    public Task InstallUpdateAsync(string file){Installs++;return Task.CompletedTask;}
    public Task<IReadOnlyList<PickedFile>> PickAsync()=>Task.FromResult<IReadOnlyList<PickedFile>>([]);
    public Task OpenFolderAsync(string f)=>Task.CompletedTask;public Task<string?> ThumbnailAsync(string f,string m)=>Task.FromResult<string?>(null);
}
sealed class Secrets:ISecretStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> values=new();
    public Task<string?> GetAsync(string k)=>Task.FromResult(values.GetValueOrDefault(k));public Task SetAsync(string k,string v){values[k]=v;return Task.CompletedTask;}public Task RemoveAsync(string k){values.TryRemove(k,out _);return Task.CompletedTask;}
}
