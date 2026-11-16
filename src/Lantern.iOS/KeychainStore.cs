using Foundation;
using Lantern.Core;
using Security;

namespace Lantern.iOS;

internal sealed class KeychainStore : ISecretStore
{
    public Task<string?> GetAsync(string key)
    {
        using var search = Record(key);
        using var found = SecKeyChain.QueryAsRecord(search, out var result);
        if (result == SecStatusCode.ItemNotFound) return Task.FromResult<string?>(null);
        Check(result);
        using var bytes = found?.ValueData;
        if (bytes is null) throw new IOException("钥匙串条目缺少数据");
        return Task.FromResult<string?>(NSString.FromData(bytes, NSStringEncoding.UTF8)?.ToString() ??
            throw new IOException("钥匙串条目编码无效"));
    }

    public Task SetAsync(string key, string value)
    {
        using var search = Record(key);
        using var bytes = NSData.FromString(value, NSStringEncoding.UTF8);
        using var update = new SecRecord { ValueData = bytes };
        var result = SecKeyChain.Update(search, update);
        if (result == SecStatusCode.ItemNotFound)
        {
            search.ValueData = bytes;
            search.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
            result = SecKeyChain.Add(search);
            if (result == SecStatusCode.DuplicateItem)
            {
                using var existing = Record(key);
                result = SecKeyChain.Update(existing, update);
            }
        }
        Check(result);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        using var search = Record(key);
        var result = SecKeyChain.Remove(search);
        if (result != SecStatusCode.ItemNotFound) Check(result);
        return Task.CompletedTask;
    }

    private static SecRecord Record(string key) => new(SecKind.GenericPassword)
    {
        Service = "com.lantern.transfer.local",
        Account = key,
        Synchronizable = false
    };

    private static void Check(SecStatusCode status)
    {
        if (status != SecStatusCode.Success) throw new IOException("无法访问设备钥匙串（" + status + "）");
    }
}
