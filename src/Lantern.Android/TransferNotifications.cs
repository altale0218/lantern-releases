using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Lantern.AppLogic;

namespace Lantern.AndroidApp;

// Only mirrors current transfers. It does not own a transfer service or restart the app.
public sealed class TransferNotifications(Activity activity)
{
    private const string ChannelId="lantern.transfer.progress.quiet";
    private const string LegacyChannelId="lantern.transfer.status";
    private const int NotificationId=7331,PreviewId=7332;
    private readonly TransferNotificationReducer reducer=new();
    private NotificationManager Manager=>(NotificationManager)activity.GetSystemService(Context.NotificationService)!;
    private readonly TransferNotificationUpdateGate updates=new();
    private bool channelCreated,posted;
    private int protocol;
    private bool enabled;
    private bool? focusAllowed;
    private bool appIdConfigured;
    private bool invitationShown;
    private CancellationTokenSource? previewStop;
    private long previewUntil;
    public object Status=>new {enabled,protocol,focusAllowed,appIdConfigured,previewSeconds=previewStop is null?0:Math.Max(0,(int)Math.Ceiling((previewUntil-SystemClock.ElapsedRealtime())/1000d))};

    public async Task RefreshStatusAsync()
    {
        bool? focus=null;
        try
        {
            using var info=activity.PackageManager!.GetApplicationInfo(activity.PackageName!,PackageInfoFlags.MetaData);
            string? appId=info.MetaData?.Get("com.xiaomi.xms.APP_ID")?.ToString();
            appIdConfigured=!string.IsNullOrWhiteSpace(appId)&&appId!="YOUR_APP_ID";
            protocol=Settings.System.GetInt(activity.ContentResolver,"notification_focus_protocol",0);
            if(protocol>=2)
                focus=await Task.Run(()=>
                {
                    try
                    {
                        using var extras=new Bundle();extras.PutString("package",activity.PackageName);
                        using var uri=Android.Net.Uri.Parse("content://miui.statusbar.notification.public");
                        using var result=activity.ContentResolver!.Call(uri!,"canShowFocus",null,extras);
                        return (bool?)result?.GetBoolean("canShowFocus",false);
                    }
                    catch(Exception ex){Android.Util.Log.Debug("Lantern.Island",ex.Message);return null;}
                });
        }
        catch(Exception ex){Android.Util.Log.Debug("Lantern.Island",ex.Message);}
        enabled=CanNotify();focusAllowed=focus;
    }
    private bool CanNotify()=>Manager.AreNotificationsEnabled()&&
        (!OperatingSystem.IsAndroidVersionAtLeast(33)||activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS")==Permission.Granted)&&
        (Manager.GetNotificationChannel(ChannelId)?.Importance!=NotificationImportance.None)&&
        (Manager.GetNotificationChannel(LegacyChannelId)?.Importance!=NotificationImportance.None);
    private void EnsureChannel()
    {
        if(channelCreated)return;
        using var channel=new NotificationChannel(ChannelId,"文件传输进度（静默）",NotificationImportance.Low);
        channel.Description="静默更新进度、速度与结果，不反复弹出或发声。";
        channel.SetSound(null,null);channel.EnableVibration(false);channel.LockscreenVisibility=NotificationVisibility.Private;
        Manager.CreateNotificationChannel(channel);
        channelCreated=true;
    }
    public void Update(TransferNotificationSnapshot snapshot)
    {
        try
        {
            var frame=reducer.Update(snapshot);
            if(!snapshot.Enabled||!CanNotify()){Cancel();return;}
            if(frame is null)return;
            if(!frame.Ongoing&&frame.Count==0){Cancel();return;}
            bool island=snapshot.XiaomiIsland&&protocol>=2&&appIdConfigured&&focusAllowed==true;
            long now=SystemClock.ElapsedRealtime();
            if(!updates.ShouldPublish(frame,now,island))return;
            using var notification=Build(frame,island,false);
            Manager.Notify(NotificationId,notification);
            posted=true;
        }
        catch(Exception ex){updates.Reset();Android.Util.Log.Warn("Lantern.Island",ex.ToString());}
    }
    private Notification Build(TransferNotificationFrame frame,bool island,bool preview,int seconds=20)
    {
        EnsureChannel();
        using var intent=new Intent(activity,typeof(MainActivity));
        intent.SetAction("com.lantern.transfer.SHOW_PROGRESS");
        intent.AddFlags(ActivityFlags.SingleTop|ActivityFlags.ClearTop);
        using var pending=PendingIntent.GetActivity(activity,7331,intent,PendingIntentFlags.UpdateCurrent|PendingIntentFlags.Immutable);
        using var builder=new Notification.Builder(activity,ChannelId);
        builder.SetSmallIcon(Resource.Drawable.ic_transfer_notification)
            .SetContentTitle(frame.Title).SetContentText(frame.Content)
            .SetContentIntent(pending).SetOnlyAlertOnce(true).SetOngoing(frame.Ongoing)
            .SetAutoCancel(!frame.Ongoing).SetShowWhen(false).SetWhen(0)
            .SetStyle(new Notification.BigTextStyle().BigText(frame.Content))
            .SetCategory(Notification.CategoryProgress).SetVisibility(NotificationVisibility.Private)
            .SetProgress(100,frame.Percent,frame.Indeterminate);
        using var publicBuilder=new Notification.Builder(activity,ChannelId);
        using var publicVersion=publicBuilder.SetSmallIcon(Resource.Drawable.ic_transfer_notification)
            .SetContentTitle("闪传 · 文件传输").SetContentText("解锁查看传输状态").Build();
        builder.SetPublicVersion(publicVersion);
        if(preview||!frame.Ongoing)builder.SetTimeoutAfter(preview?seconds*1000:8000);
        if(island)
        {
            using var extras=new Bundle();
            using var pics=new Bundle();
            using var icon=Icon.CreateWithResource(activity,Resource.Mipmap.ic_launcher);
            pics.PutParcelable(XiaomiIslandPayload.PictureKey,icon);
            extras.PutBundle("miui.focus.pics",pics);
            extras.PutString("miui.focus.param",XiaomiIslandPayload.Create(frame,preview?seconds:null));
            builder.AddExtras(extras);
        }
        return builder.Build();
    }
    public string Preview(bool useIsland)
    {
        try
        {
            if(previewStop is not null){StopPreview();return "通知测试已停止。";}
            if(!CanNotify()){RequestPermission();return "请允许系统通知后再开启测试。";}
            previewStop=new();previewUntil=SystemClock.ElapsedRealtime()+20000;
            bool island=useIsland&&appIdConfigured&&focusAllowed==true&&protocol>=2;
            _=RunPreviewAsync(previewStop,island);
            return island?"已开始 20 秒测试，可切回桌面观察。":"未取得超级岛准入，已改为 20 秒普通静默通知测试。";
        }
        catch(Exception ex){Android.Util.Log.Warn("Lantern.Island",ex.ToString());return "系统未能显示测试通知，请检查通知设置。";}
    }
    private async Task RunPreviewAsync(CancellationTokenSource source,bool island)
    {
        try
        {
            while(!source.IsCancellationRequested)
            {
                int remaining=Math.Max(0,(int)Math.Ceiling((previewUntil-SystemClock.ElapsedRealtime())/1000d));
                if(remaining==0)break;
                int progress=Math.Clamp((20-remaining)*5,0,99);
                using var notification=Build(new("传输通知测试",$"模拟进度 {progress}% · 剩余 {remaining} 秒 · 未发送文件",$"{remaining}秒",progress,false,true,1),island,true,remaining);
                Manager.Notify(PreviewId,notification);
                await Task.Delay((int)Math.Min(4000,Math.Max(1,previewUntil-SystemClock.ElapsedRealtime())),source.Token);
            }
        }
        catch(System.OperationCanceledException){}
        catch(Exception ex){Android.Util.Log.Warn("Lantern.Island",ex.ToString());}
        finally
        {
            if(ReferenceEquals(previewStop,source)){previewStop=null;try{Manager.Cancel(PreviewId);}catch(Exception ex){Android.Util.Log.Debug("Lantern.Island",ex.Message);}}
            source.Dispose();
        }
    }
    private void StopPreview()
    {
        var old=previewStop;previewStop=null;
        try{old?.Cancel();Manager.Cancel(PreviewId);}catch(Exception ex){Android.Util.Log.Debug("Lantern.Island",ex.Message);}
    }
    public void RequestPermission()
    {
        EnsureChannel();
        if(OperatingSystem.IsAndroidVersionAtLeast(33)&&activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS")!=Permission.Granted)
        {
            var prefs=activity.GetSharedPreferences("lantern.notifications",FileCreationMode.Private)!;
            if(prefs.GetBoolean("requested",false)&&!activity.ShouldShowRequestPermissionRationale("android.permission.POST_NOTIFICATIONS")){OpenSettings();return;}
            prefs.Edit()!.PutBoolean("requested",true)!.Apply();
            activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"],7331);
        }
        else OpenSettings();
    }
    public void InvitePermission()
    {
        if(invitationShown||activity.IsFinishing||CanNotify())return;
        var prefs=activity.GetSharedPreferences("lantern.notifications",FileCreationMode.Private)!;
        if(prefs.GetBoolean("invited",false))return;
        invitationShown=true;prefs.Edit()!.PutBoolean("invited",true)!.Apply();
        new AlertDialog.Builder(activity)!.SetTitle("允许显示传输通知？")!
            .SetMessage("通知栏可显示当前传输进度、速度与结果。默认不显示文件名，可在设置中关闭；不允许通知仍可在应用内传输。")!
            .SetPositiveButton("允许通知",(_,_)=>RequestPermission())!
            .SetNegativeButton("暂不",(_,_)=>{})!.Show();
    }
    public void OpenSettings()
    {
        using var intent=new Intent(Settings.ActionAppNotificationSettings);
        intent.PutExtra(Settings.ExtraAppPackage,activity.PackageName);activity.StartActivity(intent);
    }
    public void Cancel()
    {
        try{if(posted)Manager.Cancel(NotificationId);posted=false;updates.Reset();}catch(Exception ex){Android.Util.Log.Debug("Lantern.Island",ex.Message);}
    }
    public void Close(){Cancel();StopPreview();}
}
