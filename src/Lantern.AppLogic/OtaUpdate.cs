using System.Security.Cryptography;
using System.Text.Json;
using Lantern.Core;

namespace Lantern.AppLogic;

public static class AppRelease
{
    public const string Version="0.7.4";
    public const int AndroidCode=37;
    public const string PackageId="com.lantern.transfer";
    public const string Notes="0.7.4\n• 大文件加密传输减少内存分配，沿用已配对设备身份验证与 SHA-256 校验。\n• 减少启动时的重复读取和写入，优化设备刷新，支持为离线的已配对设备手动连接地址。\n• 任务详情、统计说明、右键菜单与切页新增轻量缓动，支持系统的减少动态效果设置。";
}
public record OtaManifest(int Protocol,string PackageId,string Version,int VersionCode,long Size,string Sha256,string Notes)
{
    public const long MaxBytes=256L*1024*1024;
    public void Validate()
    {
        if(Protocol!=1||PackageId!=AppRelease.PackageId||!System.Version.TryParse(Version,out _)||Version.Length>30||
            VersionCode<1||Size is <1 or >MaxBytes||Sha256 is null||Sha256.Length!=64||!Sha256.All(Uri.IsHexDigit)||Notes is null||Notes.Length>12000)
            throw new InvalidDataException("更新清单无效或不兼容");
    }
    public async Task VerifyAsync(string file,CancellationToken ct)
    {
        Validate();
        await using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(stream.Length!=Size||!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream,ct),Convert.FromHexString(Sha256)))
            throw new CryptographicException("更新包大小或 SHA-256 校验失败");
    }
}
public record OtaPackage(OtaManifest Manifest,string File);
public record OtaReply(bool Available,OtaManifest? Release,string? Message=null);
public record OtaView(string Phase="idle",string Message="",string? PeerId=null,OtaManifest? Release=null,long Completed=0,double Speed=0);

public sealed partial class AppController
{
    private bool releaseUnseen;
    private async Task AcknowledgeReleaseAsync()
    {await host.Secrets.SetAsync("lantern.release.seen",AppRelease.Version);releaseUnseen=false;}
    private OtaView ota=new();
    private readonly SemaphoreSlim otaGate=new(1);
    private CancellationTokenSource? otaCancel;
    private CancellationTokenSource? startupUpdateCancel;
    private Task startupUpdateJob=Task.CompletedTask;
    private void StartStartupUpdateCheck()
    {
        if(host.Platform!="Android"||ota.Phase=="ready")return;
        var cancel=CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        startupUpdateCancel=cancel;cancel.CancelAfter(TimeSpan.FromSeconds(45));
        startupUpdateJob=Task.Run(async()=>
        {
            var attempted=new HashSet<string>(StringComparer.Ordinal);
            try
            {
                // Discovery may arrive after the page opens. Each remembered PC is tried once,
                // using the same authenticated update connection as a manual check.
                while(!cancel.IsCancellationRequested)
                {
                    await Task.Delay(1500,cancel.Token);
                    var remembered=await trust.ListAsync();
                    foreach(var saved in remembered.Where(t=>t.Identity.Platform=="Windows"))
                    {
                        string id=saved.Identity.Id;
                        if(service.Presence(id)=="offline"||!service.Nearby.ContainsKey(id)||!attempted.Add(id))continue;
                        await CheckUpdateAsync(id,true,cancel.Token);
                        if(ota.Phase is "available" or "ready")return;
                        cancel.Token.ThrowIfCancellationRequested();
                    }
                }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) when(ex is not OutOfMemoryException) { /* Startup checks are best effort; manual checks remain available. */ }
            finally { cancel.Dispose(); }
        });
    }
    private async Task StopStartupUpdateCheckAsync()
    {
        try{startupUpdateCancel?.Cancel();}catch(ObjectDisposedException) { }
        await startupUpdateJob.ConfigureAwait(false);
    }
    private string UpdateRoot=>Path.Combine(host.DataDirectory,"OtaCache");
    private string UpdateFile=>Path.Combine(UpdateRoot,"update.apk");
    private string UpdateReady=>Path.Combine(UpdateRoot,"ready.json");
    private async Task InitializeOtaAsync()
    {
        if(host.Platform!="Android")return;
        try
        {
            if(Directory.Exists(UpdateRoot))
                foreach(string staging in Directory.GetDirectories(UpdateRoot,"Transfer-*"))
                    if((File.GetAttributes(staging)&FileAttributes.ReparsePoint)==0&&Path.GetDirectoryName(Path.GetFullPath(staging))==Path.GetFullPath(UpdateRoot))Directory.Delete(staging,true);
            var manifest=JsonSerializer.Deserialize<OtaManifest>(await File.ReadAllTextAsync(UpdateReady),Json)!;
            manifest.Validate();
            if(manifest.VersionCode<=host.ApplicationVersionCode){ClearUpdateFiles();return;}
            await manifest.VerifyAsync(UpdateFile,life.Token);await host.ValidateUpdateAsync(UpdateFile,manifest);
            ota=new("ready","安装包已校验，可继续安装",Release:manifest,Completed:manifest.Size);
        }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {
            try{ClearUpdateFiles();}catch(IOException){ota=new("failed","无法清理旧更新缓存，请稍后重试");}
            catch(UnauthorizedAccessException){ota=new("failed","无法访问旧更新缓存");}
        }
    }
    private void ClearUpdateFiles(){if(File.Exists(UpdateFile))File.Delete(UpdateFile);if(File.Exists(UpdateReady))File.Delete(UpdateReady);}
    private async Task<AuthenticatedConnection> ConnectUpdatePeerAsync(string peerId,CancellationToken ct)
    {
        var saved=await trust.FindAsync(peerId);
        if(saved?.Identity.Platform!="Windows")throw new IOException("请选择已配对的 Windows 电脑");
        if(!service.Nearby.TryGetValue(peerId,out var peer))throw new IOException("电脑离线，请刷新设备后重试");
        return await service.ConnectAsync(peer.Address,peer.Port,null,peerId,ct);
    }
    private async Task<object> CheckUpdateAsync(string peerId,bool automatic=false,CancellationToken startupToken=default)
    {
        if(host.Platform!="Android")throw new IOException("请在安卓端检查更新");
        if(!automatic)await StopStartupUpdateCheckAsync();
        if(!otaGate.Wait(0))throw new IOException("更新操作正在进行");
        try
        {
            if(ota.Phase=="ready")return new{ok=true,message="已有已校验安装包，请先安装或删除"};
            ota=new("checking","正在验证电脑身份并检查更新",peerId);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(life.Token,startupToken);timeout.CancelAfter(TimeSpan.FromSeconds(automatic?8:30));
            var connection=await ConnectUpdatePeerAsync(peerId,timeout.Token);
            await using(connection.Channel)
            {
                await connection.Channel.SendJsonAsync(new{Ota="check",Protocol=1},timeout.Token);
                var reply=await connection.Channel.ReceiveJsonAsync<OtaReply>(timeout.Token);
                if(!reply.Available||reply.Release is null){ota=new("idle",reply.Message??"电脑未提供安卓更新包",peerId);return new{ok=true};}
                reply.Release.Validate();
                bool newer=reply.Release.VersionCode>host.ApplicationVersionCode;
                ota=new(newer?"available":"current",newer?"发现新版本，请阅读更新日志后下载":"当前已是此电脑提供的最新版本",peerId,reply.Release);
                return new{ok=true};
            }
        }
        catch(Exception ex) when(ex is not OutOfMemoryException){ota=automatic?new():new("failed","检查失败："+ex.Message,peerId);return new{ok=false,message=ota.Message};}
        finally{otaGate.Release();}
    }
    private object StartUpdateDownload()
    {
        if(host.Platform!="Android"||ota.Phase!="available"||ota.Release is null||ota.PeerId is null)throw new IOException("请先检查可用更新");
        if(!otaGate.Wait(0))throw new IOException("更新操作正在进行");
        CancelSpeedForTransfer();
        var selected=ota;otaCancel=CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        otaCancel.CancelAfter(TimeSpan.FromMinutes(15));var cancel=otaCancel;
        ota=selected with{Phase="downloading",Message="正在建立加密连接"};
        string id="ota-"+Guid.NewGuid().ToString("N");
        var job=Task.Run(async()=>{try{await DownloadUpdateAsync(selected,cancel.Token);}finally{otaCancel=null;cancel.Dispose();otaGate.Release();}});
        outgoing[id]=job;_ = job.ContinueWith(_=>outgoing.TryRemove(id,out var ignored),TaskScheduler.Default);
        return new{ok=true};
    }
    private async Task DownloadUpdateAsync(OtaView selected,CancellationToken ct)
    {
        string? folder=null;
        try
        {
            var expected=selected.Release!;
            var connection=await ConnectUpdatePeerAsync(selected.PeerId!,ct);
            await using(connection.Channel)
            {
                await connection.Channel.SendJsonAsync(new{Ota="download",Protocol=1,Sha256=expected.Sha256},ct);
                var reply=await connection.Channel.ReceiveJsonAsync<OtaReply>(ct);
                if(!reply.Available||reply.Release!=expected)throw new IOException("电脑上的版本已变化，请重新检查更新");
                folder=await Transfers.ReceiveAsync(connection.Channel,connection.Peer,UpdateRoot,offer=>
                {
                    bool valid=offer.Files.Length==1&&offer.Files[0].Name=="Lantern-Android.apk"&&offer.Total==expected.Size;
                    var drive=new DriveInfo(host.DataDirectory);
                    return Task.FromResult(valid&&(!drive.IsReady||drive.AvailableFreeSpace>expected.Size+64L*1024*1024));
                },new TransferControl(),p=>{if(p.State=="transferring")ota=selected with{Phase="downloading",Message="正在接收加密更新包",Completed=p.Completed,Speed=p.BytesPerSecond};},ct,
                finalize:async(staging,token)=>
                {
                    ota=selected with{Phase="verifying",Message="正在校验包名、版本、签名与完整性",Completed=expected.Size};
                    string apk=Directory.GetFiles(staging,"*-Lantern-Android.apk").Single();
                    if(await trust.FindAsync(selected.PeerId!) is null)throw new IOException("电脑的设备信任已撤销");
                    await expected.VerifyAsync(apk,token);await host.ValidateUpdateAsync(apk,expected);
                    File.Move(apk,UpdateFile,true);await File.WriteAllTextAsync(UpdateReady,JsonSerializer.Serialize(expected,Json),token);
                });
            }
            ota=selected with{Phase="ready",Message="更新包已校验，点击安装；系统将要求确认",Completed=expected.Size,Speed=0};
        }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {
            try{ClearUpdateFiles();}catch(IOException){}catch(UnauthorizedAccessException){}
            ota=new("failed",ct.IsCancellationRequested?"更新下载已取消，可重新检查":"更新失败："+ex.Message,selected.PeerId);
        }
        finally
        {
            if(folder is not null&&Path.GetDirectoryName(Path.GetFullPath(folder))==Path.GetFullPath(UpdateRoot)&&Path.GetFileName(folder).StartsWith("Transfer-",StringComparison.Ordinal))Directory.Delete(folder,true);
        }
    }
    private async Task<object> InstallUpdateAsync()
    {
        if(host.Platform!="Android"||ota.Phase!="ready"||ota.Release is null)throw new IOException("没有已校验的更新包");
        if(!otaGate.Wait(0))throw new IOException("更新操作正在进行");
        try{await ota.Release.VerifyAsync(UpdateFile,life.Token);await host.ValidateUpdateAsync(UpdateFile,ota.Release);await host.InstallUpdateAsync(UpdateFile);return new{ok=true,message="请在系统界面确认安装；若刚授权安装权限，请返回后再次点击安装"};}
        finally{otaGate.Release();}
    }
    private async Task ServeUpdateAsync(AuthenticatedConnection connection,JsonElement request,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(15));ct=deadline.Token;
        if(host.Platform!="Windows"||connection.Peer.Platform!="Android"||await trust.FindAsync(connection.Peer.Id) is null||
            !request.TryGetProperty("Protocol",out var protocol)||protocol.GetInt32()!=1)
        {await connection.Channel.SendJsonAsync(new OtaReply(false,null,"此电脑不支持更新服务或设备未认证"),ct);return;}
        var package=await host.GetHostedUpdateAsync(ct);
        if(package is null){await connection.Channel.SendJsonAsync(new OtaReply(false,null,"电脑没有有效的安卓更新包"),ct);return;}
        string action=request.GetProperty("Ota").GetString()??"";
        if(action=="check"){await connection.Channel.SendJsonAsync(new OtaReply(true,package.Manifest),ct);return;}
        if(action!="download"||!request.TryGetProperty("Sha256",out var hash)||hash.GetString()!=package.Manifest.Sha256)
        {await connection.Channel.SendJsonAsync(new OtaReply(false,null,"更新包已变化，请重新检查"),ct);return;}
        await connection.Channel.SendJsonAsync(new OtaReply(true,package.Manifest),ct);
        var task=new UiTask{Id=Guid.NewGuid().ToString("N"),PeerId=connection.Peer.Id,Peer=connection.Peer.Name,Name="空中投送 · Android "+package.Manifest.Version,Direction="send",State="connecting",Total=package.Manifest.Size,Created=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")};
        task.ConversationKey=ConversationKey(await trust.FindAsync(connection.Peer.Id)??throw new UnauthorizedAccessException());
        var control=new TransferControl();using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);
        tasks[task.Id]=task;sessionTasks[task.Id]=0;controls[task.Id]=(control,cancel);
        var file=new PickedFile(new SourceFile("Lantern-Android.apk",package.Manifest.Size,"application/vnd.android.package-archive",()=>Task.FromResult<Stream>(new FileStream(package.File,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan))),null,package.File);
        try{await Transfers.SendAsync(connection.Channel,me,[file.Source],task.Id,new(settings.ChunkBytes,settings.LimitMiB),control,p=>Progress(task,p),cancel.Token,offer=>SetManifest(task,offer,[file]));}
        catch(Exception ex) when(ex is not OutOfMemoryException){task.State=cancel.IsCancellationRequested?"cancelled":"failed";task.Message=ex.Message;task.Speed=0;}
        finally{controls.TryRemove(task.Id,out _);await SaveHistoryAsync();}
    }
}
