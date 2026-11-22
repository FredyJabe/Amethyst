using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>One dropItem mechanic for new Phase 4 intent-based loot and the
/// original Mythic-style Items skills. The publisher is authoritative about
/// cancelling its default action; generated drops are independent.</summary>
public static class LootSkills
{
    private static readonly Regex Range = new(
        @"^(?<min>\d{1,3})(?:\s*-\s*(?<max>\d{1,3}))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly record struct Parameters(int ItemId, int StackLimit,
        int Min, int Max, double Chance, bool Looting);

    public static void Register(IShardContext context, IShardItemRegistry items)
    {
        context.RegisterSkillMechanic("dropItem", Validate, Execute);

        void Validate(ShardSkillInstruction skill) => _ = Read(skill);

        ValueTask Execute(ShardSkillInstruction skill, ShardSkillEvent evt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Parameters p = Read(skill);
            if (evt.Rng.NextDouble() >= p.Chance) return ValueTask.CompletedTask;
            int amount = p.Min == p.Max ? p.Min : evt.Rng.Next(p.Min, p.Max + 1);
            if (p.Looting)
            {
                int level = (int)Math.Clamp(evt.Runtime.Number("looting_level"), 0, 255);
                if (level > 0) amount += evt.Rng.Next(level + 1);
            }
            amount = Math.Min(amount, 64);
            IShardItemDropSink? sink = evt.PublisherCommitsDrops ? null :
                context.GetService<IShardItemDropSink>();
            while (amount > 0)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(amount, p.StackLimit);
                evt.Runtime.AddDrop(new ShardItemDrop(p.ItemId, count));
                // PR #18 dispatchers do not inspect Runtime.Drops. Keep their
                // physical-drop behavior until all callers use the publisher
                // transaction, without duplicating PR #21's deferred drops.
                if (sink is not null &&
                    !sink.TryDropItem(p.ItemId, count, evt.X, evt.Y + 0.2, evt.Z))
                    context.Log($"dropItem failed to spawn {p.ItemId} x{count} for {evt.OwnerId}");
                amount -= count;
            }
            return ValueTask.CompletedTask;
        }

        Parameters Read(ShardSkillInstruction skill)
        {
            if (skill.Target is not null &&
                !skill.Target.Equals("self", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"dropItem does not support @{skill.Target}.");
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (original, value) in skill.Arguments)
            {
                string key = original.ToLowerInvariant() switch
                {
                    "i" => "item", "a" => "amount", "c" => "chance",
                    var name => name
                };
                if (key is not ("item" or "amount" or "min" or "max" or
                    "chance" or "bonus") || !args.TryAdd(key, value))
                    throw new InvalidDataException(
                        $"Invalid or duplicate dropItem argument '{original}'.");
            }
            if (!args.TryGetValue("item", out string? identifier) ||
                !items.TryGetByIdentifier(identifier, out var definition) ||
                definition.ProtocolId <= 0 || definition.MaxStackSize <= 0)
                throw new InvalidDataException($"Unknown dropItem material '{identifier}'.");

            bool hasAmount = args.TryGetValue("amount", out string? range);
            if (hasAmount && (args.ContainsKey("min") || args.ContainsKey("max")))
                throw new InvalidDataException("dropItem accepts amount or min/max, not both.");
            int min, max;
            if (hasAmount)
            {
                Match match = Range.Match(range!);
                if (!match.Success || !int.TryParse(match.Groups["min"].Value,
                        NumberStyles.None, CultureInfo.InvariantCulture, out min) ||
                    !int.TryParse(match.Groups["max"].Success
                        ? match.Groups["max"].Value : match.Groups["min"].Value,
                        NumberStyles.None, CultureInfo.InvariantCulture, out max))
                    throw new InvalidDataException($"Invalid dropItem amount '{range}'.");
            }
            else if (!args.ContainsKey("min") && !args.ContainsKey("max"))
            {
                min = max = 1; // legacy dropItem{item=...} shorthand
            }
            else
            {
                if (!args.TryGetValue("min", out string? lo) ||
                    !args.TryGetValue("max", out string? hi) ||
                    !int.TryParse(lo, NumberStyles.None, CultureInfo.InvariantCulture,
                        out min) || !int.TryParse(hi, NumberStyles.None,
                        CultureInfo.InvariantCulture, out max))
                    throw new InvalidDataException("dropItem requires both min and max.");
            }
            if (min < 0 || max < min || max > 64 ||
                (max + Math.Max(0, 255) + definition.MaxStackSize - 1) /
                    definition.MaxStackSize > 32 && args.GetValueOrDefault("bonus") == "looting")
                throw new InvalidDataException("Invalid dropItem count range.");
            double chance = 1;
            if (args.TryGetValue("chance", out string? rawChance) &&
                (!double.TryParse(rawChance, NumberStyles.Float,
                     CultureInfo.InvariantCulture, out chance) ||
                 !double.IsFinite(chance) || chance is < 0 or > 1))
                throw new InvalidDataException("Invalid dropItem chance.");
            string bonus = args.GetValueOrDefault("bonus") ?? "none";
            if (bonus is not ("none" or "looting"))
                throw new InvalidDataException("dropItem bonus must be none or looting.");
            return new Parameters(definition.ProtocolId, definition.MaxStackSize,
                min, max, chance, bonus == "looting");
        }
    }
}
