using System.Net;
using System.Net.Sockets;
using Lantern.Core;

namespace Lantern.AppLogic;

// Bonjour instance names advertise an anonymous candidate, never a trusted identity.
public static class LocalServiceIdentity
{
    public const string Type = "_lantern._tcp";
    public const string Domain = "local.";
    public const string QualifiedType = Type + ".local";

    public static string InstanceName(DeviceIdentity identity) => identity.Platform switch
    {
        "iOS" => "iphone-" + identity.Id,
        "iPadOS" => "ipad-" + identity.Id,
        "Android" => "android-" + identity.Id,
        "Windows" => "windows-" + identity.Id,
        _ => throw new ArgumentException("Unsupported discovery platform", nameof(identity))
    };

    public static DeviceIdentity? Parse(string name)
    {
        int separator = name.IndexOf('-');
        if (separator is < 0 or > 7 || name.Length - separator != 33 ||
            !Guid.TryParseExact(name.AsSpan(separator + 1), "N", out var id) || id == Guid.Empty) return null;
        string normalized = id.ToString("N");
        var (platform, device) = name[..separator].ToLowerInvariant() switch
        {
            "iphone" => ("iOS", "iPhone"),
            "ipad" => ("iPadOS", "iPad"),
            "android" => ("Android", "Android"),
            "windows" => ("Windows", "Windows"),
            _ => (null, null)
        };
        return platform is null ? null : new(normalized, device + " · " + normalized[^4..].ToUpperInvariant(), platform);
    }

    public static bool IsEndpoint(IPAddress address, int port) => port is >= 1024 and <= 65535 &&
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && LocalNetwork.IsPrivate(address);
}
