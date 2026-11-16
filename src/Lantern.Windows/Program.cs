using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Lantern.AppLogic;

namespace Lantern.Windows;
internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, SingleInstance.Name, out bool first);
        if (!first) { SingleInstance.ActivateExisting(); return; }
        try { Application.Run(new MainWindow()); }
        finally { instance.ReleaseMutex(); }
    }
}
internal static class SingleInstance
{
    private static readonly string profile = Path.GetFullPath(Environment.GetEnvironmentVariable("LANTERN_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lantern")).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    public static readonly string Name = "Local\\Lantern-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile)));
    public static readonly uint Message = RegisterWindowMessage(Name + "-Activate");
    public static void ActivateExisting()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            PostMessage(new IntPtr(0xffff), Message, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(100);
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
public sealed class MainWindow : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(13,16,22) };
    private readonly System.Windows.Forms.Timer tick = new() { Interval = 600 };
    private readonly AppController controller;
    private readonly WindowsHost host;
    private readonly WindowsTransferNotifications notifications;
    private readonly TaskbarProgress taskbar=new();
    private static readonly uint taskbarButtonCreated=RegisterWindowMessage("TaskbarButtonCreated");
    private bool ready, closing, polling;
    private Task? initialization;

    private string? previousState;
    private bool? appliedDark;
    private int appliedScale;
    public MainWindow()
    {
        Text = "闪传 Lantern"; Size = new Size(1260,900); MinimumSize = new Size(750,650); StartPosition = FormStartPosition.CenterScreen;
        Icon=System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Color.FromArgb(13,16,22); Controls.Add(web);
        host=new WindowsHost(this);controller = new AppController(host);
        bool startupDark=WindowsHost.CurrentSystemTheme()!="light";
        try {var file=Path.Combine(host.DataDirectory,"settings.json");if(new FileInfo(file).Length<=32768){using var pref=JsonDocument.Parse(File.ReadAllText(file));var theme=pref.RootElement.GetProperty("theme").GetString();if(theme=="dark"||theme=="light")startupDark=theme=="dark";}}catch{}
        BackColor=startupDark?Color.FromArgb(13,16,22):Color.FromArgb(245,247,250);web.DefaultBackgroundColor=BackColor;

        notifications=new(Icon!,()=>_ = OpenTransferProgressAsync());
        Shown += async (_,_) =>
        {
            try
            {
                string profile = Path.Combine(host.DataDirectory,"WebView");
                initialization=Task.Run(()=>controller.InitializeAsync());
                await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(userDataFolder:profile));
                if(closing)return;
                var core = web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
                core.SetVirtualHostNameToFolderMapping("lantern.local", Path.Combine(AppContext.BaseDirectory,"ui"), CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += (_,e) => { if (e.Uri != "https://lantern.local/index.html") e.Cancel = true; };
                core.NewWindowRequested += (_,e) => e.Handled = true;
                core.PermissionRequested += (_,e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_,e) => e.Cancel = true;
                core.WebMessageReceived += async (_,e) =>
                {
                    if (e.Source != "https://lantern.local/index.html") return;
                    try
                    {
                        string message = e.TryGetWebMessageAsString(); if (message.Length > 24000) return;
                        using var doc = JsonDocument.Parse(message); int id = doc.RootElement.GetProperty("id").GetInt32();
                        if(doc.RootElement.GetProperty("command").GetProperty("type").GetString()=="drop-files")
                        {
                            object dropped;
                            try
                            {
                                if(!controller.AllowFileDrop)throw new IOException("拖拽发送已关闭，可在设置中开启");
                                var objects=e.AdditionalObjects;
                                if(objects is null||objects.Count==0)throw new IOException("未收到有效的本机文件，请从文件管理器拖入");
                                var paths=objects.OfType<CoreWebView2File>().Select(f=>f.Path).ToArray();
                                if(paths.Length==0||paths.Length!=objects.Count)throw new IOException("未收到有效的本机文件，请从文件管理器拖入");
                                bool append=doc.RootElement.GetProperty("command").TryGetProperty("append",out var add)&&add.ValueKind==JsonValueKind.True;
                                dropped=controller.SelectNativeFiles(await host.PrepareFilesAsync(paths),append);
                            }
                            catch(Exception ex){dropped=new{ok=false,message=ex.Message};}
                            await web.ExecuteScriptAsync($"window.lantern.acceptDropped?.({JsonSerializer.Serialize(dropped,AppController.Json)})");
                            return;
                        }
                        var command=doc.RootElement.GetProperty("command");
                        object result;
                        if(command.GetProperty("type").GetString()=="test-windows-notification")
                        {notifications.Test();result=new{ok=true,message="已请求 Windows 显示测试通知；请检查系统通知与勿扰设置。"};}
                        else result=await controller.DispatchAsync(command);
                        if (id == 0) ready = true;
                        await web.ExecuteScriptAsync($"window.lanternNativeResult?.({id},{JsonSerializer.Serialize(result, AppController.Json)})");
                        await PollAsync();
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                };
                await initialization;
                if(closing)return;
                ApplyTheme();
                core.Navigate("https://lantern.local/index.html");
                tick.Tick += async (_,_) => await PollAsync(); tick.Start();
            }
            catch (Exception ex) { if(closing)return;MessageBox.Show(this, ex is System.Net.Sockets.SocketException socket && socket.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse ? "传输端口 53319 已被其他进程占用，请关闭占用程序后重试。" : ex is WebView2RuntimeNotFoundException ? "未找到 Microsoft Edge WebView2 Runtime，请安装后重试。" : "启动失败：" + ex.Message, "闪传", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
        };
        FormClosing += async (_,e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true; tick.Stop();notifications.Dispose();taskbar.Dispose();
            try
            {
                if(initialization is not null)try{await initialization;}catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex.Message);}
                await controller.DisposeAsync();
            }
            finally { Close(); }
        };
    }
    private async Task PollAsync()
    {
        if (!ready || closing || polling) return; polling = true;
        try
        {
            ApplyTheme();
            var progress=controller.GetTransferNotificationSnapshot();
            notifications.Update(progress,controller.WindowsNotifications);
            taskbar.Update(Handle,progress,controller.WindowsTaskbarProgress);
            if(appliedScale!=controller.InterfaceScale)
            {
                web.ZoomFactor=controller.InterfaceScale/100d;
                appliedScale=controller.InterfaceScale;
            }
            string json=JsonSerializer.Serialize(await controller.GetStateAsync(), AppController.Json);
            if(json!=previousState)
            {
                await web.ExecuteScriptAsync("window.lantern?.setState(" + json + ")");
                previousState=json;

            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        finally { polling = false; }
    }
    private void ApplyTheme()
    {
        bool dark=(controller.ThemePreference=="system"?WindowsHost.CurrentSystemTheme():controller.ThemePreference)!="light";
        if(appliedDark==dark)return;appliedDark=dark;
        Color background=dark?Color.FromArgb(13,16,22):Color.FromArgb(245,247,250);
        BackColor=background;web.DefaultBackgroundColor=background;
        if(!IsHandleCreated)return;
        int immersive=dark?1:0;
        DwmSetWindowAttribute(Handle,20,ref immersive,sizeof(int));
        int caption=background.R|(background.G<<8)|(background.B<<16);
        int text=dark?0x00F7F1ED:0x00362920;
        DwmSetWindowAttribute(Handle,35,ref caption,sizeof(int));
        DwmSetWindowAttribute(Handle,36,ref text,sizeof(int));
        DwmSetWindowAttribute(Handle,34,ref caption,sizeof(int));
    }
    protected override void WndProc(ref Message message)
    {
        if ((uint)message.Msg == SingleInstance.Message && !closing)
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
        base.WndProc(ref message);
        if((uint)message.Msg==taskbarButtonCreated&&!closing)taskbar?.ButtonCreated();
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    private async Task OpenTransferProgressAsync()
    {
        if(closing)return;
        Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Activate();
        if(ready)try{await web.ExecuteScriptAsync("window.lantern?.showProgress()");}catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex.Message);}
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
}
