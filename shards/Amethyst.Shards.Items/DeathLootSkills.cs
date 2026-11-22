using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>All entity loot, including pinned vanilla tables and XP, is
/// explicitly produced by Items-owned ~onDeath skills. Merely killing an
/// entity never starts a default loot-table roll or XP allocation.</summary>
public static class DeathLootSkills
{
    private static readonly Regex EntityTable = new(
        @"^minecraft:entities/[a-z0-9_./-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Amount = new(
        @"^(?<min>\d{1,5})(?:-(?<max>\d{1,5}))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Register(IShardContext context, IShardItemRegistry items)
    {
        context.RegisterSkillMechanic("dropLootTable", ValidateTable, RollTable);
        context.RegisterSkillMechanic("dropExperience", ValidateExperience, DropExperience);

        static void ValidateTarget(ShardSkillInstruction skill)
        {
            if (skill.Target is not null &&
                !skill.Target.Equals("self", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Death loot skills only support @self.");
            if (skill.Trigger != "onDeath")
                throw new InvalidDataException("Death loot mechanics require ~onDeath.");
        }

        static string TableId(ShardSkillInstruction skill)
        {
            ValidateTarget(skill);
            if (skill.Arguments.Count != 1 ||
                !skill.Arguments.TryGetValue("table", out string? table) ||
                !EntityTable.IsMatch(table) || table.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException(
                    "dropLootTable needs table=minecraft:entities/<id>.");
            return table;
        }

        static (int Min, int Max) ExperienceRange(ShardSkillInstruction skill)
        {
            ValidateTarget(skill);
            if (skill.Arguments.Count != 1 ||
                !skill.Arguments.TryGetValue("amount", out string? amount))
                throw new InvalidDataException("dropExperience needs amount=5 or amount=1-3.");
            Match match = Amount.Match(amount);
            if (!match.Success ||
                !int.TryParse(match.Groups["min"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int min) ||
                !int.TryParse(match.Groups["max"].Success
                    ? match.Groups["max"].Value : match.Groups["min"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out int max) ||
                min < 0 || max < min || max > short.MaxValue)
                throw new InvalidDataException("Invalid dropExperience amount.");
            return (min, max);
        }

        void ValidateTable(ShardSkillInstruction skill) => _ = TableId(skill);
        void ValidateExperience(ShardSkillInstruction skill) =>
            _ = ExperienceRange(skill);

        ValueTask DropExperience(ShardSkillInstruction skill,
            ShardSkillEvent evt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            (int min, int max) = ExperienceRange(skill);
            evt.Runtime.AddExperience(min == max ? min : evt.Rng.Next(min, max + 1));
            return ValueTask.CompletedTask;
        }

        ValueTask RollTable(ShardSkillInstruction skill,
            ShardSkillEvent evt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string id = TableId(skill);
            if (!evt.PublisherCommitsDrops)
                throw new InvalidOperationException(
                    "dropLootTable requires an authoritative death event publisher.");
            IShardVanillaLootTables? vanilla =
                context.GetService<IShardVanillaLootTables>();
            if (vanilla is null || !vanilla.HasTable(id))
            {
                context.Log($"[loot] Explicit ~onDeath table '{id}' unavailable; no item drops.");
                return ValueTask.CompletedTask;
            }

            string? tool = items.TryGetById(evt.ItemId,
                out ShardItemDefinition definition) ? definition.Identifier : null;
            var color = evt.Runtime.Flags.FirstOrDefault(flag =>
                flag.StartsWith("color_", StringComparison.OrdinalIgnoreCase));
            var lootContext = new ShardVanillaLootContext
            {
                Random = evt.Rng,
                KilledByPlayer = evt.ActorUuid.HasValue,
                AttackerKind = evt.ActorUuid.HasValue ? "minecraft:player" : null,
                DirectDamagerKind = evt.ActorUuid.HasValue ? "minecraft:player" : null,
                DamageTypeIdentifier = evt.DamageType ??
                    (evt.ActorUuid.HasValue ? "minecraft:player_attack" : null),
                ToolIdentifier = tool,
                Looting = (int)Math.Clamp(evt.Runtime.Number("looting_level"), 0, 255),
                FireAspect = evt.Runtime.HasFlag("fire_aspect"),
                Burning = evt.Runtime.HasFlag("burning"),
                EntitySheared = !evt.Runtime.HasFlag("not_sheared"),
                EntityColor = color is null ? null : color["color_".Length..]
            };

            IReadOnlyList<ShardItemDrop> generated;
            try
            {
                generated = vanilla.Roll(id, lootContext);
                // Preflight the entire result: a partially supported table
                // must never award a truncated or invalid item collection.
                if (generated.Count + evt.Runtime.Drops.Count > 32 ||
                    generated.Any(d => !items.IsValidStack(d.ItemId, d.Count) ||
                        !ItemStackComponents.IsValid(d.ComponentData)))
                    throw new NotSupportedException(
                        "Unsupported components or stack limits in entity loot result.");
            }
            catch (NotSupportedException ex)
            {
                context.Log($"[loot] Explicit ~onDeath table '{id}' cannot roll: " +
                    ex.Message + "; no automatic fallback.");
                return ValueTask.CompletedTask;
            }

            foreach (ShardItemDrop drop in generated)
                evt.Runtime.AddDrop(drop);
            return ValueTask.CompletedTask;
        }
    }
}
