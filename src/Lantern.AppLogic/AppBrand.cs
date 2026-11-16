namespace Lantern.AppLogic;

public static class AppBrand
{
    public const string Name="闪传 Lantern";
    public const string Motto="同一网络，文件轻松互传";
}

// Unknown status is deliberately silent; Internet reachability is not required for LAN transfers.
public record LanConnectionStatus(bool Known,bool WifiEnabled,bool LanAvailable)
{
    public bool NeedsConnection=>Known&&!LanAvailable&&!WifiEnabled;
    public static readonly LanConnectionStatus Unknown=new(false,false,false);
}
