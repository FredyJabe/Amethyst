using Amethyst.Shards;

namespace Amethyst;

/// <summary>Source-fluid buckets backed by exact version-pinned block IDs.
/// This intentionally excludes fish/powder-snow buckets and waterlogging,
/// which require additional block-entity or fluid-system support.</summary>
public static class FluidBucketRules
{
    public static bool TryFill(ushort state, IShardVanillaBlockStates states,
        out string filledBucket)
    {
        filledBucket = "";
        if (!states.TryGet(state, out string block,
                out IReadOnlyDictionary<string, string> properties) ||
            !properties.TryGetValue("level", out string? level) ||
            level != "0")
            return false;
        filledBucket = block switch
        {
            "minecraft:water" => "minecraft:water_bucket",
            "minecraft:lava" => "minecraft:lava_bucket",
            _ => ""
        };
        return filledBucket.Length != 0;
    }

    public static bool TryPlace(string bucket, IShardDefaultBlockStates states,
        out ushort blockState)
    {
        string block = bucket switch
        {
            "minecraft:water_bucket" => "minecraft:water",
            "minecraft:lava_bucket" => "minecraft:lava",
            _ => ""
        };
        blockState = 0;
        return block.Length > 0 &&
            states.TryGetDefaultState(block, out blockState);
    }
}

public sealed partial class MinecraftServer
{
    /// <summary>Validate server-known source fluid or empty destination,
    /// then commit both the world edit and the exact held bucket conversion.
    /// A failed inventory mutation restores the prior block state.</summary>
    internal async Task<bool> TryHandleBucketUseAsync(ConnectedPlayer player,
        BlockUseOn hit, CancellationToken ct)
    {
        if (!hit.ValidHit || hit.Hand != 0 || _blocks is null ||
            _items is null || _vanillaBlockStates is null ||
            !CanEditAt(player, hit.X, hit.Y, hit.Z))
            return false;
        ItemStack held = player.Inventory.Held();
        if (held.Count == 0 ||
            !_items.TryGetById(held.ItemId, out ShardItemDefinition material))
            return false;
        string name = material.Identifier;
        ushort before;
        int x, y, z;
        ushort newState;
        int replacement;
        bool inventoryChange;
        if (name == "minecraft:bucket" &&
            FluidBucketRules.TryFill(
                _blocks.GetBlockState(hit.X, hit.Y, hit.Z),
                _vanillaBlockStates, out string filled) &&
            _items.TryGetByIdentifier(filled, out ShardItemDefinition output))
        {
            x = hit.X; y = hit.Y; z = hit.Z;
            before = _blocks.GetBlockState(x, y, z);
            newState = 0;
            replacement = output.ProtocolId;
            inventoryChange = true;
        }
        else if (_vanillaBlockStates is IShardDefaultBlockStates defaults &&
            FluidBucketRules.TryPlace(name, defaults, out ushort source))
        {
            (x, y, z) = BlockInteractionProtocol.Adjacent(
                hit.X, hit.Y, hit.Z, hit.Face);
            if (!CanEditAt(player, x, y, z) ||
                _blocks.GetBlockState(x, y, z) != 0 ||
                !_items.TryGetByIdentifier("minecraft:bucket",
                    out ShardItemDefinition empty))
                return false;
            before = 0;
            newState = source;
            replacement = empty.ProtocolId;
            inventoryChange = player.GameMode != PlayerGameMode.Creative;
        }
        else return false;

        if (!player.TryConsumeEditBudget())
        {
            await BlockInteractionProtocol.SendBlockUpdateAsync(
                player.Connection, x, y, z, before, ct);
            await BlockInteractionProtocol.SendAckAsync(
                player.Connection, hit.Sequence, ct);
            return true;
        }
        bool changed = _blocks.TrySetBlockState(x, y, z, newState,
            out ushort replaced);
        if (changed && inventoryChange)
        {
            try
            {
                if (!player.Inventory.TryConvertHeld(
                    held.ItemId, replacement))
                {
                    _blocks.TrySetBlockState(x, y, z, replaced, out _);
                    changed = false;
                }
            }
            catch
            {
                _blocks.TrySetBlockState(x, y, z, replaced, out _);
                throw;
            }
        }
        if (changed)
        {
            await BroadcastBlockChangeAsync(player, x, y, z, newState, ct);
            if (inventoryChange)
                await InventoryProtocol.SendContentAsync(
                    player.Connection, player.Inventory, ct);
        }
        else await BlockInteractionProtocol.SendBlockUpdateAsync(
            player.Connection, x, y, z,
            _blocks.GetBlockState(x, y, z), ct);
        await BlockInteractionProtocol.SendAckAsync(
            player.Connection, hit.Sequence, ct);
        return true;
    }
}
