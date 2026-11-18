using System.Collections.ObjectModel;
using System.Globalization;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>Immutable, version-specific vanilla item catalog. IDs and stack
/// limits are read from a pinned snapshot, not guessed from item names.
/// A vanilla protocol-777 client understands these IDs without custom mods.</summary>
public sealed class ItemCatalog : IShardItemRegistry
{
    private readonly ShardItemDefinition[] _byId;
    private readonly IReadOnlyList<ShardItemDefinition> _all;
    private readonly Dictionary<string, ShardItemDefinition> _byName;

    public string MinecraftVersion => "26.3";
    public int Count => _byId.Length;
    public IReadOnlyList<ShardItemDefinition> All => _all;

    public ItemCatalog(string catalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        var definitions = new List<ShardItemDefinition>();
        _byName = new Dictionary<string, ShardItemDefinition>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(catalogPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] fields = line.Split('\t');
            if (fields.Length != 4 ||
                !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int maxStack) ||
                id != definitions.Count || maxStack is not (1 or 16 or 64) ||
                !fields[1].StartsWith("minecraft:", StringComparison.Ordinal) ||
                fields[1].Length <= "minecraft:".Length ||
                !fields[1].Skip("minecraft:".Length).All(c =>
                    c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '/' or '.') ||
                fields[3] is not ("0" or "1"))
                throw new InvalidDataException($"Invalid item definition at index {definitions.Count}.");
            var definition = new ShardItemDefinition(id, fields[1], maxStack, fields[3] == "1");
            if (!_byName.TryAdd(definition.Identifier, definition))
                throw new InvalidDataException($"Duplicate item identifier '{fields[1]}'.");
            definitions.Add(definition);
        }
        // Fail fast on stale or incomplete data instead of silently returning
        // default stack sizes for unknown items.
        if (definitions.Count != 1658 || definitions[0].Identifier != "minecraft:air" ||
            definitions[^1].ProtocolId != 1657)
            throw new InvalidDataException("Wrong Minecraft 26.3 item catalog version or item count.");
        _byId = definitions.ToArray();
        _all = new ReadOnlyCollection<ShardItemDefinition>(_byId);
    }

    public bool TryGetById(int protocolId, out ShardItemDefinition definition)
    {
        if ((uint)protocolId < (uint)_byId.Length)
        {
            definition = _byId[protocolId];
            return true;
        }
        definition = default;
        return false;
    }

    public bool TryGetByIdentifier(string identifier, out ShardItemDefinition definition)
    {
        definition = default;
        if (string.IsNullOrWhiteSpace(identifier)) return false;
        string key = identifier.Contains(':', StringComparison.Ordinal)
            ? identifier : "minecraft:" + identifier;
        return _byName.TryGetValue(key, out definition);
    }

    public bool IsValidStack(int protocolId, int count) =>
        protocolId == 0 ? count == 0 :
        count > 0 && TryGetById(protocolId, out var item) &&
        count <= item.MaxStackSize;
}

/// <summary>Independent Shard responsible for item identity and stack rules.
/// Other Shards (world, gameplay, future crafting) use IShardItemRegistry;
/// the Amethyst networking core contains no vanilla item definitions.</summary>
public sealed class ItemsShard : IShard
{
    private IShardContext? _context;
    public Task OnLoadAsync(IShardContext context, CancellationToken ct)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnEnableAsync(CancellationToken ct)
    {
        IShardContext ctx = _context ?? throw new InvalidOperationException("Items Shard is not loaded.");
        string directory = Path.GetDirectoryName(typeof(ItemsShard).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot locate Items Shard assembly.");
        var catalog = new ItemCatalog(Path.Combine(directory, "items-26.3.tsv"));
        ctx.RegisterService<IShardItemRegistry>(catalog);
        ctx.RegisterConsoleCommand("iteminfo", (args, token) =>
        {
            string query = args.Trim();
            bool found = int.TryParse(query, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int protocolId)
                ? catalog.TryGetById(protocolId, out ShardItemDefinition byId)
                    && Report(byId)
                : catalog.TryGetByIdentifier(query, out ShardItemDefinition byName)
                    && Report(byName);
            if (!found) ctx.Log("Usage: iteminfo <item-id|minecraft:item_name> (known vanilla 26.3 item)");
            return Task.CompletedTask;

            bool Report(ShardItemDefinition item)
            {
                ctx.Log($"{item.Identifier} | protocol ID {item.ProtocolId} | " +
                    $"max stack {item.MaxStackSize} | block item: {item.IsBlockItem}");
                return true;
            }
        });
        ctx.Log($"Registered {catalog.Count} vanilla Java {catalog.MinecraftVersion} item definitions.");
        return Task.CompletedTask;
    }

    public Task OnDisableAsync(CancellationToken ct) => Task.CompletedTask;
}
