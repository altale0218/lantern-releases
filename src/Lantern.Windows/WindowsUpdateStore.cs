using System.Text.Json;
using Lantern.AppLogic;

namespace Lantern.Windows;

// One fixed APK and one manifest per Windows release; never accept a path from the web bridge.
public sealed class WindowsUpdateStore
{
    private readonly SemaphoreSlim gate=new(1);
    private (long ManifestTime,long ApkTime,long Size)? stamp;
    private OtaPackage? cached;
    public async Task<OtaPackage?> GetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            string root=Path.Combine(AppContext.BaseDirectory,"Updates");
            var info=new FileInfo(Path.Combine(root,"android-release.json"));
            var apk=new FileInfo(Path.Combine(root,"Lantern-Android.apk"));
            if(!info.Exists||!apk.Exists){cached=null;stamp=null;return null;}
            var next=(info.LastWriteTimeUtc.Ticks,apk.LastWriteTimeUtc.Ticks,apk.Length);
            if(stamp==next)return cached;
            cached=null;stamp=null;
            if(info.Length>32000||(info.Attributes&FileAttributes.ReparsePoint)!=0||(apk.Attributes&FileAttributes.ReparsePoint)!=0)return null;
            var manifest=JsonSerializer.Deserialize<OtaManifest>(await File.ReadAllTextAsync(info.FullName,ct),AppController.Json);
            if(manifest is null)return null;
            manifest.Validate();
            if(manifest.Version!=AppRelease.Version||manifest.VersionCode!=AppRelease.AndroidCode)return null;
            await manifest.VerifyAsync(apk.FullName,ct);
            stamp=next;
            return cached=new(manifest,apk.FullName);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {return null;}
        finally{gate.Release();}
    }
}
