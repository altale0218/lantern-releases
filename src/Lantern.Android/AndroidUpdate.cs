using Android.Content;
using Android.Content.PM;
using Android.OS;
using Lantern.AppLogic;
using Uri=Android.Net.Uri;

namespace Lantern.AndroidApp;

public sealed partial class AndroidHost
{
    public int ApplicationVersionCode
    {
        get
        {
            var info=activity.PackageManager!.GetPackageInfo(activity.PackageName!,PackageInfoFlags.MetaData)!;
            return checked((int)(Build.VERSION.SdkInt>=BuildVersionCodes.P?info.LongVersionCode:info.VersionCode));
        }
    }
    public Task ValidateUpdateAsync(string file,OtaManifest manifest)
    {
        manifest.Validate();
        var pm=activity.PackageManager!;
        var flags=Build.VERSION.SdkInt>=BuildVersionCodes.P?PackageInfoFlags.SigningCertificates:PackageInfoFlags.Signatures;
        var archive=pm.GetPackageArchiveInfo(file,flags)??throw new InvalidDataException("无法解析安卓安装包");
        var installed=pm.GetPackageInfo(activity.PackageName!,flags)!;
        long code=Build.VERSION.SdkInt>=BuildVersionCodes.P?archive.LongVersionCode:archive.VersionCode;
        if(archive.PackageName!=activity.PackageName||archive.PackageName!=manifest.PackageId||code!=manifest.VersionCode||archive.VersionName!=manifest.Version||code<=ApplicationVersionCode)
            throw new InvalidDataException("更新包包名或版本不匹配，不允许降级或重复安装");
        Android.Content.PM.Signature[] Signers(PackageInfo info)=>
            Build.VERSION.SdkInt>=BuildVersionCodes.P?info.SigningInfo?.GetApkContentsSigners()??[]:info.Signatures?.ToArray()??[];
        var expected=Signers(installed).Select(s=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s.ToByteArray()!))).Order().ToArray();
        var actual=Signers(archive).Select(s=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s.ToByteArray()!))).Order().ToArray();
        if(expected.Length==0||!actual.SequenceEqual(expected))throw new System.Security.Cryptography.CryptographicException("更新包签名与当前应用不一致，已阻止安装");
        return Task.CompletedTask;
    }
    public bool CanInstallUpdates=>activity.PackageManager?.CanRequestPackageInstalls()==true;

    public Task OpenUpdatePermissionAsync()
    {
        if(!CanInstallUpdates)
            activity.StartActivity(new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,Uri.Parse("package:"+activity.PackageName)));
        return Task.CompletedTask;
    }

    public Task InstallUpdateAsync(string file)
    {
        if(!CanInstallUpdates)return OpenUpdatePermissionAsync();
        var uri=ReceivedFiles.Register(file);
        var intent=new Intent(Intent.ActionView);intent.SetDataAndType(uri,"application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        intent.ClipData=ClipData.NewRawUri("Lantern update",uri);
        activity.StartActivity(intent);
        return Task.CompletedTask;
    }
}
