using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Amethyst.Shards;

/// <summary>Core implementation of the shared, Shard-extensible event-skill
/// engine. The parser is generic; only registered mechanics know gameplay.</summary>
public sealed class SkillEngine : IShardSkillEngine
{
    private static readonly Regex Identifier = new(
        @"^([a-z0-9_.-]+:)?[a-z0-9_./-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MechanicName = new(
        @"^[a-z][a-z0-9_-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TriggerName = new(
        @"^on[a-z0-9_-]{1,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Expression = new(
        @"^\s*(?<mechanic>[a-z][a-z0-9_-]*)(?:\{(?<args>[^{}]{0,512})\})?(?:\s+@(?<target>[a-z][a-z0-9_-]*))?\s+~(?<trigger>on[a-z0-9_-]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Owner, ShardSkillValidator Validate,
        ShardSkillExecutor Execute)> _mechanics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Owner, ShardSkillConditionValidator Validate,
        ShardSkillConditionEvaluator Evaluate)> _conditions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _triggers = new(StringComparer.OrdinalIgnoreCase)
        { ["onDeath"] = "amethyst" };
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<ShardSkillInstruction>>>
        _packs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Core-owned cancelEvent mechanic, available to every shard.
    /// It suppresses the publisher's default action without interrupting
    /// the remaining skills for this event.</summary>
    public SkillEngine()
    {
        RegisterMechanic("amethyst", "cancelEvent",
            static instruction =>
            {
                if (instruction.Arguments.Count != 0)
                    throw new InvalidDataException(
                        "cancelEvent does not accept arguments; use ?conditions.");
            },
            static (_, evt, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                evt.Runtime.Cancel();
                return ValueTask.CompletedTask;
            });
    }

    public int OwnerCount
    {
        get
        {
            lock (_gate) return _packs.Values.SelectMany(pack => pack.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }
    }

    public IReadOnlyList<ShardSkillInstruction> Definitions
    {
        get
        {
            lock (_gate) return Array.AsReadOnly(_packs.Values
                .SelectMany(pack => pack.Values).SelectMany(skills => skills)
                .OrderBy(x => x.OwnerId, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public void RegisterTrigger(string owner, string trigger)
    {
        if (string.IsNullOrWhiteSpace(owner) || !TriggerName.IsMatch(trigger))
            throw new ArgumentException("Invalid skill trigger.", nameof(trigger));
        lock (_gate)
        {
            if (_triggers.ContainsKey(trigger))
                throw new InvalidOperationException($"Skill trigger '~{trigger}' is already registered.");
            _triggers.Add(trigger, owner);
        }
    }

    public void RegisterMechanic(string owner, string name,
        ShardSkillValidator validate, ShardSkillExecutor execute)
    {
        if (string.IsNullOrWhiteSpace(owner) || !MechanicName.IsMatch(name))
            throw new ArgumentException("Invalid skill mechanic.", nameof(name));
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(execute);
        lock (_gate)
        {
            if (_mechanics.ContainsKey(name))
                throw new InvalidOperationException($"Skill mechanic '{name}' is already registered.");
            _mechanics.Add(name, (owner, validate, execute));
        }
    }


    public void RegisterCondition(string owner, string name,
        ShardSkillConditionValidator validate, ShardSkillConditionEvaluator evaluate)
    {
        if (string.IsNullOrWhiteSpace(owner) || !MechanicName.IsMatch(name))
            throw new ArgumentException("Invalid skill condition.", nameof(name));
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(evaluate);
        lock (_gate)
        {
            if (_conditions.ContainsKey(name))
                throw new InvalidOperationException($"Skill condition '?{name}' is registered.");
            _conditions.Add(name, (owner, validate, evaluate));
        }
    }

    /// <summary>Register parsed skill lists from a Shard's richer configuration
    /// (for example enchantments with max levels, conflicts and categories).
    /// All triggers, conditions and mechanics must be registered first.</summary>
    public void RegisterInstructions(string owner, IEnumerable<ShardSkillInstruction> instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(instructions);
        var grouped = instructions.GroupBy(s => Normalize(s.OwnerId),
            StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            // The Items Shard supplies an administrator YAML pack and also
            // generated item/enchantment instructions. Both belong to the
            // same shard; append generated instructions without overriding
            // explicit empty YAML skill lists (which disable defaults).
            var pack = _packs.TryGetValue(owner, out var existing)
                ? new Dictionary<string, IReadOnlyList<ShardSkillInstruction>>(
                    existing, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, IReadOnlyList<ShardSkillInstruction>>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var group in grouped)
            {
                if (!Identifier.IsMatch(group.Key))
                    throw new InvalidDataException($"Invalid skill owner '{group.Key}'.");
                var incoming = group.ToArray();
                foreach (var skill in incoming)
                    ValidateRegistered(skill);
                if (pack.TryGetValue(group.Key, out var previous))
                {
                    if (previous.Count == 0) continue; // explicit Skills: []
                    if (previous.Count + incoming.Length > 128)
                        throw new InvalidDataException($"Too many skills for '{group.Key}'.");
                    pack[group.Key] = Array.AsReadOnly(previous.Concat(incoming).ToArray());
                }
                else
                {
                    if (incoming.Length > 128)
                        throw new InvalidDataException($"Too many skills for '{group.Key}'.");
                    pack.Add(group.Key, Array.AsReadOnly(incoming));
                }
            }
            _packs[owner] = pack;
        }
    }

    private void ValidateRegistered(ShardSkillInstruction skill)
    {
        if (!_triggers.ContainsKey(skill.Trigger))
            throw new InvalidDataException($"Unregistered trigger ~{skill.Trigger}.");
        if (!_mechanics.TryGetValue(skill.Mechanic, out var mechanic))
            throw new InvalidDataException($"Unregistered mechanic {skill.Mechanic}.");
        mechanic.Validate(skill);
        foreach (var condition in skill.Conditions)
        {
            if (!_conditions.TryGetValue(condition.Name, out var check))
                throw new InvalidDataException($"Unregistered condition ?{condition.Name}.");
            check.Validate(condition);
        }
    }

    /// <summary>Register one source pack per Shard. Bundled defaults can be
    /// replaced by administrator YAML for that Shard; different Shards can add
    /// skills for the same owner without overwriting each other.</summary>
    public void RegisterDefinitions(string owner, string bundledFile, string? overridesDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledFile);
        Dictionary<string, ShardSkillValidator> mechanics;
        Dictionary<string, ShardSkillConditionValidator> conditions;
        HashSet<string> triggers;
        lock (_gate)
        {
            if (_packs.ContainsKey(owner))
                throw new InvalidOperationException($"Shard '{owner}' already registered skill definitions.");
            mechanics = _mechanics.ToDictionary(p => p.Key, p => p.Value.Validate,
                StringComparer.OrdinalIgnoreCase);
            triggers = new HashSet<string>(_triggers.Keys, StringComparer.OrdinalIgnoreCase);
            conditions = _conditions.ToDictionary(p => p.Key, p => p.Value.Validate,
                StringComparer.OrdinalIgnoreCase);
        }

        var pack = new Dictionary<string, IReadOnlyList<ShardSkillInstruction>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (key, skills) in ReadFile(bundledFile, mechanics, conditions, triggers))
            if (!pack.TryAdd(key, skills))
                throw new InvalidDataException($"Duplicate bundled skills for '{key}'.");

        if (overridesDirectory is not null)
        {
            Directory.CreateDirectory(overridesDirectory);
            var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(overridesDirectory, "*",
                SearchOption.AllDirectories)
                .Where(x => x.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            x.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
                foreach (var (key, skills) in ReadFile(file, mechanics, conditions, triggers))
                {
                    if (!modified.Add(key))
                        throw new InvalidDataException(
                            $"Duplicate custom skills for '{key}' ({file}).");
                    pack[key] = skills;
                }
        }
        lock (_gate)
        {
            if (_packs.ContainsKey(owner))
                throw new InvalidOperationException($"Shard '{owner}' already registered skill definitions.");
            _packs.Add(owner, pack);
        }
    }

    public bool TryGetSkills(string ownerId, out IReadOnlyList<ShardSkillInstruction> skills)
    {
        skills = Array.Empty<ShardSkillInstruction>();
        if (string.IsNullOrWhiteSpace(ownerId)) return false;
        string key = Normalize(ownerId);
        lock (_gate)
        {
            var results = new List<ShardSkillInstruction>();
            bool found = false;
            foreach (var pack in _packs.Values)
            {
                if (pack.TryGetValue(key, out var group))
                {
                    found = true;
                    results.AddRange(group);
                }
                else if (key.StartsWith("amethyst:", StringComparison.Ordinal) &&
                    pack.TryGetValue(key["amethyst:".Length..], out group))
                {
                    found = true;
                    results.AddRange(group);
                }
            }
            if (!found) return false;
            skills = Array.AsReadOnly(results.ToArray());
            return true;
        }
    }

    public async Task<bool> DispatchAsync(ShardSkillEvent context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Trigger))
            throw new ArgumentException("Skill event needs a trigger.", nameof(context));
        IReadOnlyList<ShardSkillInstruction> skills;
        bool found = TryGetSkills(context.OwnerId, out skills);
        if (!found && context.FallbackOwnerId is not null)
            found = TryGetSkills(context.FallbackOwnerId, out skills);
        if (!found) return false;
        foreach (var skill in skills)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(skill.Trigger, context.Trigger, StringComparison.OrdinalIgnoreCase) ||
                context.AllowedSkills is { } selectedSkills &&
                    !selectedSkills.Contains(skill))
                continue;
            ShardSkillExecutor executor;
            ShardSkillConditionEvaluator[] checks;
            lock (_gate)
            {
                if (!_mechanics.TryGetValue(skill.Mechanic, out var mechanic))
                    throw new InvalidOperationException(
                        $"Skill mechanic '{skill.Mechanic}' was unregistered.");
                executor = mechanic.Execute;
                checks = skill.Conditions.Select(condition =>
                    _conditions.TryGetValue(condition.Name, out var registered)
                        ? registered.Evaluate
                        : throw new InvalidOperationException(
                            $"Skill condition '?{condition.Name}' was unregistered."))
                    .ToArray();
            }
            bool allowed = true;
            for (int i = 0; i < checks.Length; i++)
                if (!checks[i](skill.Conditions[i], context))
                {
                    allowed = false;
                    break;
                }
            if (allowed) await executor(skill, context, ct);
        }
        return true;
    }

    public void ValidateConditions(IReadOnlyList<ShardSkillCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ShardSkillConditionValidator[] validators;
        lock (_gate)
            validators = conditions.Select(c =>
                _conditions.TryGetValue(c.Name, out var registered)
                    ? registered.Validate
                    : throw new InvalidDataException($"Unregistered condition ?{c.Name}."))
                .ToArray();
        for (int i = 0; i < validators.Length; i++)
            validators[i](conditions[i]);
    }

    public bool CheckConditions(IReadOnlyList<ShardSkillCondition> conditions,
        ShardSkillEvent context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateConditions(conditions);
        ShardSkillConditionEvaluator[] evaluators;
        lock (_gate)
            evaluators = conditions.Select(c =>
                _conditions.TryGetValue(c.Name, out var registered)
                    ? registered.Evaluate
                    : throw new InvalidOperationException($"Condition ?{c.Name} was unregistered."))
                .ToArray();
        for (int i = 0; i < evaluators.Length; i++)
            if (!evaluators[i](conditions[i], context)) return false;
        return true;
    }

    /// <summary>Dispatch synchronously inside server-authoritative transactions.
    /// Async mechanics cannot safely suspend while an inventory lock is held.</summary>
    public bool DispatchImmediate(ShardSkillEvent context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Trigger))
            throw new ArgumentException("Skill event needs a trigger.", nameof(context));
        if (!TryGetSkills(context.OwnerId, out var skills) &&
            (context.FallbackOwnerId is null ||
             !TryGetSkills(context.FallbackOwnerId, out skills)))
            return false;
        foreach (var skill in skills)
        {
            if (!string.Equals(skill.Trigger, context.Trigger,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            ShardSkillExecutor execute;
            ShardSkillConditionEvaluator[] checks;
            lock (_gate)
            {
                if (!_mechanics.TryGetValue(skill.Mechanic, out var mechanic))
                    throw new InvalidOperationException(
                        $"Mechanic '{skill.Mechanic}' has been unregistered.");
                execute = mechanic.Execute;
                checks = skill.Conditions.Select(c =>
                    _conditions.TryGetValue(c.Name, out var registered)
                        ? registered.Evaluate
                        : throw new InvalidOperationException(
                            $"Condition '?{c.Name}' has been unregistered.")).ToArray();
            }
            bool accepted = true;
            for (int i = 0; i < checks.Length; i++)
                if (!checks[i](skill.Conditions[i], context))
                {
                    accepted = false;
                    break;
                }
            if (!accepted) continue;
            ValueTask execution = execute(skill, context, CancellationToken.None);
            if (!execution.IsCompleted)
                throw new InvalidOperationException(
                    $"Mechanic '{skill.Mechanic}' must complete synchronously for ~{context.Trigger}.");
            execution.GetAwaiter().GetResult();
        }
        return true;
    }

    public void Unregister(string owner)
    {
        lock (_gate)
        {
            _packs.Remove(owner);
            foreach (string name in _mechanics.Where(m => m.Value.Owner == owner)
                .Select(m => m.Key).ToArray()) _mechanics.Remove(name);
            foreach (string name in _conditions.Where(c => c.Value.Owner == owner)
                .Select(c => c.Key).ToArray()) _conditions.Remove(name);
            foreach (string name in _triggers.Where(t => t.Value == owner)
                .Select(t => t.Key).ToArray()) _triggers.Remove(name);
        }
    }

    private static IEnumerable<(string Id, IReadOnlyList<ShardSkillInstruction> Skills)>
        ReadFile(string file, IReadOnlyDictionary<string, ShardSkillValidator> mechanics,
            IReadOnlyDictionary<string, ShardSkillConditionValidator> conditions,
            HashSet<string> triggers)
    {
        var yaml = new YamlStream();
        try
        {
            using var reader = File.OpenText(file);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Malformed skill YAML '{file}': {ex.Message}", ex);
        }
        if (yaml.Documents.Count == 0) yield break;
        if (yaml.Documents.Count != 1 ||
            yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"Skill YAML must contain a root mapping ({file}).");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.Children)
        {
            string id = Normalize(Scalar(entry.Key, file));
            if (!Identifier.IsMatch(id) || !seen.Add(id))
                throw new InvalidDataException($"Duplicate or invalid skill owner '{id}' ({file}).");
            if (entry.Value is not YamlMappingNode properties)
                throw new InvalidDataException($"Skills for '{id}' must be a YAML mapping ({file}).");
            bool enabled = true;
            YamlSequenceNode? list = null;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in properties.Children)
            {
                string key = Scalar(field.Key, file);
                if (!keys.Add(key))
                    throw new InvalidDataException($"Duplicate skill property '{key}' ({file}).");
                switch (key.ToLowerInvariant())
                {
                    case "enabled":
                        enabled = bool.TryParse(Scalar(field.Value, file), out bool value)
                            ? value : throw new InvalidDataException(
                                $"Invalid Enabled value for '{id}' ({file}).");
                        break;
                    case "skills":
                        list = field.Value as YamlSequenceNode ??
                            throw new InvalidDataException(
                                $"Skills for '{id}' must be a YAML list ({file}).");
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unsupported skill property '{key}' for '{id}' ({file}).");
                }
            }
            if (enabled && list is null)
                throw new InvalidDataException($"Skills list missing for '{id}' ({file}).");
            if (list is { Children.Count: > 32 })
                throw new InvalidDataException($"More than 32 skills for '{id}' ({file}).");
            var parsed = new List<ShardSkillInstruction>();
            if (enabled && list is not null)
                foreach (var node in list.Children)
                {
                    string expression = Scalar(node, file);
                    var skill = Parse(id, expression, file);
                    if (!triggers.Contains(skill.Trigger))
                        throw new InvalidDataException(
                            $"Unregistered trigger '~{skill.Trigger}' for '{id}' ({file}).");
                    if (!mechanics.TryGetValue(skill.Mechanic, out var validate))
                        throw new InvalidDataException(
                            $"Unregistered skill mechanic '{skill.Mechanic}' for '{id}' ({file}).");
                    try
                    {
                        validate(skill);
                        foreach (var condition in skill.Conditions)
                        {
                            if (!conditions.TryGetValue(condition.Name, out var check))
                                throw new InvalidDataException(
                                    $"Unregistered condition '?{condition.Name}' for '{id}' ({file}).");
                            check(condition);
                        }
                    }
                    catch (InvalidDataException) { throw; }
                    catch (Exception ex) when (ex is ArgumentException or FormatException
                        or InvalidOperationException)
                    {
                        throw new InvalidDataException(
                            $"Invalid skill '{expression}' for '{id}' ({file}): {ex.Message}", ex);
                    }
                    parsed.Add(skill);
                }
            yield return (id, Array.AsReadOnly(parsed.ToArray()));
        }
    }

    public static ShardSkillInstruction Parse(string owner, string expression, string file = "<inline>")
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 1024)
            throw new InvalidDataException($"Empty or oversized skill for '{owner}' ({file}).");
        string[] segments = Regex.Split(expression.Trim(), @"\s+\?");
        Match match = Expression.Match(segments[0]);
        if (!match.Success)
            throw new InvalidDataException(
                $"Invalid skill '{expression}' for '{owner}' ({file}). " +
                "Expected mechanic{key=value;...} @self ~onTrigger.");
        string mechanic = match.Groups["mechanic"].Value;
        string trigger = match.Groups["trigger"].Value;
        string? target = match.Groups["target"].Success
            ? match.Groups["target"].Value : null;
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (match.Groups["args"].Success && match.Groups["args"].Length != 0)
            foreach (string parameter in match.Groups["args"].Value.Split(';'))
            {
                int equals = parameter.IndexOf('=');
                if (equals <= 0 || equals == parameter.Length - 1)
                    throw new InvalidDataException(
                        $"Invalid skill parameter '{parameter}' for '{owner}' ({file}).");
                string key = parameter[..equals].Trim();
                string value = parameter[(equals + 1)..].Trim();
                if (!MechanicName.IsMatch(key) || value.Length == 0 ||
                    !arguments.TryAdd(key, value))
                    throw new InvalidDataException(
                        $"Duplicate or invalid skill parameter '{key}' for '{owner}' ({file}).");
            }
        var conditions = new List<ShardSkillCondition>();
        for (int i = 1; i < segments.Length; i++)
        {
            Match condition = Regex.Match(segments[i],
                @"^(?<name>[a-z][a-z0-9_-]*)(?:\{(?<args>[^{}]{0,512})\})?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!condition.Success || conditions.Count >= 16)
                throw new InvalidDataException($"Invalid skill condition '?{segments[i]}' ({file}).");
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string value = condition.Groups["args"].Value;
            if (value.Length > 0)
                foreach (string pair in value.Split(';'))
                {
                    int equals = pair.IndexOf('=');
                    if (equals < 1 || equals == pair.Length - 1)
                        throw new InvalidDataException($"Invalid ?condition argument '{pair}'.");
                    string key = pair[..equals].Trim();
                    string text = pair[(equals + 1)..].Trim();
                    if (!MechanicName.IsMatch(key) || text.Length == 0 ||
                        !parameters.TryAdd(key, text))
                        throw new InvalidDataException($"Invalid ?condition parameter '{pair}'.");
                }
            conditions.Add(new ShardSkillCondition(condition.Groups["name"].Value,
                new ReadOnlyDictionary<string, string>(parameters)));
        }
        return new ShardSkillInstruction(owner, mechanic, trigger, target,
            new ReadOnlyDictionary<string, string>(arguments))
        {
            Conditions = Array.AsReadOnly(conditions.ToArray())
        };
    }

    private static string Normalize(string key)
    {
        string id = key.Trim().ToLowerInvariant();
        return id.StartsWith("minecraft:", StringComparison.Ordinal)
            ? id["minecraft:".Length..] : id;
    }
    private static string Scalar(YamlNode node, string file) =>
        node is YamlScalarNode { Value: { } value } ? value :
            throw new InvalidDataException($"Expected scalar in skills YAML '{file}'.");
}
