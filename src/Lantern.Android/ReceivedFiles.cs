using Android.Content;
using Android.Database;
using Android.OS;
using Uri=Android.Net.Uri;
namespace Lantern.AndroidApp;

// Files are exposed only after an explicit user action, using a temporary read-only grant.
[ContentProvider(["com.lantern.transfer.files"],Exported=false,GrantUriPermissions=true)]
public sealed class ReceivedFiles : ContentProvider
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> grants=new();
    public static Uri Register(string file){string token=Guid.NewGuid().ToString("N");grants[token]=file;return Uri.Parse("content://com.lantern.transfer.files/"+token)!;}
    private static string Resolve(Uri uri)=>grants.TryGetValue(uri.LastPathSegment??"",out var file)?file:throw new Java.IO.FileNotFoundException();
    public static string Mime(string path)=>Path.GetExtension(path).ToLowerInvariant() switch{ ".jpg" or ".jpeg"=>"image/jpeg",".png"=>"image/png",".webp"=>"image/webp",".gif"=>"image/gif",".heic"=>"image/heic",".mp4"=>"video/mp4",".mov"=>"video/quicktime",".mkv"=>"video/x-matroska",".mp3"=>"audio/mpeg",".m4a"=>"audio/mp4",".txt"=>"text/plain",".docx"=>"application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".pptx"=>"application/vnd.openxmlformats-officedocument.presentationml.presentation", ".xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".doc"=>"application/msword", ".ppt"=>"application/vnd.ms-powerpoint", ".xls"=>"application/vnd.ms-excel", ".csv"=>"text/csv", ".md"=>"text/markdown", ".pdf"=>"application/pdf",".zip"=>"application/zip",".apk"=>"application/vnd.android.package-archive",_=>"application/octet-stream"};
    public override bool OnCreate()=>true;
    public override string? GetType(Uri uri)=>Mime(Resolve(uri));
    public override ParcelFileDescriptor? OpenFile(Uri uri,string mode)
    {if(mode!="r")throw new Java.Lang.SecurityException("Read only");return ParcelFileDescriptor.Open(new Java.IO.File(Resolve(uri)),ParcelFileMode.ReadOnly);}
    public override ICursor? Query(Uri uri,string[]? projection,string? selection,string[]? selectionArgs,string? sortOrder)
    {var file=new FileInfo(Resolve(uri));var cursor=new MatrixCursor(["_display_name","_size"]);cursor.AddRow(new Java.Lang.Object[]{new Java.Lang.String(file.Name),Java.Lang.Long.ValueOf(file.Length)!});return cursor;}
    public override Uri? Insert(Uri uri,ContentValues? values)=>throw new Java.Lang.UnsupportedOperationException();
    public override int Delete(Uri uri,string? selection,string[]? selectionArgs)=>throw new Java.Lang.UnsupportedOperationException();
    public override int Update(Uri uri,ContentValues? values,string? selection,string[]? selectionArgs)=>throw new Java.Lang.UnsupportedOperationException();
}
