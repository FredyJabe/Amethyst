using Amethyst;
using Amethyst.BuiltinShards;

internal static class DurabilityFeatureTests
{
    private sealed class FixedRandom(bool ignore) : Random
    {
        public override int Next(int maxValue) => ignore ? maxValue - 1 : 0;
        public override int Next(int minValue, int maxValue) =>
            ignore ? maxValue - 1 : minValue;
        public override double NextDouble() => ignore ? 1.0 : 0.0;
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    public static async Task RunAsync(ItemCatalog items, RecipeCatalog recipes)
    {
        Amethyst.Shards.ShardItemDefinition helm = default, axe = default, shovel = default;
        Check(items.TryGetByIdentifier("wooden_pickaxe", out var wood) &&
            items.TryGetByIdentifier("iron_helmet", out helm) &&
            items.TryGetByIdentifier("diamond_axe", out axe) &&
            items.TryGetByIdentifier("iron_shovel", out shovel),
            "Pinned items cover tool and equipment durability");
        ItemStack item = new(wood.ProtocolId, 1) { Damage = 10, Unbreaking = 3 };
        Check(DurabilityRules.ApplyWear(item, wood.MaxDurability, 10,
            random: new FixedRandom(true)) == item &&
            DurabilityRules.ApplyWear(item, wood.MaxDurability, 3,
            random: new FixedRandom(false)).Damage == 13,
            "Unbreaking uses independent per-point prevention rolls");
        Check(DurabilityRules.ApplyWear(item with { Unbreakable = true },
            wood.MaxDurability, 100) == item with { Unbreakable = true },
            "Unbreakable items do not take damage");
        ItemStack armor = new(helm.ProtocolId, 1)
            { Damage = 4, Unbreaking = 3 };
        Check(DurabilityRules.ApplyWear(armor, helm.MaxDurability, 3,
            armor: true, random: new FixedRandom(false)).Damage == 7 &&
            DurabilityRules.ApplyWear(armor, helm.MaxDurability, 3,
            armor: true, random: new FixedRandom(true)) == armor,
            "Armor has vanilla Unbreaking's reduced prevention probability");
        Check(DurabilityRules.TryCombine(
                new ItemStack(wood.ProtocolId, 1) { Damage = 50, Unbreaking = 3 },
                new ItemStack(wood.ProtocolId, 1) { Damage = 40, Mending = true },
                items, out ItemStack craftRepair) &&
            craftRepair.Damage == 29 && craftRepair.Unbreaking == 0 &&
            !craftRepair.Mending && craftRepair.RepairCost == 0,
            "Two damaged items repair with a 5% bonus and clear enchantments");
        Check(DurabilityRules.TryMaterialRepair(
            item with { Damage = 50 }, 2, wood.MaxDurability,
            out ItemStack anvilRepair, out int consumed) &&
            anvilRepair.Damage == 22 && consumed == 2 &&
            anvilRepair.Unbreaking == 3,
            "Anvil material-repair calculations retain enchantments");

        string root = Path.Combine(Path.GetTempPath(),
            "amethyst-full-wear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid playerId = Guid.NewGuid();
            var inventory = new PlayerInventory(root, playerId, items);
            Check(inventory.TryCreativeSet(36, wood.ProtocolId, 1, true) &&
                inventory.ConfigureHeld(3, mending: true, unbreakable: false),
                "Enchantments can be authored only by the authoritative server");
            Check(!inventory.DamageSlot(36, wood.ProtocolId, 8,
                    random: new FixedRandom(true)) &&
                inventory.Held().Damage == 0 &&
                inventory.Held().Unbreaking == 3,
                "Server applies Unbreaking to mining wear");
            // Replace with a known worn stack using a revision-checked,
            // server-authoritative transaction.
            var (slots, cursor, revision) = inventory.Snapshot();
            slots[36] = slots[36] with { Damage = 9 };
            Check(inventory.TryCommitWindow(revision, slots, cursor),
                "Server accepts worn enchanted tools with safe component values");
            Check(inventory.RepairFromExperience(4) == (0, 36) &&
                inventory.Held().Damage == 1 &&
                inventory.Held().Mending,
                "XP collection repairs main-hand Mending tools at two wear per XP");
            Check(new PlayerInventory(root, playerId, items).Held() ==
                inventory.Held(), "Enchantments and repairs survive reconnect");
            var writer = new PacketWriter();
            InventoryProtocol.WriteStack(writer, new ItemStack(wood.ProtocolId, 1)
            {
                Damage = 7, Unbreaking = 3, Mending = true,
                Unbreakable = true, RepairCost = 2
            });
            PacketReader patch = new(writer.ToArray());
            Check(patch.ReadVarInt() == 1 &&
                patch.ReadVarInt() == wood.ProtocolId &&
                patch.ReadVarInt() == 4 && patch.ReadVarInt() == 0 &&
                patch.ReadVarInt() == 3 && patch.ReadVarInt() == 7 &&
                patch.ReadVarInt() == 4 &&
                patch.ReadVarInt() == 13 && patch.ReadVarInt() == 2 &&
                patch.ReadVarInt() == VanillaRegistries.IndexOf(
                    "minecraft:enchantment", "minecraft:unbreaking") &&
                patch.ReadVarInt() == 3 &&
                patch.ReadVarInt() == VanillaRegistries.IndexOf(
                    "minecraft:enchantment", "minecraft:mending") &&
                patch.ReadVarInt() == 1 &&
                patch.ReadVarInt() == 19 && patch.ReadVarInt() == 2 &&
                patch.Remaining == 0,
                "All durability components are wire-compatible with protocol 777");

            Check(inventory.TryCreativeSet(5, helm.ProtocolId, 1, true),
                "Valid armor item can be equipped in helmet slot");
            var (equipment, carried, equipmentRevision) = inventory.Snapshot();
            equipment[5] = new ItemStack(helm.ProtocolId, 1)
                { Damage = 20, Mending = true, Unbreaking = 3 };
            equipment[36] = equipment[36] with { Mending = false };
            Check(inventory.TryCommitWindow(equipmentRevision, equipment, carried),
                "Armor carries its own independent enchantments and wear");
            Check(inventory.RepairFromExperience(2) == (0, 5) &&
                inventory.Snapshot().Slots[5].Damage == 16 &&
                inventory.DamageArmor(2, new FixedRandom(false)) == 1 &&
                inventory.Snapshot().Slots[5].Damage == 18,
                "Mending targets equipped armor and armor wear uses Unbreaking");
            int beforeInvalid = inventory.Snapshot().Revision;
            var (invalid, invalidCursor, _) = inventory.Snapshot();
            invalid[5] = new ItemStack(1, 1);
            Check(!inventory.TryCommitWindow(beforeInvalid, invalid, invalidCursor),
                "Server rejects non-armor items placed into equipment slots");
            Check(inventory.ConfigureHeld(0, false, true) &&
                !inventory.DamageHeld(wood.ProtocolId, 999) &&
                inventory.Held().Unbreakable,
                "Server-only unbreakable flag prevents all tool wear");

            var xp = new PlayerExperience(root, playerId);
            xp.Award(7);
            Check(xp.Snapshot() == (0.0f, 1, 7) &&
                new PlayerExperience(root, playerId).Snapshot() ==
                    (0.0f, 1, 7) &&
                xp.SpendLevels(1) && xp.Snapshot().Total == 0,
                "XP levels and progress persist, with level spending for anvils");

            int id = 800;
            string worldPath = Path.Combine(root, "world");
            var orbs = new ExperienceOrbStore(root, worldPath, () => ++id);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Check(orbs.TrySpawn(5, 1.5, 81.5, 1.5,
                out ExperienceOrb first, now) &&
                orbs.TryCollect(first.EntityId, now + 200) == 0 &&
                orbs.TryCollect(first.EntityId, now + 600) == 5 &&
                orbs.Snapshot().Length == 0,
                "XP orbs require physical pickup delay and cannot be collected twice");
            Check(orbs.TrySpawn(3, 2, 81, 2,
                out ExperienceOrb remaining, now) &&
                new ExperienceOrbStore(root, worldPath, () => ++id)
                .Snapshot().Any(x => x.Value == 3 && x.Uuid == remaining.Uuid),
                "Uncollected XP orbs survive world restart");

            Check(ToolUseRules.TryUse("minecraft:diamond_axe", 139, 1, false,
                    false, out var stripped) && stripped.NewState == 198 &&
                ToolUseRules.TryUse("minecraft:diamond_hoe", 10, 1, true,
                    false, out var tilled) && tilled.NewState == 6875 &&
                ToolUseRules.TryUse("minecraft:iron_shovel", 10, 1, true,
                    false, out var path) && path.NewState == 16796 &&
                ToolUseRules.TryUse("minecraft:shears", 10066, 2, false,
                    false, out var pumpkin) && pumpkin.Seeds == 4 &&
                ToolUseRules.TryUse("minecraft:flint_and_steel", 10, 1,
                    false, true, out var fire) && fire.Adjacent &&
                !ToolUseRules.TryUse("minecraft:diamond_hoe", 10, 0, true,
                    false, out _),
                "Mining tools perform genuine block transformations and reject invalid faces");

            var settings = new ServerOptions
            {
                Seed = 991728, GeneratorVersion = 5,
                ViewDistance = 2, SaveGeneratedChunks = false,
                WorldPath = worldPath
            };
            await using var world = new TerrainWorld(settings);
            using var output = new MemoryStream();
            using var channel = new PacketChannel(output);
            var actor = new ConnectedPlayer(Guid.NewGuid(), "DurabilityTester",
                [], 121, channel, world, null, items);
            actor.MarkReady();
            actor.SetGameMode(PlayerGameMode.Survival);
            actor.SetPose(new PlayerPose(1.5, 82, 1.5, 0, 0, true));
            Check(actor.Inventory.TryCreativeSet(36, axe.ProtocolId, 1, true) &&
                world.TrySetBlockState(1, 81, 1, 139, out _),
                "Prepare world and axe for real tool-use packet path");
            var game = new MinecraftServer(settings, world, null!, world,
                items: items, recipes: recipes);
            await game.HandleBlockUseAsync(actor,
                new BlockUseOn(0, 1, 81, 1, 1, 101, true),
                CancellationToken.None);
            Check(world.GetBlockState(1, 81, 1) == 198 &&
                actor.Inventory.Held().Damage == 1,
                "Confirmed right-click axe stripping removes exactly one durability");
            await game.HandleBlockUseAsync(actor,
                new BlockUseOn(0, 1, 81, 1, 1, 102, true),
                CancellationToken.None);
            Check(actor.Inventory.Held().Damage == 1,
                "Right-click action without a valid transformation never damages a tool");
            actor.SetGameMode(PlayerGameMode.Creative);
            Check(world.TrySetBlockState(1, 81, 1, 139, out _),
                "Restore log for Creative durability regression");
            await game.HandleBlockUseAsync(actor,
                new BlockUseOn(0, 1, 81, 1, 1, 103, true),
                CancellationToken.None);
            Check(world.GetBlockState(1, 81, 1) == 198 &&
                actor.Inventory.Held().Damage == 1,
                "Creative tool actions transform blocks without consuming durability");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
