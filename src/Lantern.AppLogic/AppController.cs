using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Lantern.Core;
namespace Lantern.AppLogic;

public record PickedFile(SourceFile Source, string? Preview, string? LocalPath=null);
public interface IPlatformHost
{
    string Platform { get; }
    string DataDirectory { get; }
    string DeviceName { get; }
    string SystemTheme => "dark";
    double SystemFontScale => 1;
    Func<IReadOnlyList<LanInterface>>? NetworkInterfaces => null;
    bool UseUdpDiscovery => true;
    object? TransferNotificationStatus => null;
    LanConnectionStatus ConnectionStatus => LanConnectionStatus.Unknown;
    Task OpenNetworkSettingsAsync()=>Task.CompletedTask;
    string TransferInbox => Path.Combine(DataDirectory,"Inbox");
    string ReceiveLocation => Path.Combine(DataDirectory,"Inbox");
    bool CanReceive => true;
    ISecretStore Secrets { get; }
    Task<IReadOnlyList<PickedFile>> PickAsync();
    Task<IReadOnlyList<PickedFile>> RestoreSourcesAsync(IReadOnlyList<string> paths)=>PickAsync();
    Task OpenFolderAsync(string folder);
    Task<string?> ThumbnailAsync(string file, string mime);
    Task ChooseReceiveFolderAsync()=>Task.CompletedTask;
    Task ResetReceiveFolderAsync()=>Task.CompletedTask;
    Task<string> PublishReceivedAsync(string folder,CancellationToken ct)=>Task.FromResult(folder);
    Task DeleteReceivedAsync(string folder)=>Task.FromException(new IOException("此平台暂不支持删除文件"));
    Task OpenLocalFileAsync(string file,bool reveal,bool received)=>Task.FromException(new IOException("此平台不支持此操作"));
    Task<Stream> OpenReceivedPreviewAsync(string path, CancellationToken token) => Task.FromResult<Stream>(DocumentPreview.OpenLocal(path));
    int ApplicationVersionCode=>AppRelease.AndroidCode;
    Task<OtaPackage?> GetHostedUpdateAsync(CancellationToken ct)=>Task.FromResult<OtaPackage?>(null);
    Task ValidateUpdateAsync(string file,OtaManifest manifest)=>Task.FromException(new IOException("此平台不支持安装更新"));
    Task InstallUpdateAsync(string file)=>Task.FromException(new IOException("此平台不支持安装更新"));
    Task CopyLocalFileAsync(string file,bool received)=>Task.FromException(new IOException("此平台不支持复制文件"));
    Task DeleteLocalFileAsync(string file)=>Task.FromException(new IOException("此平台不支持此操作"));
}
public sealed class UiFile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "";
    public long Size { get; set; }
    public long Completed { get; set; }
    public string? Preview { get; set; }
    public string? LocalPath { get; set; }
    public bool Deleted { get; set; }
}
public sealed class UiTask
{
    public string? BatchId { get; set; }
    public string Kind { get; set; } = "files";
    public string? ChatText { get; set; }
    public string ConversationKey { get; set; } = "";
    public string Id { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string Peer { get; set; } = "";
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "";
    public string State { get; set; } = "connecting";
    public long Total { get; set; }
    public long Completed { get; set; }
    public double Speed { get; set; }
    public string Created { get; set; } = "";
    public string? Message { get; set; }
    public string? Folder { get; set; }
    public List<UiFile> Files { get; set; } = [];
}
public sealed class AppSettings
{
    public string Theme { get; set; } = "system";
    public int UiScale { get; set; } = 100;
    public int FontScale { get; set; } = 100;
    public bool ShowDeviceSecurityHint { get; set; }
    public bool ShowSidebarSecurityHint { get; set; }
    public bool Discoverable { get; set; } = true;
    public bool AutoAccept { get; set; } = true;
    public int PreferencesVersion { get; set; }
    public bool AllowFileDrop { get; set; } = true;
    public bool Thumbnails { get; set; } = true;
    public bool History { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool TransferNotifications { get; set; } = true;
    public bool XiaomiIsland { get; set; }
    public bool NotificationDetails { get; set; }
    public bool WindowsNotifications { get; set; } = true;
    public bool WindowsTaskbarProgress { get; set; } = true;
    public int ChunkBytes { get; set; } = 1048576;
    public int LimitMiB { get; set; }
}
public sealed partial class AppController(IPlatformHost host) : IAsyncDisposable
{
    public string ThemePreference => settings.Theme;
    public bool AllowFileDrop => settings.AllowFileDrop;
    public bool WindowsNotifications => settings.WindowsNotifications;
    public bool WindowsTaskbarProgress => settings.WindowsTaskbarProgress;
    // Only native pickers and native dropped-file objects may supply this list.
    public object SelectNativeFiles(IReadOnlyList<PickedFile> files,bool append=false)
    {
        lock(selectionGate)
        {
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next=(append?selection.Concat(files):files).Where(f=>f.LocalPath is null||paths.Add(f.LocalPath)).ToArray();
        if(next.Length>Transfers.MaxFiles)throw new IOException("一次最多选择 1000 个文件");
        selection=next;selectionId=Guid.NewGuid().ToString("N");
        return SelectionResult(selection);
        }
    }
    public int InterfaceScale => new[] {90,100,110,125,150}.Contains(settings.UiScale)?settings.UiScale:100;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentDictionary<string, UiTask> tasks = new();
    private readonly ConcurrentDictionary<string, byte> sessionTasks = new();
    private readonly ConcurrentDictionary<string, (TransferControl Control, CancellationTokenSource Cancel)> controls = new();
    private readonly ConcurrentDictionary<string, (TransferOffer Offer, TaskCompletionSource<bool> Answer)> pending = new();
    private readonly ConcurrentDictionary<string, Task> outgoing = new();
    private readonly ConcurrentDictionary<string,IReadOnlyList<PickedFile>> retrySources=new();
    private readonly SemaphoreSlim sendSlots = new(4), saveGate = new(1), receivedGate=new(1);
    private readonly CancellationTokenSource life = new();
    private int disposed;
    private readonly object taskGate = new();
    private IReadOnlyList<PickedFile> selection = [];
    private DeviceIdentity me = null!;
    private TrustStore trust = null!;
    private LanService service = null!;
    private AppSettings settings = new();
    private string? notice;
    private (long Time,string Text) addressSnapshot;
    private const string OfflineReason="对方设备已离线，传输已中断";
    private (string Code, DateTimeOffset Expires)? pairCode;
    private string Inbox => host.TransferInbox;
    private string HistoryPath => Path.Combine(host.DataDirectory, "history.json");
    public async Task InitializeAsync(int port=LanService.DefaultPort)
    {
        Directory.CreateDirectory(host.DataDirectory);
        string? id = await host.Secrets.GetAsync("lantern.identity");
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            await host.Secrets.SetAsync("lantern.identity", id);
        }
        var name = host.DeviceName;
        me = new(id, name[..Math.Min(name.Length,80)], host.Platform);
        trust = new TrustStore(host.Secrets);
        releaseUnseen=await host.Secrets.GetAsync("lantern.release.seen")!=AppRelease.Version;
        try { settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(Path.Combine(host.DataDirectory, "settings.json")), Json) ?? new(); } catch (Exception ex) when (ex is IOException or JsonException) { }
        if(settings.PreferencesVersion<1)
        {
            settings.AutoAccept=true;settings.ShowDeviceSecurityHint=false;settings.ShowSidebarSecurityHint=false;settings.XiaomiIsland=false;settings.PreferencesVersion=1;
            await SaveSettingsAsync();
        }
        if (settings.History && !File.Exists(EncryptedHistoryPath))
        {
            try
            {
                var history = JsonSerializer.Deserialize<List<UiTask>>(await File.ReadAllTextAsync(HistoryPath), Json) ?? [];
                foreach (var item in history) tasks[item.Id] = item;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        await InitializeChatHistoryAsync();
        service = new LanService(me, trust, host.NetworkInterfaces) { UseUdpDiscovery=host.UseUdpDiscovery, Discoverable = settings.Discoverable, Incoming = IncomingAsync, ConfirmPairing=ConfirmPairingAsync };
        service.Paired += _=>{pairCode=null;notice="双方已确认配对，设备密钥已安全保存";};
        service.Error += message => notice = message;
        service.PeerUnavailable += id=>
        {
            foreach(var task in tasks.Values.Where(t=>t.PeerId==id))if(controls.TryGetValue(task.Id,out var control))
            {task.Message=OfflineReason;try{control.Cancel.Cancel();}catch(ObjectDisposedException){}}
        };
        service.Start(port);
        await InitializeOtaAsync();
        StartStartupUpdateCheck();
    }
    public DeviceIdentity LocalIdentity => me;
    public int ListeningPort => service.Port;
    public bool IsDiscoverable => settings.Discoverable;
    public void ObserveLocalService(DeviceIdentity identity,string address,int port) => service.Observe(new(identity,address,port,DateTimeOffset.UtcNow));
    public async Task<object> GetStateAsync()
    {
        var remembered = await trust.ListAsync();
        var savedById = new Dictionary<string,TrustedDevice>(StringComparer.Ordinal);
        foreach (var device in remembered) savedById[device.Identity.Id] = device;
        var nearby = service.Nearby.ToArray().ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal);
        var activePeers = new HashSet<string>();
        foreach (var id in controls.Keys)
            if (tasks.TryGetValue(id, out var task) && task.State is "transferring" or "paused" or "saving")
                activePeers.Add(task.PeerId);
        var ids = savedById.Keys.Concat(nearby.Keys).Distinct(StringComparer.Ordinal);
        var devices = ids.Select(id =>
        {
            var saved = savedById.GetValueOrDefault(id);
            var found = nearby.GetValueOrDefault(id);
            var identity = saved?.Identity ?? found!.Identity;
            var presence=service.Presence(id);bool online=presence!="offline";
            bool transferring=online&&activePeers.Contains(id);
            return new { id, conversationKey = saved is null ? "" : ConversationKey(saved), name = identity.Name, platform = identity.Platform, address = found?.Address, port = found?.Port ?? LanService.DefaultPort,
                online, status=transferring?"transferring":presence, trusted = saved is not null, pairedAt = saved?.PairedAt.ToLocalTime().ToString("yyyy-MM-dd"), fingerprint = id };
        }).OrderByDescending(d=>d.online).ThenBy(d=>d.name,StringComparer.OrdinalIgnoreCase).ThenBy(d=>d.id).ToArray();
        UiTask[] snapshot; lock (taskGate) snapshot = tasks.Values.OrderByDescending(t => t.Created).ToArray();
        // Native source paths are persisted locally, never exposed through the page bridge.
        var visibleTasks=snapshot.Select(t=>new{t.Id,t.BatchId,t.Kind,t.ChatText,t.ConversationKey,t.PeerId,t.Peer,t.Name,t.Direction,t.State,t.Total,t.Completed,t.Speed,t.Created,t.Message,t.Folder,files=t.Files.Select(f=>new{f.Id,f.Name,f.Mime,f.Size,f.Completed,preview=settings.Thumbnails?f.Preview:null,f.Deleted,canOpen=!f.Deleted&&(t.Direction=="send"?f.LocalPath is not null:t.Folder is not null)})});
        return new { mode = "native", version=AppRelease.Version, releaseNotes=new{version=AppRelease.Version,notes=AppRelease.Notes,showOnStartup=releaseUnseen}, ota, speedTest, hostedUpdate=host.Platform=="Windows"?(await host.GetHostedUpdateAsync(life.Token))?.Manifest:null, self = me, systemTheme = host.SystemTheme, systemFontScale = host.SystemFontScale, address = Addresses(), port = service.Port, inbox = host.ReceiveLocation, settings, transferNotifications = host.TransferNotificationStatus, connection = host.ConnectionStatus, devices, tasks = visibleTasks,
            pending = pending.Select(p => new { id = p.Key, peer = p.Value.Offer.Sender.Name, count = p.Value.Offer.Files.Length, total = p.Value.Offer.Total }).ToArray(),
            pairingRequests=pairingRequests.Values.Select(p=>new{id=p.Id,peer=p.Challenge.Peer,verificationCode=p.Challenge.VerificationCode,outgoing=p.Challenge.Outgoing,expires=p.Expires,answered=p.Answer.Task.IsCompleted,accepted=p.Answer.Task.IsCompletedSuccessfully&&p.Answer.Task.Result}).ToArray(),
            pairingBusy=pairingOperation is {IsCompleted:false},
            pairCode = pairCode is { } p && p.Expires>DateTimeOffset.UtcNow ? new { code = p.Code, expires = p.Expires } : null, notice };
    }
    private string Addresses()
    {
        lock (addressGate)
        {
            long now = Environment.TickCount64;
            if (addressSnapshot.Text is not null && now - addressSnapshot.Time < 2000) return addressSnapshot.Text;
            addressSnapshot = (now, ReadAddress());
            return addressSnapshot.Text;
        }
    }
    private readonly object addressGate = new();
    private string ReadAddress()
    {
        try
        {
            if(host.NetworkInterfaces is {} interfaces)return interfaces().Select(i=>i.Address).FirstOrDefault(ip=>LocalNetwork.IsPrivate(ip)&&!IPAddress.IsLoopback(ip))?.ToString()??"等待网络";
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && LocalNetwork.IsPrivate(ip) && !IPAddress.IsLoopback(ip))?.ToString() ?? "等待网络";
        }
        catch { return "请检查网络连接"; }
    }
    public async Task<object> DispatchAsync(JsonElement command)
    {
        try
        {
            string type = command.GetProperty("type").GetString()!;
            string Text(string key) => command.GetProperty(key).GetString() ?? "";
            switch (type)
            {
                case "speed-test-start": return await StartSpeedTestAsync(Text("peerId"));
                case "speed-test-cancel": return CancelSpeedTest(Text("id"));
                case "chat-send": return await SendChatAsync(Text("peerId"),Text("text"));
                case "chat-retry": return await RetryChatAsync(Text("id"));
                case "release-seen": await AcknowledgeReleaseAsync();return new{ok=true};
                case "ota-check": return await CheckUpdateAsync(Text("peerId"));
                case "ota-download": return StartUpdateDownload();
                case "ota-install": return await InstallUpdateAsync();
                case "ota-cancel": try{otaCancel?.Cancel();}catch(ObjectDisposedException){}return new{ok=true};
                case "ota-clear":
                    await StopStartupUpdateCheckAsync();
                    if(host.Platform!="Android"||!otaGate.Wait(0))throw new IOException("更新操作正在进行");
                    try{ClearUpdateFiles();ota=new();return new{ok=true,message="更新缓存已删除"};}finally{otaGate.Release();}
                case "refresh-devices":
                    lock (addressGate) addressSnapshot = default;
                    service.RefreshDiscovery();return new{ok=true,message="正在刷新附近设备与在线状态"};
                case "ready": return new { ok = true };
                case "open-pairing": pairCode = service.Pairing.Open(); return new { ok = true, code = pairCode.Value.Code, expires = pairCode.Value.Expires };
                case "close-pairing": CancelPairing(); break;
                case "pair-decision":return AnswerPairing(Text("id"),command.GetProperty("accept").GetBoolean());
                case "pair":
                {
                    string address = Text("address"), code = Text("code");
                    return BeginPairing(address,code,command.TryGetProperty("port",out var targetPort)?targetPort.GetInt32():LanService.DefaultPort);
                }
                case "reconnect-device":
                {
                    string id=Text("id"), address=Text("address");
                    if(await trust.FindAsync(id) is null)throw new IOException("找不到已配对的设备，请先完成配对");
                    int port=command.TryGetProperty("port",out var targetPort)?targetPort.GetInt32():LanService.DefaultPort;
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(life.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    var connection=await service.ConnectAsync(address,port,null,id,timeout.Token);
                    await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},timeout.Token);
                    return new{ok=true,message="已验证设备身份，连接成功"};
                }
                case "pick":
                    return SelectNativeFiles(await host.PickAsync());
                case "prepare-retry":
                {
                    string id=Text("id");
                    if(!tasks.TryGetValue(id,out var original)||original.State!="failed"||original.Direction!="send"||controls.ContainsKey(id))throw new IOException("只能重试已结束的失败发送任务");
                    if(await trust.FindAsync(original.PeerId) is null)throw new IOException("设备信任已移除，请重新配对后发送");
                    IReadOnlyList<PickedFile> files;
                    if(!retrySources.TryGetValue(id,out files!))
                    {
                        var paths=original.Files.Select(f=>f.LocalPath).ToArray();
                        files=host.Platform=="Windows"&&paths.Length>0&&paths.All(p=>p is not null)?await host.RestoreSourcesAsync(paths.Select(p=>p!).ToArray()):await host.PickAsync();
                    }
                    if(files.Count==0)return new{ok=false,message="未选择源文件，原失败记录保留"};
                    if(original.Files.Count>0&&(files.Count!=original.Files.Count||files.Where((f,i)=>f.Source.Name!=original.Files[i].Name||f.Source.Size!=original.Files[i].Size).Any()))throw new IOException("源文件已变化，请重新选择原文件；发送其他内容请新建任务");
                    // Re-open handles before offering a retry; expired URI grants and missing
                    // files must not silently create another doomed task.
                    foreach(var file in files){await using var stream=await file.Source.OpenRead();if(!stream.CanRead||(stream.CanSeek&&stream.Length!=file.Source.Size))throw new IOException("源文件已变化或不可读，请重新选择文件发送");}
                    lock(selectionGate){selection=files;selectionId=Guid.NewGuid().ToString("N");}service.RefreshDiscovery();
                    return new{ok=true,peerId=original.PeerId,selectionId,selection=files.Select((f,i)=>new{id=i.ToString(),name=f.Source.Name,size=f.Source.Size,mime=f.Source.Mime,preview=f.Preview}).ToArray(),message="已准备重试，请确认文件和目标设备；将从头创建新任务"};
                }
                case "send":
                    return await QueueSendAsync(command);
                case "decision":
                    if (pending.TryGetValue(Text("id"), out var answer)) answer.Answer.TrySetResult(command.GetProperty("accept").GetBoolean()); break;
                case "pause":
                    if (controls.TryGetValue(Text("id"), out var transfer))
                    {
                        if (transfer.Control.IsPaused) transfer.Control.Resume(); else transfer.Control.Pause();
                        lock (taskGate) { tasks[Text("id")].State = transfer.Control.IsPaused ? "paused" : "transferring"; tasks[Text("id")].Speed = 0; }
                    }
                    break;
                case "cancel": if (controls.TryGetValue(Text("id"), out var cancel)) cancel.Cancel.Cancel(); break;
                case "forget":
                    CancelSpeedForPeer(Text("id"));
                    if(ota.PeerId==Text("id"))try{otaCancel?.Cancel();}catch(ObjectDisposedException){}
                    foreach (var task in tasks.Values.Where(t => t.PeerId == Text("id"))) if (controls.TryGetValue(task.Id, out var c)) c.Cancel.Cancel();
                    await trust.ForgetAsync(Text("id")); return new { ok = true, message = "已撤销设备信任" };
                case "open-folder":
                    if (tasks.TryGetValue(Text("id"), out var open) && open.Folder is not null) await host.OpenFolderAsync(open.Folder);
                    else throw new IOException("接收文件夹不存在"); break;
                case "choose-receive-folder": await host.ChooseReceiveFolderAsync();break;
                case "reset-receive-folder": await host.ResetReceiveFolderAsync();break;
                case "preview-document": return await PreviewDocumentAsync(Text("id"), Text("fileId"));
                case "preview-selection": return await PreviewSelectionAsync(Text("selectionId"), Text("fileId"));
                case "copy-file":
                case "open-file":
                case "reveal-file":
                {
                    if(host.Platform!="Windows"||!tasks.TryGetValue(Text("id"),out var task))throw new IOException("任务不存在");
                    var file=task.Files.FirstOrDefault(f=>f.Id==Text("fileId"))??throw new IOException("文件不存在");
                    if(file.Deleted||!IsFinished(task.State)||controls.ContainsKey(task.Id))throw new IOException("文件尚不可打开");
                    if(type=="copy-file"){await host.CopyLocalFileAsync(ResolveLocalFile(task,file),task.Direction=="receive");return new{ok=true,message="文件已复制，可在文件夹中粘贴"};}
                    await host.OpenLocalFileAsync(ResolveLocalFile(task,file),type=="reveal-file",task.Direction=="receive");break;
                }
                case "delete-file":
                {
                    await receivedGate.WaitAsync(life.Token);
                    try{
                        if(!command.TryGetProperty("confirmed",out var confirmation)||!confirmation.GetBoolean())throw new IOException("删除文件需要确认");
                        if(host.Platform!="Windows"||!tasks.TryGetValue(Text("id"),out var task)||task.Direction!="receive"||!IsFinished(task.State)||controls.ContainsKey(task.Id))throw new IOException("当前任务不能删除文件");
                        var file=task.Files.FirstOrDefault(f=>f.Id==Text("fileId"))??throw new IOException("文件不存在");
                        if(file.Deleted)throw new IOException("文件已删除");
                        await host.DeleteLocalFileAsync(ResolveLocalFile(task,file));file.Deleted=true;file.Preview=null;
                        await SaveHistoryAsync();return new{ok=true,message="接收文件已删除，记录保留"};
                    }finally{receivedGate.Release();}
                }
                case "delete-records":
                {
                    if(!command.TryGetProperty("confirmed",out var confirmation)||confirmation.ValueKind!=JsonValueKind.True)throw new IOException("删除记录需要确认");
                    var ids=command.GetProperty("ids").EnumerateArray().Select(x=>x.GetString()??"").Distinct(StringComparer.Ordinal).ToArray();
                    if(ids.Length is <1 or >500)throw new IOException("请选择 1–500 条记录");
                    await receivedGate.WaitAsync(life.Token);
                    try{
                        var selectedTasks=ids.Select(id=>tasks.TryGetValue(id,out var task)?task:throw new IOException("记录已发生变化，请重新选择")).ToArray();
                        foreach(var task in selectedTasks){
                            if(!IsFinished(task.State)||controls.ContainsKey(task.Id))throw new IOException("进行中的任务不能删除记录");
                            if(host.Platform=="Android"&&task.Direction=="receive"&&task.Folder is not null&&!task.Folder.StartsWith("lantern-public:",StringComparison.Ordinal))throw new IOException("请先导出或删除旧版接收文件");
                        }
                        foreach(var task in selectedTasks){tasks.TryRemove(task.Id,out _);retrySources.TryRemove(task.Id,out _);}
                        await SaveHistoryAsync();return new{ok=true,count=selectedTasks.Length,message=$"已删除 {selectedTasks.Length} 条记录，文件保留"};
                    }finally{receivedGate.Release();}
                }
                case "delete-record":
                {
                    await receivedGate.WaitAsync(life.Token);
                    try{
                        if(!command.TryGetProperty("confirmed",out var confirmation)||!confirmation.GetBoolean())throw new IOException("删除记录需要确认");
                        if(!tasks.TryGetValue(Text("id"),out var task)||!IsFinished(task.State)||controls.ContainsKey(task.Id))throw new IOException("进行中的任务不能删除记录");
                        if(host.Platform=="Android"&&task.Direction=="receive"&&task.Folder is not null&&!task.Folder.StartsWith("lantern-public:",StringComparison.Ordinal))throw new IOException("请先导出或删除旧版接收文件");
                        tasks.TryRemove(task.Id,out _);retrySources.TryRemove(task.Id,out _);await SaveHistoryAsync();return new{ok=true,message="传输记录已删除，文件保留"};
                    }finally{receivedGate.Release();}
                }
                case "export-received":
                    await receivedGate.WaitAsync(life.Token);
                    try{
                    if(!tasks.TryGetValue(Text("id"),out var export)||export.Direction!="receive"||export.State!="completed"||export.Folder is null)throw new IOException("没有可导出的接收文件");
                    string oldFolder=export.Folder;
                    export.Folder=await host.PublishReceivedAsync(oldFolder,life.Token);await SaveHistoryAsync();
                    if(host.Platform=="Android"&&oldFolder!=export.Folder)await host.DeleteReceivedAsync(oldFolder);
                    export.Message="已保存至 "+host.ReceiveLocation;return new {ok=true,message="已保存到手机公开目录"};
                    }finally{receivedGate.Release();}
                case "delete-received":
                    await receivedGate.WaitAsync(life.Token);
                    try{
                    if(!command.TryGetProperty("confirmed",out var confirmed)||!confirmed.GetBoolean())throw new IOException("删除文件需要确认");
                    if(!tasks.TryGetValue(Text("id"),out var deletion)||deletion.Direction!="receive"||deletion.Folder is null||controls.ContainsKey(deletion.Id))throw new IOException("当前任务不能删除文件");
                    await host.DeleteReceivedAsync(deletion.Folder);deletion.Folder=null;deletion.Message="接收文件已删除";foreach(var file in deletion.Files){file.Preview=null;file.Deleted=true;}
                    await SaveHistoryAsync();return new {ok=true,message="已删除此任务接收的文件，传输记录保留"};
                    }finally{receivedGate.Release();}
                case "clear-history":
                    if(HasPrivateAndroidFiles())throw new IOException("请先将旧版接收文件保存到手机公开目录或删除，再清空记录");
                    foreach (var item in tasks.Values.Where(t => IsFinished(t.State))){tasks.TryRemove(item.Id, out var ignored);retrySources.TryRemove(item.Id,out _);}
                    await SaveHistoryAsync(); break;
                case "open-network-settings": await host.OpenNetworkSettingsAsync(); break;
                case "setting":
                    if(Text("key")=="history"&&!command.GetProperty("value").GetBoolean()&&HasPrivateAndroidFiles())throw new IOException("请先导出或删除旧版接收文件，再关闭记录");
                    ApplySetting(Text("key"), command.GetProperty("value")); service.Discoverable = settings.Discoverable;
                    await SaveSettingsAsync();
                    if (!settings.History)
                    {
                        await saveGate.WaitAsync();
                        try { File.Delete(HistoryPath); File.Delete(HistoryPath + ".tmp"); File.Delete(EncryptedHistoryPath); File.Delete(EncryptedHistoryPath + ".tmp"); }
                        finally { saveGate.Release(); }
                    }
                    else await SaveHistoryAsync(); break;
                default: throw new InvalidDataException("不支持的操作");
            }
            return new { ok = true };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new { ok = false, message = ex is OperationCanceledException ? "操作已取消或超时" : "操作失败：" + ex.Message }; }
    }
    private readonly SemaphoreSlim settingsGate=new(1);
    private async Task SaveSettingsAsync()
    {
        await settingsGate.WaitAsync();
        try { string file=Path.Combine(host.DataDirectory,"settings.json");await File.WriteAllTextAsync(file+".tmp",JsonSerializer.Serialize(settings,Json));File.Move(file+".tmp",file,true); }
        finally { settingsGate.Release(); }
    }
    private void ApplySetting(string key, JsonElement value)
    {
        switch (key)
        {
            case "theme": settings.Theme = value.GetString() is "light" or "dark" or "system" ? value.GetString()! : throw new InvalidDataException("Invalid theme"); break;
            case "uiScale": settings.UiScale = new[] {90,100,110,125,150}.Contains(value.GetInt32())?value.GetInt32():100; break;
            case "fontScale": settings.FontScale = new[] {90,100,110,125,150}.Contains(value.GetInt32())?value.GetInt32():100; break;
            case "discoverable": settings.Discoverable = value.GetBoolean(); break;
            case "autoAccept": settings.AutoAccept = value.GetBoolean(); break;
            case "allowFileDrop": settings.AllowFileDrop = value.GetBoolean(); break;
            case "thumbnails": settings.Thumbnails = value.GetBoolean(); break;
            case "showDeviceSecurityHint": settings.ShowDeviceSecurityHint=value.GetBoolean();break;
            case "showSidebarSecurityHint": settings.ShowSidebarSecurityHint=value.GetBoolean();break;
            case "history": settings.History = value.GetBoolean(); break;
            case "notifications": settings.Notifications = value.GetBoolean(); break;
            case "transferNotifications": settings.TransferNotifications = value.GetBoolean(); break;
            case "xiaomiIsland": settings.XiaomiIsland = value.GetBoolean(); break;
            case "notificationDetails": settings.NotificationDetails = value.GetBoolean(); break;
            case "windowsNotifications": settings.WindowsNotifications = value.GetBoolean(); break;
            case "windowsTaskbarProgress": settings.WindowsTaskbarProgress = value.GetBoolean(); break;
            case "chunkBytes": settings.ChunkBytes = value.GetInt32() == 262144 ? 262144 : 1048576; break;
            case "limitMiB": settings.LimitMiB = new[] { 0, 10, 30, 60 }.Contains(value.GetInt32()) ? value.GetInt32() : 0; break;
            default: throw new InvalidDataException("Unknown setting");
        }
    }
    private void SetManifest(UiTask task, TransferOffer offer, IReadOnlyList<PickedFile>? sources = null)
    {
        lock (taskGate)
        {
            task.Files = offer.Files.Select((f,i) => new UiFile { Id = f.Id, Name = f.Name, Mime = f.Mime, Size = f.Size, Preview = sources?[i].Preview, LocalPath=sources?[i].LocalPath }).ToList();
        }
    }
    private void Progress(UiTask task, TransferProgress p)
    {
        lock (taskGate)
        {
            task.Completed = p.Completed; task.Total = p.Total;
            bool paused = controls.TryGetValue(task.Id, out var c) && c.Control.IsPaused;
            task.State = paused && p.State == "transferring" ? "paused" : p.State; task.Speed = paused ? 0 : p.BytesPerSecond;
            var current = task.Files.FirstOrDefault(f => f.Id == p.FileId);
            if (current is not null) current.Completed = p.FileCompleted;
            if (p.State == "completed") foreach (var file in task.Files) file.Completed = file.Size;
        }
    }
    private async Task SendAsync(UiTask item, NearbyDevice target, IReadOnlyList<PickedFile> files)
    {
        var control = new TransferControl(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        CancelSpeedForTransfer();
        controls[item.Id] = (control, cancel); bool entered = false;
        try
        {
            await sendSlots.WaitAsync(cancel.Token); entered = true;item.State="connecting";
            var connected = await service.ConnectAsync(target.Address, target.Port, null, target.Identity.Id, cancel.Token);
            await using (connected.Channel)
            {
                item.State = "waiting";
                await Transfers.SendAsync(connected.Channel, me, files.Select(f => f.Source).ToArray(), item.Id, new(settings.ChunkBytes, settings.LimitMiB), control, p => Progress(item, p), cancel.Token, offer => SetManifest(item, offer, files));
            }
            if (settings.Notifications) notice = "已完成发送：" + item.Name;
        }
        catch (Exception ex) { bool offline=item.Message==OfflineReason;item.State = !offline&&cancel.IsCancellationRequested ? "cancelled" : "failed"; if(!offline)item.Message = ex.Message; item.Speed = 0; if (settings.Notifications) notice = "传输已结束：" + item.Message; }
        finally { controls.TryRemove(item.Id, out var ignored); if(item.State!="failed")retrySources.TryRemove(item.Id,out _);foreach(var id in retrySources.Keys.Where(id=>!tasks.ContainsKey(id)).ToArray())retrySources.TryRemove(id,out _);foreach(var old in tasks.Values.Where(t=>t.State=="failed").OrderByDescending(t=>t.Created).Skip(100))retrySources.TryRemove(old.Id,out _);if (entered) sendSlots.Release(); await SaveHistoryAsync(); }
    }
    private async Task IncomingAsync(AuthenticatedConnection connection, CancellationToken ct)
    {
        if(connection.FirstMessage is {} speed&&speed.TryGetProperty("SpeedTest",out _)){await ServeSpeedTestAsync(connection,speed,ct);return;}
        if(connection.FirstMessage is {} chat&&chat.TryGetProperty("Chat",out _)){await ReceiveChatAsync(connection,chat,ct);return;}
        if(connection.FirstMessage is {} first&&first.TryGetProperty("Ota",out _)){await ServeUpdateAsync(connection,first,ct);return;}
        CancelSpeedForTransfer();
        // Pair-only sessions close immediately; transfer sessions begin with an encrypted manifest.
        var authenticatedTrust=await trust.FindAsync(connection.Peer.Id)??throw new UnauthorizedAccessException();
        UiTask? task = null; var control = new TransferControl(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            string folder = await Transfers.ReceiveAsync(connection.Channel, connection.Peer, Inbox, async offer =>
            {
                // Disk-space check is done before accepting any content.
                if (await trust.FindAsync(connection.Peer.Id) is null||!host.CanReceive) return false;
                var drive = new DriveInfo(host.Platform == "Windows" ? Path.GetPathRoot(Path.GetFullPath(Inbox))! : host.DataDirectory);
                if (drive.IsReady && drive.AvailableFreeSpace < offer.Total + 64L * 1024 * 1024) return false;
                if (settings.AutoAccept) return true;
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[offer.Id] = (offer, answer);
                try { return await answer.Task.WaitAsync(TimeSpan.FromMinutes(2), cancel.Token); }
                finally { pending.TryRemove(offer.Id, out var ignored); }
            }, control, p => { if (task is not null) Progress(task, p); }, cancel.Token, offer =>
            {
                if(tasks.ContainsKey(offer.Id))throw new InvalidDataException("任务编号已存在，请新建发送任务");
                task = new() { Id = offer.Id, PeerId = connection.Peer.Id, Peer = connection.Peer.Name, Direction = "receive", Name = offer.Files.Length == 1 ? offer.Files[0].Name : $"收到文件 · {offer.Files.Length} 个文件", State = "waiting", Total = offer.Total, Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") };
                task.ConversationKey=ConversationKey(authenticatedTrust);
                SetManifest(task, offer); tasks[task.Id] = task; sessionTasks[task.Id] = 0; controls[task.Id] = (control, cancel);
            },async (staging,token)=>
            {
                if(task is null)return;
                task.State="saving";task.Speed=0;
                // Previews are generated while verified files remain in the private staging directory.
                if(settings.Thumbnails)foreach(var file in task.Files)
                {string? path=Directory.GetFiles(staging,file.Id[..8]+"-*").FirstOrDefault();if(path is not null)file.Preview=await host.ThumbnailAsync(path,file.Mime);}
                task.Folder=await host.PublishReceivedAsync(staging,token);
            },connection.FirstMessage);
            if (task is not null)
            {
                task.Folder ??= folder;
                if(host.Platform=="Android"&&task.Folder!=folder&&Directory.Exists(folder))Directory.Delete(folder,true);
                task.Message="已保存至 "+host.ReceiveLocation;
                if (settings.Notifications) notice = "已接收并校验：" + task.Name;
            }
        }
        catch (Exception ex)
        {
            if (task is not null) { bool offline=task.Message==OfflineReason;task.State = !offline&&cancel.IsCancellationRequested ? "cancelled" : "failed"; if(!offline)task.Message = ex.Message; task.Speed = 0; }
        }
        finally { if (task is not null){ controls.TryRemove(task.Id, out var ignored); await SaveHistoryAsync();} }
    }
    private static bool IsFinished(string state) => state is "completed" or "failed" or "cancelled" or "rejected";
    public TransferNotificationSnapshot GetTransferNotificationSnapshot()
    {
        lock(taskGate)
            return new(settings.TransferNotifications, settings.XiaomiIsland, settings.Notifications,
                tasks.Values.Where(t=>t.Kind!="text"&&sessionTasks.ContainsKey(t.Id)).Select(t=>new TransferNotificationTask(
                    t.Id,t.State,t.Direction,t.Total,t.Completed,t.Speed,
                    settings.NotificationDetails?t.Name:null)).ToArray());
    }
    private static string ResolveLocalFile(UiTask task,UiFile file)
    {
        if(task.Direction=="send")return file.LocalPath??throw new IOException("旧记录没有源文件位置，请从文件管理器打开");
        if(task.Folder is null||file.Id.Length!=32||!file.Id.All(Uri.IsHexDigit))throw new IOException("接收文件位置无效");
        Transfers.ValidateName(file.Name);
        return Path.Combine(task.Folder,file.Id[..8]+"-"+file.Name);
    }
    private bool HasPrivateAndroidFiles()=>host.Platform=="Android"&&tasks.Values.Any(t=>t.Direction=="receive"&&t.Folder is not null&&!t.Folder.StartsWith("lantern-public:",StringComparison.Ordinal));
    private async Task SaveHistoryAsync(bool required=false)
    {
        await saveGate.WaitAsync();
        try
        {
            if (!settings.History||!historyLoaded) return;
            UiTask[] copy; lock (taskGate) copy = tasks.Values.Where(t => IsFinished(t.State)||t.Kind=="text").OrderByDescending(t => t.Created).ToArray();
            byte[] plain=JsonSerializer.SerializeToUtf8Bytes(copy,Json);
            try { await File.WriteAllBytesAsync(EncryptedHistoryPath + ".tmp",HistoryVault.Seal(plain,historyKey)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            File.Move(EncryptedHistoryPath + ".tmp", EncryptedHistoryPath, true);
            File.Delete(HistoryPath);File.Delete(HistoryPath+".tmp");
        }
        catch (Exception ex) when (!required && (ex is IOException or UnauthorizedAccessException)) { notice = "文件传输状态已更新，但历史记录保存失败"; }
        finally { saveGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        life.Cancel();CancelPairing(); foreach (var c in controls.Values)try{c.Cancel.Cancel();}catch(ObjectDisposedException){}
        if (service is not null) await service.DisposeAsync();
        await startupUpdateJob;await Task.WhenAll(outgoing.Values);if(pairingOperation is {} pairing)await pairing; await SaveHistoryAsync();CryptographicOperations.ZeroMemory(historyKey); life.Dispose();
    }
}
