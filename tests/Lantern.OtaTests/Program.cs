using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;
using Lantern.Windows;

if(args is ["--preferences",var profile]){await PreferenceChecks.RunAsync(Path.GetFullPath(profile));return;}
string root=Path.GetFullPath(args[0]);
var feed=new FakeServers();
var android=new Host("Android",Path.Combine(root,"Android"));
await using var phone=new AppController(android,new ServerUpdateClient(feed));
await phone.InitializeAsync(0);
JsonElement Element(object o)=>JsonSerializer.SerializeToElement(o,AppController.Json);
async Task<JsonElement> Cmd(AppController app,object command)=>Element(await app.DispatchAsync(Element(command)));
async Task<JsonElement> Update(AppController app)=>Element(await app.GetStateAsync()).GetProperty("ota");
async Task WaitPhase(AppController app,string phase)
{
    for(int i=0;i<150;i++)
    {
        var state=await Update(app);
        if(state.GetProperty("phase").GetString()==phase)return;
        await Task.Delay(25);
    }
    throw new Exception($"Expected {phase}: {await Update(app)}");
}
void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
bool Ok(JsonElement result)=>result.GetProperty("ok").GetBoolean();
var check=new{type="ota-check"};
var download=new{type="ota-download"};
var install=new{type="ota-install"};
var clear=new{type="ota-clear"};

Assert(Ok(await Cmd(phone,check)),"Stable channel reports only stable releases");
await WaitPhase(phone,"current");
Assert(!feed.Requests.Any(r=>r.Contains("/download/")),"Current channel never downloads an installer");
Assert(Ok(await Cmd(phone,new{type="ota-options",channel="beta",source="auto"})),"Choose beta and automatic source");
feed.FailGitHub=true;
Assert(Ok(await Cmd(phone,check)),"Gitee works even when GitHub is rate-limited");
var available=await Update(phone);
Assert(available.GetProperty("release").GetProperty("source").GetString()=="gitee","Fallback verified");
Assert(!Ok(await Cmd(phone,install)),"User must first download and verify");
feed.BadPayload=true;
await Cmd(phone,download);
await Task.Delay(100);
await WaitPhase(phone,"available");
Assert(!File.Exists(Path.Combine(android.DataDirectory,"OtaCache","Lantern-Android.apk")),"Changed bytes never become installable");
feed.BadPayload=false;
android.RejectSignature=true;
await Cmd(phone,download);
await Task.Delay(100);
await WaitPhase(phone,"available");
Assert(android.Validations==1&&android.Installs==0,"APK still passes platform signature verification");
android.RejectSignature=false;
await Cmd(phone,download);
await WaitPhase(phone,"ready");
Assert(!Ok(await Cmd(phone,new{type="ota-options",channel="stable",source="auto"})),"A cached beta cannot be installed as stable");
android.CanInstallUpdates=false;
Assert(Ok(await Cmd(phone,install))&&android.PermissionRequests==1&&android.Installs==0,"Android uses its system permission dialog");
android.CanInstallUpdates=true;
Assert(Ok(await Cmd(phone,install))&&android.Installs==1,"Android opens the native installer after validation");
Assert(!((await Update(phone)).ToString().Contains(android.DataDirectory)),"Web state has no private absolute path");

var cachedHost=new Host("Android",Path.Combine(root,"Restart"));
await using(var initialized=new AppController(cachedHost,new ServerUpdateClient(new FakeServers())))
    await initialized.InitializeAsync(0);
var sourceCache=Path.Combine(android.DataDirectory,"OtaCache");
var cache=Directory.CreateDirectory(Path.Combine(cachedHost.DataDirectory,"OtaCache")).FullName;
File.Copy(Path.Combine(sourceCache,"Lantern-Android.apk"),Path.Combine(cache,"Lantern-Android.apk"),true);
File.Copy(Path.Combine(sourceCache,"server-ready.json"),Path.Combine(cache,"server-ready.json"),true);
await File.WriteAllTextAsync(Path.Combine(cachedHost.DataDirectory,"settings.json"),JsonSerializer.Serialize(new AppSettings{UpdateChannel="beta"},AppController.Json));
await using(var restarted=new AppController(cachedHost,new ServerUpdateClient(new FakeServers{FailGitHub=true,FailGitee=true})))
{
    await restarted.InitializeAsync(0);
    await WaitPhase(restarted,"ready");
    Assert(Ok(await Cmd(restarted,install))&&cachedHost.Installs==1,"Verified Android APK survives restart and offline install");
}
Assert(File.Exists(Path.Combine(cache,"Lantern-Android.apk")),"Closing the app retains the verified download");
await Cmd(phone,clear);
feed.FailGitHub=false;
feed.ConflictingHashes=true;
Assert(!Ok(await Cmd(phone,check)),"Same-version hash conflict blocks download");
feed.ConflictingHashes=false;
feed.LowAndroidCode=true;
Assert(!Ok(await Cmd(phone,check)),"Manifest with lower Android versionCode is rejected");
feed.LowAndroidCode=false;
feed.ExternalRedirect=true;
Assert(!Ok(await Cmd(phone,check)),"A redirection to an uncontrolled host is rejected");
feed.ExternalRedirect=false;
var windows=new Host("Windows",Path.Combine(root,"Windows"));
await using var desktop=new AppController(windows,new ServerUpdateClient(feed));
await desktop.InitializeAsync(0);
Assert(Ok(await Cmd(desktop,new{type="ota-options",channel="beta",source="github"})),"Manual GitHub selection works");
Assert(Ok(await Cmd(desktop,check)),"Windows checks public servers with no mobile device");
await Cmd(desktop,download);
await WaitPhase(desktop,"ready");
Assert(Ok(await Cmd(desktop,install))&&windows.OpenedFolders==1&&windows.Installs==0,"Windows just opens the verified ZIP's folder");
var reducer=new TaskbarProgressReducer();TransferNotificationSnapshot Snapshot(params TransferNotificationTask[] t)=>new(false,false,false,t);
var active=new TransferNotificationTask("one","transferring","send",100,25,0);var second=active with{Id="two",Total=300,Completed=75};
Assert(reducer.Update(Snapshot(active,second),true,0)==new TaskbarFrame(TaskbarState.Normal,2500,10000),"Weighted multiple tasks, independent of notifications switch");
Assert(reducer.Update(Snapshot(active with{State="paused"}),true,1).State==TaskbarState.Paused,"Paused yellow");
Assert(reducer.Update(Snapshot(active with{State="saving",Completed=100}),true,2).State==TaskbarState.Indeterminate,"Saving never premature success");
Assert(reducer.Update(Snapshot(active with{State="failed"}),true,3).State==TaskbarState.Error,"Failure red");
Assert(reducer.Update(Snapshot(active with{State="failed"}),true,5004).State==TaskbarState.None,"Error clears after timeout");
Assert(reducer.Update(Snapshot(active),false,5005).State==TaskbarState.None,"Opt out clears");
Assert(reducer.Update(Snapshot(active with{State="completed"}),true,5006).State==TaskbarState.None,"Completion clears");
Console.WriteLine("PASS server OTA: channels, automatic fallback, manual provider, HTTPS origin, checksum conflict, bad bytes, signature rejection, Android rollback and permission, restart/cache, and manual Windows ZIP installation.");

sealed class Host(string platform,string directory):IPlatformHost
{
    public string Platform=>platform;
    public string DeviceName=>"Server OTA fixture";
    public string DataDirectory=>directory;
    public ISecretStore Secrets {get;}=new Secrets();
    public bool CanInstallUpdates {get;set;}=true;
    public bool RejectSignature {get;set;}
    public int Validations,Installs,PermissionRequests,OpenedFolders;
    public Task OpenUpdatePermissionAsync(){PermissionRequests++;return Task.CompletedTask;}
    public Task ValidateUpdateAsync(string file,ServerRelease manifest)
    {
        Validations++;
        if(RejectSignature)throw new CryptographicException("Fixture signature mismatch");
        if(manifest.VersionCode!=41)throw new InvalidDataException("Fixture version code mismatch");
        return Task.CompletedTask;
    }
    public Task InstallUpdateAsync(string file){Installs++;return Task.CompletedTask;}
    public Task<IReadOnlyList<PickedFile>> PickAsync()=>Task.FromResult<IReadOnlyList<PickedFile>>([]);
    public Task OpenFolderAsync(string folder){OpenedFolders++;return Task.CompletedTask;}
    public Task<string?> ThumbnailAsync(string f,string m)=>Task.FromResult<string?>(null);
}

sealed class Secrets:ISecretStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> values=new();
    public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));
    public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}
    public Task RemoveAsync(string key){values.TryRemove(key,out _);return Task.CompletedTask;}
}

sealed class FakeServers : HttpMessageHandler
{
    private static readonly byte[] Payload=[1,3,3,7];
    private static readonly string Checksum=Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    public bool FailGitHub,FailGitee,BadPayload,ConflictingHashes,LowAndroidCode,ExternalRedirect;
    public System.Collections.Concurrent.ConcurrentQueue<string> Requests {get;}=new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var url=request.RequestUri!;
        string route=url.AbsolutePath;
        Requests.Enqueue(url.AbsoluteUri);
        bool github=url.Host.EndsWith("github.com",StringComparison.Ordinal);
        if(github?FailGitHub:FailGitee)return Reply(HttpStatusCode.Forbidden);
        if(route.EndsWith("/releases",StringComparison.Ordinal))
        {
            string releases=github?"https://github.com/altale0218/lantern-releases/releases/download/":"https://gitee.com/altalatic/lantern-releases/releases/download/";
            object Entry(string tag,bool prerelease)=>new
            {
                tag_name=tag,prerelease,draft=false,body="Fixture versionCode "+(LowAndroidCode?1:41),
                assets=new[]{"Lantern-Android.apk","Lantern-Windows.zip","SHA256SUMS.txt"}
                    .Select(asset=>new{name=asset,browser_download_url=releases+tag+"/"+asset,size=github?Payload.Length:(int?)null})
            };
            return Reply(HttpStatusCode.OK,new StringContent(JsonSerializer.Serialize(new[]{Entry("v0.7.6",false),Entry("v0.7.8",true)})));
        }
        if(ExternalRedirect)
        {
            var redirect=new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location=new Uri("https://example.invalid/replacement");
            return Task.FromResult(redirect);
        }
        if(route.EndsWith("SHA256SUMS.txt",StringComparison.Ordinal))
        {
            var hash=ConflictingHashes&&!github?new string('a',64):Checksum;
            return Reply(HttpStatusCode.OK,new StringContent($"{hash}  Lantern-Android.apk\n{hash}  Lantern-Windows.zip\n"));
        }
        return Reply(HttpStatusCode.OK,new ByteArrayContent(BadPayload?[1,3,3,6]:Payload));
    }
    private static Task<HttpResponseMessage> Reply(HttpStatusCode code,HttpContent? body=null)=>
        Task.FromResult(new HttpResponseMessage(code){Content=body});
}

