using Android.App;
using Android.Content;
using Android.Provider;
using Android.Graphics;
using Android.Media;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Lantern.AppLogic;
using Lantern.Core;
using System.Text;
using Encoding=System.Text.Encoding;
using Path=System.IO.Path;
using Uri=Android.Net.Uri;
namespace Lantern.AndroidApp;

public sealed partial class AndroidHost(Activity activity) : IPlatformHost
{
    private TaskCompletionSource<Intent?>? picker;
    private TaskCompletionSource<Intent?>? folderPicker;
    private readonly PublicReceivedStorage storage=new(activity);
    public string ReceiveLocation=>storage.Location;
    public bool CanReceive=>storage.CanReceive;
    public Task<string> PublishReceivedAsync(string folder,CancellationToken ct)=>storage.Publish(folder,ct);
    public Task DeleteReceivedAsync(string folder)=>storage.Delete(folder);
    public async Task<System.IO.Stream> OpenReceivedPreviewAsync(string path, CancellationToken token)
    {
        try { return await storage.OpenPreview(path, token); }
        catch (Java.IO.FileNotFoundException) { throw new IOException("文件已移动或删除"); }
        catch (Java.Lang.SecurityException) { throw new IOException("手机接收目录的读取授权已失效"); }
    }
    public async Task ChooseReceiveFolderAsync()
    {
        if(folderPicker is not null)throw new IOException("文件夹选择器已打开");
        folderPicker=new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var intent=new Intent(Intent.ActionOpenDocumentTree);intent.AddFlags(ActivityFlags.GrantReadUriPermission|ActivityFlags.GrantWriteUriPermission|ActivityFlags.GrantPersistableUriPermission|ActivityFlags.GrantPrefixUriPermission);
            activity.StartActivityForResult(intent,702);
            var data=await folderPicker.Task;if(data is not null)storage.SetTree(data);
        }finally{folderPicker=null;}
    }
    public string Platform=>"Android";
    private DateTime connectionRead;
    private LanConnectionStatus connection=LanConnectionStatus.Unknown;
    public LanConnectionStatus ConnectionStatus
    {
        get
        {
            if(DateTime.UtcNow-connectionRead<TimeSpan.FromSeconds(2))return connection;
            connectionRead=DateTime.UtcNow;
            try
            {
                var wifi=(Android.Net.Wifi.WifiManager?)activity.ApplicationContext?.GetSystemService(Context.WifiService);
                var manager=(Android.Net.ConnectivityManager?)activity.ApplicationContext?.GetSystemService(Context.ConnectivityService);
                if(wifi is null||manager is null)return connection=LanConnectionStatus.Unknown;
                bool lan=false;
                foreach(var network in manager.GetAllNetworks())
                {
                    using var caps=manager.GetNetworkCapabilities(network);
                    if(caps is not null&&(caps.HasTransport(Android.Net.TransportType.Wifi)||caps.HasTransport(Android.Net.TransportType.Ethernet))) {lan=true;break;}
                }
                return connection=new(true,wifi.IsWifiEnabled,lan);
            }
            catch {return connection=LanConnectionStatus.Unknown;}
        }
    }
    public Task OpenNetworkSettingsAsync()
    {
        try {activity.StartActivity(new Intent(OperatingSystem.IsAndroidVersionAtLeast(29)?"android.settings.panel.action.WIFI":Android.Provider.Settings.ActionWifiSettings));}
        catch(Android.Content.ActivityNotFoundException){activity.StartActivity(new Intent(Android.Provider.Settings.ActionWifiSettings));}
        return Task.CompletedTask;
    }
    public object? TransferNotificationStatus {get;set;}
    // Android supplies addresses through its supported native connectivity API.
    // Avoid desktop NetworkInterface / IPv4Mask interop during Android startup.
    public Func<IReadOnlyList<LanInterface>>? NetworkInterfaces=>ReadNetworkInterfaces;
    private IReadOnlyList<LanInterface> ReadNetworkInterfaces()
    {
        var result=new List<LanInterface>();
        try
        {
            var manager=(Android.Net.ConnectivityManager?)activity.ApplicationContext?.GetSystemService(Context.ConnectivityService);
            if(manager is null)return result;
            foreach(var network in manager.GetAllNetworks())
            {
                using var properties=manager.GetLinkProperties(network);
                if(properties is null)continue;
                foreach(var link in properties.LinkAddresses)
                {
                    string? address=link.Address?.HostAddress;
                    if(System.Net.IPAddress.TryParse(address,out var ip)&&ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&LocalNetwork.IsPrivate(ip)&&!System.Net.IPAddress.IsLoopback(ip))
                        result.Add(new(ip,link.PrefixLength));
                }
            }
        }
        catch(Exception ex){Android.Util.Log.Warn("Lantern.Network",ex.ToString());}
        return result.Distinct().ToArray();
    }
    public string SystemTheme=>(activity.Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask)==Android.Content.Res.UiMode.NightYes?"dark":"light";
    public string DeviceName=>Android.OS.Build.Model??"我的 Android";
    public string DataDirectory=>activity.FilesDir!.AbsolutePath;
    public ISecretStore Secrets {get;}=new KeystoreSecrets(activity);
    public async Task<IReadOnlyList<PickedFile>> PickAsync()
    {
        if(picker is not null)throw new IOException("文件选择器已打开");
        picker=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Intent? data;
        try
        {
            var intent=new Intent(Intent.ActionOpenDocument);intent.SetType("*/*");intent.AddCategory(Intent.CategoryOpenable);intent.PutExtra(Intent.ExtraAllowMultiple,true);intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            activity.StartActivityForResult(intent,701); data=await picker.Task;
        }
        finally{picker=null;}
        if(data is null)return [];
        var uris=new List<Uri>();
        if(data.ClipData is {} clips)for(int i=0;i<clips.ItemCount;i++){if(clips.GetItemAt(i)?.Uri is {} uri)uris.Add(uri);}
        else if(data.Data is {} uri)uris.Add(uri);
        if(uris.Count>Transfers.MaxFiles)throw new IOException("一次最多选择 1000 个文件");
        var result=new List<PickedFile>();
        foreach(var uri in uris)
        {
            using var cursor=activity.ContentResolver!.Query(uri,[IOpenableColumns.DisplayName,IOpenableColumns.Size],null,null,null);
            if(cursor is null||!cursor.MoveToFirst())continue;
            string name=cursor.GetString(0)??"file";if(cursor.IsNull(1))throw new IOException("请先将未知大小的云文件保存到本机");
            long size=cursor.GetLong(1);string mime=activity.ContentResolver.GetType(uri)??"application/octet-stream";
            result.Add(new(new(name,size,mime,()=>Task.FromResult(activity.ContentResolver.OpenInputStream(uri)??throw new IOException("无法打开文件"))),await ThumbUriAsync(uri,mime)));
        }
        return result;
    }
    public void CompletePick(int code,Result result,Intent? data){if(code==701)picker?.TrySetResult(result==Result.Ok?data:null);if(code==702)folderPicker?.TrySetResult(result==Result.Ok?data:null);}
    public void CancelPick(){picker?.TrySetResult(null);folderPicker?.TrySetResult(null);}
    public Task<string?> ThumbnailAsync(string file,string mime)=>ThumbUriAsync(Uri.FromFile(new Java.IO.File(file))!,mime);
    private Task<string?> ThumbUriAsync(Uri uri,string mime)=>Task.Run<string?>(()=>
    {
        Bitmap? bitmap=null;
        try
        {
            if(mime.StartsWith("image/"))
            {
                // Read dimensions without allocating pixels, then decode only enough
                // for a small preview. A fixed sample can still allocate huge panoramas.
                using var bounds=new BitmapFactory.Options{InJustDecodeBounds=true};
                using(var probe=activity.ContentResolver!.OpenInputStream(uri))BitmapFactory.DecodeStream(probe,null,bounds);
                if(bounds.OutWidth<=0||bounds.OutHeight<=0)return null;
                int sample=1;
                while(bounds.OutWidth/sample>480||bounds.OutHeight/sample>320)sample*=2;
                using var stream=activity.ContentResolver!.OpenInputStream(uri);
                using var options=new BitmapFactory.Options{InSampleSize=sample};
                bitmap=BitmapFactory.DecodeStream(stream,null,options);
            }
            else if(mime.StartsWith("video/"))
            {
                using var retriever=new MediaMetadataRetriever();retriever.SetDataSource(activity,uri);
                if(OperatingSystem.IsAndroidVersionAtLeast(27))bitmap=retriever.GetScaledFrameAtTime(0,Option.ClosestSync,240,160);
                else
                {
                    // API 26 has no scaled extraction; avoid full-size high-resolution
                    // frames and show the format icon instead of risking an OOM.
                    if(!int.TryParse(retriever.ExtractMetadata(MetadataKey.VideoWidth),out int width)||!int.TryParse(retriever.ExtractMetadata(MetadataKey.VideoHeight),out int height)||width<=0||height<=0||(long)width*height>4_000_000)return null;
                    bitmap=retriever.GetFrameAtTime(0);
                }
            }
            if(bitmap is null)return null;
            double scale=Math.Min(1,Math.Min(240d/bitmap.Width,160d/bitmap.Height));
            using var thumb=Bitmap.CreateScaledBitmap(bitmap,Math.Max(1,(int)(bitmap.Width*scale)),Math.Max(1,(int)(bitmap.Height*scale)),true);
            using var output=new MemoryStream();thumb.Compress(Bitmap.CompressFormat.Jpeg!,75,output);
            return "data:image/jpeg;base64,"+Convert.ToBase64String(output.ToArray());
        }
        catch{return null;}
        finally{bitmap?.Dispose();}
    });
    public Task OpenFolderAsync(string folder)
    {
        if(folder.StartsWith("lantern-public:",StringComparison.Ordinal)){storage.Open(folder);return Task.CompletedTask;}
        folder=storage.ValidatePrivate(folder);
        string root=Path.GetFullPath(Path.Combine(DataDirectory,"Inbox"))+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(folder).StartsWith(root,StringComparison.Ordinal))throw new IOException("Invalid folder");
        var files=Directory.GetFiles(folder);
        new AlertDialog.Builder(activity)!.SetTitle("已接收的文件")!.SetItems(files.Select(Path.GetFileName).ToArray()!,(_,args)=>
        {
            try
            {
                var uri=ReceivedFiles.Register(files[args.Which]);
                var intent=new Intent(Intent.ActionView);intent.SetDataAndType(uri,ReceivedFiles.Mime(files[args.Which]));intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                activity.StartActivity(Intent.CreateChooser(intent,"打开文件"));
            }
            catch { new AlertDialog.Builder(activity)!.SetMessage("没有可打开此格式的应用")!.SetPositiveButton("确定",(_,_)=>{})!.Show(); }
        })!.SetNegativeButton("关闭",(_,_)=>{})!.Show();
        return Task.CompletedTask;
    }
}
public sealed class KeystoreSecrets : ISecretStore
{
    private readonly ISharedPreferences preferences;
    private readonly KeyStore store;
    public KeystoreSecrets(Context context)
    {
        preferences=context.GetSharedPreferences("lantern.encrypted.keys",FileCreationMode.Private)!;
        store=KeyStore.GetInstance("AndroidKeyStore")!;store.Load(null);
        if(!store.ContainsAlias("lantern.master.v1"))
        {
            using var generator=KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes!,"AndroidKeyStore")!;
            generator.Init(new KeyGenParameterSpec.Builder("lantern.master.v1",KeyStorePurpose.Encrypt|KeyStorePurpose.Decrypt).SetBlockModes(KeyProperties.BlockModeGcm!).SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone!).SetRandomizedEncryptionRequired(true).Build());
            generator.GenerateKey();
        }
    }
    public Task<string?> GetAsync(string key)
    {
        string? encoded=preferences.GetString(key,null);if(encoded is null)return Task.FromResult<string?>(null);
        byte[] bytes=Convert.FromBase64String(encoded);if(bytes.Length<28)throw new IOException("Invalid secret");
        using var cipher=Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.DecryptMode,store.GetKey("lantern.master.v1",null),new GCMParameterSpec(128,bytes[..12]));
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(key));
        byte[] plain=cipher.DoFinal(bytes[12..])!;
        try{return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}
    }
    public Task SetAsync(string key,string value)
    {
        using var cipher=Cipher.GetInstance("AES/GCM/NoPadding")!;cipher.Init(CipherMode.EncryptMode,store.GetKey("lantern.master.v1",null));cipher.UpdateAAD(Encoding.UTF8.GetBytes(key));
        byte[] plain=Encoding.UTF8.GetBytes(value);
        try{byte[] encrypted=cipher.DoFinal(plain)!;if(!preferences.Edit()!.PutString(key,Convert.ToBase64String(cipher.GetIV()!.Concat(encrypted).ToArray()))!.Commit())throw new IOException("设备密钥保存失败");}
        finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}
        return Task.CompletedTask;
    }
    public Task RemoveAsync(string key){preferences.Edit()!.Remove(key)!.Commit();return Task.CompletedTask;}
}
