using Amethyst;
using Amethyst.BuiltinShards;

internal static class ShieldFeatureTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        var defending = new PlayerPose(0, 80, 0, 0, 0, true);
        Check(ShieldRules.FacesAttacker(defending,
            new PlayerPose(0, 80, 2, 0, 0, true)) &&
            !ShieldRules.FacesAttacker(defending,
                new PlayerPose(0, 80, -2, 0, 0, true)),
            "Shield blocks only forward-facing attacks");
        var control = new PlayerUseState();
        Check(control.BeginShield(1, 1000) && control.IsBlocking(1010),
            "Offhand shielding can be activated");
        control.DisableShield(2000);
        Check(!control.IsBlocking(2010) &&
            !control.BeginShield(1, 2500) &&
            control.BeginShield(1, 7001),
            "Axe disabling imposes a five-second blocking cooldown");
        control.StopShield();
        Check(!control.IsBlocking(7002), "Releasing use stops the shield");

        var writer = new PacketWriter();
        writer.WriteVarInt(1); writer.WriteVarInt(7);
        writer.WriteFloat(25); writer.WriteFloat(-10);
        UseItemAction packet = UseItemProtocol.ReadUseItem(new Packet(
            UseItemProtocol.ServerboundUseItem, writer.ToArray()));
        Check(packet.Hand == 1 && packet.Sequence == 7 &&
            packet.Yaw == 25 && packet.Pitch == -10,
            "Java 26.3 use-item packet decodes offhand and sequence");

        Check(items.TryGetByIdentifier("shield", out var shield) &&
            items.TryGetByIdentifier("iron_axe", out var axe) &&
            items.TryGetByIdentifier("diamond_sword", out var sword),
            "Shield, axe, and sword use version-pinned item IDs");
        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-shield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ServerOptions
            {
                Seed = 44531, GeneratorVersion = 5, ViewDistance = 2,
                SaveGeneratedChunks = false, WorldPath = Path.Combine(root, "world")
            };
            await using var world = new TerrainWorld(settings);
            using var aStream = new MemoryStream();
            using var bStream = new MemoryStream();
            using var aChannel = new PacketChannel(aStream);
            using var bChannel = new PacketChannel(bStream);
            var server = new MinecraftServer(settings, world,
                null!, world, items: items, recipes: recipes);
            Guid aid = Guid.NewGuid(), bid = Guid.NewGuid();
            Check(server.TryAdmit(
                    new VerifiedProfile(aid, "ShieldAttacker", []),
                    aChannel, 0, out _) &&
                server.TryAdmit(
                    new VerifiedProfile(bid, "ShieldDefender", []),
                    bChannel, 0, out _),
                "Two players can join a shield combat session");
            ConnectedPlayer attacker = server.GetPlayerForTests(aid)!;
            ConnectedPlayer victim = server.GetPlayerForTests(bid)!;
            attacker.MarkReady(); victim.MarkReady();
            attacker.SetGameMode(PlayerGameMode.Survival);
            victim.SetGameMode(PlayerGameMode.Survival);
            attacker.SetPose(new PlayerPose(0, 81, 2, 0, 0, true));
            victim.SetPose(new PlayerPose(0, 81, 0, 0, 0, true));
            Check(victim.Inventory.TryCreativeSet(45, shield.ProtocolId, 1, true) &&
                attacker.Inventory.TryCreativeSet(36, sword.ProtocolId, 1, true),
                "Offhand shield and mainhand weapon are valid inventory slots");
            await server.HandleUseItemAsync(victim,
                new UseItemAction(1, 20, 0, 0), CancellationToken.None);
            Check(victim.UseState.IsBlocking(Environment.TickCount64),
                "Offhand use-item activates server-authoritative blocking");
            await server.HandleAttackAsync(attacker, victim.EntityId,
                CancellationToken.None);
            Check(victim.Vitals.Snapshot().Health == 20 &&
                victim.Inventory.Snapshot().Slots[45].Damage == 1 &&
                attacker.Inventory.Held().Damage == 1,
                "Shield absorbs a front melee hit, loses durability, and wears the weapon");
            Check(attacker.Inventory.TryCreativeSet(36, axe.ProtocolId, 1, true),
                "Equip axe for disabling test");
            await Task.Delay(150);
            await server.HandleAttackAsync(attacker, victim.EntityId,
                CancellationToken.None);
            Check(!victim.UseState.IsBlocking(Environment.TickCount64) &&
                victim.Inventory.Snapshot().Slots[45].Damage == 2 &&
                victim.Vitals.Snapshot().Health == 20,
                "Successful axe strike disables an active shield without damaging health");
            await Task.Delay(150);
            await server.HandleAttackAsync(attacker, victim.EntityId,
                CancellationToken.None);
            Check(victim.Vitals.Snapshot().Health < 20,
                "Disabled shield no longer negates incoming melee damage");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
