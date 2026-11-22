using Amethyst;
using Amethyst.BuiltinShards;
using Amethyst.Shards;

/// <summary>Block regression fixture: production Gameplay receives the
/// authoritative entity services through ShardHost. Direct test servers must
/// pass the same services explicitly rather than creating entity stores in
/// Gameplay.</summary>
internal static class TestServerFactory
{
    private static string EntityYaml()
    {
        DirectoryInfo? folder = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (folder is not null)
        {
            string file = Path.Combine(folder.FullName, "shards",
                "Amethyst.Shards.Entities", "entities-26.3.yml");
            if (File.Exists(file)) return file;
            folder = folder.Parent;
        }
        throw new FileNotFoundException("Cannot locate pinned entity definitions.");
    }

    public static MinecraftServer Create(ServerOptions options, IShardWorld world,
        IShardPacketBus packetBus, IShardBlockWorld? blocks = null,
        string? inventoryDirectory = null, IShardItemRegistry? items = null,
        IShardRecipeRegistry? recipes = null, IShardEntityManager? entities = null,
        IShardEntityRegistry? entityTypes = null,
        IShardEntityInteractions? interactions = null,
        IShardEnchantmentRegistry? enchantments = null,
        IShardSkillEngine? skills = null,
        IShardLootRegistry? loot = null,
        IShardVanillaLootTables? vanillaLoot = null,
        IShardVanillaBlockStates? vanillaBlockStates = null,
        IShardConsumableRegistry? consumables = null)
    {
        var catalog = entityTypes ?? new EntityCatalog(EntityYaml());
        EntityManager manager = entities as EntityManager ?? new EntityManager(catalog);
        entities ??= manager;
        interactions ??= manager;
        return new MinecraftServer(options, world, packetBus, blocks,
            inventoryDirectory, items, recipes, entities, catalog,
            interactions, enchantments, skills, loot, vanillaLoot,
            vanillaBlockStates, consumables, manager, manager, manager,
            manager, manager);
    }
}
