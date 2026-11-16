using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lantern.Core;

namespace Lantern.AppLogic;

public static class HistoryVault
{
    private static ReadOnlySpan<byte> Header => "LNH1"u8;
    public static byte[] Seal(ReadOnlySpan<byte> plain,byte[] key)
    {
        byte[] result=new byte[32+plain.Length];Header.CopyTo(result);
        RandomNumberGenerator.Fill(result.AsSpan(4,12));
        using var aes=new AesGcm(key,16);
        aes.Encrypt(result.AsSpan(4,12),plain,result.AsSpan(32),result.AsSpan(16,16),Header);
        return result;
    }
    public static byte[] Open(byte[] envelope,byte[] key)
    {
        if(envelope.Length<32||!envelope.AsSpan(0,4).SequenceEqual(Header))throw new CryptographicException("聊天记录格式无效");
        byte[] plain=new byte[envelope.Length-32];using var aes=new AesGcm(key,16);
        aes.Decrypt(envelope.AsSpan(4,12),envelope.AsSpan(32),envelope.AsSpan(16,16),plain,Header);
        return plain;
    }
}

public sealed record ChatEnvelope(int Chat,string Id,string Text,string Sha256);
public sealed record ChatReceipt(string Id,string Sha256,bool Verified);

public sealed partial class AppController
{
    private readonly SemaphoreSlim chatSlots=new(4);
    private byte[] historyKey=[];
    private bool historyLoaded;
    private string EncryptedHistoryPath=>Path.Combine(host.DataDirectory,"conversations.bin");
    private static string ConversationKey(TrustedDevice device)=>device.Identity.Id+":"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Lantern/conversation/v1/"+device.Secret)))[..24];
    private async Task InitializeChatHistoryAsync()
    {
        string? stored=await host.Secrets.GetAsync("lantern.history.key.v1");
        if(stored is null)
        {
            if(File.Exists(EncryptedHistoryPath))throw new CryptographicException("聊天记录密钥丢失，原记录已保留");
            historyKey=RandomNumberGenerator.GetBytes(32);
            await host.Secrets.SetAsync("lantern.history.key.v1",Convert.ToBase64String(historyKey));
        }
        else historyKey=Convert.FromBase64String(stored);
        if(historyKey.Length!=32)throw new CryptographicException("聊天记录密钥无效");
        bool hasVault=settings.History&&File.Exists(EncryptedHistoryPath);
        bool changed=!hasVault&&File.Exists(HistoryPath);
        if(hasVault)
        {
            byte[] plain=HistoryVault.Open(await File.ReadAllBytesAsync(EncryptedHistoryPath),historyKey);
            try
            {
                var saved=JsonSerializer.Deserialize<UiTask[]>(plain,Json)??[];
                RestoreTasks(saved);
            }
            finally{CryptographicOperations.ZeroMemory(plain);}
        }
        foreach(var task in tasks.Values)
        {
            if(task.ConversationKey.Length==0)
            {
                var peer=await trust.FindAsync(task.PeerId);
                task.ConversationKey=peer is null?"legacy:"+task.PeerId:ConversationKey(peer);
                changed=true;
            }
            if(!IsFinished(task.State)){task.State="failed";task.Speed=0;task.Message="上次连接已中断，可重试发送";changed=true;}
        }
        historyLoaded=true;
        if(settings.History)
        {
            if(changed)await SaveHistoryAsync(true);
            else if(hasVault){File.Delete(HistoryPath);File.Delete(HistoryPath+".tmp");}
        }
        else {File.Delete(HistoryPath);File.Delete(HistoryPath+".tmp");File.Delete(EncryptedHistoryPath);File.Delete(EncryptedHistoryPath+".tmp");}
    }
    private static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void ValidateChat(ChatEnvelope message)
    {
        if(message.Chat!=1||!Guid.TryParseExact(message.Id,"N",out _)||string.IsNullOrWhiteSpace(message.Text)||Encoding.UTF8.GetByteCount(message.Text)>16384)
            throw new InvalidDataException("消息必须为 1–16384 字节的文字");
        if(message.Sha256 is null||message.Sha256.Length!=64||!message.Sha256.All(Uri.IsHexDigit)||!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(message.Sha256),Convert.FromHexString(Digest(message.Text))))
            throw new CryptographicException("消息完整性校验失败");
    }
    private async Task<object> SendChatAsync(string peerId,string text,UiTask? retry=null)
    {
        if(outgoing.Count>=64)throw new IOException("待发送任务较多，请等待部分任务完成后再发送");
        var message=new ChatEnvelope(1,retry?.Id??Guid.NewGuid().ToString("N"),text,Digest(text));ValidateChat(message);
        var peer=await trust.FindAsync(peerId)??throw new IOException("请先配对设备");
        if(!service.Nearby.TryGetValue(peerId,out var target))throw new IOException("设备已离线，请打开对方闪传后重试");
        if(retry is not null&&retry.ConversationKey!=ConversationKey(peer))throw new IOException("设备密钥已变化，请在新会话发送");
        var item=retry??new UiTask{Id=message.Id,Kind="text",ChatText=text,ConversationKey=ConversationKey(peer),PeerId=peerId,Peer=peer.Identity.Name,Name="文字消息",Direction="send",Created=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),Total=Encoding.UTF8.GetByteCount(text)};
        item.State="queued";item.Message=null;StoreTask(item);sessionTasks[item.Id]=0;
        try{await SaveHistoryAsync(true);}catch{item.State="failed";item.Message="消息记录保存失败，请检查存储空间后重试";throw;}
        var operation=Task.Run(async()=>
        {
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(life.Token);cancel.CancelAfter(TimeSpan.FromSeconds(30));
            bool entered=false;controls[item.Id]=(new TransferControl(),cancel);
            try
            {
                await chatSlots.WaitAsync(cancel.Token);entered=true;item.State="connecting";
                if(await trust.FindAsync(peerId) is not {} current||ConversationKey(current)!=item.ConversationKey)throw new UnauthorizedAccessException("设备信任已变化");
                var connected=await service.ConnectAsync(target.Address,target.Port,null,peerId,cancel.Token);
                await using(connected.Channel)
                {
                    item.State="transferring";await connected.Channel.SendJsonAsync(message,cancel.Token);
                    var receipt=await connected.Channel.ReceiveJsonAsync<ChatReceipt>(cancel.Token);
                    if(!receipt.Verified||receipt.Id!=message.Id||receipt.Sha256!=message.Sha256)throw new CryptographicException("未收到有效消息校验回执，请确认对方已升级");
                    item.State="completed";item.Completed=item.Total;
                }
            }
            catch(Exception ex){item.State="failed";item.Message=ex is OperationCanceledException?"发送超时或已中断；请确认对方已升级，可重试":ex.Message;}
            finally{controls.TryRemove(item.Id,out _);if(entered)chatSlots.Release();await SaveHistoryAsync();}
        });
        outgoing[item.Id]=operation;_ = operation.ContinueWith(_=>outgoing.TryRemove(item.Id,out var ignored),TaskScheduler.Default);
        return new{ok=true,id=item.Id};
    }
    private async Task<object> RetryChatAsync(string id)
    {
        if(!tasks.TryGetValue(id,out var task)||task.Kind!="text"||task.Direction!="send"||task.State!="failed"||outgoing.ContainsKey(id))throw new IOException("此消息不能重试");
        return await SendChatAsync(task.PeerId,task.ChatText!,task);
    }
    private async Task ReceiveChatAsync(AuthenticatedConnection connection,JsonElement first,CancellationToken ct)
    {
        var message=first.Deserialize<ChatEnvelope>()??throw new InvalidDataException("无效消息");ValidateChat(message);
        await receivedGate.WaitAsync(ct);
        try
        {
            var peer=await trust.FindAsync(connection.Peer.Id)??throw new UnauthorizedAccessException("设备未受信任");
            string key=ConversationKey(peer);
            if(tasks.TryGetValue(message.Id,out var existing))
            {
                if(existing.Kind!="text"||existing.Direction!="receive"||existing.ConversationKey!=key||existing.ChatText!=message.Text)throw new CryptographicException("消息编号冲突");
            }
            else
            {
                StoreTask(new UiTask{Id=message.Id,Kind="text",ChatText=message.Text,ConversationKey=key,PeerId=connection.Peer.Id,Peer=connection.Peer.Name,Name="文字消息",Direction="receive",State="completed",Created=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),Total=Encoding.UTF8.GetByteCount(message.Text),Completed=Encoding.UTF8.GetByteCount(message.Text)});
            }
            await SaveHistoryAsync(true);
            await connection.Channel.SendJsonAsync(new ChatReceipt(message.Id,message.Sha256,true),ct);
            if(settings.Notifications)notice="收到来自 "+connection.Peer.Name+" 的加密消息";
        }
        finally{receivedGate.Release();}
    }
}
