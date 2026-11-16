using System.Xml;

namespace Lantern.AppLogic;

public sealed partial class AppController
{
    private readonly SemaphoreSlim previewGate = new(1);

    private Task<object> PreviewDocumentAsync(string id, string fileId)
    {
        if (!tasks.TryGetValue(id, out var task)) throw new IOException("传输记录不存在");
        var file = task.Files.FirstOrDefault(item => item.Id == fileId) ?? throw new IOException("文件不存在");
        if (file.Deleted || !IsFinished(task.State) || controls.ContainsKey(id) ||
            task.Direction == "receive" && task.State != "completed")
            throw new IOException("文件尚未完成接收，或已删除");
        if (task.Direction == "receive")
        {
            var location = ResolveLocalFile(task, file);
            return PreviewAsync(file.Name, () => host.OpenReceivedPreviewAsync(location, life.Token));
        }
        if (file.LocalPath is not null) return PreviewAsync(file.Name, () => Task.FromResult<Stream>(DocumentPreview.OpenLocal(file.LocalPath)));
        throw new IOException("无法重新读取发送端源文件，请从文件管理器打开");
    }

    private async Task<object> PreviewSelectionAsync(string revision, string fileId)
    {
        PickedFile file;
        lock (selectionGate)
        {
            if (revision != selectionId || !int.TryParse(fileId, out int index) || index < 0 || index >= selection.Count)
                throw new IOException("文件选择已变化，请重新选中要预览的文件");
            file = selection[index];
        }
        var result = await PreviewAsync(file.Source.Name, file.Source.OpenRead);
        lock (selectionGate)
            if (revision != selectionId) throw new IOException("所选文件已经变化");
        return result;
    }

    private async Task<object> PreviewAsync(string name, Func<Task<Stream>> open)
    {
        string format = DocumentPreview.Format(name) ?? throw new IOException("此格式请使用系统应用打开");
        if (!await previewGate.WaitAsync(0, life.Token)) throw new IOException("正在读取其他预览，请稍后重试");
        try
        {
            return await Task.Run(async () =>
            {
                await using var input = await open();
                var document = await DocumentPreview.ReadAsync(input, format, life.Token);
                return new { ok = true, document };
            }, life.Token);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new IOException("文件已移动、删除，或无法读取");
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or System.Text.DecoderFallbackException)
        {
            throw new IOException("无法预览该文档，请检查文件或使用系统应用打开");
        }
        finally { previewGate.Release(); }
    }
}
