using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;

static class StartupChecks
{
    static int checks;
    static void Assert(bool value,string label){if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    static JsonElement Element(object o)=>JsonSerializer.SerializeToElement(o,AppController.Json);
    static LanService Service(AppController c)=>(LanService)typeof(AppController).GetField("service",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(c)!;
    static async Task<JsonElement> State(AppController c)=>Element(await c.GetStateAsync());
    static async Task<JsonElement> Command(AppController c,object o)=>Element(await c.DispatchAsync(Element(o)));
    static async Task WaitPhase(AppController c,string phase)
    {
        for(int i=0;i<150;i++){if((await State(c)).GetProperty("ota").GetProperty("phase").GetString()==phase)return;await Task.Delay(100);}
        throw new Exception("Expected phase "+phase+": "+(await State(c)).GetProperty("ota"));
    }
    static async Task Connect(AppController phone,AppController pc,string? code=null)
    {
        var pcId=(await State(pc)).GetProperty("self").GetProperty("id").GetString();
        var channel=await Service(phone).ConnectAsync("127.0.0.1",Service(pc).Port,code,code is null?pcId:null,CancellationToken.None);
        await using(channel.Channel)await channel.Channel.SendJsonAsync(new{PairOnly=true},CancellationToken.None);
    }
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var host=new Host("Android",Path.Combine(root,"phone"));
        var first=new Host("Windows",Path.Combine(root,"pc-current"));var second=new Host("Windows",Path.Combine(root,"pc-new"));
        string payload=Path.Combine(root,"update.apk");await File.WriteAllBytesAsync(payload,RandomNumberGenerator.GetBytes(512*1024));
        var release=new OtaManifest(1,AppRelease.PackageId,AppRelease.Version,AppRelease.AndroidCode,new FileInfo(payload).Length,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payload))),AppRelease.Notes);
        first.Package=new(release with{Version="0.1.0",VersionCode=host.Code},payload);second.Package=new(release,payload);
        await using var pc1=new AppController(first);await pc1.InitializeAsync(0);
        await using var pc2=new AppController(second);await pc2.InitializeAsync(0);
        Service(pc2).Discoverable=false;
        // Persist both device keys, then restart the phone controller as on a later app launch.
        await using(var pairing=new AppController(host))
        {
            await pairing.InitializeAsync(0);
            Service(pairing).ConfirmPairing=Service(pc1).ConfirmPairing=Service(pc2).ConfirmPairing=(_,_)=>Task.FromResult(true);
            foreach(var pc in new[]{pc1,pc2})await Connect(pairing,pc,(await Command(pc,new{type="open-pairing"})).GetProperty("code").GetString());
        }
        await using(var phone=new AppController(host))
        {
            await phone.InitializeAsync(0);
            await Connect(phone,pc1);
            await WaitPhase(phone,"current");Assert(true,"Startup checks current-version PC without manual command");
            await Task.Delay(1700);await Connect(phone,pc2);
            await WaitPhase(phone,"available");var ota=(await State(phone)).GetProperty("ota");
            Assert(ota.GetProperty("peerId").GetString()==(await State(pc2)).GetProperty("self").GetProperty("id").GetString(),"Delayed second remembered PC offers new release");
            Assert(!Directory.Exists(Path.Combine(host.DataDirectory,"OtaCache"))||!Directory.EnumerateFiles(Path.Combine(host.DataDirectory,"OtaCache")).Any(),"Automatic check does not download an APK");
            Assert(host.Installs==0,"Automatic check never installs");
            Assert((await Command(phone,new{type="ota-download"})).GetProperty("ok").GetBoolean(),"Explicit download accepted after automatic check");
            await WaitPhase(phone,"ready");Assert(host.Validations>0,"Automatic manifest uses the existing SHA/signature-gated download");
        }
        await using(var phone=new AppController(host))
        {
            await phone.InitializeAsync(0);await WaitPhase(phone,"ready");
            Assert(host.Installs==0,"Restart restores verified package without installing");
            await Command(phone,new{type="ota-clear"});await Connect(phone,pc1);await Task.Delay(1800);
            Assert((await State(phone)).GetProperty("ota").GetProperty("phase").GetString()=="idle","Clearing restored package cannot trigger a stale startup job");
        }
        await using(var phone=new AppController(host))
        {
            await phone.InitializeAsync(0);await Connect(phone,pc2);
            var id=(await State(pc1)).GetProperty("self").GetProperty("id").GetString();await Connect(phone,pc1);
            Assert((await Command(phone,new{type="ota-check",peerId=id})).GetProperty("ok").GetBoolean(),"Manual check takes ownership from startup work");
            await Task.Delay(2000);Assert((await State(phone)).GetProperty("ota").GetProperty("phase").GetString()=="current","Startup work cannot overwrite a manual result");
        }
        var freshHost=new Host("Android",Path.Combine(root,"unpaired"));
        await using(var fresh=new AppController(freshHost))
        {
            await fresh.InitializeAsync(0);
            Service(fresh).Nearby["untrusted"]=new(new("untrusted","Spoofed Windows","Windows"),"127.0.0.1",Service(pc2).Port,DateTimeOffset.UtcNow);
            await Task.Delay(1800);Assert((await State(fresh)).GetProperty("ota").GetProperty("phase").GetString()=="idle","Discovery alone cannot trigger OTA from an untrusted PC");
        }
        var watch=System.Diagnostics.Stopwatch.StartNew();var closing=new AppController(new Host("Android",Path.Combine(root,"closing")));await closing.InitializeAsync(0);await closing.DisposeAsync();
        Assert(watch.Elapsed<TimeSpan.FromSeconds(4),"Shutdown cancels startup discovery promptly");
        Console.WriteLine("PASS "+checks+" authenticated startup OTA checks");
    }
}
