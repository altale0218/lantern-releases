using CoreGraphics;
using Foundation;
using ImageIO;
using Lantern.AppLogic;
using Lantern.Core;
using QuickLook;
using QuickLookThumbnailing;
using UIKit;
using UniformTypeIdentifiers;

namespace Lantern.iOS;

internal sealed class AppleHost(Func<UIViewController> presenter) : IPlatformHost
{
    private bool picking;
    public string Platform { get; } = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad ? "iPadOS" : "iOS";
    public string DeviceName => Platform == "iPadOS" ? "iPad" : "iPhone";
    public string SystemTheme { get; set; } = "light";
    public double SystemFontScale { get; set; } = 1;
    public bool UseUdpDiscovery => false;
    public string DataDirectory { get; } = Path.Combine(Folder(NSSearchPathDirectory.ApplicationSupportDirectory), "Lantern");
    private string Documents => Folder(NSSearchPathDirectory.DocumentDirectory);
    public string TransferInbox => Path.Combine(DataDirectory, "Incoming");
    public string ReceiveLocation => "文件 App / 我的" + DeviceName + " / 闪传 Lantern / Received";
    private string Received => Path.Combine(Documents, "Received");
    public ISecretStore Secrets { get; } = new KeychainStore();

    public void PrepareDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        using var directory = NSUrl.FromFilename(DataDirectory);
        using var exclude = NSNumber.FromBoolean(true);
        // The Keychain entry is bound to this device; backing up its ciphertext cannot restore that history on another device.
        if (!directory.SetResource(NSUrl.IsExcludedFromBackupKey, exclude, out _))
            System.Diagnostics.Trace.TraceWarning("Could not exclude the private Lantern directory from device backup.");
    }

    public Task OpenNetworkSettingsAsync()
    {
        using var settings = new NSUrl(UIApplication.OpenSettingsUrlString);
        return UIApplication.SharedApplication.OpenUrlAsync(settings, new UIApplicationOpenUrlOptions());
    }

    private static string Folder(NSSearchPathDirectory location) => NSFileManager.DefaultManager
        .GetUrls(location, NSSearchPathDomain.User)[0].Path ?? throw new IOException("无法打开本机目录");

    public async Task<IReadOnlyList<PickedFile>> PickAsync()
    {
        if (picking) throw new IOException("文件选择器已打开");
        picking = true;
        try
        {
            var answer = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var picker = new UIDocumentPickerViewController([UTTypes.Item], asCopy: true) { AllowsMultipleSelection = true };
            picker.DidPickDocumentAtUrls += (_, result) =>
                answer.TrySetResult(result.Urls.Select(url => url.IsFileUrl ? url.Path : null).OfType<string>().ToArray());
            picker.WasCancelled += (_, _) => answer.TrySetResult([]);
            presenter().PresentViewController(picker, true, null);
            string[] paths = await answer.Task;
            if (paths.Length > Transfers.MaxFiles) throw new IOException("一次最多选择 1000 个文件");
            var files = new List<PickedFile>();
            foreach (string path in paths.Distinct(StringComparer.Ordinal))
            {
                var item = new FileInfo(path);
                if (!item.Exists || IsLink(item.FullName)) throw new IOException("文件已移动或无法读取");
                string mime = UTType.CreateFromExtension(item.Extension.TrimStart('.'))?.PreferredMimeType ?? "application/octet-stream";
                files.Add(new(new(item.Name, item.Length, mime, () => Task.FromResult<Stream>(Read(item.FullName))),
                    await ThumbnailAsync(item.FullName, mime), item.FullName));
            }
            return files;
        }
        finally { picking = false; }
    }

    public Task<string?> ThumbnailAsync(string file, string mime)
    {
        if (mime.StartsWith("video/", StringComparison.Ordinal)) return VideoThumbnailAsync(file);
        if (!mime.StartsWith("image/", StringComparison.Ordinal)) return Task.FromResult<string?>(null);
        return Task.Run<string?>(() =>
        {
            using var url = NSUrl.FromFilename(file);
            using var source = CGImageSource.FromUrl(url);
            using var thumbnail = source?.CreateThumbnail(0, new CGImageThumbnailOptions
            {
                CreateThumbnailFromImageAlways = true,
                CreateThumbnailWithTransform = true,
                MaxPixelSize = 320
            });
            if (thumbnail is null) return null;
            using var image = new UIImage(thumbnail);
            return EncodeThumbnail(image);
        });
    }

    private static async Task<string?> VideoThumbnailAsync(string file)
    {
        if (Path.GetExtension(file).ToLowerInvariant() is not (".mov" or ".mp4" or ".m4v")) return null;
        using var url = NSUrl.FromFilename(file);
        using var request = new QLThumbnailGenerationRequest(url, new CGSize(320, 320), 1,
            QLThumbnailGenerationRequestRepresentationTypes.Thumbnail);
        var generator = QLThumbnailGenerator.SharedGenerator;
        try
        {
            using var thumbnail = await generator.GenerateBestRepresentationAsync(request).WaitAsync(TimeSpan.FromSeconds(2));
            using var image = thumbnail?.UIImage;
            return image is null ? null : EncodeThumbnail(image);
        }
        catch (NSErrorException) { return null; }
        catch (TimeoutException) { generator.CancelRequest(request); return null; }
    }

    private static string? EncodeThumbnail(UIImage image)
    {
        using var jpeg = image.AsJPEG(.75f);
        return jpeg is { Length: > 0 and <= 131072 } ? "data:image/jpeg;base64," + Convert.ToBase64String(jpeg.ToArray()) : null;
    }

    public Task<string> PublishReceivedAsync(string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string staged = BatchFolder(folder, TransferInbox);
        Directory.CreateDirectory(Received);
        if (IsLink(Received)) throw new IOException("接收位置无法使用");
        string destination = Path.Combine(Received, Path.GetFileName(staged));
        Directory.Move(staged, destination);
        return Task.FromResult(destination);
    }

    public Task OpenFolderAsync(string folder)
    {
        var paths = Directory.EnumerateFiles(BatchFolder(folder, Received)).Where(path => !IsLink(path)).ToArray();
        ShowPreview(paths);
        return Task.CompletedTask;
    }

    public Task OpenLocalFileAsync(string file, bool reveal, bool received)
    {
        string path = received ? ReceivedFile(file) : Path.GetFullPath(file);
        if (!File.Exists(path) || IsLink(path)) throw new IOException("文件已移动或删除");
        if (reveal && received) return OpenFolderAsync(Path.GetDirectoryName(path)!);
        ShowPreview([path]);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReceivedPreviewAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(Read(ReceivedFile(path)));
    }

    public Task DeleteReceivedAsync(string folder)
    {
        Directory.Delete(BatchFolder(folder, Received), true);
        return Task.CompletedTask;
    }

    public Task DeleteLocalFileAsync(string file)
    {
        File.Delete(ReceivedFile(file));
        return Task.CompletedTask;
    }

    private string ReceivedFile(string file)
    {
        string path = Path.GetFullPath(file);
        BatchFolder(Path.GetDirectoryName(path)!, Received);
        if (!File.Exists(path) || IsLink(path)) throw new IOException("文件已移动或删除");
        return path;
    }

    private static string BatchFolder(string folder, string root)
    {
        string path = Path.GetFullPath(folder);
        string name = Path.GetFileName(path);
        if (!name.StartsWith("Transfer-", StringComparison.Ordinal) || !Guid.TryParseExact(name[9..], "N", out _) ||
            Path.GetDirectoryName(path) != root || !Directory.Exists(path) || IsLink(root) || IsLink(path))
            throw new IOException("接收文件夹已移动或无法读取");
        return path;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static FileStream Read(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private void ShowPreview(string[] paths)
    {
        if (paths.Length == 0) throw new IOException("没有可打开的接收文件");
        var preview = new FilePreview(paths);
        preview.NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) =>
            preview.DismissViewController(true, null));
        presenter().PresentViewController(new UINavigationController(preview), true, null);
    }

    private sealed class FilePreview : QLPreviewController
    {
        private readonly FileItems items;
        public FilePreview(string[] paths) { items = new(paths); DataSource = items; }
    }

    private sealed class FileItems(string[] paths) : QLPreviewControllerDataSource
    {
        public override nint PreviewItemCount(QLPreviewController controller) => paths.Length;
        public override IQLPreviewItem GetPreviewItem(QLPreviewController controller, nint index) => new FileItem(paths[(int)index]);
    }

    private sealed class FileItem(string path) : QLPreviewItem
    {
        public override NSUrl PreviewItemUrl { get; } = NSUrl.FromFilename(path);
        public override string PreviewItemTitle => Path.GetFileName(path);
    }
}
