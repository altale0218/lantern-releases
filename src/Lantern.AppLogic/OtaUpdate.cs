using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lantern.AppLogic;

public static class AppRelease
{
    public const string Version="0.7.15";
    public const int AndroidCode=48;
    public const string PackageId="com.lantern.transfer";
    public const string Notes="0.7.15\n• 放大常用导航文字、传输状态和隐私链接，保持深浅主题和窄窗口的布局。\n• 更新源短时断连或暂不可用时安全重试一次，保留原有服务器、校验和签名规则。\n• 分段读取更新目录和摘要，减少正常检查时的临时内存分配。";
}

public record OtaView(string Phase="idle",string Message="",ServerRelease? Release=null,long Completed=0,double Speed=0,object? Sources=null);

public sealed partial class AppController
{
    private bool releaseUnseen;
    private async Task AcknowledgeReleaseAsync()
    {await host.Secrets.SetAsync("lantern.release.seen",AppRelease.Version);releaseUnseen=false;}

    private readonly ServerUpdateClient serverUpdates=updateClient??new();
    private OtaView ota=new();
    private readonly SemaphoreSlim otaGate=new(1);
    private CancellationTokenSource? otaCancel;
    private CancellationTokenSource? startupUpdateCancel;
    private Task startupUpdateJob=Task.CompletedTask;
    private bool SupportsServerUpdates=>host.Platform is "Android" or "Windows";
    private string UpdateRoot=>Path.Combine(host.DataDirectory,"OtaCache");
    private string UpdateFile=>Path.Combine(UpdateRoot,host.Platform=="Android"?"Lantern-Android.apk":"Lantern-Windows.zip");
    private string UpdateReady=>Path.Combine(UpdateRoot,"server-ready.json");

    private void StartStartupUpdateCheck()
    {
        if(!SupportsServerUpdates||ota.Phase=="ready")return;
        var cancel=CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        startupUpdateCancel=cancel;
        startupUpdateJob=Task.Run(async()=>
        {
            try
            {
                await Task.Delay(600,cancel.Token);
                await CheckUpdateAsync(automatic:true,cancel.Token);
            }
            catch(OperationCanceledException) when(cancel.IsCancellationRequested) { }
            finally{cancel.Dispose();}
        });
    }

    private async Task StopStartupUpdateCheckAsync()
    {
        try{startupUpdateCancel?.Cancel();}catch(ObjectDisposedException) { }
        await startupUpdateJob.ConfigureAwait(false);
    }

    private async Task InitializeOtaAsync()
    {
        if(!SupportsServerUpdates||!Directory.Exists(UpdateRoot))return;
        try
        {
            // Legacy local-PC packages are not part of the server feed.
            if(host.Platform=="Android")
            {
                File.Delete(Path.Combine(UpdateRoot,"ready.json"));
                File.Delete(Path.Combine(UpdateRoot,"update.apk"));
            }
            foreach(string partial in Directory.EnumerateFiles(UpdateRoot,"server-*"))
                if(partial.EndsWith(".partial",StringComparison.Ordinal)||partial.EndsWith(".apk",StringComparison.Ordinal)||partial.EndsWith(".zip",StringComparison.Ordinal))DeletePartial(partial);
            if(!File.Exists(UpdateReady)||!File.Exists(UpdateFile)){ClearUpdateFiles();return;}
            if(new FileInfo(UpdateReady).Length>32*1024)throw new InvalidDataException("更新缓存清单过大");
            var cached=JsonSerializer.Deserialize<ServerRelease>(await File.ReadAllTextAsync(UpdateReady,life.Token),Json);
            if(cached is null||!cached.IsValid()||cached.Platform!=host.Platform||cached.Channel!=settings.UpdateChannel||
                Version.Parse(cached.Version)<=Version.Parse(AppRelease.Version))
            {
                ClearUpdateFiles();
                return;
            }
            await ValidateUpdateAsync(UpdateFile,cached,life.Token);
            ota=new("ready","已校验的安装包可继续使用",cached,cached.Size);
        }
        catch(OperationCanceledException) when(life.IsCancellationRequested) { }
        catch(Exception ex) when(ex is InvalidDataException or JsonException or CryptographicException)
        {
            try{ClearUpdateFiles();}
            catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException)
            {ota=new("failed","无法删除无效的更新缓存");}
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {ota=new("failed","暂时无法读取更新缓存，请稍后重试");}
    }

    private static void DeletePartial(string path)
    {
        try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}
    }

    private void ClearUpdateFiles()
    {
        File.Delete(UpdateReady);
        File.Delete(UpdateFile);
    }

    private async Task<object> SetUpdateOptionsAsync(string channel,string source)
    {
        if(!SupportsServerUpdates||channel is not ("stable" or "beta")||source is not ("auto" or "github" or "gitee"))
            throw new InvalidDataException("请选择有效的更新渠道与来源");
        await StopStartupUpdateCheckAsync();
        if(!otaGate.Wait(0))throw new IOException("更新操作正在进行");
        try
        {
            if(ota.Phase=="ready")throw new IOException("请先安装或删除已校验的安装包");
            settings.UpdateChannel=channel;
            settings.UpdateSource=source;
            await SaveSettingsAsync();
            ota=new();
            return new{ok=true};
        }
        finally{otaGate.Release();}
    }

    private async Task<object> CheckUpdateAsync(bool automatic=false,CancellationToken token=default)
    {
        if(!SupportsServerUpdates)throw new IOException("请通过本平台的应用商店查看更新");
        if(!automatic)await StopStartupUpdateCheckAsync();
        if(!otaGate.Wait(0))return new{ok=false,message="更新操作正在进行"};
        var previous=ota;
        try
        {
            if(ota.Phase=="ready")return new{ok=true,message="已有已校验安装包"};
            ota=new("checking","正在检查发布服务器");
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(life.Token,token);
            int? installedCode=host.Platform=="Android"?host.ApplicationVersionCode:null;
            var check=await serverUpdates.CheckAsync(host.Platform,settings.UpdateChannel,settings.UpdateSource,Version.Parse(AppRelease.Version),installedCode,linked.Token);
            var statuses=check.Sources.Select(p=>new{id=p.Id,status=p.Status}).ToArray();
            if(check.Release is {} release)
                ota=new("available","发现新版，请阅读更新日志后确认下载",release,Sources:statuses);
            else if(check.Sources.Any(p=>p.Status=="conflict"))
                ota=new("failed","各来源的同版安装包校验信息不一致，已暂停下载",Sources:statuses);
            else if(check.Sources.Any(p=>p.Status=="current"))
                ota=new("current","当前版本已是所选服务器提供的最新版",Sources:statuses);
            else if(check.Sources.Any(p=>p.Status=="empty"))
                ota=new("idle","所选渠道暂无发行版",Sources:statuses);
            else
                ota=new("failed","暂时无法读取更新服务器，请检查网络后重试",Sources:statuses);
            return new{ok=ota.Phase!="failed"};
        }
        catch(OperationCanceledException){ota=previous;return new{ok=false};}
        finally{otaGate.Release();}
    }

    private object StartUpdateDownload()
    {
        if(!SupportsServerUpdates||!otaGate.Wait(0))throw new IOException("更新操作正在进行");
        if(ota is not {Phase:"available",Release:{} release})
        {
            otaGate.Release();
            throw new IOException("请先检查可用更新");
        }
        var selected=ota;
        CancelSpeedForTransfer();
        var cancel=CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        otaCancel=cancel;
        ota=selected with{Phase="downloading",Message="正在从服务器下载"};
        string id="ota-"+Guid.NewGuid().ToString("N");
        var job=Task.Run(async()=>
        {
            try{await DownloadUpdateAsync(selected,release,cancel.Token);}
            finally{otaCancel=null;cancel.Dispose();otaGate.Release();}
        });
        outgoing[id]=job;
        _=job.ContinueWith(_=>outgoing.TryRemove(id,out var ignored),TaskScheduler.Default);
        return new{ok=true};
    }

    private async Task DownloadUpdateAsync(OtaView selected,ServerRelease release,CancellationToken token)
    {
        string id=Guid.NewGuid().ToString("N");
        string package=Path.Combine(UpdateRoot,"server-"+id+Path.GetExtension(release.FileName)+".partial");
        string marker=Path.Combine(UpdateRoot,"server-"+id+".json.partial");
        try
        {
            Directory.CreateDirectory(UpdateRoot);
            // Android's package parser expects the APK extension on the file under validation.
            var timer=Stopwatch.StartNew();
            long lastUi=0;
            await serverUpdates.DownloadAsync(release,package,completed=>
            {
                long now=timer.ElapsedMilliseconds;
                if(now-lastUi<170&&completed!=release.Size)return;
                lastUi=now;
                ota=selected with{Phase="downloading",Message="正在从服务器下载",Completed=completed,
                    Speed=completed/Math.Max(.2,timer.Elapsed.TotalSeconds)};
            },token);
            ota=selected with{Phase="verifying",Message="正在验证安装包",Completed=release.Size};
            string verified=Path.ChangeExtension(package,null);
            try
            {
                File.Move(package,verified);
                if(host.Platform=="Android")await host.ValidateUpdateAsync(verified,release);
                await File.WriteAllTextAsync(marker,JsonSerializer.Serialize(release,Json),token);
                token.ThrowIfCancellationRequested();
                File.Move(verified,UpdateFile,true);
                File.Move(marker,UpdateReady,true);
            }
            finally{DeletePartial(verified);}
            ota=selected with{Phase="ready",Message="安装包已校验，可稍后使用",Completed=release.Size,Speed=0};
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or HttpRequestException or CryptographicException or UnauthorizedAccessException or OperationCanceledException)
        {
            ota=selected with{Message=ex is OperationCanceledException?"下载已取消，可以重试":
                ex is CryptographicException or InvalidDataException?"校验失败，请重新检查更新或切换来源":"下载失败，请检查网络或存储空间",
                Completed=0,Speed=0};
        }
        finally{DeletePartial(package);DeletePartial(marker);}
    }

    private async Task ValidateUpdateAsync(string file,ServerRelease release,CancellationToken token)
    {
        await release.VerifyFileAsync(file,token);
        if(host.Platform=="Android")await host.ValidateUpdateAsync(file,release);
    }

    private async Task<object> InstallUpdateAsync()
    {
        if(!SupportsServerUpdates||!otaGate.Wait(0))throw new IOException("更新操作正在进行");
        try
        {
            if(ota is not {Phase:"ready",Release:{} release})throw new IOException("没有已校验的更新包");
            if(host.Platform=="Android"&&!host.CanInstallUpdates)
            {
                await host.OpenUpdatePermissionAsync();
                return new{ok=true,message="授权后返回更新中心，再点击安装"};
            }
            await ValidateUpdateAsync(UpdateFile,release,life.Token);
            if(host.Platform=="Windows")
            {
                await host.OpenFolderAsync(UpdateRoot);
                return new{ok=true,message="请解压已校验的 Windows ZIP 后手动更新"};
            }
            await host.InstallUpdateAsync(UpdateFile);
            return new{ok=true,message="请在系统安装界面确认"};
        }
        finally{otaGate.Release();}
    }
}

