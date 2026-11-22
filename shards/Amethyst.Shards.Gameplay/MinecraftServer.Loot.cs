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

    /// <summary>Commit only XP explicitly produced by an Items-owned
    /// ~onDeath dropExperience mechanic. An unconfigured entity death cannot
    /// award XP; attribution is enforced by the configured ?flag condition.</summary>
    private void SpawnEntityExperience(int amount, string definitionId,
        double x, double y, double z)
    {
        if (amount <= 0 || _experienceOrbs is null) return;
        if (!_experienceOrbs.TrySpawn(amount, x, y + 0.35, z, out _))
            Console.WriteLine($"[xp] Experience orb capacity rejected {amount} for {definitionId}.");
    }

    /// <summary>Every physical entity death dispatches exactly one ~onDeath
    /// event. Items owns every drop mechanic, including optional vanilla
    /// table evaluation and experience. There is no automatic entity loot,
    /// no YAML fallback, and no default XP on missing definitions.</summary>
    private async Task ProcessEntityDeathAsync(ShardEntityInstance dead,
        ConnectedPlayer? attacker, ItemStack weapon, CancellationToken ct)
    {
        string? vanillaKind = _entityTypes is not null &&
            _entityTypes.TryGetById(dead.ProtocolId, out var definition)
                ? definition.Identifier : null;
        string kind = vanillaKind ?? dead.DefinitionId;
        var runtime = new ShardSkillRuntime();
        if (kind == "minecraft:sheep")
        {
            runtime.SetFlag("color_" + WoolColors[
                Math.Clamp(dead.Color, 0, WoolColors.Length - 1)]);
            if (!dead.Sheared) runtime.SetFlag("not_sheared");
        }
        if (attacker is not null)
        {
            runtime.SetFlag("killed_by_player");
            if (weapon.EnchantmentLevel("fire_aspect") > 0)
                runtime.SetFlag("fire_aspect");
            await EnchantmentEventAsync(weapon, "onEntityDrop", ct,
                runtime, targetKind: EnchantmentMobCategory(kind),
                actor: attacker.PlayerUuid);
        }

        // The exact bundled/admin Items loot pack is authoritative even
        // when it has Skills: []; other Shards may still supply ~onDeath
        // skills when no loot pack exists for this entity kind.
        IReadOnlyList<ShardSkillInstruction>? selectedSkills = null;
        if (_loot is not null &&
            (_loot.TryGetSkills(dead.DefinitionId, out var configured) ||
             vanillaKind is not null &&
                 _loot.TryGetSkills(vanillaKind, out configured)))
            selectedSkills = configured;

        if (_skills is not null)
            await _skills.DispatchAsync(new ShardSkillEvent(
                dead.DefinitionId, "onDeath", dead.X, dead.Y, dead.Z,
                dead.EntityId, vanillaKind,
                ActorUuid: attacker?.PlayerUuid,
                ItemId: weapon.ItemId, Count: weapon.Count,
                TargetKind: EnchantmentMobCategory(kind),
                DamageType: attacker is null ? null : "minecraft:player_attack")
            {
                Runtime = runtime,
                PublisherCommitsDrops = true,
                AllowedSkills = selectedSkills
            }, ct);

        // cancelEvent suppresses a publisher's default gameplay action,
        // never other skill mechanics. Entity death has no default loot to
        // cancel; explicitly configured item and XP intents always commit.
        SpawnLoot(runtime.Drops, dead.X, dead.Y + 0.25, dead.Z);
        SpawnEntityExperience(runtime.Experience, dead.DefinitionId,
            dead.X, dead.Y, dead.Z);
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
