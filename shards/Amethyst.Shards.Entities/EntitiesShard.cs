using System.Globalization;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

public sealed class EntitiesShard : IShard
{
    private IShardContext? _context;

    public Task OnLoadAsync(IShardContext context, CancellationToken ct)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnEnableAsync(CancellationToken ct)
    {
        IShardContext ctx = _context ?? throw new InvalidOperationException("Entities Shard not loaded.");
        string directory = Path.GetDirectoryName(typeof(EntitiesShard).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot locate Entities Shard assembly.");
        string overrides = Path.Combine(ctx.DataDirectory, "definitions");
        Directory.CreateDirectory(overrides);
        string example = Path.Combine(overrides, "example-entities.yml");
        if (!File.Exists(example))
            File.WriteAllText(example, """
                # Each root key is an entity type or a custom alias. Uncomment to activate.
                # cow:
                #   Id: minecraft:cow
                #   Display: "Farm Cow"
                #   Health: 20
                #   Options:
                #     Invulnerable: false
                #     Spawnable: true
                #
                # guardian_cow:
                #   Id: minecraft:cow
                #   Display: "Guardian Cow"
                #   Health: 50
                #   Options:
                #     Invulnerable: true
                """ + Environment.NewLine);

        var catalog = new EntityCatalog(Path.Combine(directory, "entities-26.3.yml"), overrides);
        var manager = new EntityManager(catalog);
        ctx.RegisterService<IShardEntityRegistry>(catalog);
        ctx.RegisterService<IShardEntityManager>(manager);
        ctx.RegisterService<IShardEntityInteractions>(manager);
        ctx.RegisterService<IShardLivingEntities>(manager);
        ctx.RegisterService<IShardExperienceOrbs>(manager);
        ctx.RegisterService<IShardFishingWorld>(manager);
        ctx.RegisterService<IShardGroundItems>(manager);
        ctx.RegisterService<IShardProjectileWorld>(manager);
        // The Entities Shard supplies these interaction event triggers to the
        // universal Core skill registry; it does not own skill parsing.
        ctx.RegisterSkillTrigger("onMilk");
        ctx.RegisterSkillTrigger("onShear");
        ctx.RegisterConsoleCommand("entityinfo", (args, token) =>
        {
            string query = args.Trim();
            bool found = int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int id) ? catalog.TryGetById(id, out ShardEntityDefinition byId) && Report(byId)
                : catalog.TryGetByIdentifier(query, out ShardEntityDefinition byName) && Report(byName);
            if (!found) ctx.Log("Usage: entityinfo <entity-id|minecraft:entity_name|alias>");
            return Task.CompletedTask;

            bool Report(ShardEntityDefinition entity)
            {
                ctx.Log($"{entity.Identifier} | protocol {entity.ProtocolId} | {entity.Category} | " +
                    $"hitbox {entity.Width}x{entity.Height} | spawnable {entity.Spawnable} | " +
                    $"health {entity.MaxHealth?.ToString(CultureInfo.InvariantCulture) ?? "unconfigured"}" +
                    (entity.Display is null ? "" : $" | display {entity.Display}"));
                return true;
            }
        });
        ctx.RegisterConsoleCommand("entityspawn", (args, token) =>
        {
            string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z) ||
                !manager.TrySpawn(parts[0], x, y, z, out var spawned))
                ctx.Log("Usage: entityspawn <entity-type> <x> <y> <z> (spawnable 26.3 types only)");
            else
                ctx.Log($"Spawned {spawned.DefinitionId} as entity #{spawned.EntityId} at {x:F1}, {y:F1}, {z:F1}");
            return Task.CompletedTask;
        });
        ctx.RegisterConsoleCommand("entitykill", (args, token) =>
        {
            bool killed = int.TryParse(args.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int id) && manager.TryKill(id, out _);
            ctx.Log(killed ? $"Killed entity #{id} (loot will be rolled by Gameplay)" :
                "Usage: entitykill <active-entity-id> (non-invulnerable entity)");
            return Task.CompletedTask;
        });
        ctx.RegisterConsoleCommand("entityremove", (args, token) =>
        {
            bool removed = int.TryParse(args.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int id) && manager.TryRemove(id);
            ctx.Log(removed ? $"Removed entity #{id}" : "Usage: entityremove <active-entity-id>");
            return Task.CompletedTask;
        });
        ctx.RegisterConsoleCommand("entities", (args, token) =>
        {
            ShardEntityInstance[] active = manager.Snapshot();
            ctx.Log($"{active.Length} active entities; {catalog.Count} vanilla types. " +
                string.Join(", ", active.Take(10).Select(e => $"#{e.EntityId} {e.DefinitionId}")));
            return Task.CompletedTask;
        });
        ctx.Log($"Registered {catalog.Count} Java {catalog.MinecraftVersion} entity types; " +
            $"editable YAML: {overrides}.");
        return Task.CompletedTask;
    }

    public Task OnDisableAsync(CancellationToken ct) => Task.CompletedTask;
}
