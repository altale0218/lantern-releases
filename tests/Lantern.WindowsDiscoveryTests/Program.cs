using System.Collections.Concurrent;
using System.Text.Json;
using Lantern.AppLogic;
using Lantern.Core;
using Lantern.Windows;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DNS-SD is required.");
string root = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass a new, isolated test-data directory.");
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("The test-data directory must be empty.");

await using var windows = new AppController(new Host(Path.Combine(root, "windows"), "Windows"));
await using var tablet = new AppController(new Host(Path.Combine(root, "tablet"), "iPadOS"));
await windows.InitializeAsync(0);
await tablet.InitializeAsync(0);
using var windowsDiscovery = new WindowsServiceDiscovery(windows);
using var tabletDiscovery = new WindowsServiceDiscovery(tablet);

await Wait(async () =>
{
    windowsDiscovery.Refresh();
    tabletDiscovery.Refresh();
    return (await Peer(windows, tablet)).ValueKind == JsonValueKind.Object &&
        (await Peer(tablet, windows)).ValueKind == JsonValueKind.Object;
}, "Both Windows wire services must advertise PTR, SRV, TXT and A and resolve to private IPv4");

var candidate = await Peer(windows, tablet);
Check(!candidate.GetProperty("trusted").GetBoolean() && candidate.GetProperty("platform").GetString() == "iPadOS" &&
    candidate.GetProperty("port").GetInt32() == tablet.ListeningPort, "DNS-SD advertises a route for an untrusted candidate");

string? code = (await Command(tablet, new { type = "open-pairing" })).GetProperty("code").GetString();
var start = await Command(windows, new { type = "pair", address = candidate.GetProperty("address").GetString(),
    port = candidate.GetProperty("port").GetInt32(), code });
Check(start.GetProperty("ok").GetBoolean(), "Pairing can use the IPv4 address and port from the Windows DNS-SD browser");
await Wait(async () => (await State(windows)).GetProperty("pairingRequests").GetArrayLength() == 1 &&
    (await State(tablet)).GetProperty("pairingRequests").GetArrayLength() == 1, "Both endpoints request pairing approval");
var first = (await State(windows)).GetProperty("pairingRequests")[0];
var second = (await State(tablet)).GetProperty("pairingRequests")[0];
Check(first.GetProperty("verificationCode").GetString() == second.GetProperty("verificationCode").GetString(), "Pairing confirmation codes match");
await Confirm(windows, first);
Check(!(await Peer(windows, tablet)).GetProperty("trusted").GetBoolean(), "One approval cannot establish trust");
await Confirm(tablet, second);
await Wait(async () => (await Peer(windows, tablet)).GetProperty("trusted").GetBoolean() &&
    (await Peer(tablet, windows)).GetProperty("trusted").GetBoolean(), "Both endpoints authenticate on the discovered route");

static void Check(bool passed, string label)
{
    if (!passed) throw new IOException(label);
    Console.WriteLine("PASS " + label);
}

static async Task Wait(Func<Task<bool>> check, string label)
{
    long end = Environment.TickCount64 + 12_000;
    while (Environment.TickCount64 < end)
    {
        if (await check()) { Check(true, label); return; }
        await Task.Delay(100);
    }
    Check(false, label);
}

static async Task<JsonElement> State(AppController controller) => JsonSerializer.SerializeToElement(await controller.GetStateAsync(), AppController.Json);
static async Task<JsonElement> Peer(AppController host, AppController remote) => (await State(host)).GetProperty("devices")
    .EnumerateArray().FirstOrDefault(peer => peer.GetProperty("id").GetString() == remote.LocalIdentity.Id);
static async Task<JsonElement> Command(AppController host, object command) => JsonSerializer.SerializeToElement(
    await host.DispatchAsync(JsonSerializer.SerializeToElement(command, AppController.Json)), AppController.Json);
static async Task Confirm(AppController controller, JsonElement prompt) => Check((await Command(controller,
    new { type = "pair-decision", id = prompt.GetProperty("id").GetString(), accept = true })).GetProperty("ok").GetBoolean(), "Local approval was accepted");

sealed class Host(string root, string platform) : IPlatformHost
{
    public string Platform => platform;
    public string DataDirectory => root;
    public string DeviceName => "Synthetic " + platform;
    public bool UseUdpDiscovery => false;
    public ISecretStore Secrets { get; } = new MemorySecrets();
    public Task<IReadOnlyList<PickedFile>> PickAsync() => Task.FromResult<IReadOnlyList<PickedFile>>([]);
    public Task OpenFolderAsync(string folder) => Task.CompletedTask;
    public Task<string?> ThumbnailAsync(string file, string mime) => Task.FromResult<string?>(null);
}

sealed class MemorySecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> values = new();
    public Task<string?> GetAsync(string key) => Task.FromResult(values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value) { values[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key) { values.TryRemove(key, out _); return Task.CompletedTask; }
}
