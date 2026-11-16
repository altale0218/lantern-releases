using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;

namespace Lantern.Core;

public static class Wire
{
    public const int MaxRecord = 1024 * 1024 + 4096;
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length is < 1 or > MaxRecord) throw new InvalidDataException("Record length");
        var buffer = ArrayPool<byte>.Shared.Rent(bytes.Length + 4);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, bytes.Length);
            bytes.CopyTo(buffer.AsMemory(4));
            await WriteFramedAsync(stream, buffer.AsMemory(0, bytes.Length + 4), ct);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    internal static async Task WriteFramedAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await stream.WriteAsync(frame, ct);
        if (stream is not NetworkStream) await stream.FlushAsync(ct);
    }
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct, int max = MaxRecord)
    {
        byte[] header = new byte[4];
        int length = await ReadLengthAsync(stream, header, max, ct);
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }
    internal static async Task<int> ReadLengthAsync(Stream stream, byte[] header, int max, CancellationToken ct)
    {
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > max) throw new InvalidDataException("Record length");
        return length;
    }
    public static Task JsonAsync<T>(Stream stream, T obj, CancellationToken ct) => WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(obj), ct);
    public static async Task<T> JsonAsync<T>(Stream stream, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await ReadAsync(stream, ct, 16384)) ?? throw new InvalidDataException("Empty handshake");
}

// One reader and one writer per connection. Direction-separated keys, a strict counter,
// and authenticated length/sequence prevent nonce reuse, reordering and replay.
public sealed class SecureChannel : IAsyncDisposable
{
    internal const int MaxPlaintext = Wire.MaxRecord - 24;
    private readonly Stream stream;
    private readonly AesGcm send, receive;
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly byte[] receiveHeader = new byte[4];
    private ulong sendSequence, receiveSequence;
    private volatile bool faulted;
    public SecureChannel(Stream stream, ReadOnlySpan<byte> material, bool client)
    {
        this.stream = stream;
        Span<byte> c2s = stackalloc byte[32];
        Span<byte> s2c = stackalloc byte[32];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, material, c2s, salt: default, info: "Lantern/v1/client-write"u8);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, material, s2c, salt: default, info: "Lantern/v1/server-write"u8);
            send = new AesGcm(client ? c2s : s2c, 16);
            try { receive = new AesGcm(client ? s2c : c2s, 16); }
            catch { send.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(c2s); CryptographicOperations.ZeroMemory(s2c); }
    }
    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length > MaxPlaintext) throw new InvalidDataException("Record too large");
        await sendGate.WaitAsync(ct);
        byte[]? buffer = null;
        try
        {
            if (faulted || sendSequence == ulong.MaxValue) throw new IOException("Channel closed");
            int frameSize = bytes.Length + 28;
            buffer = ArrayPool<byte>.Shared.Rent(frameSize);
            Encrypt(bytes.Span, buffer.AsSpan(0, frameSize));
            await Wire.WriteFramedAsync(stream, buffer.AsMemory(0, frameSize), ct);
        }
        catch { faulted = true; throw; }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            sendGate.Release();
        }
    }
    private void Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> frame)
    {
        BinaryPrimitives.WriteInt32BigEndian(frame, frame.Length - 4);
        var aad = frame.Slice(4, 8);
        BinaryPrimitives.WriteUInt64BigEndian(aad, sendSequence++);
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        aad.CopyTo(nonce[4..]);
        send.Encrypt(nonce, plaintext, frame.Slice(12, plaintext.Length), frame[^16..], aad);
    }
    public async Task<byte[]> ReceiveAsync(CancellationToken ct) => (await ReadPlaintextAsync(null, ct)).Allocated!;
    internal async Task<int> ReceiveIntoAsync(Memory<byte> destination, CancellationToken ct) =>
        (await ReadPlaintextAsync(destination, ct)).Length;

    private async Task<(byte[]? Allocated, int Length)> ReadPlaintextAsync(Memory<byte>? destination, CancellationToken ct)
    {
        byte[]? frame = null;
        try
        {
            if (faulted || receiveSequence == ulong.MaxValue) throw new IOException("Channel closed");
            int length = await Wire.ReadLengthAsync(stream, receiveHeader, Wire.MaxRecord, ct);
            if (length < 24) throw new CryptographicException("Invalid encrypted record");
            int payloadSize = length - 24;
            if (destination is { } supplied && payloadSize > supplied.Length) throw new InvalidDataException("File length mismatch");
            frame = ArrayPool<byte>.Shared.Rent(length);
            await stream.ReadExactlyAsync(frame.AsMemory(0, length), ct);
            var allocated = destination is null ? new byte[payloadSize] : null;
            Memory<byte> plain = destination ?? allocated.AsMemory();
            Decrypt(frame.AsSpan(0, length), plain.Span[..payloadSize]);
            return (allocated, payloadSize);
        }
        catch { faulted = true; throw; }
        finally { if (frame is not null) ArrayPool<byte>.Shared.Return(frame); }
    }
    private void Decrypt(ReadOnlySpan<byte> frame, Span<byte> plaintext)
    {
        var aad = frame[..8];
        if (BinaryPrimitives.ReadUInt64BigEndian(aad) != receiveSequence)
            throw new CryptographicException("Replay or invalid sequence");
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        aad.CopyTo(nonce[4..]);
        receive.Decrypt(nonce, frame.Slice(8, plaintext.Length), frame[^16..], plaintext, aad);
        receiveSequence++;
    }
    public Task SendJsonAsync<T>(T value, CancellationToken ct) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), ct);
    public async Task<T> ReceiveJsonAsync<T>(CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await ReceiveAsync(ct)) ?? throw new InvalidDataException("Empty message");
    public async ValueTask DisposeAsync()
    {
        faulted = true; send.Dispose(); receive.Dispose(); await stream.DisposeAsync();
    }
}
