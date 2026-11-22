using Amethyst;
using Amethyst.BuiltinShards;
using Amethyst.Shards;

internal static class BucketFeatureTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        if (!items.TryGetByIdentifier("minecraft:bucket", out var empty) ||
            !items.TryGetByIdentifier("minecraft:water_bucket", out var water) ||
            !items.TryGetByIdentifier("minecraft:lava_bucket", out var lava) ||
            !items.TryGetByIdentifier("minecraft:stone", out var stone))
            throw new InvalidDataException("Missing vanilla fluid bucket items.");

        var blocks = new FluidWorld();
        blocks.Set(1, 80, 0, FluidWorld.SourceWater);
        blocks.Set(2, 80, 0, 1); // click base for placement
        blocks.Set(1, 80, 1, FluidWorld.SourceLava);
        blocks.Set(2, 80, 1, 1);
        blocks.Set(1, 80, 2, FluidWorld.FlowingWater);

        Check(FluidBucketRules.TryFill(FluidWorld.SourceWater, blocks, out var filled) &&
            filled == "minecraft:water_bucket" &&
            FluidBucketRules.TryFill(FluidWorld.SourceLava, blocks, out filled) &&
            filled == "minecraft:lava_bucket" &&
            !FluidBucketRules.TryFill(FluidWorld.FlowingWater, blocks, out _) &&
            FluidBucketRules.TryPlace("minecraft:water_bucket", blocks, out var state) &&
            state == FluidWorld.SourceWater &&
            !FluidBucketRules.TryPlace("minecraft:milk_bucket", blocks, out _),
            "Fluid rules reject flowing water and only resolve pinned source states");

        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-buckets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ServerOptions
            {
                Seed = 2468, GeneratorVersion = 5, ViewDistance = 2,
                SaveGeneratedChunks = false,
                WorldPath = Path.Combine(root, "world")
            };
            await using var world = new TerrainWorld(settings);
            using var output = new MemoryStream();
            using var channel = new PacketChannel(output);
            var server = TestServerFactory.Create(settings, world,
                null!, blocks, items: items, recipes: recipes,
                vanillaBlockStates: blocks);
            Guid id = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(id, "BucketTester", []),
                channel, 0, out _),
                "Server accepts player for authoritative fluid bucket tests");
            ConnectedPlayer player = server.GetPlayerForTests(id)!;
            player.MarkReady();
            player.SetGameMode(PlayerGameMode.Survival);
            player.SetPose(new PlayerPose(0.5, 80, 0.5, 0, 0, true));
            Check(player.Inventory.TryCreativeSet(36, empty.ProtocolId, 2, true),
                "Prepare a two-bucket stack");
            await server.HandleBlockUseAsync(player,
                new BlockUseOn(0, 1, 80, 0, 1, 91, true), CancellationToken.None);
            ItemStack[] afterFill = player.Inventory.Snapshot().Slots;
            Check(blocks.GetBlockState(1, 80, 0) == 0 &&
                afterFill[36].ItemId == empty.ProtocolId && afterFill[36].Count == 1 &&
                afterFill[37].ItemId == water.ProtocolId && afterFill[37].Count == 1,
                "Survival source-water pickup removes fluid and converts exactly one bucket");

            player.SelectHotbar(1);
            await server.HandleBlockUseAsync(player,
                new BlockUseOn(0, 2, 80, 0, 1, 92, true), CancellationToken.None);
            Check(blocks.GetBlockState(2, 81, 0) == FluidWorld.SourceWater &&
                player.Inventory.Held().ItemId == empty.ProtocolId,
                "Placing a filled bucket creates the canonical source state and returns bucket");

            player.SelectHotbar(0);
            await server.HandleBlockUseAsync(player,
                new BlockUseOn(0, 1, 80, 1, 1, 93, true), CancellationToken.None);
            Check(blocks.GetBlockState(1, 80, 1) == 0 &&
                player.Inventory.Held().ItemId == lava.ProtocolId,
                "Source-lava pickup is distinct from water and uses its own material");

            Check(player.Inventory.TryCreativeSet(36, empty.ProtocolId, 1, true),
                "Prepare empty bucket for invalid-flow test");
            await server.HandleBlockUseAsync(player,
                new BlockUseOn(0, 1, 80, 2, 1, 94, true), CancellationToken.None);
            Check(player.Inventory.Held().ItemId == empty.ProtocolId &&
                blocks.GetBlockState(1, 80, 2) == FluidWorld.FlowingWater,
                "Flowing fluid cannot be duplicated into a source bucket");

            // With no empty inventory slot to accept a filled bucket, the
            // source block must be restored after failed conversion.
            Check(player.Inventory.TryCreativeSet(36, empty.ProtocolId, 2, true),
                "Prepare a stack of buckets for full inventory rollback");
            foreach (int slot in Enumerable.Range(9, 27)
                .Concat(Enumerable.Range(37, 8)))
                Check(player.Inventory.TryCreativeSet(
                    slot, stone.ProtocolId, 64, true),
                    "Reserve inventory slot for full-inventory rollback");
            blocks.Set(1, 80, 0, FluidWorld.SourceWater);
            await server.HandleBlockUseAsync(player,
                new BlockUseOn(0, 1, 80, 0, 1, 95, true), CancellationToken.None);
            Check(blocks.GetBlockState(1, 80, 0) == FluidWorld.SourceWater &&
                player.Inventory.Held().ItemId == empty.ProtocolId &&
                player.Inventory.Held().Count == 2,
                "Rejected bucket conversion restores the fluid and retains both inventory items");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    private sealed class FluidWorld : IShardBlockWorld, IShardVanillaBlockStates,
        IShardDefaultBlockStates
    {
        public const ushort SourceWater = 20100;
        public const ushort SourceLava = 20101;
        public const ushort FlowingWater = 20102;
        private readonly Dictionary<(int X, int Y, int Z), ushort> _states = [];
        public int Count => 4;
        public void Set(int x, int y, int z, ushort state)
        {
            if (state == 0) _states.Remove((x, y, z));
            else _states[(x, y, z)] = state;
        }
        public ushort GetBlockState(int x, int y, int z) =>
            _states.TryGetValue((x, y, z), out ushort state) ? state : (ushort)0;
        public bool TrySetBlockState(int x, int y, int z, ushort state,
            out ushort previous)
        {
            previous = GetBlockState(x, y, z);
            if (previous == state) return false;
            Set(x, y, z, state);
            return true;
        }
        public bool TryGet(ushort state, out string blockId,
            out IReadOnlyDictionary<string, string> properties)
        {
            blockId = state switch
            {
                SourceWater or FlowingWater => "minecraft:water",
                SourceLava => "minecraft:lava",
                1 => "minecraft:stone",
                _ => ""
            };
            properties = state is SourceWater or SourceLava
                ? new Dictionary<string, string> { ["level"] = "0" }
                : state == FlowingWater
                    ? new Dictionary<string, string> { ["level"] = "5" }
                    : new Dictionary<string, string>();
            return blockId.Length > 0;
        }
        public bool TryGetDefaultState(string blockId, out ushort state)
        {
            state = blockId switch
            {
                "minecraft:water" => SourceWater,
                "minecraft:lava" => SourceLava,
                _ => (ushort)0
            };
            return state != 0;
        }
    }
}
