using Amethyst;
using Amethyst.Shards;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

DirectoryInfo? root = new(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Amethyst.slnx")))
    root = root.Parent;
if (root is null) throw new InvalidOperationException("Amethyst.slnx not found above test output.");
string installed = Path.Combine(root.FullName, "shards", "installed");
if (!Directory.Exists(installed))
    throw new InvalidOperationException("Build Amethyst.slnx to install the test Shards first.");
string temp = Path.Combine(Path.GetTempPath(), "amethyst-shard-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var options = new ServerOptions
    {
        GeneratorVersion = 1,
        Seed = 100L,
        ViewDistance = 2,
        SaveGeneratedChunks = false,
        OperatorsFile = Path.Combine(temp, "ops.yaml"),
        WorldPath = Path.Combine(temp, "world")
    };
    await using var host = new ShardHost(options, installed, Path.Combine(temp, "shards"));
    await host.LoadAsync();
    Check(host.GetService<IShardWorld>() is not null, "default overworld provider enabled");
    Check(host.GetService<IShardGameplay>() is not null, "gameplay enabled with world provider");
    IShardItemRegistry items = host.GetService<IShardItemRegistry>() ??
        throw new Exception("FAIL: Items Shard service not registered");
    Check(items.MinecraftVersion == "26.3" && items.Count == 1658 &&
        items.All.Count == 1658,
        "Items Shard auto-enabled and complete 26.3 definitions published");
    Check(items.TryGetById(405, out var table) &&
        table.Identifier == "minecraft:crafting_table" &&
        table.MaxStackSize == 64 && table.IsBlockItem &&
        items.TryGetByIdentifier("crafting_table", out var byName) && byName == table,
        "Items can be resolved by protocol ID and canonical/short identifier");
    Check(items.TryGetByIdentifier("netherite_pickaxe", out var pickaxe) &&
        pickaxe.MaxStackSize == 1 &&
        items.TryGetByIdentifier("minecraft:ender_pearl", out var pearl) &&
        pearl.MaxStackSize == 16 &&
        items.IsValidStack(pickaxe.ProtocolId, 1) &&
        !items.IsValidStack(pickaxe.ProtocolId, 2) &&
        items.IsValidStack(pearl.ProtocolId, 16) &&
        !items.IsValidStack(pearl.ProtocolId, 17) &&
        !items.IsValidStack(1658, 1),
        "Known item stack caps and unknown-ID rejection come from the Items Shard");
    Check(host.RegisteredConsoleCommands.Contains("iteminfo") &&
        await host.TryExecuteConsoleAsync("iteminfo 405", CancellationToken.None),
        "iteminfo command is registered by the Items Shard");
    Check(host.Snapshot().Any(s => s.Id == "hello" && s.State == ShardState.Loaded),
        "example Shard installed but disabled");

    bool blocked = false;
    try { await host.DisableAsync("overworld"); }
    catch (InvalidOperationException) { blocked = true; }
    Check(blocked, "live dependency protects world provider from disabling");

    await host.EnableAsync("hello");
    Check(host.RegisteredConsoleCommands.Contains("hello"), "Shard console registration visible");
    Check(await host.TryExecuteConsoleAsync("hello Test", CancellationToken.None),
        "Shard console handler called");
    await host.DisableAsync("hello");
    Check(!await host.TryExecuteConsoleAsync("hello Test", CancellationToken.None),
        "Shard commands removed upon disable");

    bool itemsBlocked = false;
    try { await host.DisableAsync("items"); }
    catch (InvalidOperationException) { itemsBlocked = true; }
    Check(itemsBlocked, "Gameplay dependency protects the Items Shard from being disabled");

    await host.DisableAsync("gameplay");
    Check(host.GetService<IShardGameplay>() is null, "gameplay service removed");
    await host.DisableAsync("items");
    Check(host.GetService<IShardItemRegistry>() is null &&
        !host.RegisteredConsoleCommands.Contains("iteminfo"),
        "Disabling Items Shard unregisters its definitions and iteminfo command");
    await host.DisableAsync("overworld");
    Check(host.GetService<IShardWorld>() is null, "world service removed");

    await host.EnableAsync("gameplay");
    Check(host.GetService<IShardWorld>() is not null &&
        host.GetService<IShardGameplay>() is not null &&
        host.GetService<IShardItemRegistry>() is not null,
        "Enabling Gameplay automatically enables both World and Items providers");
}
finally
{
    try { Directory.Delete(temp, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("PASS: Shard lifecycle, dependency ordering, service API, console commands");
