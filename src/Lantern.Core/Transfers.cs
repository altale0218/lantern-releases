using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace Lantern.Core;

public sealed class TransferControl
{
    private readonly object gate = new();
    private TaskCompletionSource<bool>? paused;
    public bool IsPaused { get { lock (gate) return paused is not null; } }
    public void Pause() { lock (gate) paused ??= new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Resume() { lock (gate) { paused?.TrySetResult(true); paused = null; } }
    public Task WaitAsync(CancellationToken ct) { lock (gate) return paused?.Task.WaitAsync(ct) ?? Task.CompletedTask; }
}
public sealed record TransferOptions(int ChunkBytes = 1024 * 1024, int LimitMiB = 0);
internal sealed class SpeedMeter
{
    private readonly Queue<(long Tick,long Bytes)> samples = new();
    public SpeedMeter() => samples.Enqueue((Stopwatch.GetTimestamp(),0));
    public double Observe(long bytes)
    {
        long now=Stopwatch.GetTimestamp(); samples.Enqueue((now,bytes));
        while(samples.Count>2 && Stopwatch.GetElapsedTime(samples.Peek().Tick,now).TotalSeconds>2) samples.Dequeue();
        var first=samples.Peek(); return (bytes-first.Bytes)/Math.Max(.01,Stopwatch.GetElapsedTime(first.Tick,now).TotalSeconds);
    }
}
public record Decision(bool Accept);
public record FileDigest(string Id, string Sha256);
public record Receipt(string TaskId, bool Verified);
public static class Transfers
{
    public const int MaxFiles = 1000;
    public const long MaxFileBytes = 64L * 1024 * 1024 * 1024;
    public const long MaxTaskBytes = 256L * 1024 * 1024 * 1024;
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || Reserved.Contains(name.Split('.')[0].ToUpperInvariant()))
            throw new InvalidDataException("Unsafe filename");
    }
    public static void Validate(TransferOffer offer)
    {
        if (!Guid.TryParseExact(offer.Id, "N", out _) || offer.Files.Length is < 1 or > MaxFiles) throw new InvalidDataException("Invalid task");
        long total = 0;
        var ids = new HashSet<string>();
        foreach (var file in offer.Files)
        {
            ValidateName(file.Name);
            if (!Guid.TryParseExact(file.Id, "N", out _) || !ids.Add(file.Id) || file.Size < 0 || file.Size > MaxFileBytes || file.Mime.Length > 120)
                throw new InvalidDataException("Invalid file metadata");
            total = checked(total + file.Size);
        }
        if (total > MaxTaskBytes) throw new InvalidDataException("Task exceeds quota");
    }
    public static async Task SendAsync(SecureChannel channel, DeviceIdentity me, IReadOnlyList<SourceFile> sources,
        string taskId, TransferOptions options, TransferControl control, Action<TransferProgress> progress, CancellationToken ct, Action<TransferOffer>? manifest = null)
    {
        var offer = new TransferOffer(taskId, me, sources.Select(f => new FileOffer(Guid.NewGuid().ToString("N"), f.Name, f.Size, f.Mime)).ToArray());
        Validate(offer);
        manifest?.Invoke(offer);
        await channel.SendJsonAsync(offer, ct);
        using (var decisionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            decisionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            if (!(await channel.ReceiveJsonAsync<Decision>(decisionTimeout.Token)).Accept) throw new IOException("对方已拒绝接收");
        }
        int chunkSize = Math.Clamp(options.ChunkBytes, 64 * 1024, 1024 * 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        long completed = 0; var meter = new SpeedMeter();
        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                var item = offer.Files[i];
                await using var source = await sources[i].OpenRead();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long fileCompleted = 0;
                while (fileCompleted < item.Size)
                {
                    await control.WaitAsync(ct);
                    long chunkStart=Stopwatch.GetTimestamp();
                    int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunkSize, item.Size - fileCompleted)), ct);
                    if (read == 0) throw new IOException("源文件在传输过程中发生变化");
                    hash.AppendData(buffer, 0, read);
                    await channel.SendAsync(buffer.AsMemory(0, read), ct);
                    fileCompleted += read; completed += read;
                    progress(new(taskId, item.Id, completed, offer.Total, fileCompleted, meter.Observe(completed), "transferring"));
                    if (options.LimitMiB > 0)
                    {
                        double delay = read / (options.LimitMiB * 1024d * 1024d) - Stopwatch.GetElapsedTime(chunkStart).TotalSeconds;
                        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    }
                }
                if (await source.ReadAsync(buffer.AsMemory(0, 1), ct) != 0) throw new IOException("源文件大小发生变化");
                await channel.SendJsonAsync(new FileDigest(item.Id, Convert.ToHexString(hash.GetHashAndReset())), ct);
                var receipt = await channel.ReceiveJsonAsync<Receipt>(ct);
                if (!receipt.Verified || receipt.TaskId != item.Id) throw new CryptographicException("File integrity failed");
            }
            progress(new(taskId, "", completed, offer.Total, 0, 0, "completed"));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public static async Task<string> ReceiveAsync(SecureChannel channel, DeviceIdentity authenticatedPeer, string inbox,
        Func<TransferOffer, Task<bool>> approve, TransferControl control, Action<TransferProgress> progress, CancellationToken ct,
        Action<TransferOffer>? manifest = null, Func<string,CancellationToken,Task>? finalize = null, JsonElement? initialMessage = null)
    {
        TransferOffer offer;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var first = initialMessage ?? await channel.ReceiveJsonAsync<JsonElement>(timeout.Token);
            if (first.TryGetProperty("PairOnly", out var pairOnly) && pairOnly.GetBoolean()) return string.Empty;
            offer = first.Deserialize<TransferOffer>() ?? throw new InvalidDataException("Invalid offer");
        }
        Validate(offer);
        if (offer.Sender.Id != authenticatedPeer.Id) throw new UnauthorizedAccessException("Sender mismatch");
        // Names shown to the user come from the authenticated identity, not manifest claims.
        offer = offer with { Sender = authenticatedPeer };
        manifest?.Invoke(offer);
        bool accepted;
        try { accepted = await approve(offer).WaitAsync(TimeSpan.FromMinutes(2), ct); }
        catch (TimeoutException) { accepted = false; }
        await channel.SendJsonAsync(new Decision(accepted), ct);
        if (!accepted) throw new IOException("已拒绝接收");
        Directory.CreateDirectory(inbox);
        string root = Path.GetFullPath(inbox);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Inbox must not be a link");
        string directory = Path.Combine(root, "Transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        long completed = 0; var meter = new SpeedMeter();
        var buffer = ArrayPool<byte>.Shared.Rent(SecureChannel.MaxPlaintext);
        try
        {
            foreach (var item in offer.Files)
            {
                string partial = Path.Combine(directory, item.Id + ".part");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long fileCompleted = 0;
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (fileCompleted < item.Size)
                    {
                        await control.WaitAsync(ct);
                        int capacity = (int)Math.Min(SecureChannel.MaxPlaintext, item.Size - fileCompleted);
                        int count = await channel.ReceiveIntoAsync(buffer.AsMemory(0, capacity), ct);
                        if (count < 1) throw new InvalidDataException("File length mismatch");
                        var bytes = buffer.AsMemory(0, count);
                        hash.AppendData(bytes.Span);
                        await output.WriteAsync(bytes, ct);
                        fileCompleted += bytes.Length; completed += bytes.Length;
                        progress(new(offer.Id, item.Id, completed, offer.Total, fileCompleted, meter.Observe(completed), "transferring"));
                    }
                    await output.FlushAsync(ct);
                }
                var digest = await channel.ReceiveJsonAsync<FileDigest>(ct);
                byte[] expected = Convert.FromHexString(digest.Sha256);
                if (digest.Id != item.Id || !CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
                    throw new CryptographicException("File SHA-256 mismatch");
                // A unique prefix avoids same-name collisions. No untrusted path components.
                File.Move(partial, Path.Combine(directory, item.Id[..8] + "-" + item.Name), false);
                // Commit the verified batch to its destination before the final success receipt.
                if(item.Id==offer.Files[^1].Id&&finalize is not null)await finalize(directory,ct);
                await channel.SendJsonAsync(new Receipt(item.Id, true), ct);
            }
            progress(new(offer.Id, "", completed, offer.Total, 0, 0, "completed"));
            return directory;
        }
        catch
        {
            // Only our freshly generated task directory can be removed; never user paths.
            if (Path.GetDirectoryName(directory) == root && Path.GetFileName(directory).StartsWith("Transfer-", StringComparison.Ordinal) && Directory.Exists(directory))
                Directory.Delete(directory, true);
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
