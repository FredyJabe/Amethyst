using Amethyst;
using Amethyst.BuiltinShards;

internal static class ElytraFeatureTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        var zero = new PacketWriter();
        zero.WriteLpVec3(0, 0, 0);
        var motion = new PacketWriter();
        motion.WriteLpVec3(0.2, 0.26, -0.3);
        var scaled = new PacketWriter();
        scaled.WriteLpVec3(5, -2, 9);
        Check(zero.ToArray().Length == 1 && zero.ToArray()[0] == 0 &&
            motion.ToArray().Length == 6 &&
            scaled.ToArray().Length >= 7,
            "Java 26.3 LP_VEC3 motion uses compact mixed-endian encoding");

        var current = new PlayerPose(0, 100, 0, 0, 0, false);
        Check(!FlightRules.PlausibleMovement(current,
                current with { X = 50 }, false) &&
            FlightRules.PlausibleMovement(current,
                current with { X = 3 }, false) &&
            FlightRules.PlausibleMovement(current,
                current with { X = 9 }, true),
            "Gliding movement bounds permit rockets but reject teleports");
        var playerState = new PlayerFlightState();
        Check(playerState.Begin(1000) &&
            !playerState.WearDue(1500) &&
            playerState.WearDue(2001) &&
            !playerState.WearDue(2100) &&
            playerState.Boost(2200) &&
            playerState.IsBoosted(2500) &&
            !playerState.IsBoosted(4000) &&
            playerState.End(),
            "Elytra wear is throttled to one second and rockets add timed boost");

        Amethyst.Shards.ShardItemDefinition elytra = default,
            rocket = default;
        Check(items.TryGetByIdentifier("elytra", out elytra) &&
            items.TryGetByIdentifier("firework_rocket", out rocket) &&
            FlightRules.CanStart(false, new ItemStack(elytra.ProtocolId, 1),
                elytra.MaxDurability) &&
            !FlightRules.CanStart(true, new ItemStack(elytra.ProtocolId, 1),
                elytra.MaxDurability) &&
            !FlightRules.CanStart(false, new ItemStack(elytra.ProtocolId, 1)
                { Damage = elytra.MaxDurability - 1 }, elytra.MaxDurability),
            "Elytra requires an airborne, nonbroken chest-slot item");

        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-elytra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ServerOptions
            {
                Seed = 77532, GeneratorVersion = 5,
                ViewDistance = 2, SaveGeneratedChunks = false,
                WorldPath = Path.Combine(root, "world")
            };
            await using var world = new TerrainWorld(settings);
            using var stream = new MemoryStream();
            using var channel = new PacketChannel(stream);
            var server = new MinecraftServer(settings, world,
                null!, world, items: items, recipes: recipes);
            Guid id = Guid.NewGuid();
            Check(server.TryAdmit(new VerifiedProfile(id,
                "ElytraTester", []), channel, 0, out _),
                "Player admitted for flight test");
            var actor = server.GetPlayerForTests(id)!;
            actor.MarkReady();
            actor.SetGameMode(PlayerGameMode.Survival);
            actor.SetPose(new PlayerPose(0.5, 120, 0.5, 0, -10, false));
            Check(actor.Inventory.TryCreativeSet(6, elytra.ProtocolId, 1, true) &&
                actor.Inventory.TryCreativeSet(36, rocket.ProtocolId, 2, true),
                "Equip real Elytra in chest slot and firework rockets in hotbar");
            var commandWriter = new PacketWriter();
            commandWriter.WriteVarInt(actor.EntityId);
            commandWriter.WriteVarInt(FlightProtocol.StartFallFlying);
            commandWriter.WriteVarInt(0);
            PlayerCommandAction command = FlightProtocol.ReadPlayerCommand(
                new Packet(FlightProtocol.ServerboundPlayerCommand,
                    commandWriter.ToArray()));
            await server.HandleFlightCommandAsync(actor, command,
                CancellationToken.None);
            Check(actor.Flight.IsGliding,
                "Java 26.3 START_FALL_FLYING activates authoritative gliding");
            await server.TickElytraAsync(CancellationToken.None,
                Environment.TickCount64 + 1300);
            Check(actor.Inventory.Snapshot().Slots[6].Damage == 1,
                "Gliding costs one Elytra durability per elapsed second");
            Check(await server.HandleElytraUseAsync(actor,
                new UseItemAction(0, 61, 0, -10),
                CancellationToken.None) &&
                actor.Flight.IsBoosted(Environment.TickCount64) &&
                actor.Inventory.Held().Count == 1,
                "Firework boost consumes one rocket and increases flight momentum");
            actor.SetPose(actor.Pose with { OnGround = true });
            await server.UpdateGlideMovementAsync(actor,
                actor.Pose with { OnGround = false },
                actor.Pose, 1, CancellationToken.None);
            Check(!actor.Flight.IsGliding,
                "Landing ends flight and resets Elytra animation");

            actor.SetPose(actor.Pose with { OnGround = false });
            var (slots, cursor, revision) = actor.Inventory.Snapshot();
            slots[6] = slots[6] with { Damage = elytra.MaxDurability - 2 };
            Check(actor.Inventory.TryCommitWindow(revision, slots, cursor),
                "Set up an Elytra with two durability points remaining");
            await server.HandleFlightCommandAsync(actor, command,
                CancellationToken.None);
            Check(actor.Flight.IsGliding,
                "Elytra remains flyable until one remaining durability");
            await server.TickElytraAsync(CancellationToken.None,
                Environment.TickCount64 + 1300);
            Check(!actor.Flight.IsGliding &&
                actor.Inventory.Snapshot().Slots[6].Count == 1 &&
                actor.Inventory.Snapshot().Slots[6].Damage ==
                    elytra.MaxDurability - 1,
                "Elytra stops gliding at one remaining point without breaking");

            actor.SetGameMode(PlayerGameMode.Creative);
            slots = actor.Inventory.Snapshot().Slots;
            var newState = actor.Inventory.Snapshot();
            slots[6] = slots[6] with { Damage = 5 };
            Check(actor.Inventory.TryCommitWindow(newState.Revision, slots,
                newState.Cursor), "Restore Elytra for creative flight");
            await server.HandleFlightCommandAsync(actor, command,
                CancellationToken.None);
            await server.TickElytraAsync(CancellationToken.None,
                Environment.TickCount64 + 2000);
            Check(actor.Inventory.Snapshot().Slots[6].Damage == 5,
                "Creative gliding does not damage Elytra equipment");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
