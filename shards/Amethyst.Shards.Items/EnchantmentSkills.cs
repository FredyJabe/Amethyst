using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>Items-owned, generic enchantment skill mechanics. A mechanic
/// never decides what entity is hit or trusts client-reported effects: it
/// adjusts the server's event-local numbers, flags and effect intents.</summary>
public static class EnchantmentSkills
{
    private static readonly string[] Triggers =
    [
        "onAttack", "onHurt", "onMine", "onBlockDrop", "onEntityDrop",
        "onProjectileHit", "onProjectileLaunch", "onProjectileReturn",
        "onProjectileCharge", "onFishing", "onMove", "onAirTick",
        "onExperiencePickup", "onItemDamage", "onUnequip", "onSmash",
        "onSweep", "onSpearAttack", "onLeftClick", "onRightClick",
        "onDrop", "onStackDrop", "onSwap", "onPickup", "onEquip"
    ];
    private static readonly Regex Key = new(
        @"^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Register(IShardContext context)
    {
        foreach (string trigger in Triggers) context.RegisterSkillTrigger(trigger);
        context.RegisterSkillMechanic("setFlag", ValidateFlag, SetFlag);
        context.RegisterSkillMechanic("effect", ValidateEffect, Effect);
        context.RegisterSkillCondition("level", ValidateLevel, Level);
        context.RegisterSkillCondition("target", ValidateTarget, Target);
        context.RegisterSkillCondition("damageType", ValidateDamageType, DamageType);
        context.RegisterSkillCondition("flag", ValidateFlagCondition, Flag);
        context.RegisterSkillCondition("chance", ValidateChance, Chance);
    }
    private static string Required(IReadOnlyDictionary<string, string> args, string name) =>
        args.TryGetValue(name, out string? value) && value.Length > 0 ? value :
        throw new InvalidDataException($"Missing skill argument '{name}'.");

    private static void Allow(IReadOnlyDictionary<string, string> args, params string[] keys)
    {
        foreach (string key in args.Keys)
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unknown skill argument '{key}'.");
    }
    private static double Number(IReadOnlyDictionary<string, string> args,
        string name, double fallback = 0)
    {
        if (!args.TryGetValue(name, out string? value)) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
            out double number) || !double.IsFinite(number) || Math.Abs(number) > 1_000_000)
            throw new InvalidDataException($"Invalid numeric skill argument {name}={value}.");
        return number;
    }
    private static void ValidateFlag(ShardSkillInstruction skill)
    {
        Allow(skill.Arguments, "name");
        if (!Key.IsMatch(Required(skill.Arguments, "name")))
            throw new InvalidDataException("Invalid flag name.");
    }
    private static ValueTask SetFlag(ShardSkillInstruction skill, ShardSkillEvent evt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        evt.Runtime.SetFlag(Required(skill.Arguments, "name"));
        return ValueTask.CompletedTask;
    }
    private static void ValidateEffect(ShardSkillInstruction skill)
    {
        Allow(skill.Arguments, "id", "duration", "perLevel", "amplifier");
        if (!Key.IsMatch(Required(skill.Arguments, "id")))
            throw new InvalidDataException("Invalid effect ID.");
        if (Number(skill.Arguments, "duration") < 0 ||
            Number(skill.Arguments, "amplifier") < 0)
            throw new InvalidDataException("Negative effect duration/amplifier.");
        _ = Number(skill.Arguments, "perLevel");
    }
    private static ValueTask Effect(ShardSkillInstruction skill, ShardSkillEvent evt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int duration = checked((int)Math.Clamp(
            Number(skill.Arguments, "duration") +
            evt.EnchantmentLevel * Number(skill.Arguments, "perLevel"), 0, 1000000));
        int amplifier = (int)Number(skill.Arguments, "amplifier");
        evt.Runtime.AddEffect(new ShardSkillEffect("enchantment",
            Required(skill.Arguments, "id"), duration, amplifier));
        return ValueTask.CompletedTask;
    }

    private static void ValidateLevel(ShardSkillCondition c)
    {
        Allow(c.Arguments, "min", "max");
        _ = Number(c.Arguments, "min", 1);
        _ = Number(c.Arguments, "max", 255);
    }
    private static bool Level(ShardSkillCondition c, ShardSkillEvent evt) =>
        evt.EnchantmentLevel >= Number(c.Arguments, "min", 1) &&
        evt.EnchantmentLevel <= Number(c.Arguments, "max", 255);

    private static void ValidateTarget(ShardSkillCondition c)
    {
        Allow(c.Arguments, "type");
        if (!Key.IsMatch(Required(c.Arguments, "type")))
            throw new InvalidDataException("Invalid target type.");
    }
    private static bool Target(ShardSkillCondition c, ShardSkillEvent evt) =>
        string.Equals(Required(c.Arguments, "type"), evt.TargetKind,
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateDamageType(ShardSkillCondition c)
    {
        Allow(c.Arguments, "type");
        if (!Key.IsMatch(Required(c.Arguments, "type")))
            throw new InvalidDataException("Invalid damage type.");
    }
    private static bool DamageType(ShardSkillCondition c, ShardSkillEvent evt) =>
        string.Equals(Required(c.Arguments, "type"), evt.DamageType,
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateFlagCondition(ShardSkillCondition c)
    {
        Allow(c.Arguments, "name");
        if (!Key.IsMatch(Required(c.Arguments, "name")))
            throw new InvalidDataException("Invalid flag condition.");
    }
    private static bool Flag(ShardSkillCondition c, ShardSkillEvent evt) =>
        evt.Runtime.HasFlag(Required(c.Arguments, "name"));

    private static void ValidateChance(ShardSkillCondition c)
    {
        Allow(c.Arguments, "base", "perLevel", "mode", "factor");
        string mode = c.Arguments.GetValueOrDefault("mode") ?? "linear";
        if (mode is not ("linear" or "level_ratio"))
            throw new InvalidDataException("Chance mode must be linear or level_ratio.");
        if (mode == "level_ratio" &&
            (c.Arguments.ContainsKey("base") || c.Arguments.ContainsKey("perLevel")))
            throw new InvalidDataException("level_ratio takes the enchantment level, not Base/PerLevel.");
        double basis = Number(c.Arguments, "base");
        double per = Number(c.Arguments, "perLevel");
        double factor = Number(c.Arguments, "factor", 1);
        if (basis is < 0 or > 1 || per is < 0 or > 1 || factor is < 0 or > 1)
            throw new InvalidDataException("Chance values must use fractions 0..1.");
    }
    private static bool Chance(ShardSkillCondition c, ShardSkillEvent evt)
    {
        string mode = c.Arguments.GetValueOrDefault("mode") ?? "linear";
        double probability = mode == "level_ratio"
            ? evt.EnchantmentLevel <= 0 ? 0 :
                (double)evt.EnchantmentLevel / (evt.EnchantmentLevel + 1)
            : Math.Clamp(Number(c.Arguments, "base") +
                Number(c.Arguments, "perLevel") * evt.EnchantmentLevel, 0, 1);
        probability *= Number(c.Arguments, "factor", 1);
        return evt.Rng.NextDouble() < probability;
    }
}
