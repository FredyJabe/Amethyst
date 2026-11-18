using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Amethyst;

// A profile's authenticated UUID is the ONLY identity used for privileges.
// Names are informational and may change; neither an offline username nor a
// client-supplied Login Start UUID is accepted as proof of operator identity.
public sealed class OperatorEntry
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public int Level { get; set; } = 4;
}

public sealed class OperatorFile
{
    public List<OperatorEntry> Operators { get; set; } = new();
}

public sealed class OperatorStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<Guid, OperatorEntry> _entries = new();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public OperatorStore(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(path), ".yml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("operatorsFile must end in .yaml or .yml.");
        _path = Path.GetFullPath(path);
        if (!File.Exists(_path))
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, "# Authenticated Java UUIDs only. Use the server console to grant the first OP.\noperators: []\n");
        }
        try
        {
            var file = Deserializer.Deserialize<OperatorFile>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Empty operators file.");
            foreach (OperatorEntry entry in file.Operators ?? throw new InvalidDataException("Missing operators list."))
            {
                if (!Guid.TryParseExact(entry.Uuid, "D", out Guid uuid) ||
                    entry.Level is < 1 or > 4 || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 16 ||
                    !_entries.TryAdd(uuid, new OperatorEntry { Uuid = uuid.ToString("D"), Name = entry.Name, Level = entry.Level }))
                    throw new InvalidDataException($"Invalid or duplicate operator entry: {entry.Uuid}");
            }
        }
        catch (YamlException e)
        {
            throw new InvalidDataException($"Invalid YAML in {_path}: {e.Message}", e);
        }
    }

    public int Level(Guid authenticatedUuid)
    {
        lock (_gate) return _entries.TryGetValue(authenticatedUuid, out OperatorEntry? entry) ? entry.Level : 0;
    }

    public OperatorEntry[] Snapshot()
    {
        lock (_gate) return _entries.Values.Select(Copy).ToArray();
    }

    public bool TryGrant(Guid authenticatedUuid, string authenticatedName, int level, out string response)
    {
        if (level is < 1 or > 4) { response = "OP level must be between 1 and 4."; return false; }
        if (string.IsNullOrWhiteSpace(authenticatedName) || authenticatedName.Length > 16)
        { response = "Invalid authenticated username."; return false; }
        lock (_gate)
        {
            if (_entries.TryGetValue(authenticatedUuid, out OperatorEntry? existing) && existing.Level == level)
            { response = $"{authenticatedName} is already OP level {level}."; return false; }
            _entries[authenticatedUuid] = new OperatorEntry
            { Uuid = authenticatedUuid.ToString("D"), Name = authenticatedName, Level = level };
            SaveLocked();
            response = $"Granted OP level {level} to {authenticatedName} ({authenticatedUuid}).";
            return true;
        }
    }

    public bool TryRevoke(string usernameOrUuid, out Guid revoked, out string response)
    {
        lock (_gate)
        {
            Guid uuid;
            if (Guid.TryParse(usernameOrUuid, out Guid parsed)) uuid = parsed;
            else
            {
                var matches = _entries.Where(p => string.Equals(p.Value.Name, usernameOrUuid,
                    StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray();
                if (matches.Length != 1)
                {
                    revoked = Guid.Empty;
                    response = matches.Length == 0 ? "No such OP. Use their UUID if they are offline." :
                        "Ambiguous stored username; use UUID.";
                    return false;
                }
                uuid = matches[0];
            }
            if (!_entries.Remove(uuid, out OperatorEntry? entry))
            {
                revoked = Guid.Empty; response = "That UUID is not an OP."; return false;
            }
            SaveLocked();
            revoked = uuid;
            response = $"Removed OP privileges from {entry?.Name ?? usernameOrUuid} ({uuid}).";
            return true;
        }
    }

    private void SaveLocked()
    {
        var file = new OperatorFile { Operators = _entries.Values.Select(Copy)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList() };
        string temp = _path + ".tmp";
        File.WriteAllText(temp, Serializer.Serialize(file));
        File.Move(temp, _path, overwrite: true);
    }

    private static OperatorEntry Copy(OperatorEntry entry) => new()
    { Uuid = entry.Uuid, Name = entry.Name, Level = entry.Level };
}
