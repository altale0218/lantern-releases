using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using BigInt = Org.BouncyCastle.Math.BigInteger;

namespace Lantern.Core;

public record Hello(int Version, string Mode, string Id, string Ephemeral, string Nonce);
public record Pake1(string Id, string G1, string G2, string[] P1, string[] P2);
public record Pake2(string Id, string A, string[] Proof);
public record Pake3(string Id, string Mac);
public record Authentication(string Id, string Mac);
public record AuthenticatedConnection(SecureChannel Channel, DeviceIdentity Peer, JsonElement? FirstMessage=null,bool NewlyPaired=false);
public sealed record PairingChallenge(DeviceIdentity Peer,string VerificationCode,bool Outgoing,Action? Cancel=null);
public sealed record PairingApproval(int Pairing,bool Accepted);

public static class Handshake
{
    private static string Encode(BigInt value) => Convert.ToBase64String(value.ToByteArrayUnsigned());
    private static BigInt Decode(string value)
    {
        if (value.Length is < 1 or > 700) throw new InvalidDataException("PAKE integer limit");
        return new BigInt(1, Convert.FromBase64String(value));
    }
    private static BigInt[] Proof(string[] values)
    {
        if (values.Length != 2) throw new InvalidDataException("PAKE proof length");
        return values.Select(Decode).ToArray();
    }
    public static async Task<byte[]> PairKeyAsync(Stream stream, string code, bool client, CancellationToken ct)
    {
        if (code.Length != 8 || !code.All(char.IsAsciiDigit)) throw new InvalidDataException("8 digit code required");
        var password = code.ToCharArray();
        var participant = new JPakeParticipant(Guid.NewGuid().ToString("N"), password);
        Array.Clear(password);
        var r1 = participant.CreateRound1PayloadToSend();
        var mine = new Pake1(r1.ParticipantId, Encode(r1.Gx1), Encode(r1.Gx2), r1.KnowledgeProofForX1.Select(Encode).ToArray(), r1.KnowledgeProofForX2.Select(Encode).ToArray());
        await Wire.JsonAsync(stream, mine, ct);
        var other = await Wire.JsonAsync<Pake1>(stream, ct);
        if (!Guid.TryParseExact(other.Id, "N", out _)) throw new InvalidDataException("PAKE participant id");
        participant.ValidateRound1PayloadReceived(new JPakeRound1Payload(other.Id, Decode(other.G1), Decode(other.G2), Proof(other.P1), Proof(other.P2)));
        var r2 = participant.CreateRound2PayloadToSend();
        var second = new Pake2(r2.ParticipantId, Encode(r2.A), r2.KnowledgeProofForX2s.Select(Encode).ToArray());
        await Wire.JsonAsync(stream, second, ct);
        var remoteSecond = await Wire.JsonAsync<Pake2>(stream, ct);
        participant.ValidateRound2PayloadReceived(new JPakeRound2Payload(remoteSecond.Id, Decode(remoteSecond.A), Proof(remoteSecond.Proof)));
        var material = participant.CalculateKeyingMaterial();
        var r3 = participant.CreateRound3PayloadToSend(material);
        await Wire.JsonAsync(stream, new Pake3(r3.ParticipantId, Convert.ToBase64String(r3.MacTag.ToByteArray())), ct);
        var remoteThird = await Wire.JsonAsync<Pake3>(stream, ct);
        if (remoteThird.Mac.Length > 100) throw new InvalidDataException("PAKE confirmation limit");
        participant.ValidateRound3PayloadReceived(new JPakeRound3Payload(remoteThird.Id, new BigInt(Convert.FromBase64String(remoteThird.Mac))), material);
        byte[] transcript = JsonSerializer.SerializeToUtf8Bytes(client ? new object[] { mine, other, second, remoteSecond } : new object[] { other, mine, remoteSecond, second });
        byte[] raw = material.ToByteArrayUnsigned();
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 32, SHA256.HashData(transcript), "Lantern/v1/pair"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    public static async Task<AuthenticatedConnection> ConnectAsync(Stream stream, DeviceIdentity me, ITrustStore trust, string? code, string? expectedPeerId, CancellationToken ct,Func<PairingChallenge,CancellationToken,Task<bool>>? confirm=null)
    {
        if (code is not null)
        {
            await Wire.JsonAsync(stream, new Hello(1, confirm is null?"pair":"pair-confirm", "", "", ""), ct);
            var material = await PairKeyAsync(stream, code, true, ct);
            return await FinishPairAsync(stream, me, trust, material, true, ct,confirm);
        }
        var peer = await trust.FindAsync(expectedPeerId ?? "") ?? throw new UnauthorizedAccessException("Device not paired");
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new Hello(1, "trusted", me.Id, Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await Wire.JsonAsync(stream, hello, ct);
        var reply = await Wire.JsonAsync<Hello>(stream, ct);
        ValidateHello(reply);
        if (reply.Mode != "trusted" || reply.Id != peer.Identity.Id) throw new UnauthorizedAccessException("Unexpected device");
        return await FinishTrustedAsync(stream, me, peer, ephemeral, hello, reply, true, ct);
    }
    public static async Task<AuthenticatedConnection> AcceptAsync(Stream stream, DeviceIdentity me, ITrustStore trust, PairingWindow window, CancellationToken ct,Func<PairingChallenge,CancellationToken,Task<bool>>? confirm=null)
    {
        var hello = await Wire.JsonAsync<Hello>(stream, ct);
        if (hello.Version != 1) throw new InvalidDataException("Unsupported protocol version");
        if (hello.Mode is "pair" or "pair-confirm")
        {
            // A client without mutual approval support cannot silently bypass the UI.
            if((confirm is null)!=(hello.Mode=="pair"))throw new UnauthorizedAccessException("请将两端更新到支持双方确认的版本后再配对");
            var reservation=window.ReserveSession();string code=reservation.Code;bool success=false;
            using var pairing=CancellationTokenSource.CreateLinkedTokenSource(ct,reservation.Token);
            try
            {
                var material = await PairKeyAsync(stream, code, false, pairing.Token);
                var result = await FinishPairAsync(stream, me, trust, material, false, pairing.Token,confirm);
                success = true; return result;
            }
            finally { window.Complete(success); }
        }
        ValidateHello(hello);
        if (hello.Mode != "trusted") throw new InvalidDataException("Unknown handshake");
        var peer = await trust.FindAsync(hello.Id) ?? throw new UnauthorizedAccessException("Device not paired");
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var reply = new Hello(1, "trusted", me.Id, Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await Wire.JsonAsync(stream, reply, ct);
        return await FinishTrustedAsync(stream, me, peer, ephemeral, hello, reply, false, ct);
    }
    private static void ValidateHello(Hello hello)
    {
        if (hello.Version != 1 || !Guid.TryParseExact(hello.Id, "N", out _) || hello.Ephemeral.Length > 200 || Convert.FromBase64String(hello.Nonce).Length != 32)
            throw new InvalidDataException("Malformed handshake");
    }
    private static async Task<AuthenticatedConnection> FinishPairAsync(Stream stream, DeviceIdentity me, ITrustStore trust, byte[] material, bool client, CancellationToken ct,Func<PairingChallenge,CancellationToken,Task<bool>>? confirm)
    {
        var channel = new SecureChannel(stream, material, client);
        byte[] secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, info: "Lantern/v1/trust"u8.ToArray());
        CryptographicOperations.ZeroMemory(material);
        try
        {
            await channel.SendJsonAsync(me, ct);
            var peer = await channel.ReceiveJsonAsync<DeviceIdentity>(ct);
            if (!Guid.TryParseExact(peer.Id, "N", out _) || peer.Id == me.Id || peer.Name is null || peer.Name.Length is < 1 or > 80 || peer.Platform is null || peer.Platform.Length is <1 or >24)
                throw new InvalidDataException("Invalid identity");
            if(confirm is not null)
            {
                // Display a session-bound code only after the PAKE-authenticated channel
                // exists. Device names and discovery IDs are never approval credentials.
                byte[] verification=HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes("Lantern/pair-confirm/v1/"+string.Join("/",new[]{me.Id,peer.Id}.Order(StringComparer.Ordinal))));
                string digits=Convert.ToHexString(verification.AsSpan(0,4));CryptographicOperations.ZeroMemory(verification);
                using var approval=CancellationTokenSource.CreateLinkedTokenSource(ct);approval.CancelAfter(TimeSpan.FromSeconds(90));
                async Task<bool> LocalAsync()
                {
                    bool accepted=await confirm(new(peer,digits[..4]+" · "+digits[4..],client,()=>approval.Cancel()),approval.Token);
                    await channel.SendJsonAsync(new PairingApproval(1,accepted),approval.Token);return accepted;
                }
                async Task<bool> RemoteAsync()
                {
                    var decision=await channel.ReceiveJsonAsync<PairingApproval>(approval.Token);
                    if(decision.Pairing!=1)throw new InvalidDataException("Invalid pairing confirmation");return decision.Accepted;
                }
                var local=LocalAsync();var remote=RemoteAsync();
                try
                {
                    var first=await Task.WhenAny(local,remote);
                    if(!await first)throw new UnauthorizedAccessException("配对已拒绝");
                    if(!await (first==local?remote:local))throw new UnauthorizedAccessException("配对已拒绝");
                }
                finally
                {
                    approval.Cancel();
                    // Observe cancelled peer/local work before disposing the shared channel.
                    try{await Task.WhenAll(local,remote);}catch(Exception ex) when(ex is not OutOfMemoryException){}
                }
            }
            await channel.SendJsonAsync(new { Ready = true }, ct);
            var ready = await channel.ReceiveJsonAsync<JsonElement>(ct);
            if (!ready.GetProperty("Ready").GetBoolean()) throw new UnauthorizedAccessException();
            await trust.SaveAsync(new TrustedDevice(peer, Convert.ToBase64String(secret), DateTimeOffset.UtcNow));
            return new(channel, peer,NewlyPaired:true);
        }
        catch { await channel.DisposeAsync(); throw; }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    private static async Task<AuthenticatedConnection> FinishTrustedAsync(Stream stream, DeviceIdentity me, TrustedDevice peer, ECDiffieHellman ephemeral, Hello clientHello, Hello serverHello, bool client, CancellationToken ct)
    {
        byte[] psk = Convert.FromBase64String(peer.Secret);
        byte[] transcript = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { clientHello, serverHello }));
        byte[] Mac(string role) => HMACSHA256.HashData(psk, transcript.Concat(Encoding.UTF8.GetBytes(role)).ToArray());
        try
        {
            await Wire.JsonAsync(stream, new Authentication(me.Id, Convert.ToBase64String(Mac(client ? "client" : "server"))), ct);
            var auth = await Wire.JsonAsync<Authentication>(stream, ct);
            if (auth.Id != peer.Identity.Id || !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(auth.Mac), Mac(client ? "server" : "client")))
                throw new UnauthorizedAccessException("Device authentication failed");
            using var remote = ECDiffieHellman.Create();
            remote.ImportSubjectPublicKeyInfo(Convert.FromBase64String(client ? serverHello.Ephemeral : clientHello.Ephemeral), out int used);
            if (used != Convert.FromBase64String(client ? serverHello.Ephemeral : clientHello.Ephemeral).Length || remote.KeySize != 256)
                throw new CryptographicException("Invalid ephemeral key");
            byte[] shared = ephemeral.DeriveKeyMaterial(remote.PublicKey);
            byte[] material = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, HMACSHA256.HashData(psk, transcript), "Lantern/v1/reconnect"u8.ToArray());
            CryptographicOperations.ZeroMemory(shared);
            var channel = new SecureChannel(stream, material, client);
            CryptographicOperations.ZeroMemory(material);
            // Key confirmation before any task metadata is released.
            await channel.SendAsync("Lantern/ready/v1"u8.ToArray(), ct);
            if (!(await channel.ReceiveAsync(ct)).AsSpan().SequenceEqual("Lantern/ready/v1"u8)) throw new CryptographicException("Key confirmation");
            return new(channel, peer.Identity);
        }
        finally { CryptographicOperations.ZeroMemory(psk); }
    }
}
