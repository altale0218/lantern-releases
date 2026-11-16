using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lantern.Core;

public sealed record SpeedTestRequest(int SpeedTest,string Id,int DurationMs=3000,long MaxBytes=256L*1024*1024,int ChunkBytes=1024*1024);
public sealed record SpeedTestReply(int SpeedTest,string Id,bool Accept,string? Message=null);
public sealed record SpeedTestMeasurement(long Bytes,double Seconds,double BytesPerSecond,bool Verified);
public sealed record SpeedTestProgress(string Direction,long Bytes,double Seconds,double BytesPerSecond,SpeedTestMeasurement? Result=null);
public sealed record SpeedTestResult(SpeedTestMeasurement Upload,SpeedTestMeasurement Download);
public sealed class SpeedTestUnsupportedException() : IOException("未收到兼容的测速响应，请确认对方在线且双端均为 0.7.0 或以上");

// Runs only inside an already authenticated SecureChannel. No file-system access or peer clock is used.
public static class SpeedTests
{
    public const long MaxDirectionBytes=256L*1024*1024;
    private sealed record BurstEnd(string Id,string Direction,long Bytes,string Sha256);
    private sealed record BurstReceipt(string Id,string Direction,long Bytes,string Sha256,bool Verified);
    public static void Validate(SpeedTestRequest request)
    {
        if(request.SpeedTest!=1||!Guid.TryParseExact(request.Id,"N",out _)||request.DurationMs is <250 or >5000||
           request.MaxBytes is <1048576 or >MaxDirectionBytes||request.ChunkBytes is <65536 or >1048576||request.ChunkBytes%65536!=0)
            throw new InvalidDataException("测速请求超出协议限制");
    }
    public static string RequestId(JsonElement request)=>request.TryGetProperty("Id",out var id)&&id.ValueKind==JsonValueKind.String&&Guid.TryParseExact(id.GetString(),"N",out _)?id.GetString()!:"";
    private static SpeedTestMeasurement Measurement(long bytes,long start)
    {double seconds=Math.Max(.000001,Stopwatch.GetElapsedTime(start).TotalSeconds);return new(bytes,seconds,bytes/seconds,true);}
    private static bool HashEquals(string? expected,string actual)=>expected is {Length:64}&&expected.All(Uri.IsHexDigit)&&CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected),Convert.FromHexString(actual));
    private static CancellationTokenSource BurstDeadline(SpeedTestRequest request,CancellationToken ct)
    {var source=CancellationTokenSource.CreateLinkedTokenSource(ct);source.CancelAfter(TimeSpan.FromMilliseconds(request.DurationMs+5000));return source;}
    public static async Task<SpeedTestResult> RunAsync(SecureChannel channel,SpeedTestRequest request,Action<SpeedTestProgress> progress,CancellationToken ct)
    {
        Validate(request);
        using(var negotiation=CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            negotiation.CancelAfter(TimeSpan.FromSeconds(6));
            await channel.SendJsonAsync(request,negotiation.Token);
            SpeedTestReply reply;
            try{reply=await channel.ReceiveJsonAsync<SpeedTestReply>(negotiation.Token);}
            catch(Exception ex) when(ex is EndOfStreamException or JsonException){throw new SpeedTestUnsupportedException();}
            if(reply.SpeedTest!=1||reply.Id!=request.Id)throw new SpeedTestUnsupportedException();
            if(!reply.Accept)throw new IOException(reply.Message is {Length:>0 and <=200}?reply.Message:"对方暂时无法测速");
        }
        var upload=await SendBurstAsync(channel,request,"upload",progress,ct);
        var download=await ReceiveBurstAsync(channel,request,"download",progress,ct);
        return new(upload,download);
    }
    public static async Task<SpeedTestResult> RespondAsync(SecureChannel channel,SpeedTestRequest request,Action<SpeedTestProgress> progress,CancellationToken ct)
    {
        Validate(request);await channel.SendJsonAsync(new SpeedTestReply(1,request.Id,true),ct);
        var upload=await ReceiveBurstAsync(channel,request,"upload",progress,ct);
        var download=await SendBurstAsync(channel,request,"download",progress,ct);
        return new(upload,download);
    }
    private static async Task<SpeedTestMeasurement> SendBurstAsync(SecureChannel channel,SpeedTestRequest request,string direction,Action<SpeedTestProgress> progress,CancellationToken ct)
    {
        using var deadline=BurstDeadline(request,ct);ct=deadline.Token;
        byte[] buffer=new byte[request.ChunkBytes+1];buffer[0]=1;RandomNumberGenerator.Fill(buffer.AsSpan(1));
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long start=Stopwatch.GetTimestamp(),last=start,total=0;progress(new(direction,0,0,0));
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();int length=(int)Math.Min(request.ChunkBytes,request.MaxBytes-total);
                digest.AppendData(buffer,1,length);await channel.SendAsync(buffer.AsMemory(0,length+1),ct);total+=length;
                if(Stopwatch.GetElapsedTime(last).TotalMilliseconds>=150)
                {var sample=Measurement(total,start);progress(new(direction,total,sample.Seconds,sample.BytesPerSecond));last=Stopwatch.GetTimestamp();}
            }while(total<request.MaxBytes&&Stopwatch.GetElapsedTime(start).TotalMilliseconds<request.DurationMs);
            string hash=Convert.ToHexString(digest.GetHashAndReset());
            byte[] json=JsonSerializer.SerializeToUtf8Bytes(new BurstEnd(request.Id,direction,total,hash));
            byte[] end=new byte[json.Length+1];end[0]=2;json.CopyTo(end,1);await channel.SendAsync(end,ct);
            var receipt=await channel.ReceiveJsonAsync<BurstReceipt>(ct);
            if(receipt.Id!=request.Id||receipt.Direction!=direction||receipt.Bytes!=total||!receipt.Verified||!HashEquals(receipt.Sha256,hash))
                throw new CryptographicException("测速数据未收到有效校验回执");
            var result=Measurement(total,start);progress(new(direction,total,result.Seconds,result.BytesPerSecond,result));return result;
        }
        finally{CryptographicOperations.ZeroMemory(buffer);}
    }
    private static async Task<SpeedTestMeasurement> ReceiveBurstAsync(SecureChannel channel,SpeedTestRequest request,string direction,Action<SpeedTestProgress> progress,CancellationToken ct)
    {
        using var deadline=BurstDeadline(request,ct);ct=deadline.Token;
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long start=Stopwatch.GetTimestamp(),last=start,total=0;progress(new(direction,0,0,0));
        int capacity=request.ChunkBytes+1;
        byte[] frame=ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            while(true)
            {
                int count=await channel.ReceiveIntoAsync(frame.AsMemory(0,capacity),ct);
                if(count<2)throw new InvalidDataException("测速数据帧无效");
                if(frame[0]==1)
                {
                    int length=count-1;
                    if(length!=Math.Min(request.ChunkBytes,request.MaxBytes-total))throw new InvalidDataException("测速数据超出协定大小");
                    digest.AppendData(frame,1,length);total+=length;
                    if(Stopwatch.GetElapsedTime(last).TotalMilliseconds>=150)
                    {var sample=Measurement(total,start);progress(new(direction,total,sample.Seconds,sample.BytesPerSecond));last=Stopwatch.GetTimestamp();}
                    continue;
                }
                if(frame[0]!=2||count>1024)throw new InvalidDataException("测速结束帧无效");
                var end=JsonSerializer.Deserialize<BurstEnd>(frame.AsSpan(1,count-1))??throw new InvalidDataException("测速数据无效");
                string hash=Convert.ToHexString(digest.GetHashAndReset());
                if(total<1||end.Id!=request.Id||end.Direction!=direction||end.Bytes!=total||!HashEquals(end.Sha256,hash))
                    throw new CryptographicException("测速数据完整性校验失败");
                var result=Measurement(total,start);
                await channel.SendJsonAsync(new BurstReceipt(request.Id,direction,total,hash,true),ct);
                progress(new(direction,total,result.Seconds,result.BytesPerSecond,result));return result;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(frame, clearArray:true); }
    }
}
