namespace Amethyst;

// Registry snapshots are ordered to match vanilla Java 26.3, protocol 777.
// Source: ezchr/go-mcjava v777/vanilla/03..34_registry_data.bin.
// Each entry has HasData=false, so the vanilla client resolves its NBT from
// the version-matched minecraft:core known pack negotiated in Configuration.
public static class VanillaRegistries
{
    public const string CorePackNamespace = "minecraft";
    public const string CorePackId = "core";
    private static readonly Lazy<Registry[]> Registries = new(Load);

    public static int Count => Registries.Value.Length;

    public static int IndexOf(string registryId, string entryId)
    {
        var registry = Registries.Value.Single(r => r.Id == registryId);
        var index = Array.IndexOf(registry.Entries, entryId);
        if (index < 0) throw new InvalidDataException($"Unknown entry {registryId}/{entryId}.");
        return index;
    }

    public static string EntryAt(string registryId, int index)
    {
        var registry = Registries.Value.Single(r => r.Id == registryId);
        if ((uint)index >= (uint)registry.Entries.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        return registry.Entries[index];
    }

    public static IEnumerable<(string Id, int EntryCount)> Summary =>
        Registries.Value.Select(r => (r.Id, r.Entries.Length));

    public static async Task SendAsync(Stream stream, CancellationToken ct)
    {
        foreach (var registry in Registries.Value)
        {
            await Protocol.WritePacketAsync(stream, 0x07, writer =>
            {
                writer.WriteString(registry.Id);
                writer.WriteVarInt(registry.Entries.Length);
                foreach (string entry in registry.Entries)
                {
                    writer.WriteString(entry);
                    writer.WriteBool(false); // Built-in entry NBT supplied by known pack
                }
            }, ct);
        }
    }

    private static Registry[] Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "VanillaRegistries", "registry_entries.txt");
        var lines = File.ReadAllLines(path)
            .Where(l => l.Length > 0 && !l.StartsWith('#'));
        var registries = lines.Select(line =>
        {
            var pieces = line.Split('|', 2);
            if (pieces.Length != 2)
                throw new InvalidDataException($"Invalid registry line: {line}");
            return new Registry(pieces[0], pieces[1].Split(',')
                .Select(entry => "minecraft:" + entry).ToArray());
        }).ToArray();
        if (registries.Length != 32 || registries.Select(r => r.Id).Distinct().Count() != 32)
            throw new InvalidDataException("Expected 32 distinct 26.3 vanilla registries.");
        return registries;
    }

    private sealed record Registry(string Id, string[] Entries);
}
