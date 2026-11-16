using System.Text.Json;
using Lantern.AppLogic;

static class PreferenceChecks
{
    public static async Task RunAsync(string root)
    {
        int checks=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
        JsonElement Element(object value)=>JsonSerializer.SerializeToElement(value,AppController.Json);
        Directory.CreateDirectory(root);
        foreach(string platform in new[]{"Android","Windows"})
        {
            var host=new Host(platform,Path.Combine(root,platform));Directory.CreateDirectory(host.DataDirectory);
            string file=Path.Combine(host.DataDirectory,"settings.json");
            await using(var fresh=new AppController(host))
            {
                await fresh.InitializeAsync(0);
                var settings=Element(await fresh.GetStateAsync()).GetProperty("settings");
                Check(settings.GetProperty("autoAccept").GetBoolean(),platform+" fresh default allows authenticated transfers");
            }
            await File.WriteAllTextAsync(file,"{\"autoAccept\":false,\"theme\":\"light\",\"fontScale\":125,\"discoverable\":false,\"notificationDetails\":false,\"xiaomiIsland\":true}");
            await using(var upgrade=new AppController(host))
            {
                await upgrade.InitializeAsync(0);
                var settings=Element(await upgrade.GetStateAsync()).GetProperty("settings");
                Check(settings.GetProperty("autoAccept").GetBoolean()&&settings.GetProperty("preferencesVersion").GetInt32()==1,platform+" one-time migration");
                Check(settings.GetProperty("theme").GetString()=="light"&&settings.GetProperty("fontScale").GetInt32()==125&&!settings.GetProperty("discoverable").GetBoolean()&&!settings.GetProperty("notificationDetails").GetBoolean(),platform+" other preferences preserved");
                Check(!settings.GetProperty("xiaomiIsland").GetBoolean(),platform+" unavailable experimental feature disabled");
                var result=Element(await upgrade.DispatchAsync(Element(new{type="setting",key="autoAccept",value=false})));
                Check(result.GetProperty("ok").GetBoolean(),platform+" explicit opt-out accepted");
            }
            await using(var restart=new AppController(host))
            {
                await restart.InitializeAsync(0);
                Check(!Element(await restart.GetStateAsync()).GetProperty("settings").GetProperty("autoAccept").GetBoolean(),platform+" opt-out persists across launch");
                Check(!File.Exists(file+".tmp"),platform+" atomic settings write committed");
            }
        }
        Check(new LanConnectionStatus(true,false,false).NeedsConnection,"Wi-Fi disabled and no LAN requires reminder");
        Check(!new LanConnectionStatus(true,false,true).NeedsConnection,"Ethernet remains usable with Wi-Fi off");
        Check(!new LanConnectionStatus(true,true,false).NeedsConnection,"Enabled Wi-Fi is not called disabled");
        Check(!LanConnectionStatus.Unknown.NeedsConnection,"Unknown network state remains silent");
        Console.WriteLine("PASS "+checks+" preference and network policy checks");
    }
}
