using System.Security.Cryptography;
namespace Lantern.Core;

public sealed class PairingWindow(TimeProvider? clock = null):IDisposable
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private readonly object gate = new();
    private string? code;
    private DateTimeOffset expires;
    private int attempts;
    private bool busy;
    private CancellationTokenSource? session;
    public (string Code, DateTimeOffset Expires) Open()
    {
        lock (gate)
        {
            if (busy) throw new InvalidOperationException("Pairing is in progress");
            session?.Dispose();session=new();
            code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
            expires = Now.AddSeconds(90); attempts = 0;
            return (code, expires);
        }
    }
    public (string Code,CancellationToken Token) ReserveSession()
    {lock(gate){return (Reserve(),session?.Token??CancellationToken.None);}}
    public string Reserve()
    {
        lock (gate)
        {
            if (code is null || Now >= expires || attempts >= 5 || busy)
                throw new UnauthorizedAccessException("Pairing unavailable");
            busy = true; attempts++; return code;
        }
    }
    public void Complete(bool success)
    {
        lock (gate) { busy = false; if (success || attempts >= 5) code = null; }
    }
    public void Close()
    {
        CancellationTokenSource? cancel;
        lock(gate){code=null;cancel=session;}
        try{cancel?.Cancel();}catch(ObjectDisposedException){}
    }
    public void Dispose(){Close();lock(gate){session?.Dispose();session=null;}}
}
