namespace Amethyst.Shards;

/// <summary>Parsed, validated Mythic-style skill. The engine knows no
/// item IDs, entities, combat mechanics or other Shard-specific behavior.</summary>
public sealed record ShardSkillInstruction(
    string OwnerId, string Mechanic, string Trigger, string? Target,
    IReadOnlyDictionary<string, string> Arguments)
{
    public IReadOnlyList<ShardSkillCondition> Conditions { get; init; } = [];
}

/// <summary>A ?condition appended to a Mythic-style skill instruction.</summary>
public sealed record ShardSkillCondition(
    string Name, IReadOnlyDictionary<string, string> Arguments);

/// <summary>Mutable event-local output from skill mechanics. Only gameplay
/// event publishers decide how to commit these suggested changes.</summary>
public sealed class ShardSkillRuntime
{
    private readonly Dictionary<string, double> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ShardSkillEffect> _effects = [];

    /// <summary>Whether a mechanic cancelled the current event. The event
    /// publisher must check this before committing its gameplay action.</summary>
    public bool IsCancelled { get; private set; }
    /// <summary>Cancel only this event instance; future events start uncancelled.</summary>
    public void Cancel() => IsCancelled = true;

    public IReadOnlyCollection<string> Flags => _flags;
    public IReadOnlyList<ShardSkillEffect> Effects => _effects;
    public double Number(string key, double fallback = 0) =>
        _numbers.TryGetValue(key, out double value) ? value : fallback;
    public void SetNumber(string key, double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        _numbers[key] = value;
    }
    public void SetFlag(string flag) => _flags.Add(flag);
    public bool HasFlag(string flag) => _flags.Contains(flag);
    public void AddEffect(ShardSkillEffect effect) => _effects.Add(effect);
}
public sealed record ShardSkillEffect(string Type, string Id, int Duration = 0, int Amplifier = 0);

/// <summary>Event data supplied by a Shard when dispatching a trigger.
/// OwnerId is the exact entity/item/other object definition key;
/// FallbackOwnerId is consulted only when OwnerId has no skill list.</summary>
public sealed record ShardSkillEvent(
    string OwnerId, string Trigger, double X = 0, double Y = 0, double Z = 0,
    int EntityId = 0, string? FallbackOwnerId = null, Random? Random = null,
    Guid? ActorUuid = null, int ItemId = 0, int Count = 0,
    int EnchantmentLevel = 0, string? TargetKind = null, string? DamageType = null)
{
    public Random Rng => Random ?? System.Random.Shared;
    public ShardSkillRuntime Runtime { get; init; } = new();
}

public delegate void ShardSkillValidator(ShardSkillInstruction skill);
public delegate void ShardSkillConditionValidator(ShardSkillCondition condition);
public delegate bool ShardSkillConditionEvaluator(
    ShardSkillCondition condition, ShardSkillEvent context);
public delegate ValueTask ShardSkillExecutor(
    ShardSkillInstruction skill, ShardSkillEvent context, CancellationToken ct);

/// <summary>Core-owned registry, parser and event dispatcher. All enabled
/// Shards share one instance. Shards contribute mechanics and YAML skill packs;
/// the ShardHost cleans up contributions when a Shard is disabled.</summary>
public interface IShardSkillEngine
{
    int OwnerCount { get; }
    IReadOnlyList<ShardSkillInstruction> Definitions { get; }
    bool TryGetSkills(string ownerId, out IReadOnlyList<ShardSkillInstruction> skills);
    Task<bool> DispatchAsync(ShardSkillEvent context, CancellationToken ct = default);
    /// <summary>Dispatch for synchronous transactions such as durability wear.
    /// Throws if a mechanic cannot complete without awaiting.</summary>
    bool DispatchImmediate(ShardSkillEvent context);
    /// <summary>Validate named conditions against registered validators at startup.</summary>
    void ValidateConditions(IReadOnlyList<ShardSkillCondition> conditions);
    /// <summary>Check Stats conditions with the same evaluators used by Skills.</summary>
    bool CheckConditions(IReadOnlyList<ShardSkillCondition> conditions, ShardSkillEvent context);
}

/// <summary>Side-effect capability supplied by Gameplay. Items' dropItem
/// mechanic resolves it when invoked rather than owning entity physics.</summary>
public interface IShardItemDropSink
{
    bool TryDropItem(int itemId, int count, double x, double y, double z);
}
