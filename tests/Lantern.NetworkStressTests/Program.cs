using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lantern.Core;

var server=new DeviceIdentity(Guid.NewGuid().ToString("N"),"Isolated server","Windows");
var client=new DeviceIdentity(Guid.NewGuid().ToString("N"),"Expected peer","Android");
var store=new Secrets();var trust=new TrustStore(store);var clientStore=new Secrets();var clientTrust=new TrustStore(clientStore);string key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
await trust.SaveAsync(new(client,key,DateTimeOffset.UtcNow));await clientTrust.SaveAsync(new(server,key,DateTimeOffset.UtcNow));
int approvals=0,incoming=0;
await using var service=new LanService(server,trust,()=>[]){ConfirmPairing=(_,_)=>{Interlocked.Increment(ref approvals);return Task.FromResult(false);},Incoming=(_,_)=>{Interlocked.Increment(ref incoming);return Task.CompletedTask;}};service.Start(0);
var handlers=(ConcurrentDictionary<int,Task>)typeof(LanService).GetField("handlers",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
async Task Drained(){for(int i=0;i<200;i++){if(handlers.Count==0)return;await Task.Delay(10);}throw new Exception("Malformed sockets were not released");}
async Task Healthy()
{
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));using var tcp=new TcpClient();await tcp.ConnectAsync("127.0.0.1",service.Port,deadline.Token);
    var connection=await Handshake.ConnectAsync(tcp.GetStream(),client,clientTrust,null,server.Id,deadline.Token);
    await using(connection.Channel)await connection.Channel.SendJsonAsync(new{PairOnly=true},deadline.Token);await Drained();
}
await Healthy();long before=GC.GetTotalMemory(true);int rejectedPackets=0;
for(int round=0;round<20;round++)
{
    await Task.WhenAll(Enumerable.Range(0,64).Select(async n=>
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));using var tcp=new TcpClient();
        try
        {
            await tcp.ConnectAsync("127.0.0.1",service.Port,deadline.Token);var stream=tcp.GetStream();
            if(n%8==0){byte[] header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,n%16==0?int.MaxValue:-1);await stream.WriteAsync(header,deadline.Token);}
            else if(n%8==1)await Wire.WriteAsync(stream,"null"u8.ToArray(),deadline.Token);
            else if(n%8==2)await Wire.WriteAsync(stream,Encoding.UTF8.GetBytes("{\"Version\":1,\"Mode\":\"trusted\",\"Id\":null}"),deadline.Token);
            else if(n%8==3)await Wire.WriteAsync(stream,RandomNumberGenerator.GetBytes(75),deadline.Token);
            else if(n%8==4){await Wire.JsonAsync(stream,new Hello(1,"pair","","",""),deadline.Token);}
            else if(n%8==5){await Wire.JsonAsync(stream,new Hello(1,"pair-confirm","","",""),deadline.Token);}
            else if(n%8==6){byte[] partial=new byte[5];BinaryPrimitives.WriteInt32BigEndian(partial,16384);partial[4]=(byte)'{';await stream.WriteAsync(partial,deadline.Token);}
            else
            {
                using var ephemeral=ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                await Wire.JsonAsync(stream,new Hello(1,"trusted",client.Id,Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),deadline.Token);
                _=await Wire.JsonAsync<Hello>(stream,deadline.Token);
                await Wire.JsonAsync(stream,new Authentication(client.Id,Convert.ToBase64String(new byte[32])),deadline.Token);
            }
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException){}
        finally{Interlocked.Increment(ref rejectedPackets);}
    }));
    await Drained();await Healthy();
    if(approvals!=0||incoming!=0)throw new Exception("Unauthenticated input reached an approval or data callback");
}
long after=GC.GetTotalMemory(true);
if(after>before+32L*1024*1024)throw new Exception("Malformed connection memory did not settle");
if((await trust.ListAsync()).Count!=1||(await trust.FindAsync(client.Id))?.Secret!=key)throw new Exception("Malformed traffic changed trusted device keys");
Console.WriteLine(JsonSerializer.Serialize(new{success=true,rejectedPackets,healthyReconnects=21,approvals,incoming,remainingHandlers=handlers.Count,managedBefore=before,managedAfter=after}));
Console.WriteLine("PASS bounded malformed TCP framing/JSON, legacy pairing rejection, closed matching window, forged authentication and connection churn; valid trusted reconnect survives every round and trust is unchanged.");
sealed class Secrets:ISecretStore
{
    readonly ConcurrentDictionary<string,string> values=new();public Task<string?> GetAsync(string key)=>Task.FromResult(values.GetValueOrDefault(key));public Task SetAsync(string key,string value){values[key]=value;return Task.CompletedTask;}public Task RemoveAsync(string key){values.TryRemove(key,out _);return Task.CompletedTask;}
}
