using System.Text;
using Amethyst.Shards;

using Amethyst;
using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>A plain item entity in the Overworld. Coordinates are world-space,
/// Y is the bottom of the entity's 0.25-block bounding box.</summary>
/// <summary>Owns loose item entities and atomically persists them, separately from
/// player inventories. Packet visibility is managed by the Gameplay Shard.
/// First milestone physics models gravity and full-block floors; fluids, damage,
/// merging, partial collision shapes and hopper interaction are not included.</summary>
public sealed class GroundItemStore : IShardGroundItems
{
    private static readonly byte[] Magic = "AMDROP03"u8.ToArray();
    private static readonly byte[] PreviousMagic = "AMDROP02"u8.ToArray();
    private static readonly byte[] LegacyMagic = "AMDROP01"u8.ToArray();
    public const int MaximumEntities = 8192;
    public const long PickupDelayMilliseconds = 500;
    public const long DespawnMilliseconds = 300_000;
    private readonly object _gate = new();
    private readonly Dictionary<int, GroundItem> _entities = new();
    private readonly Func<int> _allocateEntityId;
    private readonly string? _path;
    private readonly IShardItemRegistry? _items;
    private int _frames;
    private long _lastSimulationMs;

    public GroundItemStore(string? dataDirectory, string worldPath, Func<int> allocateEntityId,
        IShardItemRegistry? items = null)
    {
        _allocateEntityId = allocateEntityId;
        _items = items;
        if (dataDirectory is null) return;
        string folder = Path.Combine(Path.GetFullPath(dataDirectory), "ground-items");
        Directory.CreateDirectory(folder);
        string worldKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(worldPath)))).Substring(0, 20);
        _path = Path.Combine(folder, worldKey + ".bin");
        Load();
    }

    private bool ValidStack(ItemStack stack)
    {
        if (stack.Count < 1 || stack.Damage < 0 ||
            !ItemStackComponents.IsValid(stack.ComponentData) ||
            stack.Unbreaking is < 0 or > 3 ||
            stack.RepairCost is < 0 or > 1_000_000) return false;
        bool patched = stack.Damage > 0 || stack.Unbreaking > 0 ||
            stack.Mending || stack.Unbreakable || stack.RepairCost > 0 ||
            !string.IsNullOrEmpty(stack.EnchantmentData);
        if (_items is null)
            return !patched && stack.ItemId is > 0 and <= 1657 &&
                stack.Count <= 64;
        if (!_items.IsValidStack(stack.ItemId, stack.Count)) return false;
        if (!patched) return true;
        if (stack.Count != 1 ||
            !_items.TryGetById(stack.ItemId, out var definition)) return false;
        if (definition.MaxDurability > 0)
            return stack.Damage < definition.MaxDurability;
        return stack.Damage == 0 && stack.Unbreaking == 0 &&
            !stack.Mending && !stack.Unbreakable &&
            stack.RepairCost == 0 && !string.IsNullOrEmpty(stack.EnchantmentData) &&
            definition.Identifier is "minecraft:book" or "minecraft:enchanted_book";
    }

    public GroundItem[] Snapshot()
    {
        lock (_gate) return _entities.Values.OrderBy(x => x.EntityId).ToArray();
    }

    public bool TrySpawn(int itemId, int count, double x, double y, double z,
        out GroundItem entity, long? nowMilliseconds = null) =>
        TrySpawn(new ItemStack(itemId, count), x, y, z,
            out entity, nowMilliseconds);

    public bool TrySpawn(ItemStack stack, double x, double y, double z,
        out GroundItem entity, long? nowMilliseconds = null)
    {
        entity = default;
        if (!ValidStack(stack) ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            return false;
        lock (_gate)
        {
            if (_entities.Count >= MaximumEntities) return false;
            int entityId = _allocateEntityId();
            entity = new GroundItem(entityId, Guid.NewGuid(),
                stack.ItemId, stack.Count, x, y, z, 0, false,
                nowMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            { Data = stack };
            _entities.Add(entityId, entity);
            try { Persist(); }
            catch { _entities.Remove(entityId); throw; }
            return true;
        }
    }

    /// <summary>Rollback an uncommitted player-item drop. The item must not
    /// have been delivered to a collector; the normal pickup delay prevents
    /// an immediate collection before the inventory commit.</summary>
    public bool TryRemove(int entityId)
    {
        lock (_gate)
        {
            if (!_entities.Remove(entityId, out GroundItem removed)) return false;
            try { Persist(); }
            catch { _entities.Add(entityId, removed); throw; }
            return true;
        }
    }

    public void Simulate(IShardBlockWorld world, long? nowMilliseconds = null)
    {
        long now = nowMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            // Fixed-rate 20Hz gravity, bounded catch-up after server stalls.
            int steps = _lastSimulationMs == 0 ? 1 :
                (int)Math.Clamp((now - _lastSimulationMs) / 50, 1, 4);
            _lastSimulationMs = now;
            bool removed = false;
            foreach (GroundItem original in _entities.Values.ToArray())
            {
                if (now - original.CreatedAtMs >= DespawnMilliseconds)
                {
                    _entities.Remove(original.EntityId);
                    removed = true;
                    continue;
                }
                GroundItem drop = original;
                for (int i = 0; i < steps; i++)
                {
                    if (drop.OnGround) break;
                    double velocity = Math.Max(-0.4, (drop.VelocityY - 0.04) * 0.98);
                    double nextY = drop.Y + velocity;
                    int x = (int)Math.Floor(drop.X);
                    int z = (int)Math.Floor(drop.Z);
                    int blockY = (int)Math.Floor(nextY - 0.001);
                    // Ground items collide only with ordinary solid blocks in
                    // this initial version. Existing generated/edited terrain
                    // is queried from the same IShardBlockWorld as mining.
                    if (blockY >= -64 && world.GetBlockState(x, blockY, z) != 0)
                    {
                        drop = drop with { Y = blockY + 1, VelocityY = 0, OnGround = true };
                        break;
                    }
                    drop = drop with { Y = nextY, VelocityY = velocity };
                    if (drop.Y < -80) break;
                }
                if (drop.Y < -80)
                {
                    _entities.Remove(drop.EntityId);
                    removed = true;
                }
                else _entities[drop.EntityId] = drop;
            }
            // Save position periodically, rather than fsyncing at 20Hz.
            if (removed || ++_frames % 20 == 0) Persist();
        }
    }

    /// <summary>Complete or partial pickup. Inventory insertion is authoritative
    /// and persisted before the loose stack is adjusted. Uninserted items stay
    /// on the ground, including when the player is full.</summary>
    public int TryCollect(int entityId, IShardPickupInventory inventory,
        out GroundItem remaining, long? nowMilliseconds = null)
    {
        remaining = default;
        long now = nowMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            if (!_entities.TryGetValue(entityId, out GroundItem entity) ||
                now - entity.CreatedAtMs < PickupDelayMilliseconds)
                return 0;
            int taken = inventory.GiveStack(entity.ResolvedStack);
            if (taken == 0) return 0;
            remaining = entity with
            {
                Count = entity.Count - taken,
                Data = entity.ResolvedStack with { Count = entity.Count - taken }
            };
            if (remaining.Count == 0) _entities.Remove(entityId);
            else _entities[entityId] = remaining;
            Persist();
            return taken;
        }
    }

    /// <summary>Discard an uncommitted drop when its source inventory
    /// transaction is rejected. Never turn a failed drop into free items.</summary>
    public bool TryDiscard(int entityId)
    {
        lock (_gate)
        {
            if (!_entities.Remove(entityId, out GroundItem entity)) return false;
            try { Persist(); }
            catch { _entities[entityId] = entity; throw; }
            return true;
        }
    }

    public void Save()
    {
        lock (_gate) Persist();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        using var stream = File.OpenRead(_path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        byte[] header = reader.ReadBytes(Magic.Length);
        bool legacy = header.SequenceEqual(LegacyMagic);
        bool previous = header.SequenceEqual(PreviousMagic);
        if (!legacy && !previous && !header.SequenceEqual(Magic))
            throw new InvalidDataException($"Invalid ground-items header in {_path}");
        int count = reader.ReadInt32();
        if (count is < 0 or > MaximumEntities)
            throw new InvalidDataException($"Invalid ground-items count in {_path}");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < count; i++)
        {
            Guid uuid = new(reader.ReadBytes(16));
            int item = reader.ReadInt32(), amount = reader.ReadInt32();
            int damage = legacy ? 0 : reader.ReadInt32();
            int unbreaking = legacy ? 0 : reader.ReadByte();
            bool mending = !legacy && reader.ReadBoolean();
            bool unbreakable = !legacy && reader.ReadBoolean();
            int repairCost = legacy ? 0 : reader.ReadInt32();
            string enchantments = legacy || previous ? "" : reader.ReadString();
            string components = legacy || previous ? "" : reader.ReadString();
            if (enchantments.Length > 4096 ||
                components.Length > ItemStackComponents.MaximumCharacters)
                throw new InvalidDataException("Oversized dropped-item component.");
            var stack = new ItemStack(item, amount)
            {
                Damage = damage, Unbreaking = unbreaking, Mending = mending,
                Unbreakable = unbreakable, RepairCost = repairCost,
                EnchantmentData = enchantments.Length == 0 ? null : enchantments,
                ComponentData = components.Length == 0 ? null : components
            };
            double x = reader.ReadDouble(), y = reader.ReadDouble(), z = reader.ReadDouble();
            double velocityY = reader.ReadDouble();
            bool onGround = reader.ReadBoolean();
            long created = reader.ReadInt64();
            if (!ValidStack(stack) ||
                !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
                !double.IsFinite(velocityY))
                throw new InvalidDataException($"Invalid ground item in {_path}");
            if (created <= now - DespawnMilliseconds) continue;
            int entityId = _allocateEntityId(); // collision-free after restart
            _entities.Add(entityId, new GroundItem(entityId, uuid, item, amount,
                x, y, z, velocityY, onGround, created) { Data = stack });
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException($"Trailing ground item data in {_path}");
    }

    private void Persist()
    {
        if (_path is null) return;
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(_entities.Count);
                foreach (GroundItem item in _entities.Values.OrderBy(x => x.EntityId))
                {
                    writer.Write(item.Uuid.ToByteArray());
                    ItemStack data = item.ResolvedStack;
                    writer.Write(data.ItemId);
                    writer.Write(data.Count);
                    writer.Write(data.Damage);
                    writer.Write((byte)data.Unbreaking);
                    writer.Write(data.Mending);
                    writer.Write(data.Unbreakable);
                    writer.Write(data.RepairCost);
                    writer.Write(data.EnchantmentData ?? "");
                    writer.Write(data.ComponentData ?? "");
                    writer.Write(item.X);
                    writer.Write(item.Y);
                    writer.Write(item.Z);
                    writer.Write(item.VelocityY);
                    writer.Write(item.OnGround);
                    writer.Write(item.CreatedAtMs);
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
