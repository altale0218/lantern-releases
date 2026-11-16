using Lantern.AppLogic;
using System.Text.Json;

void Assert(bool value,string message){if(!value)throw new Exception(message);}
TransferNotificationTask Job(string id,string state,long total=100,long done=50,double speed=1048576)=>new(id,state,"receive",total,done,speed);
TransferNotificationSnapshot Snapshot(params TransferNotificationTask[] jobs)=>new(true,true,true,jobs);
var reducer=new TransferNotificationReducer();
var frame=reducer.Update(Snapshot(Job("a","transferring"),Job("b","transferring",300,75,2097152)))!;
Assert(frame.Count==2&&frame.Percent==31&&frame.Content.Contains("3.0 MB/s"),"Aggregate bytes and speeds across peers");
Assert(!frame.Content.Contains("secret")&&frame.Ongoing,"Default notification is generic and ongoing");
frame=reducer.Update(Snapshot(Job("a","saving",100,100),Job("b","saving",300,300)))!;
Assert(frame.Ongoing&&frame.Percent==99&&frame.Indeterminate&&frame.ShortText=="保存中","Receiving bytes must not announce success before saving");
frame=reducer.Update(Snapshot(Job("a","failed"),Job("b","transferring")))!;
Assert(frame.Ongoing&&frame.Count==1,"One failed peer does not dismiss remaining transfers");
frame=reducer.Update(Snapshot(Job("a","failed"),Job("b","completed")))!;
Assert(!frame.Ongoing&&frame.Title=="传输未完成"&&frame.Count==2,"Mixed outcomes cannot be reported as complete");
Assert(reducer.Update(Snapshot(Job("a","failed"),Job("b","completed"))) is null,"Do not replay terminal alerts on every poll");
frame=new TransferNotificationReducer().Update(Snapshot(Job("a","paused",0,0,double.NaN)))!;
Assert(frame.Ongoing&&frame.ShortText=="已暂停"&&!frame.Content.Contains("NaN"),"Pause and zero sizes are safe");
var disabled=new TransferNotificationReducer();
Assert(disabled.Update(new(false,true,true,[Job("old","completed")])) is null,"Disabled notifications remain off");
Assert(disabled.Update(Snapshot(Job("old","completed"))) is null,"Enabling must not replay old completion");
frame=new TransferNotificationFrame("超级岛功能测试","模拟进度 · 未发送文件","20秒",0,false,true,1);
using(var doc=JsonDocument.Parse(XiaomiIslandPayload.Create(frame,20)))
{
    var p=doc.RootElement.GetProperty("param_v2");
    Assert(p.GetProperty("param_island").GetProperty("islandTimeout").GetInt32()==20,"Test island has explicit 20 second lifetime");
    Assert(!p.GetProperty("filterWhenNoPermission").GetBoolean(),"Missing island permission must permit ordinary notification fallback");
    Assert(!p.GetProperty("enableFloat").GetBoolean()&&!p.GetProperty("islandFirstFloat").GetBoolean(),"Do not repeatedly open panels");
    Assert(p.GetProperty("picInfo").GetProperty("pic").GetString()==XiaomiIslandPayload.PictureKey,"Icons refer only to bundled local resource keys");
    Assert(!doc.RootElement.ToString().Contains("http"),"No cloud notification assets");
}
Assert(new AppSettings().TransferNotifications&&!new AppSettings().XiaomiIsland&&!new AppSettings().NotificationDetails,"Default to standard notifications with optional island and private filenames");
var gate=new TransferNotificationUpdateGate();
var progress=new TransferNotificationFrame("安全传输中","0% · 0.0 MB/s","0%",0,false,true,1);
int publications=0;
for(int tick=0;tick<34;tick++)
{
    var sample=progress with {Percent=tick,Content=$"{tick}% · {tick}.0 MB/s",ShortText=tick+"%"};
    if(gate.ShouldPublish(sample,tick*600,false))publications++;
}
Assert(publications==5,"Coalesce 600ms speed samples into updates at least four seconds apart");
var paused=progress with {Title="传输已暂停",Content="暂停",ShortText="已暂停"};
Assert(gate.ShouldPublish(paused,19900,false),"Phase transitions must publish immediately");
Assert(!gate.ShouldPublish(paused,25000,false),"Unchanged notifications never repost");
Assert(gate.ShouldPublish(paused with{Title="传输完成",Ongoing=false,Percent=100},25100,false),"Final result bypasses interval");
gate.Reset();Assert(gate.ShouldPublish(progress,25200,false),"A new transfer can publish after cancellation");
Console.WriteLine("PASS: aggregate progress, verified-save boundary, mixed results, deduplication, pause/empty size, opt-out, local payload and 20-second lifetime");
