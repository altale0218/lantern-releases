using System.Text.Json;
using Foundation;
using Lantern.AppLogic;
using UIKit;
using WebKit;

namespace Lantern.iOS;

internal sealed class LanternTabs : UITabBarController
{
    private static readonly string[] Routes = ["transfer", "progress", "history", "settings"];
    private const string Handler = "lantern";
    private const string DiscoveryPreference = "lantern.discovery.requested";
    private readonly AppleHost host;
    private readonly AppController controller;
    private readonly BonjourDiscovery discovery;
    private readonly NSUrl appUrl;
    private readonly WKWebView web;
    private readonly WKUserContentController scripts;
    private readonly NavigationPolicy navigation;
    private readonly UIViewController[] tabs;
    private readonly TabSelection? nativeSelection;
    private Task? initializing;
    private NSTimer? timer;
    private NSLayoutConstraint[]? constraints;
    private UIView? webParent;
    private string? lastState;
    private string? contentSizeCategory;
    private bool loaded, ready, polling, suspended, closing, discoveryRequested;
    private bool showingWebModal;

    public LanternTabs()
    {
        discoveryRequested = NSUserDefaults.StandardUserDefaults.BoolForKey(DiscoveryPreference);
        host = new AppleHost(Presenter);
        controller = new AppController(host);
        discovery = new BonjourDiscovery(controller);
        string directory = Path.Combine(NSBundle.MainBundle.ResourcePath!, "ui");
        appUrl = NSUrl.FromFilename(Path.Combine(directory, "index.html"));
        scripts = new WKUserContentController();
        scripts.AddScriptMessageHandler(new ScriptMessages(this), Handler);
        string bridge = "window.LanternNativePlatform=" + JsonSerializer.Serialize(host.Platform) + ";" +
            "window.LanternNative=Object.freeze({postMessage:message=>window.webkit.messageHandlers.lantern.postMessage(message)});";
        using var source = new NSString(bridge);
        using var script = new WKUserScript(source, WKUserScriptInjectionTime.AtDocumentStart, true);
        scripts.AddUserScript(script);
        var options = new WKWebViewConfiguration { UserContentController = scripts, WebsiteDataStore = WKWebsiteDataStore.NonPersistentDataStore };
        options.Preferences.JavaScriptCanOpenWindowsAutomatically = false;
        navigation = new NavigationPolicy(appUrl.AbsoluteString!);
        web = new WKWebView(CoreGraphics.CGRect.Empty, options)
        {
            NavigationDelegate = navigation,
            TranslatesAutoresizingMaskIntoConstraints = false,
            Opaque = false,
            BackgroundColor = UIColor.SystemBackground
        };
        web.ScrollView.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
        web.ScrollView.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive;
        tabs = [CreateTab("传输", "arrow.left.arrow.right", 0), CreateTab("进度", "chart.bar", 1),
            CreateTab("聊天", "bubble.left.and.bubble.right", 2), CreateTab("设置", "gearshape", 3)];
        if (OperatingSystem.IsIOSVersionAtLeast(18) && host.Platform == "iPadOS")
        {
            Mode = UITabBarControllerMode.TabSidebar;
            SetTabs(tabs.Select((view, index) => new UITab(view.TabBarItem.Title!, view.TabBarItem.Image!, Routes[index], _ => view)).ToArray(), false);
            nativeSelection = new TabSelection(this);
            Delegate = nativeSelection;
        }
        else
        {
            SetViewControllers(tabs, false);
            ViewControllerSelected += (_, eventArgs) => ShowTab(eventArgs.ViewController);
            ShouldSelectViewController = (_, _) => !showingWebModal;
        }
        AttachWebView(tabs[0]);
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        if (loaded) return;
        loaded = true;
        UpdateSystemAppearance();
        initializing = Task.Run(() =>
        {
            host.PrepareDataDirectory();
            return controller.InitializeAsync();
        });
        using var resourceDirectory = NSUrl.FromFilename(Path.GetDirectoryName(appUrl.Path!)!);
        web.LoadFileUrl(appUrl, resourceDirectory);
    }

    private static UIViewController CreateTab(string title, string symbol, int index) => new()
    {
        TabBarItem = new UITabBarItem(title, UIImage.GetSystemImage(symbol), index)
    };

    private UIViewController Presenter()
    {
        UIViewController view = this;
        while (view.PresentedViewController is { IsBeingDismissed: false } next) view = next;
        return view;
    }

    private void AttachWebView(UIViewController child)
    {
        UIView view = child.View!;
        if (view == webParent) return;
        if (constraints is not null) NSLayoutConstraint.DeactivateConstraints(constraints);
        web.RemoveFromSuperview();
        view.BackgroundColor = UIColor.SystemBackground;
        view.AddSubview(web);
        webParent = view;
        var safe = view.SafeAreaLayoutGuide;
        constraints = [web.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor), web.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor),
            web.TopAnchor.ConstraintEqualTo(safe.TopAnchor), web.BottomAnchor.ConstraintEqualTo(safe.BottomAnchor)];
        NSLayoutConstraint.ActivateConstraints(constraints);
    }

    private void ShowTab(UIViewController child)
    {
        AttachWebView(child);
        int selected = Array.IndexOf(tabs, child);
        if (ready && selected >= 0) Evaluate("window.lantern?.openTab(" + JsonSerializer.Serialize(Routes[selected]) + ")");
    }

    private void UpdateSystemAppearance()
    {
        // Read the scene's traits so overriding this controller's appearance does not override the system preference.
        host.SystemTheme = View?.Window?.WindowScene?.TraitCollection.UserInterfaceStyle == UIUserInterfaceStyle.Dark ? "dark" : "light";
        var traits = TraitCollection;
        if (contentSizeCategory != traits.PreferredContentSizeCategory)
        {
            contentSizeCategory = traits.PreferredContentSizeCategory;
            double systemSize = (double)UIFontMetrics.DefaultMetrics.GetScaledValue(17, traits);
            host.SystemFontScale = Math.Round(Math.Clamp(systemSize / 17, 1, 1.5), 2);
        }
    }

    public void Resume()
    {
        suspended = false;
        if (ready)
        {
            StartTimer();
            _ = PublishStateAsync();
        }
    }

    public void Suspend()
    {
        suspended = true;
        StopTimer();
        discovery.Stop();
    }

    private void StartTimer()
    {
        if (timer is null) timer = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromMilliseconds(600), timer => { _ = PublishStateAsync(); });
    }

    private void StopTimer()
    {
        timer?.Invalidate();
        timer?.Dispose();
        timer = null;
    }

    private async Task PublishStateAsync()
    {
        if (polling || closing || suspended || !ready) return;
        polling = true;
        try
        {
            UpdateSystemAppearance();
            if (discoveryRequested) discovery.Refresh();
            var appearance = controller.ThemePreference switch
            {
                "dark" => UIUserInterfaceStyle.Dark,
                "light" => UIUserInterfaceStyle.Light,
                _ => UIUserInterfaceStyle.Unspecified
            };
            if (OverrideUserInterfaceStyle != appearance) OverrideUserInterfaceStyle = appearance;
            string state = JsonSerializer.Serialize(await controller.GetStateAsync(includeThumbnailData: false), AppController.Json);
            if (closing || suspended || state == lastState) return;
            await web.EvaluateJavaScriptAsync("window.lantern?.setState(" + state + ")");
            lastState = state;
        }
        catch (NSErrorException) { /* A navigation or scene teardown may invalidate evaluation. */ }
        finally { polling = false; }
    }

    private async Task HandleMessageAsync(WKScriptMessage message)
    {
        if (closing || !message.FrameInfo.MainFrame || message.FrameInfo.Request.Url?.AbsoluteString != appUrl.AbsoluteString ||
            message.Body is not NSString text || text.Length > 24000) return;
        int id = 0;
        try
        {
            using var document = JsonDocument.Parse(text.ToString());
            id = document.RootElement.GetProperty("id").GetInt32();
            var command = document.RootElement.GetProperty("command");
            string? type = command.GetProperty("type").GetString();
            if (id == 0 && type == "modal-state")
            {
                showingWebModal = command.GetProperty("open").GetBoolean();
                return;
            }
            if (id == 0 && type == "sync-tab")
            {
                int index = Array.IndexOf(Routes, command.GetProperty("tab").GetString());
                if (index >= 0)
                {
                    if (OperatingSystem.IsIOSVersionAtLeast(18) && nativeSelection is not null) SelectedTab = GetTab(Routes[index]);
                    else SelectedIndex = index;
                    AttachWebView(tabs[index]);
                }
                return;
            }
            if (id == 0 && type == "ready")
            {
                await (initializing ?? Task.CompletedTask);
                if (closing) return;
                ready = true;
                if (!suspended) StartTimer();
                ShowTab(SelectedViewController ?? tabs[0]);
            }
            else if (id > 0 && ready)
            {
                if (!discoveryRequested && type is ("refresh-devices" or "open-pairing" or "pair" or "reconnect-device"))
                {
                    discoveryRequested = true;
                    NSUserDefaults.StandardUserDefaults.SetBool(true, DiscoveryPreference);
                }
                if (!suspended && discoveryRequested) discovery.Refresh(restart: type == "refresh-devices");
                var result = await controller.DispatchAsync(command);
                Evaluate($"window.lanternNativeResult?.({id},{JsonSerializer.Serialize(result, AppController.Json)})");
            }
            await PublishStateAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (closing) return;
            if (id > 0)
                Evaluate($"window.lanternNativeResult?.({id},{{\"ok\":false,\"message\":\"操作暂时无法完成\"}})");
            else if (!ready)
            {
                var alert = UIAlertController.Create("闪传启动失败", "请重新打开闪传。若仍无法启动，请检查设备存储与局域网设置。", UIAlertControllerStyle.Alert);
                alert.AddAction(UIAlertAction.Create("确定", UIAlertActionStyle.Default, null));
                Presenter().PresentViewController(alert, true, null);
            }
        }
    }

    private void Evaluate(string script)
    {
        if (!closing) web.EvaluateJavaScript(script, (_, _) => { });
    }

    public async Task CloseAsync()
    {
        if (closing) return;
        closing = true;
        Suspend();
        scripts.RemoveScriptMessageHandler(Handler);
        try { if (initializing is not null) await initializing; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally
        {
            await controller.DisposeAsync();
        }
    }

    private sealed class ScriptMessages(LanternTabs owner) : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message) =>
            _ = owner.HandleMessageAsync(message);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("ios18.0")]
    private sealed class TabSelection(LanternTabs owner) : UITabBarControllerDelegate
    {
        public override void DidSelectTab(UITabBarController controller, UITab selectedTab, UITab? previousTab) =>
            owner.ShowTab(selectedTab.ViewController!);

        public override bool ShouldSelectTab(UITabBarController controller, UITab tab) => !owner.showingWebModal;
    }

    private sealed class NavigationPolicy(string entry) : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction action, Action<WKNavigationActionPolicy> decisionHandler) =>
            decisionHandler(action.TargetFrame is { MainFrame: true } && action.Request.Url?.AbsoluteString == entry
                ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
    }
}
