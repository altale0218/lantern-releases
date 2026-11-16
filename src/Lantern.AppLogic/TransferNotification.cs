using System.Globalization;
using System.Text.Json;

namespace Lantern.AppLogic;

// Deliberately exclude paths, peer identities, thumbnails and pairing secrets.
public record TransferNotificationTask(string Id,string State,string Direction,long Total,long Completed,double Speed,string? Name=null);
public record TransferNotificationSnapshot(bool Enabled,bool XiaomiIsland,bool CompletionAlerts,TransferNotificationTask[] Tasks);
public record TransferNotificationFrame(string Title,string Content,string ShortText,int Percent,bool Indeterminate,bool Ongoing,int Count);

// Coalesce volatile speed samples without delaying phase changes or final results.
public sealed class TransferNotificationUpdateGate
{
    private TransferNotificationFrame? last;
    private long publishedAt;
    private bool lastIsland;
    public bool ShouldPublish(TransferNotificationFrame frame,long now,bool island)
    {
        if(frame==last&&island==lastIsland)return false;
        bool samePhase=last is not null&&frame.Ongoing&&last.Ongoing&&
            frame.Title==last.Title&&frame.Count==last.Count&&frame.Indeterminate==last.Indeterminate&&island==lastIsland;
        if(samePhase&&now-publishedAt<4000)return false;
        last=frame;publishedAt=now;lastIsland=island;return true;
    }
    public void Reset(){last=null;publishedAt=0;}
}

public sealed class TransferNotificationReducer
{
    private readonly HashSet<string> finished=new(StringComparer.Ordinal);
    private readonly List<TransferNotificationTask> batchFinished=[];
    private bool hadActive;
    public static bool IsFinished(string state)=>state is "completed" or "failed" or "cancelled" or "rejected";
    public TransferNotificationFrame? Update(TransferNotificationSnapshot snapshot)
    {
        var terminal=snapshot.Tasks.Where(t=>IsFinished(t.State)).ToArray();
        var fresh=terminal.Where(t=>finished.Add(t.Id)).ToArray();
        var active=snapshot.Tasks.Where(t=>!IsFinished(t.State)).ToArray();
        bool previouslyActive=hadActive;hadActive=active.Length>0;
        if(!snapshot.Enabled){batchFinished.Clear();return null;}
        batchFinished.AddRange(fresh);
        if(active.Length==0)
        {
            if(fresh.Length==0&&!previouslyActive)return null;
            var results=batchFinished.ToArray();batchFinished.Clear();
            bool failed=results.Any(t=>t.State=="failed");
            bool cancelled=results.Any(t=>t.State is "cancelled" or "rejected");
            string title=failed?"传输未完成":cancelled?"传输已结束":"传输完成";
            string text=results.Length==1&&results[0].Name is {} name?Clean(name):$"{results.Length} 个任务 · 返回应用查看详情";
            // No completion alert requested: still issue a non-ongoing frame to remove the active island.
            return new(title,text,title,failed||cancelled?0:100,false,false,snapshot.CompletionAlerts?results.Length:0);
        }
        decimal total=active.Sum(t=>(decimal)Math.Max(0,t.Total));
        decimal done=active.Sum(t=>(decimal)Math.Clamp(t.Completed,0,Math.Max(0,t.Total)));
        bool saving=active.All(t=>t.State=="saving");
        bool paused=active.All(t=>t.State=="paused");
        bool moving=active.Any(t=>t.State=="transferring");
        // Receiving all bytes is not success: public storage publication and receipt must finish first.
        int percent=total>0?(int)Math.Min(99,decimal.Floor(done*100/total)):0;
        string stage=saving?"正在校验与保存":paused?"传输已暂停":moving?"安全传输中":"正在连接或等待确认";
        double speed=active.Where(t=>t.State=="transferring").Sum(t=>double.IsFinite(t.Speed)?Math.Max(0,t.Speed):0);
        string meter=$"{percent}% · {(speed/1048576).ToString("0.0",CultureInfo.InvariantCulture)} MB/s";
        string detail=active.Length==1&&active[0].Name is {} single?Clean(single):$"{active.Length} 个传输任务";
        return new(stage,$"{detail} · {meter}",paused?"已暂停":saving?"保存中":moving?$"{percent}%":"等待中",percent,total==0||(!moving&&!paused),true,active.Length);
    }
    private static string Clean(string value)=>new(value.Where(c=>!char.IsControl(c)).Take(80).ToArray());
}

public static class XiaomiIslandPayload
{
    public const string PictureKey="miui.focus.pic_lantern";
    public static string Create(TransferNotificationFrame frame,int? lifetimeSeconds=null)=>JsonSerializer.Serialize(new
    {
        param_v2=new
        {
            protocol=1,business="file_transfer",updatable=frame.Ongoing,reopen="reopen",
            enableFloat=false,islandFirstFloat=false,filterWhenNoPermission=false,timeout=frame.Ongoing?720:1,
            ticker=frame.ShortText,aodTitle=frame.Title,
            // OS2/3 compatible text + icon + plain progress template. No remote images.
            baseInfo=new {type=2,title=frame.Title,content=frame.Content},
            picInfo=new {type=1,pic=PictureKey},
            progressInfo=new {progress=frame.Percent,colorProgress="#D3F98A",colorProgressEnd="#8BAF55"},
            param_island=new
            {
                islandProperty=1,islandOrder=false,islandTimeout=lifetimeSeconds??(frame.Ongoing?3600:8),dismissIsland=!frame.Ongoing,
                bigIslandArea=new
                {
                    imageTextInfoLeft=new {type=1,picInfo=new {type=1,pic=PictureKey},textInfo=new {title="闪传",content=frame.Count>1?$"{frame.Count} 项":"",showHighlightColor=false}},
                    textInfo=new {title=frame.ShortText,showHighlightColor=false}
                },
                smallIslandArea=new {combinePicInfo=new {picInfo=new {type=1,pic=PictureKey},progressInfo=new {progress=frame.Percent,colorReach="#D3F98A",colorUnReach="#40464F",isCCW=false}}}
            }
        }
    });
}
