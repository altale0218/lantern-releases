using System.Net.Sockets;
using Lantern.Core;
public static class NativePeer
{
    public static async Task<int> RunAsync(string code)
    {
        using var socket=new TcpClient();await socket.ConnectAsync("127.0.0.1",53319);
        var identity=new DeviceIdentity(Guid.NewGuid().ToString("N"),"安全测试设备","Android");
        var trust=new TrustStore(new EphemeralStore());using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var connection=await Handshake.ConnectAsync(socket.GetStream(),identity,trust,code,null,timeout.Token);
        await using var channel=connection.Channel;
        Console.WriteLine("AUTHENTICATED");
        byte[] content=Enumerable.Range(0,2*1024*1024+17).Select(i=>(byte)(i%251)).ToArray();
        byte[] png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        SourceFile[] files=[new("verification.bin",content.Length,"application/octet-stream",()=>Task.FromResult<Stream>(new MemoryStream(content))),new("preview.png",png.Length,"image/png",()=>Task.FromResult<Stream>(new MemoryStream(png)))];
        await Transfers.SendAsync(channel,identity,files,Guid.NewGuid().ToString("N"),new(),new(),_=>{},timeout.Token);
        Console.WriteLine("TRANSFER_COMPLETED");return 0;
    }
    private sealed class EphemeralStore:ISecretStore
    {
        private readonly Dictionary<string,string> values=new();
        public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));
        public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}
        public Task RemoveAsync(string key){values.Remove(key);return Task.CompletedTask;}
    }
}
