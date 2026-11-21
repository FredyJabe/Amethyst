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

    /// <summary>Player-attributed mob deaths create physical, persistent XP
    /// orbs from the Entities Shard's YAML. Console/plugin kills do not
    /// impersonate players or grant XP from nowhere.</summary>
    private void SpawnEntityExperience(string definitionId,
        ConnectedPlayer? killer, double x, double y, double z)
    {
        if (killer is null || _experienceOrbs is null || _entityTypes is null ||
            !_entityTypes.TryGetByIdentifier(definitionId, out var definition))
            return;
        int minimum = definition.ExperienceMin;
        int maximum = definition.ExperienceMax;
        if (maximum <= 0 || minimum < 0 || maximum < minimum) return;
        int amount = minimum == maximum ? minimum :
            Random.Shared.Next(minimum, maximum + 1);
        if (amount > 0 && !_experienceOrbs.TrySpawn(amount, x,
                y + 0.35, z, out _))
            Console.WriteLine($"[xp] Experience orb capacity rejected {amount} for {definitionId}.");
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
        // A user-supplied loot pack is the authoritative owner-level
        // replacement, not additive with starter death skills from legacy
        // YAML. Skills: [] is a legitimate way to disable drops entirely.
        bool overridden = _loot?.HasOverride(dead.DefinitionId) == true ||
            vanillaKind is not null && _loot?.HasOverride(vanillaKind) == true;
        IReadOnlyList<ShardSkillInstruction>? selectedSkills = null;
        if (overridden && _loot is not null)
        {
            string owner = _loot.HasOverride(dead.DefinitionId)
                ? dead.DefinitionId : vanillaKind!;
            if (_loot.TryGetSkills(owner, out var configured))
                selectedSkills = configured;
        }
        if (_skills is not null)
            await _skills.DispatchAsync(new ShardSkillEvent(
                dead.DefinitionId, "onDeath", dead.X, dead.Y, dead.Z,
                dead.EntityId, vanillaKind,
                ActorUuid: attacker?.PlayerUuid,
                ItemId: weapon.ItemId, Count: weapon.Count,
                TargetKind: EnchantmentMobCategory(kind))
            {
                Runtime = runtime,
                PublisherCommitsDrops = true,
                AllowedSkills = selectedSkills
            }, ct);
        // An onDeath cancelEvent can suppress the default native loot
        // publication without stopping later configured dropItem mechanics.
        // The physical death has already been committed by Entities.
        IReadOnlyList<ShardItemDrop>? vanilla =
            !runtime.IsCancelled && !overridden && table is not null
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
        if (!runtime.IsCancelled)
            SpawnEntityExperience(dead.DefinitionId, attacker,
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
