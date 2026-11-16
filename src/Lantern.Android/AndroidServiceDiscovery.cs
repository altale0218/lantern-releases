using System.Net;
using Android.App;
using Android.Net.Nsd;
using Lantern.AppLogic;
using Lantern.Core;

namespace Lantern.AndroidApp;

internal sealed class AndroidServiceDiscovery(Activity activity, AppController controller) : IDisposable
{
    private readonly NsdManager? manager = activity.GetSystemService("servicediscovery") as NsdManager;
    private readonly Dictionary<string, NsdServiceInfo> found = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NearbyDevice> routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<NsdServiceInfo> pending = new();
    private Browser? browser;
    private Registration? registration;
    private Resolver? resolving;
    private long startAfter, observeAfter;

    public void Refresh()
    {
        if (manager is null) return;
        if (!controller.IsDiscoverable) { Stop(); return; }
        long now = Environment.TickCount64;
        if (now >= startAfter)
        {
            startAfter = now + 12_000;
            if (registration is null)
            {
                var info = new NsdServiceInfo { ServiceName = LocalServiceIdentity.InstanceName(controller.LocalIdentity),
                    ServiceType = LocalServiceIdentity.Type, Port = controller.ListeningPort };
                registration = new Registration(this);
                if (!TryNsd(() => manager.RegisterService(info, NsdProtocol.DnsSd, registration))) registration = null;
            }
            if (browser is null)
            {
                browser = new Browser(this);
                if (!TryNsd(() => manager.DiscoverServices(LocalServiceIdentity.Type, NsdProtocol.DnsSd, browser))) browser = null;
            }
            foreach (var (name, service) in found)
                if (!routes.ContainsKey(name) && pending.Count < 100 && !pending.Contains(service) && resolving?.Service != service)
                    pending.Enqueue(service);
            ResolveNext();
        }
        if (now < observeAfter) return;
        observeAfter = now + 2_000;
        foreach (var route in routes.Values) controller.ObserveLocalService(route.Identity, route.Address, route.Port);
    }

    private void Found(Browser source, NsdServiceInfo info)
    {
        if (browser != source || found.Count >= 100 || info.ServiceName is not { } name ||
            LocalServiceIdentity.Parse(name) is not { } identity || identity.Id == controller.LocalIdentity.Id || !found.TryAdd(name, info)) return;
        if (pending.Count < 100) pending.Enqueue(info);
        ResolveNext();
    }

    private void Lost(Browser source, NsdServiceInfo info)
    {
        if (browser != source || info.ServiceName is not { } name) return;
        found.Remove(name);
        routes.Remove(name);
    }

    private void ResolveNext()
    {
        // Older Android releases only support one pending resolve per NSD client.
        while (browser is not null && resolving is null && pending.TryDequeue(out var info))
        {
            if (info.ServiceName is not { } name || !found.TryGetValue(name, out var current) || current != info) continue;
            resolving = new Resolver(this, info);
            // The listener API is also available on Android 8 through 13.
#pragma warning disable CA1422
            if (!TryNsd(() => manager!.ResolveService(info, resolving))) resolving = null;
#pragma warning restore CA1422
        }
    }

    private void Resolved(Resolver source, NsdServiceInfo? info)
    {
        if (resolving != source) return;
        resolving = null;
        if (info is not null && source.Service.ServiceName is { } name && found.TryGetValue(name, out var current) && current == source.Service &&
            LocalServiceIdentity.Parse(name) is { } identity)
        {
            var addresses = OperatingSystem.IsAndroidVersionAtLeast(34) ? info.HostAddresses.Select(host => host.HostAddress) : [info.Host?.HostAddress];
            string? address = addresses.FirstOrDefault(host => IPAddress.TryParse(host, out var ip) && LocalServiceIdentity.IsEndpoint(ip, info.Port));
            if (address is not null)
            {
                routes[name] = new(identity, address, info.Port, DateTimeOffset.UtcNow);
                controller.ObserveLocalService(identity, address, info.Port);
            }
        }
        ResolveNext();
    }

    private void Stop()
    {
        var oldBrowser = browser;
        var oldRegistration = registration;
        browser = null;
        registration = null;
        found.Clear();
        routes.Clear();
        pending.Clear();
        startAfter = observeAfter = 0;
        if (oldBrowser is not null) TryNsd(() => manager!.StopServiceDiscovery(oldBrowser));
        if (oldRegistration is not null) TryNsd(() => manager!.UnregisterService(oldRegistration));
    }

    public void Dispose() => Stop();

    private static bool TryNsd(Action call)
    {
        try { call(); return true; }
        catch (Java.Lang.RuntimeException) { return false; }
    }

    private void OnMain(Action callback) => activity.RunOnUiThread(callback);

    private sealed class Browser(AndroidServiceDiscovery owner) : Java.Lang.Object, NsdManager.IDiscoveryListener
    {
        public void OnDiscoveryStarted(string? serviceType) { }
        public void OnDiscoveryStopped(string? serviceType) { }
        public void OnServiceFound(NsdServiceInfo? serviceInfo) { if (serviceInfo is not null) owner.OnMain(() => owner.Found(this, serviceInfo)); }
        public void OnServiceLost(NsdServiceInfo? serviceInfo) { if (serviceInfo is not null) owner.OnMain(() => owner.Lost(this, serviceInfo)); }
        public void OnStartDiscoveryFailed(string? serviceType, NsdFailure errorCode) => owner.OnMain(() => { if (owner.browser == this) owner.browser = null; });
        public void OnStopDiscoveryFailed(string? serviceType, NsdFailure errorCode) { }
    }

    private sealed class Registration(AndroidServiceDiscovery owner) : Java.Lang.Object, NsdManager.IRegistrationListener
    {
        public void OnServiceRegistered(NsdServiceInfo? serviceInfo) { }
        public void OnServiceUnregistered(NsdServiceInfo? serviceInfo) { }
        public void OnUnregistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) { }
        public void OnRegistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) => owner.OnMain(() => { if (owner.registration == this) owner.registration = null; });
    }

    private sealed class Resolver(AndroidServiceDiscovery owner, NsdServiceInfo service) : Java.Lang.Object, NsdManager.IResolveListener
    {
        public NsdServiceInfo Service => service;
        public void OnServiceResolved(NsdServiceInfo? serviceInfo) => owner.OnMain(() => owner.Resolved(this, serviceInfo));
        public void OnResolveFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) => owner.OnMain(() => owner.Resolved(this, null));
    }
}
