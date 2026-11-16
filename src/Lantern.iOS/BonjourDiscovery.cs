using System.Net;
using Foundation;
using Lantern.AppLogic;
using Lantern.Core;

namespace Lantern.iOS;

// Bonjour records only locate a candidate. The shared handshake authenticates every operation.
internal sealed class BonjourDiscovery(AppController controller) : IDisposable
{
    private const string Type = LocalServiceIdentity.Type + ".";
    private readonly Dictionary<string, NSNetService> services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NearbyDevice> resolved = new(StringComparer.Ordinal);
    private NSNetServiceBrowser? browser;
    private NSNetService? advertised;
    private long retryAt, nextObservation;

    public void Refresh(bool restart = false)
    {
        if (!controller.IsDiscoverable) { Stop(); return; }
        long now = Environment.TickCount64;
        if (restart || retryAt != 0 && now >= retryAt) Stop();
        if (browser is null)
        {
            advertised = new NSNetService(LocalServiceIdentity.Domain, Type, LocalServiceIdentity.InstanceName(controller.LocalIdentity), controller.ListeningPort);
            advertised.PublishFailure += (_, _) => Retry();
            advertised.Publish(NSNetServiceOptions.NoAutoRename);
            browser = new NSNetServiceBrowser();
            browser.FoundService += (_, found) => Resolve(found.Service);
            browser.ServiceRemoved += (_, removed) => Remove(removed.Service.Name);
            browser.NotSearched += (_, _) => Retry();
            browser.SearchForServices(Type, LocalServiceIdentity.Domain);
        }
        if (now < nextObservation) return;
        nextObservation = now + 2_000;
        foreach (var route in resolved.Values) controller.ObserveLocalService(route.Identity, route.Address, route.Port);
    }

    private void Retry() => retryAt = Environment.TickCount64 + 12_000;

    private void Resolve(NSNetService service)
    {
        string name = service.Name;
        if (services.Count >= 100 || services.ContainsKey(name)) return;
        if (LocalServiceIdentity.Parse(name) is not { } identity || identity.Id == controller.LocalIdentity.Id) return;
        services.Add(name, service);
        service.AddressResolved += (_, _) =>
        {
            if (!services.TryGetValue(name, out var observed) || !ReferenceEquals(observed, service)) return;
            foreach (var endpoint in service.Addresses ?? [])
            {
                byte[] bytes = endpoint.ToArray();
                // Darwin sockaddr_in: length, AF_INET, network-order port, four IPv4 bytes.
                if (bytes.Length < 16 || bytes[1] != 2) continue;
                var ip = new IPAddress(bytes.AsSpan(4, 4));
                if (!LocalServiceIdentity.IsEndpoint(ip, (int)service.Port)) continue;
                resolved[name] = new(identity, ip.ToString(), (int)service.Port, DateTimeOffset.UtcNow);
                controller.ObserveLocalService(identity, ip.ToString(), (int)service.Port);
                break;
            }
        };
        service.ResolveFailure += (_, _) => Remove(name);
        service.Resolve(5);
    }

    private void Remove(string name)
    {
        resolved.Remove(name);
        if (services.Remove(name, out var service)) service.Stop();
    }

    public void Stop()
    {
        retryAt = nextObservation = 0;
        browser?.Stop();
        browser?.Dispose();
        browser = null;
        advertised?.Stop();
        advertised?.Dispose();
        advertised = null;
        foreach (string key in services.Keys.ToArray()) Remove(key);
    }

    public void Dispose() => Stop();
}
