using System.Text.Json;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>Immutable, canonical JSON representation of one stack's component
/// patch. Values are the same encoded JSON values accepted by the Items
/// Shard. Only known 26.3 component IDs and valid wire codecs are accepted.
/// A canonical string preserves value equality for inventory merging.</summary>
public static class ItemStackComponents
{
    public const int MaximumCharacters = 32768;

    public static string? Encode(IReadOnlyDictionary<string, string> patches)
    {
        ArgumentNullException.ThrowIfNull(patches);
        if (patches.Count == 0) return null;
        if (patches.Count > ShardItemComponentIds.Count)
            throw new InvalidDataException("Too many stack components.");
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, encoded) in patches)
        {
            if (!id.StartsWith("minecraft:", StringComparison.Ordinal) ||
                !ShardItemComponentIds.TryGetId(id, out _) ||
                encoded is null || encoded.Length > MaximumCharacters)
                throw new InvalidDataException($"Invalid stack component '{id}'.");
            using var json = JsonDocument.Parse(encoded);
            // Use the JSON value's canonical spelling to prevent distinct
            // spellings of the same patch from becoming different stack identities.
            ordered.Add(id, json.RootElement.GetRawText());
        }
        var added = new SortedDictionary<int, Action<PacketWriter>>();
        var removed = new SortedSet<int>();
        ItemComponentWire.AddComponents(added, removed, ordered);
        // Check payloads before persisting, not only during packet send.
        foreach (var encode in added.Values)
        {
            var writer = new PacketWriter();
            encode(writer);
            if (writer.ToArray().Length > 65536)
                throw new InvalidDataException("Stack component payload exceeds 64 KiB.");
        }
        string result = JsonSerializer.Serialize(ordered);
        if (result.Length > MaximumCharacters)
            throw new InvalidDataException("Stack component patch is too large.");
        return result;
    }

    public static IReadOnlyDictionary<string, string> Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        if (encoded.Length > MaximumCharacters)
            throw new InvalidDataException("Stack component patch is too large.");
        using var json = JsonDocument.Parse(encoded);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            json.RootElement.EnumerateObject().Count() > ShardItemComponentIds.Count)
            throw new InvalidDataException("Stack components must be a JSON object.");
        var patches = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in json.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String ||
                !patches.TryAdd(entry.Name, entry.Value.GetString()!))
                throw new InvalidDataException($"Invalid component entry {entry.Name}.");
        }
        return patches;
    }

    public static bool IsValid(string? encoded)
    {
        if (encoded is null) return true;
        try { return string.Equals(Encode(Decode(encoded)), encoded, StringComparison.Ordinal); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException
            or ArgumentException or OverflowException or InvalidOperationException)
        { return false; }
    }
}
