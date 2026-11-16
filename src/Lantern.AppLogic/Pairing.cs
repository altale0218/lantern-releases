using System.Collections.Concurrent;
using System.Text.Json;
using Lantern.Core;

namespace Lantern.AppLogic;

public sealed partial class AppController
{
    private sealed record PendingPair(string Id,PairingChallenge Challenge,DateTimeOffset Expires,TaskCompletionSource<bool> Answer);
    private readonly ConcurrentDictionary<string,PendingPair> pairingRequests=new();
    private readonly object pairingGate=new();
    private CancellationTokenSource? pairingCancel;
    private Task? pairingOperation;

    private async Task<bool> ConfirmPairingAsync(PairingChallenge challenge,CancellationToken ct)
    {
        var request=new PendingPair(Guid.NewGuid().ToString("N"),challenge,DateTimeOffset.UtcNow.AddSeconds(90),new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock(pairingGate)
        {
            if(!pairingRequests.IsEmpty)return false;
            pairingRequests[request.Id]=request;
        }
        // Keep the accepted request visible while the other device is deciding.
        // The core approval token is cancelled when both decisions finish or fail.
        ct.Register(()=>pairingRequests.TryRemove(request.Id,out _));
        try{return await request.Answer.Task.WaitAsync(TimeSpan.FromSeconds(90),ct);}
        catch{pairingRequests.TryRemove(request.Id,out _);throw;}
    }
    private object BeginPairing(string address,string code,int port)
    {
        LocalNetwork.Parse(address);
        if(code.Length!=8||!code.All(char.IsAsciiDigit)||port is <1024 or >65535)throw new IOException("请输入有效的局域网地址和 8 位匹配码");
        lock(pairingGate)
        {
            if(pairingOperation is {IsCompleted:false}||!pairingRequests.IsEmpty)throw new IOException("已有配对请求，请先完成或取消");
            pairingCancel=CancellationTokenSource.CreateLinkedTokenSource(life.Token);
            var cancel=pairingCancel;
            pairingOperation=Task.Run(async()=>
            {
                try
                {
                    var connection=await service.ConnectAsync(address,port,code,null,cancel.Token);
                    await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},cancel.Token);
                    notice="双方已确认配对，设备密钥已安全保存";
                }
                catch(OperationCanceledException){if(!life.IsCancellationRequested)notice="配对已取消或超时，未建立新的信任";}
                catch(Exception ex) when(ex is not OutOfMemoryException)
                {if(!life.IsCancellationRequested)notice=ex is UnauthorizedAccessException?"配对被拒绝或对方版本不支持双方确认，请检查两端版本":"配对未完成，请检查匹配码、两端版本与局域网连接后重试";}
                finally{lock(pairingGate){if(pairingCancel==cancel)pairingCancel=null;}cancel.Dispose();}
            });
        }
        return new{ok=true,started=true,message="正在验证匹配码，随后请在两端确认设备"};
    }
    private object AnswerPairing(string id,bool accept)
    {
        if(!pairingRequests.TryGetValue(id,out var request)||DateTimeOffset.UtcNow>=request.Expires)throw new IOException("配对请求已过期");
        if(!request.Answer.TrySetResult(accept))throw new IOException("此请求已经处理");
        return new{ok=true,message=accept?"已确认，等待对方确认":"已拒绝配对"};
    }
    private void CancelPairing()
    {
        lock(pairingGate)
        {
            try{pairingCancel?.Cancel();}catch(ObjectDisposedException){}
            foreach(var request in pairingRequests.Values)
            {
                request.Answer.TrySetResult(false);
                try{request.Challenge.Cancel?.Invoke();}catch(ObjectDisposedException){}
            }
        }
        service?.Pairing.Close();pairCode=null;
    }
}
