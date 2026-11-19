using System.Globalization;
using System.Text.RegularExpressions;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>Items-owned mechanic plugin for Core's shared SkillEngine.
/// Core parses the expression and fires the trigger; Items resolves item IDs
/// and stack sizes, and Gameplay provides physical world item spawning.</summary>
public sealed class DropItemSkillMechanic
{
    private static readonly Regex AmountRange = new(
        @"^(?<min>\d{1,3})(?:\s*-\s*(?<max>\d{1,3}))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IShardItemRegistry _items;
    private readonly Func<IShardItemDropSink?> _getDropSink;
    private readonly Action<string>? _log;

    public DropItemSkillMechanic(IShardItemRegistry items,
        Func<IShardItemDropSink?> getDropSink, Action<string>? log = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _getDropSink = getDropSink ?? throw new ArgumentNullException(nameof(getDropSink));
        _log = log;
    }

    public void Validate(ShardSkillInstruction skill) => _ = Read(skill);

    public ValueTask ExecuteAsync(ShardSkillInstruction skill, ShardSkillEvent evt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (itemId, stackLimit, min, max, chance) = Read(skill);
        if (evt.Rng.NextDouble() >= chance) return ValueTask.CompletedTask;
        int amount = evt.Rng.Next(min, max + 1);
        if (amount == 0) return ValueTask.CompletedTask;
        IShardItemDropSink sink = _getDropSink() ??
            throw new InvalidOperationException(
                "dropItem requires an enabled Shard providing IShardItemDropSink.");
        while (amount > 0)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(amount, stackLimit);
            if (!sink.TryDropItem(itemId, count, evt.X, evt.Y + 0.2, evt.Z))
                _log?.Invoke($"dropItem failed to spawn item {itemId} x{count} for {evt.OwnerId}");
            amount -= count;
        }
        return ValueTask.CompletedTask;
    }

    private (int ItemId, int StackLimit, int Min, int Max, double Chance)
        Read(ShardSkillInstruction skill)
    {
        if (skill.Target is not null &&
            !skill.Target.Equals("self", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"dropItem does not support the @{skill.Target} target.");
        string? itemName = null;
        string amount = "1";
        double chance = 1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in skill.Arguments)
        {
            string name = property.Key.ToLowerInvariant() switch
            {
                "i" => "item", "a" => "amount", "c" => "chance",
                var key => key
            };
            if (!seen.Add(name))
                throw new InvalidDataException($"Duplicate dropItem parameter '{name}'.");
            switch (name)
            {
                case "item": itemName = property.Value; break;
                case "amount": amount = property.Value; break;
                case "chance":
                    if (!double.TryParse(property.Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out chance) ||
                        !double.IsFinite(chance) || chance is < 0 or > 1)
                        throw new InvalidDataException(
                            $"Invalid dropItem chance '{property.Value}'.");
                    break;
                default:
                    throw new InvalidDataException($"Unsupported dropItem parameter '{name}'.");
            }
        }
        if (itemName is null || !_items.TryGetByIdentifier(itemName, out var item) ||
            item.ProtocolId <= 0 || item.MaxStackSize <= 0)
            throw new InvalidDataException($"Unknown dropItem item '{itemName}'.");
        Match match = AmountRange.Match(amount);
        if (!match.Success ||
            !int.TryParse(match.Groups["min"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out int min) ||
            !int.TryParse(match.Groups["max"].Success
                    ? match.Groups["max"].Value : match.Groups["min"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out int max) ||
            min < 0 || max < min || max > 64)
            throw new InvalidDataException($"Invalid dropItem amount '{amount}'.");
        return (item.ProtocolId, item.MaxStackSize, min, max, chance);
    }
}
