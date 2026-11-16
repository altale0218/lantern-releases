namespace Lantern.Core;

public record DeviceIdentity(string Id, string Name, string Platform);
public record TrustedDevice(DeviceIdentity Identity, string Secret, DateTimeOffset PairedAt);
public record NearbyDevice(DeviceIdentity Identity, string Address, int Port, DateTimeOffset SeenAt);
public record FileOffer(string Id, string Name, long Size, string Mime);
public record TransferOffer(string Id, DeviceIdentity Sender, FileOffer[] Files)
{
    public long Total => Files.Sum(f => f.Size);
}
public record TransferProgress(string TaskId, string FileId, long Completed, long Total,
    long FileCompleted, double BytesPerSecond, string State);
public record SourceFile(string Name, long Size, string Mime, Func<Task<Stream>> OpenRead);
public interface ISecretStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}
public interface ITrustStore
{
    Task<TrustedDevice?> FindAsync(string id);
    Task SaveAsync(TrustedDevice device);
    Task<IReadOnlyList<TrustedDevice>> ListAsync();
    Task ForgetAsync(string id);
}
public sealed class TrustStore(ISecretStore secrets) : ITrustStore
{
    private readonly SemaphoreSlim gate = new(1);
    private const string Index = "lantern.trust.index.v1";
    public async Task<TrustedDevice?> FindAsync(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        var json = await secrets.GetAsync("lantern.trust." + id);
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<TrustedDevice>(json);
    }
    public async Task SaveAsync(TrustedDevice device)
    {
        if (!Guid.TryParseExact(device.Identity.Id, "N", out _) || Convert.FromBase64String(device.Secret).Length != 32)
            throw new InvalidDataException("Invalid device identity");
        await gate.WaitAsync();
        try
        {
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(await secrets.GetAsync(Index) ?? "[]")!;
            await secrets.SetAsync("lantern.trust." + device.Identity.Id, System.Text.Json.JsonSerializer.Serialize(device));
            if (!ids.Contains(device.Identity.Id)) ids.Add(device.Identity.Id);
            await secrets.SetAsync(Index, System.Text.Json.JsonSerializer.Serialize(ids));
        }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<TrustedDevice>> ListAsync()
    {
        var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(await secrets.GetAsync(Index) ?? "[]")!;
        var devices = await Task.WhenAll(ids.Select(FindAsync));
        return devices.OfType<TrustedDevice>().ToArray();
    }
    public async Task ForgetAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(await secrets.GetAsync(Index) ?? "[]")!;
            ids.Remove(id);
            await secrets.RemoveAsync("lantern.trust." + id);
            await secrets.SetAsync(Index, System.Text.Json.JsonSerializer.Serialize(ids));
        }
        finally { gate.Release(); }
    }
}
