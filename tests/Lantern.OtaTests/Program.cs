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
int githubCatalogRequests() => feed.Requests.Count(uri=>uri.StartsWith("https://api.github.com/",StringComparison.Ordinal));
int beforeRetry=githubCatalogRequests();
feed.GitHubServiceFailures=1;
Assert(Ok(await Cmd(desktop,check))&&githubCatalogRequests()==beforeRetry+2,"temporary 503 is retried exactly once and recovers");
beforeRetry=githubCatalogRequests();
feed.GitHubTransportFailures=1;
Assert(Ok(await Cmd(desktop,check))&&githubCatalogRequests()==beforeRetry+2,"a dropped connection retries the same validated HTTPS endpoint");
beforeRetry=githubCatalogRequests();
feed.GitHubServiceFailures=2;
Assert(!Ok(await Cmd(desktop,check))&&githubCatalogRequests()==beforeRetry+2,"consecutive transient failures stop after one retry");
beforeRetry=githubCatalogRequests();
feed.FailGitHub=true;
Assert(!Ok(await Cmd(desktop,check))&&githubCatalogRequests()==beforeRetry+1,"a forbidden response is not retried");
feed.FailGitHub=false;
feed.OversizedCatalog=true;
Assert(!Ok(await Cmd(desktop,check)),"the actual catalog length is bounded even with a short length header");
feed.OversizedCatalog=false;
Assert(Ok(await Cmd(desktop,check)),"Windows checks public servers with no mobile device");
await Cmd(desktop,download);
await WaitPhase(desktop,"ready");
Assert(Ok(await Cmd(desktop,install))&&windows.OpenedFolders==1&&windows.Installs==0,"Windows just opens the verified ZIP's folder");
var pagedFeed=new FakeServers{LeadingBetaPages=4};
var pagedHost=new Host("Windows",Path.Combine(root,"Paged"));
await using(var paged=new AppController(pagedHost,new ServerUpdateClient(pagedFeed)))
{
    await paged.InitializeAsync(0);
    foreach(string source in new[]{"gitee","github"})
    {
        Assert(Ok(await Cmd(paged,new{type="ota-options",channel="stable",source})),"Choose a single source for stable update coverage");
        pagedFeed.LeadingBetaPages=4;
        pagedFeed.Requests.Clear();
        Assert(!Ok(await Cmd(paged,check)),"Exhausting the bounded catalog is reported as an incomplete check");
        Assert((await Update(paged)).GetProperty("sources")[0].GetProperty("status").GetString()=="limited","No false claim of current or empty after four full pages");
        Assert(pagedFeed.Requests.Count(uri=>uri.Contains("/releases?per_page=30&page="))==4&&pagedFeed.Requests.All(uri=>!uri.Contains("/download/")),"A check reads at most four catalog pages and no package");
        pagedFeed.LeadingBetaPages=1;
        pagedFeed.Requests.Clear();
        Assert(Ok(await Cmd(paged,check)),"Stable release is found on page two behind a full page of betas");
        var stable=(await Update(paged)).GetProperty("release");
        Assert(stable.GetProperty("channel").GetString()=="stable"&&stable.GetProperty("source").GetString()==source,"The correct stable channel and source are retained");
        var catalogRequests=pagedFeed.Requests.Where(uri=>uri.Contains("/releases?",StringComparison.Ordinal)).ToArray();
        Assert(catalogRequests.Length==2&&catalogRequests[1].Contains("&page=2",StringComparison.Ordinal),"Pagination ends when a newer release is found");
        if(source=="gitee")Assert(catalogRequests.All(uri=>uri.EndsWith("&direction=desc",StringComparison.Ordinal)),"Gitee requests newest releases first explicitly");
    }
    Assert(Ok(await Cmd(paged,new{type="ota-options",channel="beta",source="gitee"})),"Beta remains independently selectable");
    pagedFeed.Requests.Clear();
    Assert(Ok(await Cmd(paged,check))&&pagedFeed.Requests.Count(uri=>uri.Contains("/releases?",StringComparison.Ordinal))==1,"A newer beta needs only the first page");
    pagedFeed.BlockReleaseApi=true;
    pagedFeed.PublishStaticCatalog=true;
    pagedFeed.Requests.Clear();
    Assert(Ok(await Cmd(paged,check))&&(await Update(paged)).GetProperty("phase").GetString()=="available","Gitee reads the published catalog when the API is rate limited");
    Assert(pagedFeed.Requests.Contains("https://raw.giteeusercontent.com/altalatic/lantern-releases/raw/lantern-update-catalog/gitee/beta.json"),"Fallback uses the fixed Gitee CDN and channel path without a redirect");
    Assert(Ok(await Cmd(paged,new{type="ota-options",channel="beta",source="github"})),"Select GitHub fallback");
    Assert(Ok(await Cmd(paged,check))&&(await Update(paged)).GetProperty("release").GetProperty("source").GetString()=="github","GitHub fallback retains the chosen source and checks the package checksum");
    pagedFeed.StaticCatalogRedirect=true;
    pagedFeed.Requests.Clear();
    Assert(!Ok(await Cmd(paged,check))&&pagedFeed.Requests.All(uri=>!uri.Contains("different-project",StringComparison.Ordinal)),"A static catalog never follows a redirect");
    pagedFeed.StaticCatalogRedirect=false;
    Assert(Ok(await Cmd(paged,new{type="ota-options",channel="stable",source="github"})),"Select independently published stable catalog");
    Assert(Ok(await Cmd(paged,check))&&(await Update(paged)).GetProperty("phase").GetString()=="idle","An empty stable catalog never exposes a beta release");
    Assert(Ok(await Cmd(paged,new{type="ota-options",channel="beta",source="github"})),"Choose beta for the stale listing case");
    pagedFeed.BlockReleaseApi=false;
    pagedFeed.StaleReleaseApi=true;
    pagedFeed.Requests.Clear();
    var staleCheck=await Cmd(paged,check);
    var afterStale=await Update(paged);
    Assert(Ok(staleCheck)&&afterStale.GetProperty("phase").GetString()=="available"&&
        Version.Parse(afterStale.GetProperty("release").GetProperty("version").GetString()!)>Version.Parse(AppRelease.Version),
        "A successful but stale release API cannot hide the newer static beta");
    Assert(pagedFeed.Requests.Any(uri=>uri.Contains("/lantern-update-catalog/github/beta.json",StringComparison.Ordinal)),
        "Cross-check the selected channel at the fixed official endpoint");
    pagedFeed.PublishStaticCatalog=false;
    Assert(Ok(await Cmd(paged,check))&&(await Update(paged)).GetProperty("phase").GetString()=="current",
        "A missing static catalog leaves a complete and current primary listing usable");
}
var reducer=new TaskbarProgressReducer();TransferNotificationSnapshot Snapshot(params TransferNotificationTask[] t)=>new(false,false,false,t);
var active=new TransferNotificationTask("one","transferring","send",100,25,0);var second=active with{Id="two",Total=300,Completed=75};
Assert(reducer.Update(Snapshot(active,second),true,0)==new TaskbarFrame(TaskbarState.Normal,2500,10000),"Weighted multiple tasks, independent of notifications switch");
Assert(reducer.Update(Snapshot(active with{State="paused"}),true,1).State==TaskbarState.Paused,"Paused yellow");
Assert(reducer.Update(Snapshot(active with{State="saving",Completed=100}),true,2).State==TaskbarState.Indeterminate,"Saving never premature success");
Assert(reducer.Update(Snapshot(active with{State="failed"}),true,3).State==TaskbarState.Error,"Failure red");
Assert(reducer.Update(Snapshot(active with{State="failed"}),true,5004).State==TaskbarState.None,"Error clears after timeout");
Assert(reducer.Update(Snapshot(active),false,5005).State==TaskbarState.None,"Opt out clears");
Assert(reducer.Update(Snapshot(active with{State="completed"}),true,5006).State==TaskbarState.None,"Completion clears");
Console.WriteLine("PASS server OTA: channels, bounded catalog paging, newest-first Gitee, static catalog during API limits, automatic fallback, manual provider, HTTPS origin, checksum conflict, bad bytes, signature rejection, Android rollback and permission, restart/cache, and manual Windows ZIP installation.");

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
        if(manifest.VersionCode!=FakeServers.NextAndroidCode)throw new InvalidDataException("Fixture version code mismatch");
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
    internal const int NextAndroidCode=AppRelease.AndroidCode+1;
    private static readonly Version CurrentVersion=Version.Parse(AppRelease.Version);
    private static readonly Version NextVersion=new(CurrentVersion.Major,CurrentVersion.Minor,CurrentVersion.Build+1);
    private static readonly byte[] Payload=[1,3,3,7];
    private static readonly string Checksum=Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    public bool FailGitHub,FailGitee,BadPayload,ConflictingHashes,LowAndroidCode,ExternalRedirect,OversizedCatalog;
    public bool BlockReleaseApi,PublishStaticCatalog,StaticCatalogRedirect,StaleReleaseApi;
    public int GitHubServiceFailures,GitHubTransportFailures,LeadingBetaPages;
    public System.Collections.Concurrent.ConcurrentQueue<string> Requests {get;}=new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var url=request.RequestUri!;
        string route=url.AbsolutePath;
        Requests.Enqueue(url.AbsoluteUri);
        bool github=url.Host is "api.github.com" or "github.com" or "raw.githubusercontent.com";
        string releases=github?"https://github.com/altale0218/lantern-releases/releases/download/":"https://gitee.com/altalatic/lantern-releases/releases/download/";
        object Entry(string tag,bool prerelease)=>new
        {
            tag_name=tag,prerelease,draft=false,body="Fixture versionCode "+(LowAndroidCode?1:NextAndroidCode),
            assets=new[]{"Lantern-Android.apk","Lantern-Windows.zip","SHA256SUMS.txt"}
                .Select(asset=>new{name=asset,browser_download_url=releases+tag+"/"+asset,size=github?Payload.Length:(int?)null})
        };
        if(route.Contains("/lantern-update-catalog/",StringComparison.Ordinal))
        {
            if(!PublishStaticCatalog)return Reply(HttpStatusCode.NotFound);
            if(StaticCatalogRedirect)
            {
                var redirect=new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location=new Uri(url,"/different-project/beta.json");
                return Task.FromResult(redirect);
            }
            return Reply(HttpStatusCode.OK,new StringContent(route.EndsWith("/stable.json",StringComparison.Ordinal)?"[]":
                JsonSerializer.Serialize(new[]{Entry("v"+NextVersion,true)})));
        }
        if(github?FailGitHub:FailGitee)return Reply(HttpStatusCode.Forbidden);
        if(route.EndsWith("/releases",StringComparison.Ordinal))
        {
            if(BlockReleaseApi)return Reply(HttpStatusCode.Forbidden);
            if(StaleReleaseApi)return Reply(HttpStatusCode.OK,new StringContent(JsonSerializer.Serialize(new[]{Entry("v"+CurrentVersion,true)})));
            if(OversizedCatalog)
            {
                var invalid=new ByteArrayContent(new byte[2*1024*1024+1]);
                invalid.Headers.ContentLength=16;
                return Reply(HttpStatusCode.OK,invalid);
            }
            if(github&&GitHubTransportFailures>0)
            {
                GitHubTransportFailures--;
                throw new HttpRequestException(HttpRequestError.ResponseEnded,"The simulated connection ended early");
            }
            if(github&&GitHubServiceFailures>0)
            {
                GitHubServiceFailures--;
                return Reply(HttpStatusCode.ServiceUnavailable);
            }
            if(LeadingBetaPages>0)
            {
                var query=url.Query.TrimStart('?').Split('&');
                string? pageValue=query.FirstOrDefault(part=>part.StartsWith("page=",StringComparison.Ordinal));
                if(pageValue is null||!int.TryParse(pageValue[5..],out int page)||!query.Contains("per_page=30")||(!github&&!query.Contains("direction=desc")))
                    return Reply(HttpStatusCode.BadRequest);
                string Tag(int increment)=>"v"+new Version(CurrentVersion.Major,CurrentVersion.Minor,CurrentVersion.Build+increment);
                var rows=page<=LeadingBetaPages?Enumerable.Range(2+30*(LeadingBetaPages-page),30).Reverse().Select(increment=>Entry(Tag(increment),true)):
                    new[]{Entry(Tag(1),false)};
                return Reply(HttpStatusCode.OK,new StringContent(JsonSerializer.Serialize(rows)));
            }
            return Reply(HttpStatusCode.OK,new StringContent(JsonSerializer.Serialize(new[]{Entry("v"+CurrentVersion,false),Entry("v"+NextVersion,true)})));
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

