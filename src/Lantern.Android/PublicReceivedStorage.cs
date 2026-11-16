using Android.App;
using Android.Content;
using Android.Provider;
using System.Text.Json;
using Path=System.IO.Path;
using Uri=Android.Net.Uri;
namespace Lantern.AndroidApp;

// Only URIs created by this app are retained; deletion never targets a selected tree.
public sealed class PublicReceivedStorage(Activity activity)
{
    public record Entry(string Uri,string Name,string Mime,string? StoredName=null);
    private readonly SemaphoreSlim gate=new(1);
    private string IndexPath=>Path.Combine(activity.FilesDir!.AbsolutePath,"received-public.json");
    private ISharedPreferences Preferences=>activity.GetSharedPreferences("lantern.storage",FileCreationMode.Private)!;
    private string? Tree=>Preferences.GetString("tree",null);
    public bool CanReceive=>Tree is {} tree?activity.ContentResolver!.PersistedUriPermissions.Any(p=>p.Uri?.ToString()==tree&&p.IsWritePermission):Android.OS.Build.VERSION.SdkInt>=Android.OS.BuildVersionCodes.Q;
    public string Location=>Tree is null?(CanReceive?"手机存储 / Download / Lantern":"请先选择手机接收文件夹"):"手机文件夹："+Uri.Decode(Tree);
    private Dictionary<string,List<Entry>> ReadIndex()=>File.Exists(IndexPath)?JsonSerializer.Deserialize<Dictionary<string,List<Entry>>>(File.ReadAllText(IndexPath))??new():new();
    private async Task WriteIndex(Dictionary<string,List<Entry>> index)
    {await File.WriteAllTextAsync(IndexPath+".tmp",JsonSerializer.Serialize(index));File.Move(IndexPath+".tmp",IndexPath,true);}
    public void SetTree(Intent intent)
    {
        var tree=intent.Data??throw new IOException("没有选择文件夹");
        activity.ContentResolver!.TakePersistableUriPermission(tree,intent.Flags&(ActivityFlags.GrantReadUriPermission|ActivityFlags.GrantWriteUriPermission));
        if(!Preferences.Edit()!.PutString("tree",tree.ToString())!.Commit())throw new IOException("保存接收位置失败");
    }
    public void UseDownloads(){if(!Preferences.Edit()!.Remove("tree")!.Commit())throw new IOException("保存接收位置失败");}
    public string ValidatePrivate(string folder)
    {
        string root=Path.GetFullPath(Path.Combine(activity.FilesDir!.AbsolutePath,"Inbox"));
        string full=Path.GetFullPath(folder);
        if(Directory.Exists(root)&&(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("不能操作链接接收目录");
        if(Path.GetDirectoryName(full)!=root||!Path.GetFileName(full).StartsWith("Transfer-",StringComparison.Ordinal))throw new IOException("不是此应用的接收任务");
        if(Directory.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("不能操作链接目录");
        return full;
    }
    public async Task<string> Publish(string folder,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var index=ReadIndex();if(folder.StartsWith("lantern-public:",StringComparison.Ordinal)){if(!index.ContainsKey(folder))throw new IOException("接收文件索引不存在");return folder;}
            folder=ValidatePrivate(folder);if(!CanReceive)throw new IOException("请先选择手机接收文件夹");
            var resolver=activity.ContentResolver!;var entries=new List<Entry>();Uri? taskDirectory=null;
            string handle="lantern-public:"+Guid.NewGuid().ToString("N");string? selectedTree=Tree;
            string taskName=Path.GetFileName(folder);
            try
            {
                if(selectedTree is not null)
                {
                    var tree=Uri.Parse(selectedTree)!;
                    var parent=DocumentsContract.BuildDocumentUriUsingTree(tree,DocumentsContract.GetTreeDocumentId(tree))!;
                    taskDirectory=DocumentsContract.CreateDocument(resolver,parent,"vnd.android.document/directory",taskName)??throw new IOException("无法创建接收文件夹，请重新选择位置");
                }
                foreach(string source in Directory.GetFiles(folder))
                {
                    ct.ThrowIfCancellationRequested();string name=Path.GetFileName(source);if(name.Length>9&&name[8]=='-')name=name[9..];
                    string mime=ReceivedFiles.Mime(source);Uri uri;
                    if(taskDirectory is not null)uri=DocumentsContract.CreateDocument(resolver,taskDirectory,mime,name)??throw new IOException("无法创建接收文件");
                    else
                    {
                        using var values=new ContentValues();values.Put("_display_name",name);values.Put("mime_type",mime);values.Put("relative_path","Download/Lantern/"+taskName+"/");values.Put("is_pending",1);
                        uri=resolver.Insert(Uri.Parse("content://media/external_primary/downloads")!,values)??throw new IOException("无法写入下载目录");
                    }
                    entries.Add(new(uri.ToString()!,name,mime,Path.GetFileName(source)));
                    await using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,1048576,true);
                    await using var output=resolver.OpenOutputStream(uri,"w")??throw new IOException("无法写入接收文件");
                    await input.CopyToAsync(output,1048576,ct);await output.FlushAsync(ct);
                }
                ct.ThrowIfCancellationRequested();
                if(taskDirectory is null)foreach(var entry in entries){using var values=new ContentValues();values.Put("is_pending",0);if(resolver.Update(Uri.Parse(entry.Uri)!,values,null,null)!=1)throw new IOException("发布下载文件失败");}
                index[handle]=entries;await WriteIndex(index);return handle;
            }
            catch
            {
                var remaining=new List<Entry>();foreach(var entry in entries)try{DeleteUri(entry.Uri);}catch{remaining.Add(entry);}
                if(remaining.Count>0){index[handle]=remaining;await WriteIndex(index);}
                if(taskDirectory is not null&&remaining.Count==0)try{DocumentsContract.DeleteDocument(resolver,taskDirectory);}catch{}
                throw;
            }
        }
        finally{gate.Release();}
    }
    private void DeleteUri(string value)
    {
        var uri=Uri.Parse(value)!;
        if(DocumentsContract.IsDocumentUri(activity,uri)){if(!DocumentsContract.DeleteDocument(activity.ContentResolver!,uri))throw new IOException("文件删除失败");}
        else activity.ContentResolver!.Delete(uri,null,null);
    }
    public async Task<Stream> OpenPreview(string path, CancellationToken token)
    {
        var folder = Path.GetDirectoryName(path) ?? throw new IOException("文件位置无效");
        var storedName = Path.GetFileName(path);
        if (!folder.StartsWith("lantern-public:", StringComparison.Ordinal))
        {
            string file = Path.Combine(ValidatePrivate(folder), storedName);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("无法预览链接文件");
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        }

        await gate.WaitAsync(token);
        try
        {
            if (!ReadIndex().TryGetValue(folder, out var entries)) throw new IOException("接收文件索引不存在");
            var entry = entries.FirstOrDefault(item => item.StoredName == storedName);
            if (entry is null && storedName.Length > 9)
            {
                var oldMatches = entries.Where(item => item.StoredName is null && item.Name == storedName[9..]).Take(2).ToArray();
                if (oldMatches.Length == 1) entry = oldMatches[0];
            }
            if (entry is null) throw new IOException("无法辨别此接收文件，请从接收位置打开");
            return activity.ContentResolver!.OpenInputStream(Uri.Parse(entry.Uri)!) ?? throw new IOException("无法读取接收文件");
        }
        finally { gate.Release(); }
    }
    public async Task Delete(string folder)
    {
        await gate.WaitAsync();try
        {
            if(!folder.StartsWith("lantern-public:",StringComparison.Ordinal)){folder=ValidatePrivate(folder);if(Directory.Exists(folder))Directory.Delete(folder,true);return;}
            var index=ReadIndex();if(!index.TryGetValue(folder,out var entries))throw new IOException("接收文件索引不存在");
            var remaining=new List<Entry>();foreach(var entry in entries)try{DeleteUri(entry.Uri);}catch(Java.IO.FileNotFoundException){}catch{remaining.Add(entry);}
            if(remaining.Count==0)index.Remove(folder);else index[folder]=remaining;
            await WriteIndex(index);if(remaining.Count>0)throw new IOException("部分文件未能删除，请检查文件夹授权后重试");
        }finally{gate.Release();}
    }
    public void Open(string handle)
    {
        var index=ReadIndex();if(!index.TryGetValue(handle,out var entries))throw new IOException("接收文件索引不存在");
        new AlertDialog.Builder(activity)!.SetTitle("已保存到手机的文件")!.SetItems(entries.Select(e=>e.Name).ToArray(),(_,args)=>
        {
            try{var file=entries[args.Which];var intent=new Intent(Intent.ActionView);intent.SetDataAndType(Uri.Parse(file.Uri),file.Mime);intent.AddFlags(ActivityFlags.GrantReadUriPermission);activity.StartActivity(Intent.CreateChooser(intent,"打开文件"));}
            catch{new AlertDialog.Builder(activity)!.SetMessage("文件已移动、删除或没有可打开此格式的应用")!.SetPositiveButton("确定",(_,_)=>{})!.Show();}
        })!.SetNegativeButton("关闭",(_,_)=>{})!.Show();
    }
}
