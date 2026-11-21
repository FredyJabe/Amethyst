using Amethyst.Shards;

namespace Amethyst;

/// <summary>All loot, whether emitted by a YAML skill or evaluated from the
/// Mojang 26.3 data pack, becomes authoritative persistent ground items here.
/// No client-reported drop is trusted.</summary>
public sealed partial class MinecraftServer
{
    private bool SpawnLoot(IEnumerable<ShardItemDrop> drops,
        double x, double y, double z)
    {
        if (_groundItems is null) return false;
        bool success = true;
        foreach (ShardItemDrop drop in drops)
        {
            if (drop.ItemId <= 0 || drop.Count <= 0 ||
                !ItemStackComponents.IsValid(drop.ComponentData))
            {
                Console.WriteLine($"[loot] Rejected invalid loot stack {drop.ItemId}.");
                success = false;
                continue;
            }
            int wear = 0;
            if (ItemStackComponents.Decode(drop.ComponentData)
                .TryGetValue("minecraft:damage", out string? encodedDamage))
            {
                try { wear = System.Text.Json.JsonSerializer.Deserialize<int>(
                    encodedDamage); }
                catch (System.Text.Json.JsonException)
                {
                    Console.WriteLine("[loot] Invalid item durability patch.");
                    success = false;
                    continue;
                }
            }
            var stack = new ItemStack(drop.ItemId, drop.Count)
            {
                ComponentData = drop.ComponentData,
                Damage = wear
            };
            if (!_groundItems.TrySpawn(stack, x, y, z, out _))
            {
                Console.WriteLine($"[loot] Ground item capacity rejected {drop.ItemId} x{drop.Count}.");
                success = false;
            }
        }
        return success;
    }

    /// <summary>Single source of truth for all Entities-Shard deaths.
    /// A pinned Mojang loot table takes precedence unless Items has an
    /// explicit YAML override. Skills always execute, but their drop intents
    /// are committed only for a fallback/override so no two drop providers
    /// can award the same kill.</summary>
    private async Task ProcessEntityDeathAsync(ShardEntityInstance dead,
        ConnectedPlayer? attacker, ItemStack weapon, CancellationToken ct)
    {
        string? vanillaKind = _entityTypes is not null &&
            _entityTypes.TryGetById(dead.ProtocolId, out var definition)
                ? definition.Identifier : null;
        string kind = vanillaKind ?? dead.DefinitionId;
        string? table = kind.StartsWith("minecraft:", StringComparison.Ordinal)
            ? "minecraft:entities/" + kind["minecraft:".Length..] : null;

        var runtime = new ShardSkillRuntime();
        if (kind == "minecraft:sheep")
        {
            runtime.SetFlag("color_" + WoolColors[
                Math.Clamp(dead.Color, 0, WoolColors.Length - 1)]);
            if (!dead.Sheared) runtime.SetFlag("not_sheared");
        }
        if (attacker is not null)
            await EnchantmentEventAsync(weapon, "onEntityDrop", ct,
                runtime, targetKind: EnchantmentMobCategory(kind),
                actor: attacker.PlayerUuid);
        if (_skills is not null)
            await _skills.DispatchAsync(new ShardSkillEvent(
                dead.DefinitionId, "onDeath", dead.X, dead.Y, dead.Z,
                dead.EntityId, vanillaKind,
                ActorUuid: attacker?.PlayerUuid,
                ItemId: weapon.ItemId, Count: weapon.Count,
                TargetKind: EnchantmentMobCategory(kind))
            {
                Runtime = runtime,
                PublisherCommitsDrops = true
            }, ct);

        bool overridden = _loot?.HasOverride(dead.DefinitionId) == true ||
            vanillaKind is not null && _loot?.HasOverride(vanillaKind) == true;
        IReadOnlyList<ShardItemDrop>? vanilla = !overridden && table is not null
            ? TryVanillaLoot(table, new ShardVanillaLootContext
            {
                KilledByPlayer = attacker is not null,
                AttackerKind = attacker is null ? null : "minecraft:player",
                DirectDamagerKind = attacker is null ? null : "minecraft:player",
                DamageTypeIdentifier = attacker is null ? null :
                    "minecraft:player_attack",
                ToolIdentifier = attacker is not null && _items is not null &&
                    _items.TryGetById(weapon.ItemId, out var tool)
                        ? tool.Identifier : null,
                Looting = (int)Math.Clamp(
                    runtime.Number("looting_level"), 0, 255),
                FireAspect = attacker is not null &&
                    weapon.EnchantmentLevel("fire_aspect") > 0,
                EntityColor = kind == "minecraft:sheep"
                    ? WoolColors[Math.Clamp(dead.Color, 0, WoolColors.Length - 1)]
                    : null,
                EntitySheared = dead.Sheared
            })
            : null;

        if (vanilla is not null)
            SpawnLoot(vanilla, dead.X, dead.Y + 0.25, dead.Z);
        else
            SpawnLoot(runtime.Drops, dead.X, dead.Y + 0.25, dead.Z);
    }

    private IReadOnlyList<ShardItemDrop>? TryVanillaLoot(
        string tableId, ShardVanillaLootContext context)
    {
        if (_vanillaLoot?.HasTable(tableId) != true) return null;
        try
        {
            var rolled = _vanillaLoot.Roll(tableId, context);
            // A loot table with yet-unimplemented complex components is
            // not a successful result; fail over before spawning ANY item,
            // avoiding partially generated or client-invalid loot.
            foreach (var drop in rolled)
            {
                if (!ItemStackComponents.IsValid(drop.ComponentData) ||
                    _items is not null &&
                    (!_items.IsValidStack(drop.ItemId, drop.Count) ||
                     !_items.TryGetById(drop.ItemId, out _)))
                    throw new NotSupportedException(
                        $"The loot result for {tableId} uses an unsupported component or stack size.");
            }
            return rolled;
        }
        catch (NotSupportedException ex)
        {
            // Use a clearly logged fallback until every 26.3 loot predicate,
            // function and parameter provider has a matching implementation.
            Console.WriteLine($"[loot] Incomplete {tableId}: {ex.Message}; " +
                "using the configured fallback.");
            return null;
        }
    }
}
