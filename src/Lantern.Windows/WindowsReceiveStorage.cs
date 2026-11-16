using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lantern.Windows;

// Only task directories registered after verification are eligible for file operations.
public sealed class WindowsReceiveStorage
{
    public sealed class Config
    {
        public string? Location {get;set;}
        public HashSet<string> Roots {get;set;} = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Folders {get;set;} = new(StringComparer.OrdinalIgnoreCase);
    }
    private readonly object gate=new();
    private readonly string dataDirectory,defaultDirectory,indexPath;
    private readonly Config config;
    public WindowsReceiveStorage(string dataDirectory,string? downloadsDirectory=null)
    {
        this.dataDirectory=Path.GetFullPath(dataDirectory);
        defaultDirectory=Path.GetFullPath(downloadsDirectory??Downloads());
        indexPath=Path.Combine(this.dataDirectory,"receive-location.json");
        try{config=JsonSerializer.Deserialize<Config>(File.ReadAllText(indexPath))??new();}
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){config=new();}
        config.Roots=new(config.Roots??[],StringComparer.OrdinalIgnoreCase);
        config.Folders=new(config.Folders??[],StringComparer.OrdinalIgnoreCase);
        config.Roots.Add(defaultDirectory);config.Roots.Add(Path.Combine(this.dataDirectory,"Inbox"));
        if(config.Location is {} current)config.Roots.Add(Path.GetFullPath(current));
    }
    public string Location {get{lock(gate)return config.Location??defaultDirectory;}}
    public void SetLocation(string? folder)
    {
        string target=Path.GetFullPath(folder??defaultDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if(Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar)==target)throw new IOException("请选择磁盘内的文件夹");
        Directory.CreateDirectory(target);CheckLinks(target);
        // Fail during selection rather than accepting a transfer into an unwritable directory.
        using(var probe=new FileStream(Path.Combine(target,".lantern-write-"+Guid.NewGuid().ToString("N")),FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose)){}
        lock(gate)
        {
            string? old=config.Location;bool added=config.Roots.Add(target);config.Location=folder is null?null:target;
            try{Save();}catch{config.Location=old;if(added)config.Roots.Remove(target);throw;}
        }
    }
    public string Register(string folder)
    {
        lock(gate)
        {
            string full=Path.GetFullPath(folder),parent=Path.GetDirectoryName(full)??"";
            if(!config.Roots.Contains(parent)||!IsTaskFolder(full))throw new IOException("接收任务目录无效");
            CheckLinks(full);
            foreach(string entry in Directory.EnumerateFileSystemEntries(full))
                if(Directory.Exists(entry)||(File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0)throw new IOException("接收目录包含不支持的链接或子目录");
            if(config.Folders.Add(full))try{Save();}catch{config.Folders.Remove(full);throw;}
            return full;
        }
    }
    public string ValidateFolder(string folder)
    {
        lock(gate)
        {
            string full=Path.GetFullPath(folder);
            bool legacy=string.Equals(Path.GetDirectoryName(full),Path.Combine(dataDirectory,"Inbox"),StringComparison.OrdinalIgnoreCase)&&IsTaskFolder(full);
            if(!config.Folders.Contains(full)&&!legacy)throw new IOException("不是此应用保存的接收任务");
            CheckLinks(full);return full;
        }
    }
    public string ValidateFile(string file)
    {
        string full=Path.GetFullPath(file);ValidateFolder(Path.GetDirectoryName(full)??"");CheckLinks(full);return full;
    }
    public void DeleteFolder(string folder)
    {
        string full=ValidateFolder(folder);
        if(!Directory.Exists(full))return;
        var entries=Directory.GetFileSystemEntries(full);
        foreach(var entry in entries)if(Directory.Exists(entry)||(File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0)throw new IOException("目录内有新增子目录或链接，请从文件管理器处理");
        foreach(var entry in entries){ValidateFile(entry);File.Delete(entry);}
        Directory.Delete(full,false); // Never recursively delete a user-selected root.
    }
    private void Save()
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(indexPath+".tmp",JsonSerializer.Serialize(config));File.Move(indexPath+".tmp",indexPath,true);
    }
    private static bool IsTaskFolder(string path)=>Path.GetFileName(path) is {} name&&name.StartsWith("Transfer-",StringComparison.Ordinal)&&Guid.TryParseExact(name[9..],"N",out _);
    private static void CheckLinks(string path)
    {
        for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("不能操作链接文件或目录");
    }
    public static string Downloads()
    {
        // Resolve the Windows known folder, respecting redirection and localized names.
        Guid id=new("374DE290-123F-4565-9164-39C4925E467B");
        int result=SHGetKnownFolderPath(ref id,0,IntPtr.Zero,out var buffer);
        try{Marshal.ThrowExceptionForHR(result);return Marshal.PtrToStringUni(buffer)??throw new IOException("无法取得系统下载文件夹");}
        finally{if(buffer!=IntPtr.Zero)Marshal.FreeCoTaskMem(buffer);}
    }
    [DllImport("shell32.dll")]private static extern int SHGetKnownFolderPath(ref Guid id,uint flags,IntPtr token,out IntPtr path);
}
