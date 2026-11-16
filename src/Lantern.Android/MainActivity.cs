using Android.App;
using Android.OS;
using Android.Content;
using Android.Webkit;
using Android.Net.Wifi;
using Android.Runtime;
using Java.Interop;
using Lantern.AppLogic;
using System.Text.Json;
using WebView = Android.Webkit.WebView;
namespace Lantern.AndroidApp;

[Activity(Name="com.lantern.transfer.MainActivity",Label="闪传 Lantern",MainLauncher=false,Exported=false,LaunchMode=Android.Content.PM.LaunchMode.SingleTask,ConfigurationChanges=Android.Content.PM.ConfigChanges.Orientation|Android.Content.PM.ConfigChanges.ScreenSize|Android.Content.PM.ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private WebView web = null!;
    private Android.Widget.FrameLayout root = null!;
    private int safeLeft,safeTop,safeRight,safeBottom;
    private bool keyboardVisible;
    private bool? appliedDark;
    private AppController controller = null!;
    private AndroidHost host = null!;
    private readonly CancellationTokenSource stopped = new();
    private WifiManager.MulticastLock? multicast;
    private AndroidServiceDiscovery? localDiscovery;
    private JsBridge? bridge;
    private bool ready;
    private BrandSplash? splash;
    private bool destroyed;
    private Task? polling;
    private Task? initialization;
    private TransferNotifications? transferNotifications;
    private bool showProgress;
    private BackCallback? backCallback;
    private bool navigatingBack;
    private string startupStage = "创建页面";
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        SetStartupStage("进入 .NET MainActivity.OnCreate，准备调用系统生命周期");
        base.OnCreate(savedInstanceState);
        try
        {
        bool initialDark=ReadStartupDark();
        var loading = new BrandSplash(this,initialDark);
        bool startupDark=ReadStartupDark();
        var startupBackground=startupDark?Android.Graphics.Color.Rgb(13,16,22):Android.Graphics.Color.Rgb(245,247,250);
        
        loading.SetBackgroundColor(startupBackground);
        SetContentView(loading);
        Window?.SetStatusBarColor(Android.Graphics.Color.Transparent);
        Window?.SetNavigationBarColor(Android.Graphics.Color.Transparent);
        // Transfers are foreground-only in this release; keep screen awake while app is open.
        Window?.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
        Window?.SetSoftInputMode(Android.Views.SoftInput.AdjustResize);
        SetStartupStage("初始化系统 WebView");
        if(OperatingSystem.IsAndroidVersionAtLeast(30))Window?.SetDecorFitsSystemWindows(false);
        else if(Window?.DecorView is {} decor)decor.SystemUiFlags|=Android.Views.SystemUiFlags.LayoutStable|Android.Views.SystemUiFlags.LayoutFullscreen|Android.Views.SystemUiFlags.LayoutHideNavigation;
        if(OperatingSystem.IsAndroidVersionAtLeast(28)&&Window?.Attributes is {} attributes)
        {attributes.LayoutInDisplayCutoutMode=Android.Views.LayoutInDisplayCutoutMode.ShortEdges;Window.Attributes=attributes;}
        root=new Android.Widget.FrameLayout(this);
        web = new WebView(this);
        // The web UI owns focus indicators; avoid a second native blue focus layer.
        web.DefaultFocusHighlightEnabled=false;
        root.AddView(web,new Android.Widget.FrameLayout.LayoutParams(-1,-1));splash=new(this,startupDark);root.AddView(splash,new Android.Widget.FrameLayout.LayoutParams(-1,-1));SetContentView(root);
        root.SetOnApplyWindowInsetsListener(new InsetsListener(this));root.RequestApplyInsets();
        if(OperatingSystem.IsAndroidVersionAtLeast(33))
        {backCallback=new BackCallback(this);OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0,backCallback);}
        if(OperatingSystem.IsAndroidVersionAtLeast(29)&&Window is {} window){window.NavigationBarContrastEnforced=false;window.StatusBarContrastEnforced=false;}
        root.SetBackgroundColor(startupBackground);web.SetBackgroundColor(startupBackground);
        web.Visibility=Android.Views.ViewStates.Invisible;
        web.Settings.JavaScriptEnabled=true; web.Settings.DomStorageEnabled=true;
        web.Settings.AllowFileAccess=true; web.Settings.AllowContentAccess=false;
        web.Settings.AllowFileAccessFromFileURLs=false; web.Settings.AllowUniversalAccessFromFileURLs=false;
        web.Settings.MixedContentMode=MixedContentHandling.NeverAllow;
        web.Settings.SetSupportMultipleWindows(false); WebView.SetWebContentsDebuggingEnabled(false);
        web.SetWebViewClient(new LocalOnlyClient(this));
        SetStartupStage("初始化安全密钥库");
        host=new AndroidHost(this); controller=new AppController(host);
        bridge=new JsBridge(this); web.AddJavascriptInterface(bridge,"LanternNative");
        SetStartupStage("启动局域网服务");
            try
            {
                var wifi=(WifiManager?)ApplicationContext?.GetSystemService(WifiService);
                multicast=wifi?.CreateMulticastLock("Lantern.Discovery"); multicast?.SetReferenceCounted(false); multicast?.Acquire();
            }
            catch(Exception ex) { System.Diagnostics.Debug.WriteLine("Multicast lock: "+ex.Message); }
            initialization=controller.InitializeAsync();
            await initialization;
            if(destroyed)return;
            ApplyTheme();
            showProgress=Intent?.Action=="com.lantern.transfer.SHOW_PROGRESS";
            transferNotifications=new(this);
            _=RefreshNotificationStatusAsync();
            SetStartupStage("加载应用界面");
            web.LoadUrl("file:///android_asset/ui/index.html");
            polling=PollAsync(stopped.Token);
        }
        catch(Exception ex){ShowStartupFailure(ex);}
    }
    private void SetStartupStage(string stage)
    {
        startupStage=stage;
        try{if(FilesDir?.AbsolutePath is {} folder)File.WriteAllText(System.IO.Path.Combine(folder,"startup-stage.txt"),stage);}
        catch(Exception ex){Android.Util.Log.Warn("Lantern.Diagnostics",ex.Message);}
    }
    private void ShowStartupFailure(Exception ex)
    {
        Android.Util.Log.Error("Lantern.Startup", startupStage+": "+ex);
        if(destroyed||IsFinishing)return;
        string diagnostic=$"Lantern 0.7.6\n阶段：{startupStage}\n设备：{Android.OS.Build.Manufacturer} {Android.OS.Build.Model}\nAndroid：{Android.OS.Build.VERSION.Release} / API {(int)Android.OS.Build.VERSION.SdkInt}\n错误：{ex}";
        var panel=new Android.Widget.LinearLayout(this){Orientation=Android.Widget.Orientation.Vertical};
        panel.SetPadding(32,64,32,32);
        var text=new Android.Widget.TextView(this){Text="启动未完成，请复制下面的诊断信息。\n\n"+diagnostic};
        panel.AddView(text);
        var copy=new Android.Widget.Button(this){Text="复制诊断信息"};
        copy.Click+=(_,_)=>((ClipboardManager)GetSystemService(ClipboardService)!).PrimaryClip=ClipData.NewPlainText("Lantern 启动诊断",diagnostic);
        panel.AddView(copy);
        var close=new Android.Widget.Button(this){Text="关闭应用后重新打开"};close.Click+=(_,_)=>FinishAndRemoveTask();panel.AddView(close);
        var scroll=new Android.Widget.ScrollView(this);scroll.AddView(panel);SetContentView(scroll);
    }
    private async Task PollAsync(CancellationToken token)
    {
        string? previousState=null;
        try
        {
            while(!token.IsCancellationRequested)
            {
                // Native progress does not depend on WebView readiness or page visibility.
                if(transferNotifications is not null){transferNotifications.Update(controller.GetTransferNotificationSnapshot());host.TransferNotificationStatus=transferNotifications.Status;}
                if(ready)
                {
                    (localDiscovery ??= new AndroidServiceDiscovery(this,controller)).Refresh();
                    ApplyTheme();
                    string json=JsonSerializer.Serialize(await controller.GetStateAsync(),AppController.Json);
                    if(json!=previousState)
                    {
                        RunOnUiThread(()=>{try{if(!destroyed&&ready&&web is not null)web.EvaluateJavascript("window.lantern?.setState("+json+")",null);}catch(Exception ex){Android.Util.Log.Warn("Lantern.State",ex.ToString());}});
                        previousState=json;
                    }
                }
                await Task.Delay(600,token);
            }
        }
        catch(System.OperationCanceledException){}
        catch(Exception ex){Android.Util.Log.Error("Lantern.State",ex.ToString());}
    }
    internal async void Dispatch(string message)
    {
        if(destroyed||web is null||message.Length>24000)return;
        try
        {
            using var doc=JsonDocument.Parse(message); int id=doc.RootElement.GetProperty("id").GetInt32();
            var command=doc.RootElement.GetProperty("command");
            object result;
            switch(command.GetProperty("type").GetString())
            {
                case "startup-notification-invite":transferNotifications?.InvitePermission();result=new{ok=true};break;
                case "notification-permission":transferNotifications?.RequestPermission();result=new{ok=true};break;
                case "notification-settings":transferNotifications?.OpenSettings();result=new{ok=true};break;
                case "test-transfer-notification":result=new{ok=true,message=transferNotifications?.Preview(true)};break;
                default:result=await controller.DispatchAsync(command);break;
            }
            if(id==0){ready=true;ApplyTheme();string stateJson=JsonSerializer.Serialize(await controller.GetStateAsync(),AppController.Json);web.EvaluateJavascript("window.lantern?.setState("+stateJson+",true)",null);web.PostVisualStateCallback(0,new ReadyFrame(this));}
            string json=JsonSerializer.Serialize(result,AppController.Json);
            if(!destroyed)web.EvaluateJavascript($"window.lanternNativeResult?.({id},{json})",null);
            if(ready&&showProgress){showProgress=false;web.EvaluateJavascript("window.lantern?.showProgress()",null);}
            // The shared UI invites only after startup connection and release dialogs close.
        }
        catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex);}
    }
    private bool ReadStartupDark()
    {
        bool system=(Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask)==Android.Content.Res.UiMode.NightYes;
        try{var path=System.IO.Path.Combine(FilesDir!.AbsolutePath,"settings.json");if(new FileInfo(path).Length>32768)return system;using var settings=JsonDocument.Parse(File.ReadAllText(path));string? theme=settings.RootElement.GetProperty("theme").GetString();return theme=="dark"||(theme!="light"&&system);}catch{return system;}
    }
    private void ApplyTheme()
    {
        bool dark=(controller.ThemePreference=="system"?host.SystemTheme:controller.ThemePreference)!="light";
        if(appliedDark==dark)return;appliedDark=dark;
        var background=dark?Android.Graphics.Color.Rgb(13,16,22):Android.Graphics.Color.Rgb(245,247,250);
        root.SetBackgroundColor(background);web.SetBackgroundColor(background);
        if(OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            const int lightBars=8|16;
            Window?.InsetsController?.SetSystemBarsAppearance(dark?0:lightBars,lightBars);
        }
        else if(Window?.DecorView is {} decor)
        {
            var flags=decor.SystemUiFlags;
            const Android.Views.SystemUiFlags lightBars=Android.Views.SystemUiFlags.LightStatusBar|Android.Views.SystemUiFlags.LightNavigationBar;
            decor.SystemUiFlags=dark?flags&~lightBars:flags|lightBars;
        }
    }
    protected override void OnResume()
    {
        base.OnResume();
        if(transferNotifications is not null)_=RefreshNotificationStatusAsync();
    }
    [Obsolete("Pre-Android 13 fallback; newer versions use OnBackInvokedDispatcher.")]
    public override void OnBackPressed()=>NavigateBack();
    private void NavigateBack()
    {
        if(destroyed||navigatingBack)return;
        if(!ready||web is null){MoveTaskToBack(true);return;}
        navigatingBack=true;
        web.EvaluateJavascript("window.lantern?.navigateBack?.() === true",new BackResult(this));
    }
    public sealed class BackCallback(MainActivity activity):Java.Lang.Object,Android.Window.IOnBackInvokedCallback
    {public void OnBackInvoked()=>activity.NavigateBack();}
    private sealed class BackResult(MainActivity activity):Java.Lang.Object,IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
        {activity.navigatingBack=false;if(!activity.destroyed&&value?.ToString()!="true")activity.MoveTaskToBack(true);}
    }
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if(intent?.Action!="com.lantern.transfer.SHOW_PROGRESS")return;
        showProgress=true;
        if(ready&&!destroyed){showProgress=false;web.EvaluateJavascript("window.lantern?.showProgress()",null);}
    }
    public override void OnRequestPermissionsResult(int requestCode,string[] permissions,[Android.Runtime.GeneratedEnum] Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode,permissions,grantResults);
        if(requestCode==7331)_=RefreshNotificationStatusAsync();
    }
    private async Task RefreshNotificationStatusAsync()
    {
        try
        {
            if(transferNotifications is null)return;
            await transferNotifications.RefreshStatusAsync();
            if(!destroyed&&host is not null)host.TransferNotificationStatus=transferNotifications.Status;
        }
        catch(Exception ex){Android.Util.Log.Warn("Lantern.Island",ex.ToString());}
    }
    protected override void OnActivityResult(int requestCode,Result resultCode,Intent? data)
    { base.OnActivityResult(requestCode,resultCode,data); host?.CompletePick(requestCode,resultCode,data); }
    protected override void OnDestroy()
    {
        // Android requires the base lifecycle callback before this method returns.
        // Never await network or history cleanup inside an async-void OnDestroy.
        destroyed=true;
        try
        {
            localDiscovery?.Dispose();
            stopped.Cancel();host?.CancelPick();
            if(OperatingSystem.IsAndroidVersionAtLeast(33)&&backCallback is not null)
            {OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(backCallback);backCallback.Dispose();backCallback=null;}
            transferNotifications?.Close();
            try { if(multicast?.IsHeld==true)multicast.Release(); } catch(Exception ex){Android.Util.Log.Warn("Lantern.Cleanup",ex.Message);}
            try{web?.RemoveJavascriptInterface("LanternNative");web?.StopLoading();web?.Destroy();bridge?.Dispose();}
            catch(Exception ex){Android.Util.Log.Warn("Lantern.Cleanup",ex.ToString());}
        }
        finally { base.OnDestroy(); }
        _=CleanupAsync();
    }
    private async Task CleanupAsync()
    {
        try
        {
            if(polling is not null)await polling;
            if(initialization is not null)try{await initialization;}catch{}
            if(controller is not null)await controller.DisposeAsync();
        }
        catch(Exception ex){Android.Util.Log.Warn("Lantern.Cleanup",ex.ToString());}
        finally{multicast?.Dispose();stopped.Dispose();}
    }
    public sealed class JsBridge(MainActivity activity) : Java.Lang.Object
    {
        [JavascriptInterface][Export("postMessage")]
        public void PostMessage(string message) => activity.RunOnUiThread(()=>activity.Dispatch(message));
    }
    private sealed class ReadyFrame(MainActivity activity) : WebView.VisualStateCallback
    {
        public override void OnComplete(long requestId){if(!activity.destroyed){activity.web.Visibility=Android.Views.ViewStates.Visible;if(activity.splash is {} splash){activity.root.RemoveView(splash);splash.Dispose();activity.splash=null;}}}
    }
    public sealed class LocalOnlyClient(MainActivity activity) : WebViewClient
    {
        public override void OnPageFinished(WebView? view,string? url)
        {base.OnPageFinished(view,url);if(url=="file:///android_asset/ui/index.html")activity.PublishInsets();}
        public override bool OnRenderProcessGone(WebView? view,RenderProcessGoneDetail? detail)
        {
            activity.ready=false;
            activity.startupStage="系统 WebView 渲染进程";
            activity.ShowStartupFailure(new IOException(detail?.DidCrash()==true?"系统 WebView 渲染进程崩溃，请更新系统 WebView 后重试":"系统回收了 WebView 渲染进程，请重新启动应用"));
            view?.Destroy();activity.web=null!;
            return true;
        }
        public override bool ShouldOverrideUrlLoading(WebView? view,IWebResourceRequest? request)=>request?.Url?.ToString()!="file:///android_asset/ui/index.html";
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view,IWebResourceRequest? request)
        {
            string uri=request?.Url?.ToString()??"";
            return uri.StartsWith("file:///android_asset/ui/",StringComparison.Ordinal)?base.ShouldInterceptRequest(view,request):new WebResourceResponse("text/plain","utf-8",new MemoryStream());
        }
    }
    public sealed class InsetsListener(MainActivity activity) : Java.Lang.Object, Android.Views.View.IOnApplyWindowInsetsListener
    {
        public Android.Views.WindowInsets OnApplyWindowInsets(Android.Views.View view,Android.Views.WindowInsets insets)
        {
            if(OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                int types=Android.Views.WindowInsets.Type.SystemBars()|Android.Views.WindowInsets.Type.DisplayCutout();
                var bars=insets.GetInsets(types);
                activity.UpdateInsets(bars.Left,bars.Top,bars.Right,bars.Bottom,insets.IsVisible(Android.Views.WindowInsets.Type.Ime()));
                // The full-size WebView draws the surfaces; CSS protects only controls.
                view.SetPadding(0,0,0,0);
                return new Android.Views.WindowInsets.Builder(insets).SetInsets(types,Android.Graphics.Insets.None!).SetInsetsIgnoringVisibility(types,Android.Graphics.Insets.None!).Build();
            }
            int left=insets.SystemWindowInsetLeft,top=insets.SystemWindowInsetTop,right=insets.SystemWindowInsetRight,bottom=insets.SystemWindowInsetBottom;
            if(OperatingSystem.IsAndroidVersionAtLeast(28)&&insets.DisplayCutout is {} cutout)
            {left=Math.Max(left,cutout.SafeInsetLeft);top=Math.Max(top,cutout.SafeInsetTop);right=Math.Max(right,cutout.SafeInsetRight);bottom=Math.Max(bottom,cutout.SafeInsetBottom);}
            int stableBottom=insets.StableInsetBottom;
            bool keyboard=bottom>stableBottom+100*(activity.Resources?.DisplayMetrics?.Density??1);
            activity.UpdateInsets(left,top,right,keyboard?stableBottom:bottom,keyboard);view.SetPadding(0,0,0,0);
            return insets.ReplaceSystemWindowInsets(0,0,0,0);
        }
    }
    private void UpdateInsets(int left,int top,int right,int bottom,bool keyboard=false)
    {
        if(safeLeft==left&&safeTop==top&&safeRight==right&&safeBottom==bottom&&keyboardVisible==keyboard)return;
        safeLeft=left;safeTop=top;safeRight=right;safeBottom=bottom;keyboardVisible=keyboard;PublishInsets();
    }
    private void PublishInsets()
    {
        if(destroyed||web is null)return;
        float density=Resources?.DisplayMetrics?.Density??1;
        string json=JsonSerializer.Serialize(new {left=safeLeft/density,top=safeTop/density,right=safeRight/density,bottom=safeBottom/density,keyboardVisible});
        web.EvaluateJavascript("window.lantern?.setInsets("+json+")",null);
    }
}
