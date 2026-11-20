using System.Globalization;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>dropItem mechanics add explicit drop intents to the current
/// event. The Gameplay publisher decides when/where physical items spawn.
/// The mechanic never modifies inventories or the world by itself.</summary>
public static class LootSkills
{
    public static void Register(IShardContext context, IShardItemRegistry items)
    {
        context.RegisterSkillMechanic("dropItem", Validate, Execute);

        void Validate(ShardSkillInstruction skill)
        {
            foreach (string key in skill.Arguments.Keys)
                if (key is not ("item" or "amount" or "min" or "max" or "bonus"))
                    throw new InvalidDataException($"Unknown dropItem argument '{key}'.");
            string identifier = Required(skill, "item");
            if (!identifier.StartsWith("minecraft:", StringComparison.Ordinal) ||
                !items.TryGetByIdentifier(identifier, out var definition) ||
                definition.ProtocolId == 0)
                throw new InvalidDataException($"Unknown vanilla drop item '{identifier}'.");
            bool fixedAmount = skill.Arguments.ContainsKey("amount");
            if (fixedAmount && (skill.Arguments.ContainsKey("min") ||
                skill.Arguments.ContainsKey("max")))
                throw new InvalidDataException("Use amount or min/max, not both.");
            int min = fixedAmount ? Positive(skill, "amount") :
                Positive(skill, "min");
            int max = fixedAmount ? min : Positive(skill, "max");
            if (min > max || max > 64 || max > definition.MaxStackSize)
                throw new InvalidDataException("Invalid dropItem amount range.");
            string bonus = skill.Arguments.GetValueOrDefault("bonus") ?? "none";
            if (bonus is not ("none" or "looting"))
                throw new InvalidDataException("dropItem bonus must be none or looting.");
        }

        ValueTask Execute(ShardSkillInstruction skill, ShardSkillEvent evt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var def = items.TryGetByIdentifier(Required(skill, "item"),
                out var item) ? item : throw new InvalidOperationException(
                    "Previously validated dropItem material missing.");
            bool fixedAmount = skill.Arguments.ContainsKey("amount");
            int min = fixedAmount ? Positive(skill, "amount") : Positive(skill, "min");
            int max = fixedAmount ? min : Positive(skill, "max");
            int count = min == max ? min : evt.Rng.Next(min, max + 1);
            if (skill.Arguments.GetValueOrDefault("bonus") == "looting")
            {
                int level = (int)Math.Clamp(evt.Runtime.Number("looting_level"), 0, 255);
                if (level > 0) count += evt.Rng.Next(level + 1);
            }
            if (count > 0)
                evt.Runtime.AddDrop(new ShardItemDrop(def.ProtocolId,
                    Math.Min(count, def.MaxStackSize)));
            return ValueTask.CompletedTask;
        }
    }

    private static string Required(ShardSkillInstruction skill, string name) =>
        skill.Arguments.TryGetValue(name, out string? value) && value.Length > 0
            ? value : throw new InvalidDataException($"dropItem missing {name}.");

    private static int Positive(ShardSkillInstruction skill, string name)
    {
        string raw = Required(skill, name);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture,
            out int parsed) || parsed is < 1 or > 64)
            throw new InvalidDataException($"Invalid dropItem {name}={raw}.");
        return parsed;
    }
}
