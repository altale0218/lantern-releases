using System.Text.Json;
using Lantern.Core;

namespace Lantern.AppLogic;

public sealed partial class AppController
{
    private readonly object selectionGate=new();
    private readonly SemaphoreSlim batchGate=new(1);
    private string selectionId=Guid.NewGuid().ToString("N");
    private object SelectionResult(IReadOnlyList<PickedFile> files)=>new{ok=true,selectionId,selection=files.Select((f,i)=>new{id=i.ToString(),name=f.Source.Name,size=f.Source.Size,mime=f.Source.Mime,preview=f.Preview}).ToArray()};

    private async Task<object> QueueSendAsync(JsonElement command)
    {
        if(!await batchGate.WaitAsync(0,life.Token))throw new IOException("正在创建发送任务，请稍后再试");
        try
        {
            IReadOnlyList<PickedFile> files;string revision;
            lock(selectionGate)
            {
                files=selection;revision=selectionId;
                if(files.Count==0)throw new IOException("请先选择文件");
                if(command.TryGetProperty("selectionId",out var expected)&&expected.GetString()!=revision)throw new IOException("文件选择已变化，请检查最新文件后重新发送");
            }
            var ids=command.TryGetProperty("peerIds",out var list)
                ?list.EnumerateArray().Select(v=>v.GetString()??"").Distinct(StringComparer.Ordinal).ToArray()
                :new[]{command.GetProperty("peerId").GetString()??""};
            if(ids.Length is <1 or >16||ids.Any(id=>!Guid.TryParseExact(id,"N",out _)))throw new IOException("请选择 1–16 台已配对设备");
            if(outgoing.Count+ids.Length>64)throw new IOException("待发送任务较多，请等待部分任务完成后再发送；文件选择已保留");
            var targets=new List<(TrustedDevice Trust,NearbyDevice Found)>();
            foreach(var id in ids)
            {
                var saved=await trust.FindAsync(id)??throw new IOException("所选设备尚未配对，请先确认设备身份");
                if(!service.Nearby.TryGetValue(id,out var found)||service.Presence(id)=="offline")throw new IOException("设备“"+saved.Identity.Name+"”已离线，请刷新或取消选择；所选文件已保留");
                targets.Add((saved,found));
            }
            lock(selectionGate)
            {
                if(selectionId!=revision)throw new IOException("文件选择已变化，请检查最新文件后重新发送");
                // Validate once before consuming the selection or creating any task.
                Transfers.Validate(new(Guid.NewGuid().ToString("N"),me,files.Select(f=>new FileOffer(Guid.NewGuid().ToString("N"),f.Source.Name,f.Source.Size,f.Source.Mime)).ToArray()));
                selection=[];selectionId=Guid.NewGuid().ToString("N");
            }
            var created=new List<string>();var batchId=Guid.NewGuid().ToString("N");
            foreach(var (peer,found) in targets)
            {
                var item=new UiTask{Id=Guid.NewGuid().ToString("N"),BatchId=batchId,PeerId=peer.Identity.Id,Peer=peer.Identity.Name,ConversationKey=ConversationKey(peer),Direction="send",Name=files.Count==1?files[0].Source.Name:$"文件分享 · {files.Count} 个文件",State="queued",Total=files.Sum(f=>f.Source.Size),Created=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),Files=files.Select((f,i)=>new UiFile{Id=i.ToString(),Name=f.Source.Name,Mime=f.Source.Mime,Size=f.Source.Size,LocalPath=f.LocalPath,Preview=f.Preview}).ToList()};
                tasks[item.Id]=item;retrySources[item.Id]=files;sessionTasks[item.Id]=0;created.Add(item.Id);
                var operation=Task.Run(()=>SendAsync(item,found,files));outgoing[item.Id]=operation;
                _=operation.ContinueWith(_=>outgoing.TryRemove(item.Id,out var ignored),TaskScheduler.Default);
            }
            return new{ok=true,ids=created,batchId,recipients=targets.Count,message=targets.Count>1?$"已为 {targets.Count} 台设备分别创建加密发送任务":"已创建加密发送任务"};
        }
        finally{batchGate.Release();}
    }
}
