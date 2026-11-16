using System.Text.Json;
using Lantern.Core;

namespace Lantern.AppLogic;

public sealed record SpeedTestView(string Phase="idle",string Message="",string? Id=null,string? PeerId=null,string? PeerName=null,bool InitiatedHere=true,
    SpeedTestMeasurement? Upload=null,SpeedTestMeasurement? Download=null,long CurrentBytes=0,double CurrentSpeed=0,double CurrentSeconds=0,double Progress=0,string? CompletedAt=null);

public sealed partial class AppController
{
    private readonly SemaphoreSlim speedGate=new(1);
    private readonly object speedStateGate=new();
    private volatile SpeedTestView speedTest=new();
    private CancellationTokenSource? speedCancel;
    private bool SpeedBusy=>speedTest.Phase is "connecting" or "negotiating" or "upload" or "download";
    private bool HasSpeedContention=>tasks.Values.Any(t=>t.Kind!="text"&&controls.ContainsKey(t.Id))||ota.Phase is "downloading" or "verifying";
    private void CancelSpeedForPeer(string peerId){lock(speedStateGate){if(speedTest.PeerId==peerId)try{speedCancel?.Cancel();}catch(ObjectDisposedException){}}}
    private void CancelSpeedForTransfer(){lock(speedStateGate){if(SpeedBusy)try{speedCancel?.Cancel();}catch(ObjectDisposedException){}}}
    private object CancelSpeedTest(string id)
    {lock(speedStateGate){if(speedTest.Id!=id)throw new IOException("没有对应的测速");if(!SpeedBusy)return new{ok=true};try{speedCancel?.Cancel();}catch(ObjectDisposedException){}return new{ok=true};}}
    private void SpeedProgress(SpeedTestProgress progress,bool incoming)
    {
        string direction=incoming?(progress.Direction=="upload"?"download":"upload"):progress.Direction;
        lock(speedStateGate)
        {
            double part=progress.Result is not null?1:Math.Min(.99,Math.Max(progress.Seconds/3,progress.Bytes/(double)SpeedTests.MaxDirectionBytes));
            speedTest=speedTest with{Phase=direction,Message=direction=="upload"?"正在测试本机 → 对方":"正在测试对方 → 本机",CurrentBytes=progress.Bytes,CurrentSpeed=progress.BytesPerSecond,CurrentSeconds=progress.Seconds,
                Progress=((progress.Direction=="upload"?0:1)+part)/2,
                Upload=direction=="upload"&&progress.Result is not null?progress.Result:speedTest.Upload,
                Download=direction=="download"&&progress.Result is not null?progress.Result:speedTest.Download};
        }
    }
    private void FinishSpeed(string phase,string message)
    {lock(speedStateGate)speedTest=speedTest with{Phase=phase,Message=message,CurrentSpeed=0,Progress=phase=="completed"?1:speedTest.Progress,CompletedAt=DateTimeOffset.Now.ToString("O")};}
    private async Task<object> StartSpeedTestAsync(string peerId)
    {
        if(life.IsCancellationRequested)throw new IOException("应用正在关闭");
        var saved=await trust.FindAsync(peerId)??throw new IOException("请先配对并认证设备");
        if(!service.Nearby.TryGetValue(peerId,out var peer)||service.Presence(peerId)=="offline")throw new IOException("设备离线，请先打开对方闪传");
        if(HasSpeedContention)throw new IOException("请等待当前文件传输或更新下载完成后再测速");
        if(!speedGate.Wait(0))throw new IOException("已有测速正在进行，请稍后重试");
        string id=Guid.NewGuid().ToString("N");var cancel=CancellationTokenSource.CreateLinkedTokenSource(life.Token);cancel.CancelAfter(TimeSpan.FromSeconds(45));
        lock(speedStateGate){speedCancel=cancel;speedTest=new("connecting","正在验证设备身份",id,peerId,saved.Identity.Name);}
        var job=Task.Run(async()=>
        {
            try
            {
                if((await trust.FindAsync(peerId))?.Secret!=saved.Secret)throw new UnauthorizedAccessException("设备信任已变化");
                var connection=await service.ConnectAsync(peer.Address,peer.Port,null,peerId,cancel.Token);
                await using(connection.Channel)
                {
                    if((await trust.FindAsync(peerId))?.Secret!=saved.Secret)throw new UnauthorizedAccessException("设备信任已变化");
                    lock(speedStateGate)speedTest=speedTest with{Phase="negotiating",Message="正在协商双向测速"};
                    var request=new SpeedTestRequest(1,id,ChunkBytes:settings.ChunkBytes==262144?262144:1048576);
                    await SpeedTests.RunAsync(connection.Channel,request,p=>SpeedProgress(p,false),cancel.Token);
                    if((await trust.FindAsync(peerId))?.Secret!=saved.Secret)throw new UnauthorizedAccessException("设备信任已撤销");
                    cancel.Token.ThrowIfCancellationRequested();
                    FinishSpeed("completed","双向测速完成，两个方向的数据均已校验");
                }
            }
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {FinishSpeed(cancel.IsCancellationRequested?"cancelled":"failed",cancel.IsCancellationRequested?"测速已停止或超时，可重新测试":ex.Message);}
            finally{lock(speedStateGate){speedCancel=null;}cancel.Dispose();speedGate.Release();}
        });
        outgoing["speed-"+id]=job;_ = job.ContinueWith(_=>outgoing.TryRemove("speed-"+id,out var ignored),TaskScheduler.Default);
        return new{ok=true,id};
    }
    private async Task ServeSpeedTestAsync(AuthenticatedConnection connection,JsonElement first,CancellationToken ct)
    {
        string id=SpeedTests.RequestId(first);SpeedTestRequest request;
        async Task Reject(string message)=>await connection.Channel.SendJsonAsync(new SpeedTestReply(1,id,false,message),ct);
        try{request=first.Deserialize<SpeedTestRequest>()??throw new InvalidDataException();SpeedTests.Validate(request);}
        catch(Exception ex) when(ex is JsonException or InvalidDataException or NotSupportedException){await Reject("测速请求无效或版本不兼容");return;}
        var saved=await trust.FindAsync(connection.Peer.Id);
        if(saved is null){await Reject("设备信任已撤销，请重新配对");return;}
        if(HasSpeedContention){await Reject("对方正在传输文件或下载更新，请稍后测速");return;}
        if(!speedGate.Wait(0)){await Reject("对方正在测速，请稍后重试");return;}
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,life.Token);cancel.CancelAfter(TimeSpan.FromSeconds(20));
        lock(speedStateGate){speedCancel=cancel;speedTest=new("download","正在响应已认证设备的测速",id,connection.Peer.Id,saved.Identity.Name,false);}
        try
        {
            await SpeedTests.RespondAsync(connection.Channel,request,p=>SpeedProgress(p,true),cancel.Token);
            if((await trust.FindAsync(connection.Peer.Id))?.Secret!=saved.Secret)throw new UnauthorizedAccessException("设备信任已撤销");
            cancel.Token.ThrowIfCancellationRequested();
            FinishSpeed("completed","双向测速完成，两个方向的数据均已校验");
        }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {FinishSpeed(cancel.IsCancellationRequested?"cancelled":"failed",cancel.IsCancellationRequested?"测速已停止或超时":"测速中断："+ex.Message);}
        finally{lock(speedStateGate){speedCancel=null;}speedGate.Release();}
    }
}
