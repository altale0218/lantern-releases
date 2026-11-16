using Foundation;
using UIKit;

namespace Lantern.iOS;

internal static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("LanternAppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate { }

[Register("LanternSceneDelegate")]
public sealed class SceneDelegate : UIWindowSceneDelegate
{
    private LanternTabs? tabs;

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        tabs = new LanternTabs();
        Window = new UIWindow(windowScene) { RootViewController = tabs };
        Window.MakeKeyAndVisible();
    }

    public override void DidBecomeActive(UIScene scene) => tabs?.Resume();
    public override void DidEnterBackground(UIScene scene) => tabs?.Suspend();
    public override void DidDisconnect(UIScene scene)
    {
        if (tabs is { } closing) _ = closing.CloseAsync();
        tabs = null;
    }
}
