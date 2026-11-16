using Lantern.AppLogic;

namespace Lantern.Windows;

public sealed class WindowsTransferNotifications : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly TransferNotificationReducer reducer=new();
    private bool active;
    public WindowsTransferNotifications(Icon appIcon,Action openProgress)
    {
        icon=new NotifyIcon {Icon=appIcon,Text="闪传 Lantern",Visible=false};
        icon.BalloonTipClicked+=(_,_)=>openProgress();icon.DoubleClick+=(_,_)=>openProgress();
        var menu=new ContextMenuStrip();menu.Items.Add("查看传输进度",null,(_,_)=>openProgress());icon.ContextMenuStrip=menu;
    }
    public void Update(TransferNotificationSnapshot snapshot,bool enabled)
    {
        var frame=reducer.Update(snapshot with{Enabled=enabled});
        if(!enabled){icon.Visible=false;active=false;return;}
        if(frame is null)return;
        icon.Visible=true;
        var text=$"闪传 · {frame.ShortText} · {frame.Content}";
        icon.Text=text.Length>63?text[..63]:text;
        if(frame.Ongoing)
        {
            if(!active)Show("文件传输已开始",frame.Content,ToolTipIcon.Info);
            active=true;
        }
        else
        {
            active=false;
            if(frame.Count>0)Show(frame.Title,frame.Content,frame.Title=="传输未完成"?ToolTipIcon.Warning:ToolTipIcon.Info);
        }
    }
    public void Test(){icon.Visible=true;Show("闪传 · 系统通知测试","点击此通知返回传输进度。此测试不会发送文件。",ToolTipIcon.Info);}
    private void Show(string title,string text,ToolTipIcon kind)=>icon.ShowBalloonTip(5000,title,text,kind);
    public void Dispose(){icon.Visible=false;icon.ContextMenuStrip?.Dispose();icon.Dispose();}
}
