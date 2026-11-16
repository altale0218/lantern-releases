using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lantern.AppLogic;

public sealed record ServerRelease(string Version,string Platform,string Channel,string Source,long Size,string Sha256,string Notes,int? VersionCode=null)
{
    internal const long MaxBytes=256L*1024*1024;
    internal string FileName => Platform=="Android" ? "Lantern-Android.apk" : "Lantern-Windows.zip";

    internal bool IsValid() => Platform is "Android" or "Windows" && Channel is "stable" or "beta" &&
        (Platform=="Android"?VersionCode is >0:VersionCode is null) &&
        Source is "github" or "gitee" && ServerUpdateClient.TryVersion(Version,out _) && Size is >0 and <=MaxBytes &&
        Sha256 is {Length:64} && Sha256.All(Uri.IsHexDigit) && Notes is {Length:<=6000};

    internal async Task VerifyFileAsync(string path,CancellationToken token)
    {
        if(!IsValid())throw new InvalidDataException("服务器更新清单无效");
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(stream.Length!=Size||!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream,token),Convert.FromHexString(Sha256)))
            throw new CryptographicException("安装包大小或 SHA-256 校验失败");
    }
}

internal sealed record ServerProbe(string Id,string Status,ServerRelease? Release=null);
internal sealed record ServerCheck(ServerRelease? Release,ServerProbe[] Sources);

public sealed class ServerUpdateClient : IDisposable
{
    private const int CatalogPageSize=30,CatalogPageLimit=4;
    private record Mirror(string Id,Uri Catalog,Uri StaticCatalog,string Releases,string[] Hosts)
    {
        public Uri CatalogPage(int page) => new(Catalog,$"?per_page={CatalogPageSize}&page={page}"+(Id=="gitee"?"&direction=desc":""));
    }
    private record RemoteAsset(string Name,[property:JsonPropertyName("browser_download_url")] string? BrowserUrl,long? Size,string? Digest);
    private record RemoteRelease([property:JsonPropertyName("tag_name")] string TagName,bool Draft,bool Prerelease,string? Body,RemoteAsset[]? Assets);
    private readonly record struct CatalogResult((Version Version,RemoteRelease Release)? Candidate,bool Complete);

    private static readonly Mirror[] Mirrors =
    [
        new("github",new("https://api.github.com/repos/altale0218/lantern-releases/releases"),
            new("https://raw.githubusercontent.com/altale0218/lantern-releases/lantern-update-catalog/github/"),
            "https://github.com/altale0218/lantern-releases/releases/download/",["api.github.com","github.com","release-assets.githubusercontent.com","objects.githubusercontent.com"]),
        new("gitee",new("https://gitee.com/api/v5/repos/altalatic/lantern-releases/releases"),
            new("https://raw.giteeusercontent.com/altalatic/lantern-releases/raw/lantern-update-catalog/gitee/"),
            "https://gitee.com/altalatic/lantern-releases/releases/download/",["gitee.com","foruda.gitee.com"])
    ];
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=20};
    private readonly HttpClient http;

    public ServerUpdateClient(HttpMessageHandler? transport=null)
    {
        http=new(transport??new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=Timeout.InfiniteTimeSpan};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Lantern-Update/1.0");
    }

    internal static bool TryVersion(string? text,out Version version)
    {
        version=new();
        if(text is null||text.Length>29||!Regex.IsMatch(text,@"\A(?:0|[1-9][0-9]{0,8})(?:\.(?:0|[1-9][0-9]{0,8})){2}\z")||!Version.TryParse(text,out var parsed))return false;
        version=parsed;
        return true;
    }

    internal async Task<ServerCheck> CheckAsync(string platform,string channel,string selected,Version installed,int? installedCode,CancellationToken token)
    {
        if(platform is not ("Android" or "Windows") || channel is not ("stable" or "beta") || selected is not ("auto" or "github" or "gitee"))
            throw new InvalidDataException("更新平台、渠道或来源无效");
        var probes=await Task.WhenAll(Mirrors.Where(m=>selected=="auto"||m.Id==selected).Select(m=>ProbeAsync(m,platform,channel,installed,installedCode,token)));
        var available=probes.Where(p=>p.Release is not null).Select(p=>p.Release!).OrderByDescending(r=>Version.Parse(r.Version)).ToArray();
        var newest=available.FirstOrDefault();
        if(newest is not null&&available.Any(r=>r.Version==newest.Version&&(r.Sha256!=newest.Sha256||r.Size!=newest.Size||r.VersionCode!=newest.VersionCode)))
            return new(null,probes.Select(p=>p.Release?.Version==newest.Version?p with{Status="conflict",Release=null}:p).ToArray());
        return new(newest,probes);
    }

    private async Task<ServerProbe> ProbeAsync(Mirror mirror,string platform,string channel,Version installed,int? installedCode,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var ct=deadline.Token;
        try
        {
            var (candidate,complete)=await ReadCatalogAsync(mirror,channel,installed,ct);
            if(candidate is not { } match || match.Version<=installed)
                return new(mirror.Id,!complete?"limited":candidate is null?"empty":"current");
            var (latest,release)=match;

            string notes=release.Body??"";
            int? versionCode=platform=="Android"?ParseAndroidVersionCode(notes):null;
            if(platform=="Android"&&(installedCode is not >0||versionCode<=installedCode))
                throw new InvalidDataException("服务器 Android 版本号不递增");

            string name=platform=="Android"?"Lantern-Android.apk":"Lantern-Windows.zip";
            var package=FindAsset(mirror,release,name);
            var checksum=FindAsset(mirror,release,"SHA256SUMS.txt");
            var sum=ReadAsync(mirror,checksum,16*1024,ct);
            var asset=release.Assets!.Single(a=>a?.Name==name);
            var length=PackageLengthAsync(mirror,package,asset.Size,ct);
            await Task.WhenAll(sum,length);
            string digest=ParseChecksum(await sum,name);
            string? reported=asset.Digest;
            if(reported is not null&&!reported.Equals("sha256:"+digest,StringComparison.OrdinalIgnoreCase))throw new CryptographicException("发行附件摘要不一致");
            var selected=new ServerRelease(latest.ToString(),platform,channel,mirror.Id,await length,digest,notes[..Math.Min(6000,notes.Length)],versionCode);
            if(!selected.IsValid())throw new InvalidDataException("版本或安装包信息无效");
            return new(mirror.Id,"available",selected);
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested){return new(mirror.Id,"timeout");}
        catch(HttpRequestException){return new(mirror.Id,"unavailable");}
        catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or CryptographicException){return new(mirror.Id,"invalid");}
    }

    private async Task<CatalogResult> ReadCatalogAsync(Mirror mirror,string channel,Version installed,CancellationToken token)
    {
        CatalogResult primary;
        try { primary=await FindCandidateAsync(mirror,channel,installed,token); }
        catch(HttpRequestException ex) when(ex.StatusCode is null or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or
            HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            if(await ReadStaticCatalogAsync(mirror,channel,token) is { } fallback)return fallback;
            throw;
        }
        if(primary.Complete||primary.Candidate is { } match&&match.Version>installed)return primary;
        return await ReadStaticCatalogAsync(mirror,channel,token)??primary;
    }

    private async Task<CatalogResult?> ReadStaticCatalogAsync(Mirror mirror,string channel,CancellationToken token)
    {
        var url=new Uri(mirror.StaticCatalog,channel+".json");
        byte[] data;
        try { data=await ReadAsync(mirror with{Hosts=[url.IdnHost]},url,64*1024,token,noRedirect:true); }
        catch(HttpRequestException ex) when(ex.StatusCode==HttpStatusCode.NotFound){return null;}
        var releases=JsonSerializer.Deserialize<RemoteRelease[]>(data,Json);
        if(releases is null or {Length:>1})throw new InvalidDataException("备用更新清单无效");
        var candidate=RankReleases(releases,channel=="beta");
        if(releases.Length==1&&candidate is null)throw new InvalidDataException("备用更新清单的频道或版本无效");
        return new(candidate,true);
    }

    private async Task<CatalogResult> FindCandidateAsync(
        Mirror mirror,string channel,Version installed,CancellationToken token)
    {
        (Version Version,RemoteRelease Release)? best=null;
        bool prerelease=channel=="beta";
        for(int page=1;page<=CatalogPageLimit;page++)
        {
            var data=await ReadAsync(mirror,mirror.CatalogPage(page),2*1024*1024,token);
            var releases=JsonSerializer.Deserialize<RemoteRelease[]>(data,Json)??throw new InvalidDataException("服务器发行列表无效");
            if(RankReleases(releases,prerelease) is { } ranked&&(best is null||ranked.Version>best.Value.Version))best=ranked;
            if(releases.Length<CatalogPageSize)return new(best,true);
            if(best is { } match&&match.Version>installed)return new(best,false);
        }
        return new(best,false);
    }

    private static (Version Version,RemoteRelease Release)? RankReleases(RemoteRelease[] releases,bool prerelease)
    {
        (Version Version,RemoteRelease Release)? best=null;
        foreach(var item in releases)
        {
            if(item is null||item.Draft||item.Prerelease!=prerelease||item.TagName is not {Length:>1} tag||tag[0]!='v'||!TryVersion(tag[1..],out var version))continue;
            if(best is null||version>best.Value.Version)best=(version,item);
        }
        return best;
    }

    private static int ParseAndroidVersionCode(string notes)
    {
        var matches=Regex.Matches(notes,@"\bversionCode[ \t]+([1-9][0-9]{0,8})\b");
        if(matches.Count!=1||!int.TryParse(matches[0].Groups[1].Value,out int code))
            throw new InvalidDataException("发行说明缺少唯一的 Android versionCode");
        return code;
    }

    private static Uri FindAsset(Mirror mirror,RemoteRelease release,string name)
    {
        var assets=release.Assets?.Where(a=>a?.Name==name).ToArray();
        string exact=mirror.Releases+release.TagName+"/"+name;
        if(assets is not {Length:1}||assets[0].BrowserUrl!=exact)throw new InvalidDataException("发行附件缺失或链接无效");
        return new Uri(exact);
    }

    private static string ParseChecksum(byte[] bytes,string name)
    {
        string pattern=@"^([0-9a-fA-F]{64})[ \t]+\*?"+Regex.Escape(name)+@"\r?$";
        var rows=Regex.Matches(System.Text.Encoding.UTF8.GetString(bytes),pattern,RegexOptions.Multiline);
        if(rows.Count!=1)throw new InvalidDataException("安装包校验清单缺失或歧义");
        return rows[0].Groups[1].Value.ToLowerInvariant();
    }

    private async Task<long> PackageLengthAsync(Mirror mirror,Uri url,long? reported,CancellationToken token)
    {
        if(reported is >0 and <=ServerRelease.MaxBytes)return reported.Value;
        using var response=await RequestAsync(mirror,url,HttpMethod.Head,token);
        return response.Content.Headers.ContentLength??throw new InvalidDataException("服务器未返回安装包大小");
    }

    private async Task<byte[]> ReadAsync(Mirror mirror,Uri url,int max,CancellationToken token,bool noRedirect=false)
    {
        using var response=await RequestAsync(mirror,url,HttpMethod.Get,token,noRedirect);
        long? length=response.Content.Headers.ContentLength;
        if(length>max)throw new InvalidDataException("更新服务器响应过大");
        await using var stream=await response.Content.ReadAsStreamAsync(token);
        const int chunkSize=8192;
        using var content=new MemoryStream((int)(length??Math.Min(max,chunkSize)));
        var chunk=ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            while(true)
            {
                int remaining=max-(int)content.Length;
                int count=await stream.ReadAsync(chunk.AsMemory(0,Math.Min(chunkSize,remaining+1)),token);
                if(count==0)return content.ToArray();
                if(count>remaining)throw new InvalidDataException("更新服务器响应过大");
                content.Write(chunk,0,count);
            }
        }
        finally{ArrayPool<byte>.Shared.Return(chunk);}
    }

    internal async Task DownloadAsync(ServerRelease release,string path,Action<long> progress,CancellationToken token)
    {
        if(!release.IsValid())throw new InvalidDataException("更新包清单无效");
        var mirror=Mirrors.Single(m=>m.Id==release.Source);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var ct=deadline.Token;
        var url=new Uri(mirror.Releases+"v"+release.Version+"/"+release.FileName);
        using var response=await RequestAsync(mirror,url,HttpMethod.Get,ct);
        if(response.Content.Headers.ContentLength is long size&&size!=release.Size)throw new InvalidDataException("服务器文件大小已变化，请重新检查更新");
        await using var data=await response.Content.ReadAsStreamAsync(ct);
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=ArrayPool<byte>.Shared.Rent(65536);
        long total=0;
        try
        {
            int count;
            while((count=await data.ReadAsync(buffer.AsMemory(0,65536),ct))!=0)
            {
                total+=count;
                if(total>release.Size)throw new InvalidDataException("服务器文件超出预期大小");
                hash.AppendData(buffer,0,count);
                await file.WriteAsync(buffer.AsMemory(0,count),ct);
                progress(total);
            }
        }
        finally{ArrayPool<byte>.Shared.Return(buffer);}
        if(total!=release.Size||!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),Convert.FromHexString(release.Sha256)))
            throw new CryptographicException("下载文件的 SHA-256 校验失败");
    }

    private async Task<HttpResponseMessage> RequestAsync(Mirror mirror,Uri url,HttpMethod method,CancellationToken token,bool noRedirect=false)
    {
        for(int hop=0;hop<=5;hop++)
        {
            if(url.Scheme!=Uri.UriSchemeHttps||!url.IsDefaultPort||url.UserInfo.Length!=0||!mirror.Hosts.Contains(url.IdnHost,StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("更新服务器跳转地址无效");
            var response=await SendWithRetryAsync(mirror,url,method,token);
            if(response.IsSuccessStatusCode)return response;
            var status=response.StatusCode;
            var location=response.Headers.Location;
            response.Dispose();
            if((int)status is not (301 or 302 or 303 or 307 or 308))throw new HttpRequestException("更新服务器不可用",null,status);
            if(noRedirect)throw new InvalidDataException("备用更新清单地址不能重定向");
            if(location is null||hop==5)throw new InvalidDataException("更新服务器重定向过多或缺少地址");
            url=new Uri(url,location);
        }
        throw new InvalidDataException("更新服务器重定向过多");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Mirror mirror,Uri url,HttpMethod method,CancellationToken token)
    {
        for(int attempt=0;;attempt++)
        {
            try
            {
                using var request=new HttpRequestMessage(method,url);
                if(url.Host==mirror.Catalog.Host&&url.AbsolutePath==mirror.Catalog.AbsolutePath)
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
                bool retry=attempt==0&&response.Headers.RetryAfter is null&&response.StatusCode is
                    HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                if(!retry)return response;
                response.Dispose();
            }
            catch(HttpRequestException ex) when(attempt==0&&(ex.HttpRequestError is
                HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded or HttpRequestError.SecureConnectionError))
            {
                // The retry opens a fresh request with the same HTTPS and host checks.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200),token);
        }
    }

    public void Dispose()=>http.Dispose();
}
