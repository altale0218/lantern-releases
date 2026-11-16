using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Lantern.AppLogic;
using Lantern.Core;
namespace Lantern.Windows;

public sealed class WindowsHost(Form owner) : IPlatformHost
{
    public string Platform => "Windows";
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
                var adapters=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                bool lan=adapters.Any(a=>a.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up&&
                    a.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211&&
                    a.GetIPProperties().UnicastAddresses.Any(u=>u.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&LocalNetwork.IsPrivate(u.Address)));
                return connection=new(true,lan,lan);
            }
            catch{return connection=LanConnectionStatus.Unknown;}
        }
    }
    public Task OpenNetworkSettingsAsync(){System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-wifi"){UseShellExecute=true});return Task.CompletedTask;}
    public string SystemTheme => CurrentSystemTheme();
    public static string CurrentSystemTheme()
    {
        try { using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");return key?.GetValue("AppsUseLightTheme") is int value&&value==0?"dark":"light"; }
        catch{return "dark";}
    }
    public string DeviceName => Environment.MachineName;
    public string DataDirectory => Environment.GetEnvironmentVariable("LANTERN_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Lantern");
    private WindowsReceiveStorage? receivedStorage;
    private WindowsReceiveStorage Storage=>receivedStorage??=new(DataDirectory);
    public string ReceiveLocation=>Storage.Location;
    public string TransferInbox=>Storage.Location;
    public Task ChooseReceiveFolderAsync()
    {
        using var dialog=new FolderBrowserDialog {Description="选择接收文件的默认保存文件夹",UseDescriptionForTitle=true,SelectedPath=ReceiveLocation,ShowNewFolderButton=true};
        if(dialog.ShowDialog(owner)==DialogResult.OK)Storage.SetLocation(dialog.SelectedPath);
        return Task.CompletedTask;
    }
    public Task ResetReceiveFolderAsync(){Storage.SetLocation(null);return Task.CompletedTask;}
    public Task<string> PublishReceivedAsync(string folder,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(Storage.Register(folder));}
    public ISecretStore Secrets { get; } = new DpapiStore();
    public async Task<IReadOnlyList<PickedFile>> PickAsync()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "选择需要安全传输的文件", CheckFileExists = true };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return [];
        return await PrepareFilesAsync(dialog.FileNames);
    }
    public Task<IReadOnlyList<PickedFile>> PrepareFilesAsync(IEnumerable<string> paths)
    {
        var files=paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.Run<IReadOnlyList<PickedFile>>(() =>
        {
            if (files.Length > Transfers.MaxFiles) throw new IOException("一次最多选择 1000 个文件");
            var result = files.Select(file =>
            {
                if (!File.Exists(file) || Directory.Exists(file)) throw new IOException("请拖入本机文件；文件夹暂不支持");
                var info = new FileInfo(file);
                var source = new SourceFile(info.Name, info.Length, Mime(info.Extension), () =>
                    Task.FromResult<Stream>(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan)));
                return new PickedFile(source, null, info.FullName);
            }).ToArray();

            var media = Enumerable.Range(0, result.Length).Where(index => HasThumbnail(result[index].Source.Mime));
            Parallel.ForEach(media, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
            {
                var item = result[index];
                result[index] = item with { Preview = ReadThumbnail(item.LocalPath!) };
            });
            return result;
        });
    }
    public Task<IReadOnlyList<PickedFile>> RestoreSourcesAsync(IReadOnlyList<string> paths)=>PrepareFilesAsync(paths);
    public Task OpenFolderAsync(string folder)
    {
        folder=Storage.ValidateFolder(folder);
        if(!Directory.Exists(folder))throw new IOException("接收文件夹已移动或删除");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }); return Task.CompletedTask;
    }
    public Task DeleteReceivedAsync(string folder)
    {
        Storage.DeleteFolder(folder);
        return Task.CompletedTask;
    }
    private string ValidateReceivedFile(string file)
    {
        return Storage.ValidateFile(file);
    }
    public Task<Stream> OpenReceivedPreviewAsync(string path, CancellationToken token)
    {
        return Task.FromResult<Stream>(new FileStream(ValidateReceivedFile(path), FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous));
    }
    public Task OpenLocalFileAsync(string file,bool reveal,bool received)
    {
        string full=received?ValidateReceivedFile(file):Path.GetFullPath(file);
        if(!File.Exists(full))throw new IOException("文件已移动或删除");
        if(reveal)
        {
            var info=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){UseShellExecute=false};
            info.ArgumentList.Add("/select,");info.ArgumentList.Add(full);System.Diagnostics.Process.Start(info);
        }
        else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full){UseShellExecute=true});
        return Task.CompletedTask;
    }
    public Task CopyLocalFileAsync(string file,bool received)
    {
        string full=received?ValidateReceivedFile(file):Path.GetFullPath(file);
        if(!File.Exists(full))throw new IOException("文件已移动或删除");
        var data=new DataObject();data.SetFileDropList(new System.Collections.Specialized.StringCollection{full});
        data.SetData("Preferred DropEffect",new MemoryStream(BitConverter.GetBytes(1)));
        try{Clipboard.SetDataObject(data,true,5,100);}
        catch(ExternalException){throw new IOException("剪贴板正在被其他程序使用，请稍后重试");}
        return Task.CompletedTask;
    }
    public Task DeleteLocalFileAsync(string file)
    {
        string full=ValidateReceivedFile(file);File.Delete(full);return Task.CompletedTask;
    }
    public Task<string?> ThumbnailAsync(string file,string mime) =>
        HasThumbnail(mime) ? Task.Run(() => ReadThumbnail(file)) : Task.FromResult<string?>(null);

    private static bool HasThumbnail(string mime) =>
        mime.StartsWith("image/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal);

    private static string? ReadThumbnail(string file)
    {
        IntPtr handle = IntPtr.Zero; IShellItemImageFactory? factory = null;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(file,IntPtr.Zero,ref iid,out factory) != 0 || factory is null) return null;
            if (factory.GetImage(new SIZE(240,160),8,out handle) != 0 || handle == IntPtr.Zero) return null;
            using var image = Image.FromHbitmap(handle); using var output = new MemoryStream();
            image.Save(output,System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch { return null; }
        finally { if (handle != IntPtr.Zero) DeleteObject(handle); if(factory is not null) Marshal.ReleaseComObject(factory); }
    }
    private static string Mime(string ext) => ext.ToLowerInvariant() switch
    { ".jpg" or ".jpeg"=>"image/jpeg", ".png"=>"image/png", ".webp"=>"image/webp", ".gif"=>"image/gif", ".mp4"=>"video/mp4", ".mov"=>"video/quicktime", ".mkv"=>"video/x-matroska", ".docx"=>"application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".pptx"=>"application/vnd.openxmlformats-officedocument.presentationml.presentation", ".xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".doc"=>"application/msword", ".ppt"=>"application/vnd.ms-powerpoint", ".xls"=>"application/vnd.ms-excel", ".csv"=>"text/csv", ".md"=>"text/markdown", ".pdf"=>"application/pdf", ".zip"=>"application/zip", _=>"application/octet-stream" };
    [StructLayout(LayoutKind.Sequential)] private struct SIZE(int cx,int cy) { public int Cx=cx; public int Cy=cy; }
    [ComImport,Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size,uint flags,out IntPtr bitmap); }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)] private static extern int SHCreateItemFromParsingName(string path,IntPtr bind,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItemImageFactory? item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
public sealed class DpapiStore : ISecretStore
{
    private readonly string directory = Path.Combine(Environment.GetEnvironmentVariable("LANTERN_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Lantern"),"Keys");
    private string FilePath(string key) => Path.Combine(directory,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))+".key");
    public async Task<string?> GetAsync(string key)
    {
        string file = FilePath(key); if(!File.Exists(file))return null;
        byte[] plain=ProtectedData.Unprotect(await File.ReadAllBytesAsync(file),null,DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public async Task SetAsync(string key,string value)
    {
        Directory.CreateDirectory(directory); byte[] plain=Encoding.UTF8.GetBytes(value);
        try { string file=FilePath(key); await File.WriteAllBytesAsync(file+".tmp",ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser)); File.Move(file+".tmp",file,true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public Task RemoveAsync(string key) { File.Delete(FilePath(key)); return Task.CompletedTask; }
}
