using System.Text.Json;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>Gameplay runtime supplied by the gameplay Shard. Network listener,
/// login authentication and configuration are handled by AmethystNetworkServer.</summary>
public sealed partial class MinecraftServer : IShardItemDropSink
{
    private static readonly bool TraceMining =
        Environment.GetEnvironmentVariable("AMETHYST_TRACE_MINING") == "1";
    private readonly ServerOptions _options;
    private readonly IShardWorld _world;
    private readonly IShardBlockWorld? _blocks;
    private readonly string? _inventoryDirectory;
    private readonly WorldClock _clock;
    private readonly IShardPacketBus _packetBus;
    private readonly PlayerRegistry _players;
    private readonly OperatorStore _operators;
    private readonly GroundItemStore? _groundItems;
    private readonly ExperienceOrbStore? _experienceOrbs;
    private readonly LivingEntityStore? _livingEntities;
    private readonly PlayerPositionStore _positions;
    private readonly WorkstationManager _stations;
    private readonly IShardItemRegistry? _items;
    private readonly IShardEntityManager? _entities;
    private readonly IShardSkillEngine? _skills;
    private readonly IShardEntityRegistry? _entityTypes;
    private readonly IShardEntityInteractions? _interactions;
    // Only the 20Hz item ticker mutates these per-viewer entity tracking maps.
    private readonly Dictionary<Guid, Dictionary<int, GroundItem>> _itemViews = new();
    private readonly Dictionary<Guid, Dictionary<int, ExperienceOrb>> _experienceViews = new();
    private readonly Dictionary<Guid, Dictionary<int, ShardEntityInstance>> _entityViews = new();
    private CancellationTokenSource? _clockLifetime;
    private Task? _clockSync;
    private Task? _itemsSync;
    private Task? _entitiesSync;
    private Task? _positionSync;
    private Task? _stationSync;

    public MinecraftServer(ServerOptions options, IShardWorld world, IShardPacketBus packetBus,
        IShardBlockWorld? blocks = null, string? inventoryDirectory = null,
        IShardItemRegistry? items = null, IShardRecipeRegistry? recipes = null,
        IShardEntityManager? entities = null, IShardSkillEngine? skills = null,
        IShardEntityRegistry? entityTypes = null,
        IShardEntityInteractions? interactions = null)
    {
        _options = options;
        _world = world;
        _blocks = blocks;
        _inventoryDirectory = inventoryDirectory;
        _items = items;
        _entities = entities;
        _skills = skills;
        _entityTypes = entityTypes;
        _interactions = interactions;
        _packetBus = packetBus;
        _players = new PlayerRegistry(options.MaxPlayers, entities is null ? null : entities.AllocateEntityId);
        _operators = new OperatorStore(options.OperatorsFile);
        _clock = new WorldClock(options);
        _positions = new PlayerPositionStore(inventoryDirectory, options.WorldPath, world);
        _stations = new WorkstationManager(inventoryDirectory, options.WorldPath, world,
            items, recipes ?? throw new ArgumentNullException(nameof(recipes)));
        if (_blocks is not null)
        {
            _groundItems = new GroundItemStore(inventoryDirectory,
                options.WorldPath, _players.AllocateEntityId, items);
            _experienceOrbs = new ExperienceOrbStore(inventoryDirectory,
                options.WorldPath, _players.AllocateEntityId);
            _livingEntities = new LivingEntityStore(inventoryDirectory,
                options.WorldPath, _players.AllocateEntityId);
            _projectiles = new ProjectileWorld(_players.AllocateEntityId);
            _fishing = new FishingWorld(_players.AllocateEntityId);
        }
    }

    public ShardPlayerSummary[] OnlinePlayers => _players.Snapshot()
        .Select(p => new ShardPlayerSummary(p.Username, p.PlayerUuid)).ToArray();

    internal GroundItem[] GroundItemsForTests => _groundItems?.Snapshot() ?? [];
    internal ExperienceOrb[] ExperienceOrbsForTests =>
        _experienceOrbs?.Snapshot() ?? [];
    internal WorkstationManager StationsForTests => _stations;
    internal ConnectedPlayer? GetPlayerForTests(Guid id) =>
        _players.Snapshot().FirstOrDefault(p => p.PlayerUuid == id);

    public void Start()
    {
        if (_clockLifetime is not null) throw new InvalidOperationException("Already started.");
        _clockLifetime = new CancellationTokenSource();
        _clockSync = BroadcastWorldTimeAsync(_clockLifetime.Token);
        if (_groundItems is not null)
            _itemsSync = RunGroundItemsAsync(_clockLifetime.Token);
        if (_entities is not null)
            _entitiesSync = RunEntitiesAsync(_clockLifetime.Token);
        if (_positions.Enabled)
            _positionSync = SavePositionsPeriodicallyAsync(_clockLifetime.Token);
        if (_blocks is not null)
            _stationSync = RunStationTicksAsync(_clockLifetime.Token);
        Console.WriteLine($"[gameplay] Enabled: chat={_options.ChatEnabled}, " +
            $"operators={_operators.Snapshot().Length}, viewDistance={_world.ViewDistance}");
    }

    public async Task StopAsync()
    {
        if (_players.Snapshot().Length != 0)
            throw new InvalidOperationException("Cannot disable gameplay while players are connected.");
        if (_clockLifetime is not null)
        {
            await _clockLifetime.CancelAsync();
            if (_clockSync is not null)
                try { await _clockSync; } catch (OperationCanceledException) { }
            if (_itemsSync is not null)
                try { await _itemsSync; } catch (OperationCanceledException) { }
            if (_entitiesSync is not null)
                try { await _entitiesSync; } catch (OperationCanceledException) { }
            if (_positionSync is not null)
                try { await _positionSync; } catch (OperationCanceledException) { }
            if (_stationSync is not null)
                try { await _stationSync; } catch (OperationCanceledException) { }
            _stations.Save();
            _groundItems?.Save();
            _experienceOrbs?.Save();
            _livingEntities?.Save();
            _livingViews.Clear();
            _projectileViews.Clear();
            _fishingViews.Clear();
            _itemViews.Clear();
            _experienceViews.Clear();
            _entityViews.Clear();
            _itemsSync = null;
            _entitiesSync = null;
            _positionSync = null;
            _stationSync = null;
            _clockLifetime.Dispose();
            _clockLifetime = null;
            _clockSync = null;
        }
        _clock.Save();
    }

    public bool TryAdmit(VerifiedProfile profile, PacketChannel channel, int chatMode, out string reason)
    {
        var player = new ConnectedPlayer(profile.Uuid, profile.Username, profile.Properties,
            _players.AllocateEntityId(), channel, _world, _inventoryDirectory, _items);
        // Set the game mode before SpawnPackets.SendAsync constructs the Join Game
        // packet, player abilities, and the initial tab-list state.
        player.SetGameMode(Enum.Parse<PlayerGameMode>(_options.DefaultGameMode, ignoreCase: true));
        // Initialize to the actual world spawn, not the ConnectedPlayer's
        // historical (0.5,80,0.5) placeholder. Saved locations are only
        // restored for the same world, seed and dimension.
        player.SetPose(new PlayerPose(_world.SpawnXPosition, _world.SpawnYPosition,
            _world.SpawnZPosition, 0, 0, true));
        if (_positions.TryLoad(profile.Uuid, out PlayerPose lastPosition))
        {
            player.SetPose(lastPosition);
            Console.WriteLine($"[position] Restored {player.Username} at " +
                $"{lastPosition.X:F2}, {lastPosition.Y:F2}, {lastPosition.Z:F2}");
        }
        player.SetChatMode(chatMode);
        return _players.TryAdd(player, out reason);
    }

    public void SetChatMode(Guid uuid, int mode)
    {
        ConnectedPlayer? player = _players.Snapshot().FirstOrDefault(p => p.PlayerUuid == uuid);
        player?.SetChatMode(mode);
    }

    public async Task RunPlayAsync(VerifiedProfile profile, CancellationToken ct)
    {
        ConnectedPlayer player = _players.Snapshot().FirstOrDefault(p => p.PlayerUuid == profile.Uuid)
            ?? throw new InvalidOperationException("Player admission was not completed.");
        await SpawnPackets.SendAsync(player, _world, _options, _clock, ct);
        await InventoryProtocol.SendContentAsync(player.Connection, player.Inventory, ct);
        await ExperienceProtocol.SendExperienceAsync(player.Connection,
            player.Experience, ct);
        await CombatProtocol.SendHealthAsync(player, ct);
        int actualOpLevel = _options.OnlineMode ? _operators.Level(player.PlayerUuid) : 0;
        await GameModeProtocol.SendClientPermissionsAsync(player.Connection, player.EntityId, actualOpLevel, ct);
        await CommandTree.SendAsync(player.Connection, actualOpLevel, ct);
        Console.WriteLine($"[play] {player.Username} received initial world");
        await HandlePlayAsync(player, ct);
    }

    public async Task OnDisconnectedAsync(Guid uuid)
    {
        ConnectedPlayer? player = _players.Snapshot().FirstOrDefault(p => p.PlayerUuid == uuid);
        if (player is null) return;
        // The Play receive loop has finished by this point. Save only players
        // who completed login; rejected/half-loaded sessions must not overwrite
        // a valid previous save with the initial spawn placeholder.
        CloseStation(player);
        _fishing?.Cancel(player.PlayerUuid);
        TrySavePosition(player);
        _players.Remove(player.ConnectionId);
        await NotifyDepartureAsync(player);
        Console.WriteLine($"[leave] {player.Username}; {_players.Snapshot().Length} connected");
    }

    private void TrySavePosition(ConnectedPlayer player)
    {
        try { player.PersistPosition(_positions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[position] Unable to save {player.Username}: {ex.Message}");
        }
    }

    private async Task SavePositionsPeriodicallyAsync(CancellationToken ct)
    {
        // In addition to saving on logout, recover recent positions if the
        // process terminates unexpectedly. Avoid disk I/O on every move packet.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
            foreach (ConnectedPlayer player in _players.Snapshot())
                TrySavePosition(player);
    }

    public IReadOnlyList<string> CompleteConsole(string prefix) =>
        ConsoleCommandCompletion.Suggest(prefix,
            _players.Snapshot().Where(p => p.Ready).Select(p => p.Username),
            _operators.Snapshot().Select(p => p.Name));

    private async Task HandlePlayAsync(ConnectedPlayer player, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, player.DisconnectToken);
        bool teleportConfirmed = false, playerLoaded = false;
        int expectedTeleportId = SpawnPackets.TeleportId;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Packet? packet = await player.Connection.ReceiveAsync(lifetime.Token);
                if (packet is null) break;
                if (await _packetBus.DispatchAsync(player, packet.Value, lifetime.Token)) continue;
                var reader = packet.Value.Reader();
                switch (packet.Value.Id)
                {
                    case 0x00: // Accept Teleportation
                    {
                        int teleportId = reader.ReadVarInt();
                        double x = reader.ReadDouble(), y = reader.ReadDouble(), z = reader.ReadDouble();
                        float yaw = reader.ReadFloat(), pitch = reader.ReadFloat();
                        reader.RequireEnd();
                        if (teleportId == expectedTeleportId && double.IsFinite(x) && double.IsFinite(y)
                            && double.IsFinite(z) && float.IsFinite(yaw) && float.IsFinite(pitch))
                            teleportConfirmed = true;
                        break;
                    }
                    case FlightProtocol.ServerboundPlayerCommand:
                    {
                        PlayerCommandAction command =
                            FlightProtocol.ReadPlayerCommand(packet.Value);
                        await HandleFlightCommandAsync(player, command,
                            lifetime.Token);
                        break;
                    }
                    case 0x2C: // Player Loaded
                        reader.RequireEnd();
                        playerLoaded = true;
                        break;
                    case 0x0D: // Client Tick End (Java 26.3)
                        // A reliable, ordered 50ms client tick boundary. Needed
                        // for mining when chunk streaming stalls packet reading.
                        reader.RequireEnd();
                        player.AdvanceClientTick();
                        break;
                    case 0x0C: // Respawn request (Java 26.3)
                    {
                        int command = reader.ReadVarInt();
                        reader.RequireEnd();
                        if (command == 0 && player.Vitals.Snapshot().Health <= 0)
                        {
                            expectedTeleportId++;
                            teleportConfirmed = false;
                            await RespawnPlayerAsync(player, expectedTeleportId,
                                lifetime.Token);
                        }
                        break;
                    }
                    case CombatProtocol.ServerboundAttack:
                    {
                        int entityId = CombatProtocol.ReadAttack(packet.Value);
                        await HandleAttackAsync(player, entityId, lifetime.Token);
                        break;
                    }
                    case EntityInteractionProtocol.ServerboundInteract:
                    {
                        EntityInteraction interaction =
                            EntityInteractionProtocol.Read(packet.Value);
                        await HandleEntityInteractAsync(player, interaction,
                            lifetime.Token);
                        break;
                    }
                    case 0x1C: // KeepAlive reply
                        _ = reader.ReadInt64(); reader.RequireEnd();
                        break;
                    case 0x1E: // Move position
                    case 0x1F: // Move position and rotation
                    case 0x20: // Move rotation
                    case 0x21: // Move status only
                    {
                        PlayerPose old = player.Pose;
                        double x = old.X, y = old.Y, z = old.Z;
                        float yaw = old.Yaw, pitch = old.Pitch;
                        if (packet.Value.Id is 0x1E or 0x1F)
                        {
                            x = reader.ReadDouble(); y = reader.ReadDouble(); z = reader.ReadDouble();
                        }
                        if (packet.Value.Id is 0x1F or 0x20)
                        {
                            yaw = reader.ReadFloat(); pitch = reader.ReadFloat();
                        }
                        byte flags = reader.ReadByte();
                        reader.RequireEnd();
                        if (!teleportConfirmed || !playerLoaded) break;
                        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
                            !float.IsFinite(yaw) || !float.IsFinite(pitch) ||
                            Math.Abs(x) > 29_999_984 || Math.Abs(z) > 29_999_984 ||
                            y is < -20000000 or > 20000000 ||
                            Math.Abs(x - old.X) > 1000 || Math.Abs(y - old.Y) > 1000 ||
                            Math.Abs(z - old.Z) > 1000)
                            throw new InvalidDataException("Invalid player movement.");
                        // Until physics/collision is implemented server-side, recover
                        // players who fell through terrain before its chunks arrived.
                        // Do not let an out-of-world position persist on the server.
                        if (_world.UsesDensity ? y < _world.MinY - 12 :
                            y < _world.GetGroundHeight((int)Math.Floor(x), (int)Math.Floor(z)) - 12)
                        {
                            Console.WriteLine($"[spawn] Recovering {player.Username} from Y={y:F1} " +
                                $"(ground Y={_world.GetGroundHeight((int)Math.Floor(x), (int)Math.Floor(z))})");
                            expectedTeleportId++;
                            teleportConfirmed = false;
                            player.SetPose(new PlayerPose(_world.SpawnXPosition, _world.SpawnYPosition,
                                _world.SpawnZPosition, 0, 0, true));
                            await SpawnPackets.SendPositionAsync(player.Connection,
                                expectedTeleportId, _world.SpawnXPosition, _world.SpawnYPosition,
                                _world.SpawnZPosition, lifetime.Token);
                            break;
                        }
                        var current = new PlayerPose(x, y, z, yaw, pitch,
                            (flags & 1) != 0);
                        if (player.Flight.IsGliding &&
                            !FlightRules.PlausibleMovement(old, current,
                                player.Flight.IsBoosted(Environment.TickCount64)))
                        {
                            expectedTeleportId++;
                            teleportConfirmed = false;
                            await SpawnPackets.SendPositionAsync(player.Connection,
                                expectedTeleportId, old.X, old.Y, old.Z,
                                old.Yaw, old.Pitch, lifetime.Token);
                            break;
                        }
                        player.SetPose(current);
                        await UpdateGlideMovementAsync(player, old, current,
                            flags, lifetime.Token);
                        if (_stations.GetMenu(player.PlayerUuid) is StationMenu active)
                        {
                            double sx = active.Position.X + 0.5 - current.X;
                            double sy = active.Position.Y + 0.5 - (current.Y + 1.62);
                            double sz = active.Position.Z + 0.5 - current.Z;
                            if (sx * sx + sy * sy + sz * sz > 64)
                                await CloseStationAsync(player, true, lifetime.Token);
                        }
                        await player.View.UpdateAsync(player.Connection, current, lifetime.Token);
                        if (player.Ready)
                            await BroadcastMovementAsync(player, old, current, lifetime.Token);
                        break;
                    }
                    case ChatProtocol.ServerboundChat:
                    {
                        IncomingChat message = ChatProtocol.ParseChat(packet.Value);
                        if (player.Ready) await HandleChatAsync(player, message, lifetime.Token);
                        break;
                    }
                    case GameModeProtocol.ServerboundChangeGameMode: // F3+F4 switcher (26.3)
                    {
                        PlayerGameMode mode = GameModeProtocol.ParseSwitchRequest(packet.Value);
                        if (!GameModeProtocol.CanChange(Level(player)))
                            await ReplyAsync(player, "OP level 1 is required to change game mode.", lifetime.Token);
                        else if (player.Ready)
                            await ChangeGameModeAsync(player, mode, player, lifetime.Token);
                        break;
                    }
                    case ChatProtocol.ServerboundCommand:
                    case ChatProtocol.ServerboundSignedCommand:
                    {
                        IncomingCommand command = ChatProtocol.ParseCommand(packet.Value);
                        if (player.Ready)
                        {
                            if (command.HasSignedArguments)
                                await ReplyAsync(player, "Signed command arguments are not supported yet.", lifetime.Token);
                            else await ExecuteCommandAsync(player, command.Command, lifetime.Token);
                        }
                        break;
                    }
                    case ChatProtocol.ServerboundChatAck:
                        ChatProtocol.ParseAck(packet.Value);
                        break;
                    case ChatProtocol.ServerboundSessionUpdate:
                        ChatProtocol.ParseSessionUpdate(packet.Value);
                        break;
                    case BlockInteractionProtocol.ServerboundSetHeldItem:
                        player.UseState.CancelCharge();
                        player.SelectHotbar(BlockInteractionProtocol.ReadHeldItem(packet.Value));
                        await ReleaseUsingItemAsync(player, lifetime.Token);
                        break;
                    case BlockInteractionProtocol.ServerboundCreativeSlot:
                    {
                        CreativeSlotUpdate update = BlockInteractionProtocol.ReadCreativeSlot(packet.Value);
                        if (player.Ready)
                        {
                            player.SetCreativeSlot(update.Slot, update.ItemId,
                                update.Count, update.PlainItem);
                            await InventoryProtocol.SendContentAsync(player.Connection, player.Inventory, lifetime.Token);
                        }
                        break;
                    }
                    case InventoryProtocol.ServerboundContainerClick:
                    {
                        InventoryClick click = InventoryProtocol.ReadClick(packet.Value);
                        if (player.Ready)
                        {
                            if (_stations.GetMenu(player.PlayerUuid) is not null || click.Window != 0)
                                await HandleStationClickAsync(player, click, lifetime.Token);
                            else
                            {
                                player.Inventory.ApplyClick(click);
                                await InventoryProtocol.SendContentAsync(player.Connection, player.Inventory, lifetime.Token);
                            }
                        }
                        break;
                    }
                    case InventoryProtocol.ServerboundContainerClose:
                    {
                        int window = reader.ReadVarInt();
                        reader.RequireEnd();
                        if (player.Ready && window != 0 &&
                            _stations.GetMenu(player.PlayerUuid)?.WindowId == window)
                            await CloseStationAsync(player, false, lifetime.Token);
                        else if (window == 0 && player.Ready)
                            await InventoryProtocol.SendContentAsync(player.Connection, player.Inventory, lifetime.Token);
                        break;
                    }
                    case BlockInteractionProtocol.ServerboundPlayerAction:
                    {
                        PlayerBlockAction action = BlockInteractionProtocol.ReadAction(packet.Value);
                        await HandleBlockActionAsync(player, action, lifetime.Token);
                        break;
                    }
                    case EntityInteractionProtocol.ServerboundInteract:
                    {
                        EntityInteraction hit = EntityInteractionProtocol.Read(packet.Value);
                        if (player.Ready)
                            await HandleEntityInteractionAsync(player, hit, lifetime.Token);
                        break;
                    }
                    case BlockInteractionProtocol.ServerboundUseItemOn:
                    {
                        BlockUseOn hit = BlockInteractionProtocol.ReadUseOn(packet.Value);
                        await HandleBlockUseAsync(player, hit, lifetime.Token);
                        break;
                    }
                    case UseItemProtocol.ServerboundUseItem:
                    {
                        UseItemAction use = UseItemProtocol.ReadUseItem(packet.Value);
                        await HandleUseItemAsync(player, use, lifetime.Token);
                        break;
                    }
                    case 0x0E: // Client information (Play): chat visibility preference
                    {
                        ApplyClientInformation(player, packet.Value);
                        break;
                    }
                    case 0x0B: // Chunk Batch Received: client-driven flow control
                    {
                        float desiredPerTick = reader.ReadFloat();
                        reader.RequireEnd();
                        await player.View.OnBatchReceivedAsync(player.Connection, desiredPerTick,
                            lifetime.Token);
                        break;
                    }
                    default:
                        // Unimplemented inventory, combat, and abilities packets.
                        break;
                }
                if (teleportConfirmed && playerLoaded && !player.Ready)
                    await NotifyArrivalAsync(player, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
        }
    }

    private async Task NotifyArrivalAsync(ConnectedPlayer player, CancellationToken ct)
    {
        // Publish the player only after the client has confirmed spawn and
        // loaded the world. This prevents ghost players during Configuration.
        ConnectedPlayer[] peers = _players.Snapshot()
            .Where(p => p.ConnectionId != player.ConnectionId && p.Ready).ToArray();
        // The local player has a tab-list entry but must never receive its own Add Entity.
        await PlayerPackets.SendTabEntryAsync(player.Connection, player, ct);
        foreach (ConnectedPlayer peer in peers)
        {
            try
            {
                await PlayerPackets.SendVisibleAsync(player.Connection, peer, ct);
                await PlayerPackets.SendVisibleAsync(peer.Connection, player, ct);
            }
            catch (IOException) { /* stale peer cleaned up by its own handler */ }
            catch (ObjectDisposedException) { }
        }
        player.MarkReady();
        Console.WriteLine($"[ready] {player.Username} can see {peers.Length} player(s)");
        await BroadcastChatAsync($"[Server] {player.Username} joined the game", ct);
    }

    private bool CanEditAt(ConnectedPlayer player, int x, int y, int z)
    {
        if (_blocks is null || !player.Ready || player.GameMode is PlayerGameMode.Adventure or PlayerGameMode.Spectator ||
            y is < -64 or > 319 || x is < -29_999_984 or > 29_999_984 ||
            z is < -29_999_984 or > 29_999_984) return false;
        PlayerPose pose = player.Pose;
        double dx = x + 0.5 - pose.X;
        double dy = y + 0.5 - (pose.Y + 1.62);
        double dz = z + 0.5 - pose.Z;
        return dx * dx + dy * dy + dz * dz <= 36; // Creative interaction range
    }

    /// <summary>Dispatch an item skill by the canonical identity of the
    /// authoritative stack. Never use a client-provided item ID as the owner.
    /// Coordinates come from the server player pose unless an event specifies
    /// the actual world-item position.</summary>
    private Task DispatchItemSkillAsync(ConnectedPlayer player, int itemId,
        string trigger, CancellationToken ct, int count = 1,
        double? x = null, double? y = null, double? z = null)
    {
        if (_skills is null || _items is null || !player.Ready ||
            itemId <= 0 || count <= 0 ||
            !_items.TryGetById(itemId, out ShardItemDefinition definition))
            return Task.CompletedTask;
        PlayerPose pose = player.Pose;
        return _skills.DispatchAsync(new ShardSkillEvent(definition.Identifier,
            trigger, x ?? pose.X, y ?? pose.Y, z ?? pose.Z,
            ActorUuid: player.PlayerUuid, ItemId: itemId, Count: count), ct);
    }

    internal async Task HandleBlockActionAsync(ConnectedPlayer player,
        PlayerBlockAction action, CancellationToken ct)
    {
        // Run with AMETHYST_TRACE_MINING=1 ./run.sh to see the exact server
        // decision for every START/DIRECTION/ABORT/STOP and its resulting block state.
        if (TraceMining)
            Console.WriteLine($"[mining] recv {player.Username} mode={player.GameMode} " +
                $"status={action.Status} pos={action.X},{action.Y},{action.Z} " +
                $"face={action.Face} seq={action.Sequence} held={player.Inventory.Held().ItemId}");
        if (action.Status == BlockInteractionProtocol.ReleaseUseItem)
        {
            await ReleaseUsingItemAsync(player, ct);
            await BlockInteractionProtocol.SendAckAsync(
                player.Connection, action.Sequence, ct);
            return;
        }
        if (action.Status is BlockInteractionProtocol.DropAllItems
            or BlockInteractionProtocol.DropItem)
        {
            if (player.Ready && _groundItems is not null)
            {
                ItemStack held = player.Inventory.Held();
                int count = action.Status == BlockInteractionProtocol.DropAllItems
                    ? held.Count : Math.Min(held.Count, 1);
                if (held.ItemId > 0 && count > 0)
                {
                    PlayerPose pose = player.Pose;
                    if (_groundItems.TrySpawn(held.ItemId, count,
                        pose.X, pose.Y + 1.2, pose.Z, out GroundItem ground))
                    {
                        bool committed;
                        try { committed = player.Inventory.TryTakeHeld(held.ItemId, count); }
                        catch
                        {
                            _groundItems.TryRemove(ground.EntityId);
                            throw;
                        }
                        if (committed)
                        {
                            await DispatchItemSkillAsync(player, held.ItemId,
                                "onDrop", ct, count, ground.X, ground.Y, ground.Z);
                            if (action.Status == BlockInteractionProtocol.DropAllItems)
                                await DispatchItemSkillAsync(player, held.ItemId,
                                    "onStackDrop", ct, count, ground.X, ground.Y, ground.Z);
                        }
                        else _groundItems.TryRemove(ground.EntityId);
                    }
                }
            }
            // Always reconcile the client, including empty slots, full ground
            // item stores and creative inventories with modified hotbar items.
            await InventoryProtocol.SendContentAsync(player.Connection, player.Inventory, ct);
            return;
        }
        // RELEASE_USE_ITEM, SWAP_ITEM_WITH_OFFHAND, STAB need separate
        // gameplay implementations; they must NOT be interpreted as mining.
        if (action.Status is < BlockInteractionProtocol.StartDestroyBlock
            or > BlockInteractionProtocol.StopDestroyBlock) return;
        bool valid = action.Face is >= 0 and <= 5 &&
            CanEditAt(player, action.X, action.Y, action.Z);
        ushort state = valid ? _blocks!.GetBlockState(action.X, action.Y, action.Z) : (ushort)0;
        if (valid && state != 0 && action.Status == BlockInteractionProtocol.StartDestroyBlock)
            await DispatchItemSkillAsync(player, player.Inventory.Held().ItemId,
                "onLeftClick", ct);
        bool changed = false;
        bool toolWearChanged = false;
        bool survival = player.GameMode == PlayerGameMode.Survival;
        string outcome = valid ? "no change" : "invalid target / range / mode / not ready";
        switch (action.Status)
        {
            case BlockInteractionProtocol.StartDestroyBlock: // Start; Creative / one-tick Survival are instant.
            {
                player.CancelMining();
                if (!valid || state == 0 || !player.TryConsumeEditBudget())
                {
                    if (valid) outcome = state == 0 ? "already air" : "edit budget exceeded";
                    break;
                }
                int held = player.Inventory.Held().ItemId;
                int duration = player.GameMode == PlayerGameMode.Creative ? 0 :
                    MiningRules.RequiredMilliseconds(state, held);
                if (duration == int.MaxValue)
                {
                    outcome = "unbreakable";
                    break;
                }
                // Protocol 777: if a block takes at most one client tick to
                // mine, vanilla predicts AIR and sends START but NEVER STOP.
                // Previously we only completed hardness-zero blocks here,
                // leaving all tool-instamined blocks as server-side ghosts.
                if (duration <= 50)
                {
                    changed = _blocks!.TrySetBlockState(action.X, action.Y, action.Z, 0, out _);
                    outcome = changed ? "one-tick/Creative break committed" : "write rejected";
                }
                else if (survival)
                {
                    player.StartMining(action.X, action.Y, action.Z, state, held, duration);
                    outcome = $"digging started; expected={duration}ms";
                }
                else
                {
                    outcome = "mode disallows mining";
                }
                break;
            }
            case BlockInteractionProtocol.ChangeDestroyDirection:
                // Java 26.3 sends this while continuing an active dig when
                // the hit face changes. Preserve the original mining session.
                outcome = "digging direction changed";
                break;
            case BlockInteractionProtocol.AbortDestroyBlock:
                player.CancelMining();
                outcome = "digging cancelled";
                break;
            case BlockInteractionProtocol.StopDestroyBlock: // Finished digging
            {
                if (!survival || !valid || state == 0 || !player.TryConsumeEditBudget())
                {
                    player.CancelMining();
                    outcome = !survival ? "STOP outside Survival" :
                        !valid ? "invalid target / range / not ready" :
                        state == 0 ? "already air" : "edit budget exceeded";
                    break;
                }
                int held = player.Inventory.Held().ItemId;
                if (!player.FinishMining(action.X, action.Y, action.Z, state, held,
                    out string reason))
                {
                    outcome = $"STOP rejected: {reason}";
                    break;
                }
                changed = _blocks!.TrySetBlockState(action.X, action.Y, action.Z, 0, out _);
                outcome = changed ? $"STOP committed: {reason}" : "STOP write rejected";
                break;
            }
        }

        if (changed)
        {
            // The last point of durability may break a tool, but this block
            // must still yield drops based on the tool that mined it.
            int miningTool = player.Inventory.Held().ItemId;
            if (WorkstationManager.FromBlockState(state) is not null)
                await BreakStationAsync(action.X, action.Y, action.Z, ct);
            await BroadcastBlockChangeAsync(player, action.X, action.Y, action.Z, 0, ct);
            if (survival)
            {
                MiningRules.BlockInfo info = MiningRules.ForState(state);
                if (info.DropItem > 0 && MiningRules.IsHarvestable(info, miningTool))
                {
                    // A break produces a world item. The player receives it
                    // only after touching the item and after its pickup delay.
                    // The shared 20Hz item ticker handles spawn/pickup packets.
                    if (_groundItems is null || !_groundItems.TrySpawn(info.DropItem, 1,
                        action.X + 0.5, action.Y + 0.35, action.Z + 0.5, out _))
                        Console.WriteLine($"[items] No capacity to spawn drop {info.DropItem} " +
                            $"at {action.X},{action.Y},{action.Z}");
                }
                int wear = MiningRules.DurabilityCostForBlock(state, miningTool, _items);
                toolWearChanged = wear > 0 &&
                    player.Inventory.DamageHeld(miningTool, wear);
                // XP is a physical orb, not an instant mining reward.
                // Only correctly harvested ore can spawn experience.
                if (MiningRules.IsHarvestable(info, miningTool) &&
                    _experienceOrbs is not null)
                {
                    int xp = OreExperience.Roll(state);
                    if (xp > 0)
                        _experienceOrbs.TrySpawn(xp,
                            action.X + 0.5, action.Y + 0.35,
                            action.Z + 0.5, out _);
                }
            }
        }
        else if (valid && action.Status == BlockInteractionProtocol.StopDestroyBlock)
        {
            // A rejected STOP reconciles the client's predicted air before
            // acknowledging it. A normal START must not correct a live dig.
            await BlockInteractionProtocol.SendBlockUpdateAsync(player.Connection,
                action.X, action.Y, action.Z, state, ct);
        }

        if (TraceMining)
        {
            ushort authoritative = valid ? _blocks!.GetBlockState(action.X, action.Y, action.Z) : (ushort)0;
            Console.WriteLine($"[mining] result {player.Username} status={action.Status} " +
                $"seq={action.Sequence} before={state} after={authoritative} " +
                $"changed={changed} {outcome}");
        }
        await BlockInteractionProtocol.SendAckAsync(player.Connection, action.Sequence, ct);
        if (toolWearChanged)
            await InventoryProtocol.SendSlotAsync(player.Connection,
                player.Inventory, player.Inventory.SelectedSlot, ct);
    }

    private async Task OpenStationAsync(ConnectedPlayer player,
        StationPos pos, StationKind kind, CancellationToken ct)
    {
        if (_stations.GetMenu(player.PlayerUuid) is not null)
            await CloseStationAsync(player, false, ct);
        StationMenu menu = _stations.Open(player.PlayerUuid, pos, kind);
        await StationProtocol.OpenAsync(player.Connection, menu.WindowId,
            StationProtocol.MenuType(kind), StationProtocol.Title(kind), ct);
        await SendStationContentAsync(player, ct);
    }

    private async Task SendStationContentAsync(ConnectedPlayer player, CancellationToken ct)
    {
        var content = _stations.Content(player.PlayerUuid, player.Inventory);
        if (content is not { } menu) return;
        await StationProtocol.SendContentAsync(player.Connection, menu.WindowId,
            menu.Revision, menu.Slots, menu.Cursor, ct, _items);
        StationKind? kind = _stations.GetMenu(player.PlayerUuid)?.Kind;
        if (kind == StationKind.Anvil)
        {
            var data = _stations.Data(player.PlayerUuid);
            await StationProtocol.SendDataAsync(player.Connection,
                menu.WindowId, 0, data.Burn, ct); // anvil level cost
        }
        else if (kind is StationKind.Furnace or StationKind.Smoker or
            StationKind.BlastFurnace)
        {
            var data = _stations.Data(player.PlayerUuid);
            await StationProtocol.SendDataAsync(player.Connection, menu.WindowId, 0, data.Burn, ct);
            await StationProtocol.SendDataAsync(player.Connection, menu.WindowId, 1, data.TotalBurn, ct);
            await StationProtocol.SendDataAsync(player.Connection, menu.WindowId, 2, data.Progress, ct);
            await StationProtocol.SendDataAsync(player.Connection, menu.WindowId, 3, data.TotalProgress, ct);
        }
    }

    private async Task HandleStationClickAsync(ConnectedPlayer player,
        InventoryClick click, CancellationToken ct)
    {
        StationMenu? active = _stations.GetMenu(player.PlayerUuid);
        if (active is null || click.Window != active.WindowId)
        {
            // A forged/stale window may not mutate either inventory or a
            // removed block entity. Resynchronize the normal inventory.
            if (click.Window != 0)
                await StationProtocol.CloseAsync(player.Connection, click.Window, ct);
            await InventoryProtocol.SendContentAsync(player.Connection, player.Inventory, ct);
            return;
        }
        bool changed = _stations.Click(player.PlayerUuid, player.Inventory,
            click, player.Experience,
            player.GameMode == PlayerGameMode.Creative);
        // Even rejected predictions need an authoritative menu resync.
        await SendStationContentAsync(player, ct);
        if (changed && active.Kind == StationKind.Anvil)
        {
            await ExperienceProtocol.SendExperienceAsync(player.Connection,
                player.Experience, ct);
            // The output must be committed (including XP payment) before
            // the 12% anvil wear roll can change or destroy its block.
            if (click.Slot == 2 && player.GameMode == PlayerGameMode.Survival &&
                _blocks is not null)
            {
                StationPos position = active.Position;
                ushort current = _blocks.GetBlockState(
                    position.X, position.Y, position.Z);
                if (DurabilityRules.TryWearAnvil(current, out ushort updated) &&
                    _blocks.TrySetBlockState(position.X, position.Y,
                        position.Z, updated, out _))
                {
                    await BroadcastBlockChangeAsync(player, position.X,
                        position.Y, position.Z, updated, ct);
                    if (updated == 0)
                        await BreakStationAsync(position.X, position.Y,
                            position.Z, ct);
                }
            }
        }
        if (!changed || active.Kind is StationKind.Crafting or
            StationKind.Anvil or StationKind.Grindstone) return;
        foreach (ConnectedPlayer observer in _players.Snapshot().Where(p =>
            p.Ready && p.ConnectionId != player.ConnectionId &&
            _stations.GetMenu(p.PlayerUuid)?.Position == active.Position))
        {
            try { await SendStationContentAsync(observer, ct); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private void ReturnCraftingInputs(ConnectedPlayer player, ItemStack[] stacks)
    {
        PlayerPose pose = player.Pose;
        foreach (ItemStack item in stacks)
        {
            int inserted = player.Inventory.GiveStack(item);
            int left = item.Count - inserted;
            if (left > 0 && (_groundItems is null ||
                !_groundItems.TrySpawn(item with { Count = left },
                    pose.X, pose.Y + 0.2, pose.Z, out _)))
                Console.WriteLine($"[station] Unable to return {left} of item {item.ItemId} " +
                    $"for {player.Username}");
        }
    }

    private void CloseStation(ConnectedPlayer player)
    {
        ItemStack[] returned = _stations.Close(player.PlayerUuid);
        ReturnCraftingInputs(player, returned);
    }

    private async Task CloseStationAsync(ConnectedPlayer player,
        bool sendClose, CancellationToken ct)
    {
        StationMenu? active = _stations.GetMenu(player.PlayerUuid);
        if (active is null) return;
        CloseStation(player);
        if (sendClose)
            await StationProtocol.CloseAsync(player.Connection, active.WindowId, ct);
        await InventoryProtocol.SendContentAsync(player.Connection, player.Inventory, ct);
    }

    private async Task BreakStationAsync(int x, int y, int z, CancellationToken ct)
    {
        StationPos pos = new(x, y, z);
        var (contents, viewers) = _stations.Break(pos);
        foreach (ItemStack item in contents)
        {
            if (_groundItems is null || !_groundItems.TrySpawn(item,
                x + 0.5, y + 0.4, z + 0.5, out _))
                Console.WriteLine($"[station] Unable to drop station contents {item.ItemId}");
        }
        foreach (Guid id in viewers)
        {
            ConnectedPlayer? player = _players.Snapshot().FirstOrDefault(p => p.PlayerUuid == id);
            if (player is null) continue;
            try { await CloseStationAsync(player, true, ct); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task RunStationTicksAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(ct))
        {
            StationPos[] changed = _stations.Tick();
            if (changed.Length == 0) continue;
            ConnectedPlayer[] players = _players.Snapshot().Where(p => p.Ready).ToArray();
            foreach (StationPos pos in changed)
            {
                StationState? station = _stations.SnapshotState(pos);
                if (station is null) continue;
                // Keep block's actual lit property in sync with burn state.
                // The Java 26.3 registry stores lit=true before lit=false
                // for each of the four facing variants.
                int start = station.Kind switch
                {
                    StationKind.Furnace => 6883,
                    StationKind.Smoker => 24111,
                    StationKind.BlastFurnace => 24119,
                    _ => -1
                };
                if (start >= 0 && _blocks is not null)
                {
                    ushort current = _blocks.GetBlockState(pos.X, pos.Y, pos.Z);
                    if (WorkstationManager.FromBlockState(current) == station.Kind)
                    {
                        int desired = start + ((current - start) / 2) * 2 +
                            (station.BurnRemaining > 0 ? 0 : 1);
                        if (current != desired &&
                            _blocks.TrySetBlockState(pos.X, pos.Y, pos.Z,
                                (ushort)desired, out _))
                        {
                            int chunkX = (int)Math.Floor(pos.X / 16.0);
                            int chunkZ = (int)Math.Floor(pos.Z / 16.0);
                            foreach (ConnectedPlayer viewer in players)
                            {
                                try
                                {
                                    if (viewer.View is IShardChunkSubscriptions chunks &&
                                        !await chunks.HasLoadedChunkAsync(chunkX, chunkZ, ct)) continue;
                                    await BlockInteractionProtocol.SendBlockUpdateAsync(
                                        viewer.Connection, pos.X, pos.Y, pos.Z,
                                        (ushort)desired, ct);
                                }
                                catch (IOException) { }
                                catch (ObjectDisposedException) { }
                            }
                        }
                    }
                }
                foreach (ConnectedPlayer player in players.Where(p =>
                    _stations.GetMenu(p.PlayerUuid)?.Position == pos))
                {
                    try { await SendStationContentAsync(player, ct); }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }
            }
        }
    }

    /// <summary>Handle Java 26.3 entity right-clicks. The client-supplied EID
    /// is checked against the authoritative Entities manager, reach distance,
    /// and the held stack. Entities owns species and sheared state; Gameplay
    /// owns inventory transactions and physical drops; Core dispatches events.</summary>
    internal async Task HandleEntityInteractionAsync(ConnectedPlayer player,
        EntityInteraction hit, CancellationToken ct)
    {
        if (!player.Ready || _entities is null || _interactions is null ||
            _items is null || _skills is null || hit.Hand != 0)
            return; // Offhand items are not represented by PlayerInventory yet.
        ShardEntityInstance entity = default;
        bool found = false;
        foreach (ShardEntityInstance next in _entities.Snapshot())
            if (next.EntityId == hit.EntityId)
            {
                entity = next;
                found = true;
                break;
            }
        if (!found) return;
        PlayerPose pose = player.Pose;
        double dx = entity.X - pose.X, dz = entity.Z - pose.Z;
        double dy = entity.Y + 0.8 - (pose.Y + 1.62);
        if (dx * dx + dy * dy + dz * dz > 25) return;
        ItemStack held = player.Inventory.Held();
        if (held.Count < 1) return;
        await DispatchItemSkillAsync(player, held.ItemId, "onRightClick", ct);
        string? trigger = null;
        if (_items.TryGetByIdentifier("minecraft:bucket", out var bucket) &&
            _items.TryGetByIdentifier("minecraft:milk_bucket", out var milk) &&
            held.ItemId == bucket.ProtocolId &&
            _interactions.TryMilk(hit.EntityId, out var milked) &&
            player.Inventory.TryConvertHeld(bucket.ProtocolId, milk.ProtocolId))
        {
            entity = milked;
            trigger = "onMilk";
        }
        else if (_groundItems is not null &&
            _items.TryGetByIdentifier("minecraft:shears", out var shears) &&
            held.ItemId == shears.ProtocolId &&
            _interactions.TryShear(hit.EntityId, out var sheared))
        {
            entity = sheared;
            // Reward items are entirely Items skills (dropItem ~onShear).
            // Entities only changes sheep state and exposes the trigger.
            trigger = "onShear";
        }
        if (trigger is null) return;
        await InventoryProtocol.SendContentAsync(player.Connection, player.Inventory, ct);
        string? fallback = _entityTypes is not null &&
            _entityTypes.TryGetById(entity.ProtocolId, out var vanilla)
            ? vanilla.Identifier : null;
        await _skills.DispatchAsync(new ShardSkillEvent(entity.DefinitionId,
            trigger, entity.X, entity.Y, entity.Z, entity.EntityId, fallback), ct);
    }

    internal async Task HandleBlockUseAsync(ConnectedPlayer player,
        BlockUseOn hit, CancellationToken ct)
    {
        if (hit.Hand == 0 && hit.ValidHit &&
            CanEditAt(player, hit.X, hit.Y, hit.Z))
            await DispatchItemSkillAsync(player, player.Inventory.Held().ItemId,
                "onRightClick", ct);
        if (hit.ValidHit && hit.Hand == 0 &&
            CanEditAt(player, hit.X, hit.Y, hit.Z) &&
            WorkstationManager.FromBlockState(
                _blocks!.GetBlockState(hit.X, hit.Y, hit.Z)) is StationKind station)
        {
            await OpenStationAsync(player,
                new StationPos(hit.X, hit.Y, hit.Z), station, ct);
            await BlockInteractionProtocol.SendAckAsync(player.Connection, hit.Sequence, ct);
            return;
        }
        if (await TryHandleToolUseAsync(player, hit, ct)) return;
        var (x, y, z) = BlockInteractionProtocol.Adjacent(hit.X, hit.Y, hit.Z, hit.Face);
        ushort selected = 0;
        bool allowed = hit.ValidHit && hit.Hand == 0 &&
            CanEditAt(player, hit.X, hit.Y, hit.Z) && CanEditAt(player, x, y, z) &&
            player.TryGetSelectedBlock(hit.Face, out selected);
        if (allowed) allowed = _blocks!.GetBlockState(hit.X, hit.Y, hit.Z) != 0 &&
            _blocks.GetBlockState(x, y, z) == 0 && player.TryConsumeEditBudget();
        if (allowed)
        {
            PlayerPose pose = player.Pose;
            // Do not enclose the player's body in a newly placed solid block.
            if (pose.X > x - 0.3 && pose.X < x + 1.3 &&
                pose.Z > z - 0.3 && pose.Z < z + 1.3 &&
                pose.Y < y + 1 && pose.Y + 1.8 > y)
                allowed = false;
        }
        ItemStack held = player.Inventory.Held();
        bool changed = allowed && player.Inventory.Held().Count > 0 &&
            _blocks!.TrySetBlockState(x, y, z, selected, out _);
        if (changed)
        {
            // The world mutation is durable before inventory consumption.
            // Only Survival consumes items; Creative has infinite placement.
            await BroadcastBlockChangeAsync(player, x, y, z, selected, ct);
            if (player.Inventory.ConsumeHeld(player.GameMode == PlayerGameMode.Creative, held.ItemId))
                await InventoryProtocol.SendSlotAsync(player.Connection, player.Inventory,
                    player.Inventory.SelectedSlot, ct);
        }
        else
        {
            // Corrections must precede the sequence acknowledgement. The client
            // predicts placements, but the ack itself carries no accept/reject bit.
            if (CanEditAt(player, hit.X, hit.Y, hit.Z))
                await BlockInteractionProtocol.SendBlockUpdateAsync(player.Connection,
                    hit.X, hit.Y, hit.Z,
                    _blocks!.GetBlockState(hit.X, hit.Y, hit.Z), ct);
            if (CanEditAt(player, x, y, z))
                await BlockInteractionProtocol.SendBlockUpdateAsync(player.Connection,
                    x, y, z, _blocks!.GetBlockState(x, y, z), ct);
        }
        await BlockInteractionProtocol.SendAckAsync(player.Connection, hit.Sequence, ct);
    }

    /// <summary>Server-authoritative right-click tool use. Never spends
    /// durability on invalid interactions or rejected block-state writes.</summary>
    private async Task<bool> TryHandleToolUseAsync(ConnectedPlayer player,
        BlockUseOn hit, CancellationToken ct)
    {
        if (!hit.ValidHit || hit.Hand != 0 || _blocks is null ||
            _items is null || !CanEditAt(player, hit.X, hit.Y, hit.Z))
            return false;
        ItemStack held = player.Inventory.Held();
        if (held.Count != 1 ||
            !_items.TryGetById(held.ItemId, out var definition) ||
            definition.MaxDurability <= 0) return false;
        ushort block = _blocks.GetBlockState(hit.X, hit.Y, hit.Z);
        if (block == 0) return false;
        var (ax, ay, az) = BlockInteractionProtocol.Adjacent(
            hit.X, hit.Y, hit.Z, hit.Face);
        bool adjacentAir = CanEditAt(player, ax, ay, az) &&
            _blocks.GetBlockState(ax, ay, az) == 0;
        bool aboveAir = hit.Y < 319 &&
            _blocks.GetBlockState(hit.X, hit.Y + 1, hit.Z) == 0;
        if (!ToolUseRules.TryUse(definition.Identifier, block, hit.Face,
            aboveAir, adjacentAir, out var action)) return false;

        int bx = action.Adjacent ? ax : hit.X;
        int by = action.Adjacent ? ay : hit.Y;
        int bz = action.Adjacent ? az : hit.Z;
        if (!CanEditAt(player, bx, by, bz) ||
            !player.TryConsumeEditBudget())
        {
            await BlockInteractionProtocol.SendAckAsync(player.Connection,
                hit.Sequence, ct);
            return true;
        }
        bool changed = _blocks.TrySetBlockState(bx, by, bz,
            action.NewState, out _);
        if (changed)
        {
            await BroadcastBlockChangeAsync(player, bx, by, bz,
                action.NewState, ct);
            if (action.Seeds > 0 && _groundItems is not null &&
                _items.TryGetByIdentifier("minecraft:pumpkin_seeds",
                    out var seed))
                _groundItems.TrySpawn(seed.ProtocolId, action.Seeds,
                    hit.X + 0.5, hit.Y + 0.4, hit.Z + 0.5, out _);
        }
        else await BlockInteractionProtocol.SendBlockUpdateAsync(
            player.Connection, bx, by, bz,
            _blocks.GetBlockState(bx, by, bz), ct);
        await BlockInteractionProtocol.SendAckAsync(player.Connection,
            hit.Sequence, ct);
        if (changed && player.GameMode == PlayerGameMode.Survival &&
            player.Inventory.DamageHeld(held.ItemId, 1))
            await InventoryProtocol.SendSlotAsync(player.Connection,
                player.Inventory, player.Inventory.SelectedSlot, ct);
        return true;
    }

    private async Task BroadcastBlockChangeAsync(ConnectedPlayer actor,
        int x, int y, int z, ushort state, CancellationToken ct)
    {
        // Always send the actor the confirmed state BEFORE the sequence ACK.
        // A chunk subscription may still be pending during Play startup, but
        // that must never allow the ACK to restore a predicted broken block.
        await BlockInteractionProtocol.SendBlockUpdateAsync(actor.Connection, x, y, z, state, ct);
        int chunkX = (int)Math.Floor(x / 16.0), chunkZ = (int)Math.Floor(z / 16.0);
        foreach (ConnectedPlayer viewer in _players.Snapshot().Where(p =>
                     p.Ready && p.ConnectionId != actor.ConnectionId))
        {
            try
            {
                if (viewer.View is IShardChunkSubscriptions subscriber &&
                    await subscriber.HasLoadedChunkAsync(chunkX, chunkZ, ct))
                    await BlockInteractionProtocol.SendBlockUpdateAsync(viewer.Connection,
                        x, y, z, state, ct);
            }
            catch (IOException) { /* disconnected viewer */ }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task RunEntitiesAsync(CancellationToken ct)
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await ticker.WaitForNextTickAsync(ct))
            await TickEntitiesAsync(ct);
    }

    /// <summary>Replicate Entities Shard instances to loaded, nearby players.
    /// The manager is authoritative; this loop owns only per-viewer state.</summary>
    internal async Task TickEntitiesAsync(CancellationToken ct)
    {
        if (_entities is null) return;
        // The Entities Shard reports death exactly once; Core dispatches
        // the trigger to registered mechanics without understanding item IDs.
        // The Items Shard owns dropItem and calls Gameplay's world-item sink.
        if (_skills is not null)
        {
            foreach (ShardEntityInstance dead in _entities.DrainDeaths())
            {
                string? fallback = _entityTypes is not null &&
                    _entityTypes.TryGetById(dead.ProtocolId, out var vanilla)
                        ? vanilla.Identifier : null;
                await _skills.DispatchAsync(new ShardSkillEvent(
                    dead.DefinitionId, "onDeath", dead.X, dead.Y, dead.Z,
                    dead.EntityId, fallback), ct);
            }
        }
        ShardEntityInstance[] instances = _entities.Snapshot();
        ConnectedPlayer[] viewers = _players.Snapshot().Where(p => p.Ready).ToArray();
        var online = viewers.Select(p => p.ConnectionId).ToHashSet();
        foreach (Guid id in _entityViews.Keys.ToArray())
            if (!online.Contains(id)) _entityViews.Remove(id);

        foreach (ConnectedPlayer viewer in viewers)
        {
            if (!_entityViews.TryGetValue(viewer.ConnectionId,
                out Dictionary<int, ShardEntityInstance>? seen))
            {
                seen = new Dictionary<int, ShardEntityInstance>();
                _entityViews.Add(viewer.ConnectionId, seen);
            }
            var visible = new HashSet<int>();
            PlayerPose pose = viewer.Pose;
            foreach (ShardEntityInstance entity in instances)
            {
                double dx = entity.X - pose.X, dz = entity.Z - pose.Z;
                if (dx * dx + dz * dz > 48 * 48 || Math.Abs(entity.Y - pose.Y) > 48)
                    continue;
                int cx = (int)Math.Floor(entity.X / 16.0);
                int cz = (int)Math.Floor(entity.Z / 16.0);
                if (viewer.View is IShardChunkSubscriptions chunks &&
                    !await chunks.HasLoadedChunkAsync(cx, cz, ct)) continue;
                visible.Add(entity.EntityId);
                try
                {
                    if (!seen.TryGetValue(entity.EntityId, out ShardEntityInstance previous))
                        await EntityProtocol.SendSpawnAsync(viewer.Connection, entity, ct);
                    else if (previous.X != entity.X || previous.Y != entity.Y ||
                             previous.Z != entity.Z || previous.Yaw != entity.Yaw ||
                             previous.Pitch != entity.Pitch)
                        await EntityProtocol.SendTeleportAsync(viewer.Connection, entity, ct);
                    seen[entity.EntityId] = entity;
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            foreach (int entityId in seen.Keys.ToArray())
            {
                if (visible.Contains(entityId)) continue;
                try { await EntityProtocol.SendRemoveAsync(viewer.Connection, entityId, ct); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                seen.Remove(entityId);
            }
        }
    }

    private async Task RunGroundItemsAsync(CancellationToken ct)
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await ticker.WaitForNextTickAsync(ct))
            await TickGroundItemsAsync(ct);
            await TickExperienceOrbsAsync(ct);
            await TickLivingEntitiesAsync(ct);
            await TickProjectilesAsync(ct);
            await TickFishingAsync(ct);
            await TickWoolRegrowthAsync(ct);
            await TickElytraAsync(ct);
    }

    /// <summary>One authoritative 20Hz tick for loose items. Exposed internally
    /// for regression tests: network packets are still sent via PacketChannel.</summary>
    internal async Task TickGroundItemsAsync(CancellationToken ct)
    {
        if (_groundItems is null || _blocks is null) return;
        _groundItems.Simulate(_blocks);
        GroundItem[] items = _groundItems.Snapshot();
        ConnectedPlayer[] viewers = _players.Snapshot().Where(p => p.Ready).ToArray();
        var online = viewers.Select(p => p.ConnectionId).ToHashSet();
        foreach (Guid id in _itemViews.Keys.ToArray())
            if (!online.Contains(id)) _itemViews.Remove(id);

        // Visibility follows chunk subscriptions. Players entering range or
        // joining after a drop was created receive the complete entity stack.
        foreach (ConnectedPlayer viewer in viewers)
        {
            if (!_itemViews.TryGetValue(viewer.ConnectionId,
                out Dictionary<int, GroundItem>? seen))
            {
                seen = new Dictionary<int, GroundItem>();
                _itemViews.Add(viewer.ConnectionId, seen);
            }
            var visible = new HashSet<int>();
            foreach (GroundItem item in items)
            {
                PlayerPose pose = viewer.Pose;
                double dx = item.X - pose.X, dz = item.Z - pose.Z;
                if (dx * dx + dz * dz > 48 * 48 || Math.Abs(item.Y - pose.Y) > 48)
                    continue;
                int cx = (int)Math.Floor(item.X / 16.0), cz = (int)Math.Floor(item.Z / 16.0);
                if (viewer.View is IShardChunkSubscriptions chunks &&
                    !await chunks.HasLoadedChunkAsync(cx, cz, ct)) continue;
                visible.Add(item.EntityId);
                try
                {
                    if (!seen.TryGetValue(item.EntityId, out GroundItem last))
                        await DroppedItemProtocol.SendSpawnAsync(viewer.Connection, item, ct, _items);
                    else
                    {
                        if (last.Count != item.Count)
                            await DroppedItemProtocol.SendStackAsync(viewer.Connection, item, ct, _items);
                        if (last.X != item.X || last.Y != item.Y || last.Z != item.Z)
                            await DroppedItemProtocol.SendPositionAsync(viewer.Connection, item, ct);
                    }
                    seen[item.EntityId] = item;
                }
                catch (IOException) { /* disconnect handled by Play loop */ }
                catch (ObjectDisposedException) { }
            }
            foreach (int entityId in seen.Keys.ToArray())
            {
                if (visible.Contains(entityId)) continue;
                try { await DroppedItemProtocol.SendRemoveAsync(viewer.Connection, entityId, ct); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                seen.Remove(entityId);
            }
        }

        // Picking up a stack is a real collision, not an instant reward for
        // mining. Full inventories leave all items on the ground.
        foreach (GroundItem item in items)
        {
            foreach (ConnectedPlayer collector in viewers)
            {
                PlayerPose pose = collector.Pose;
                double dx = item.X - pose.X, dz = item.Z - pose.Z;
                if (dx * dx + dz * dz > 1.5 * 1.5 ||
                    Math.Abs(item.Y - (pose.Y + 0.35)) > 1.6) continue;
                if (!_itemViews.TryGetValue(collector.ConnectionId, out var collectorView) ||
                    !collectorView.ContainsKey(item.EntityId)) continue;
                int count = _groundItems.TryCollect(item.EntityId, collector.Inventory,
                    out GroundItem remaining);
                if (count == 0) continue;
                foreach (ConnectedPlayer viewer in viewers)
                {
                    if (!_itemViews.TryGetValue(viewer.ConnectionId, out var seen) ||
                        !seen.ContainsKey(item.EntityId)) continue;
                    try
                    {
                        await DroppedItemProtocol.SendPickupAsync(viewer.Connection,
                            item.EntityId, collector.EntityId, count, ct);
                        if (remaining.Count == 0)
                        {
                            await DroppedItemProtocol.SendRemoveAsync(viewer.Connection,
                                item.EntityId, ct);
                            seen.Remove(item.EntityId);
                        }
                        else
                        {
                            await DroppedItemProtocol.SendStackAsync(viewer.Connection,
                                remaining, ct, _items);
                            seen[item.EntityId] = remaining;
                        }
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }
                try
                {
                    await InventoryProtocol.SendContentAsync(collector.Connection,
                        collector.Inventory, ct);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                await DispatchItemSkillAsync(collector, item.ItemId, "onPickup", ct,
                    count, item.X, item.Y, item.Z);
                if (remaining.Count == 0) break;
            }
        }
    }

    /// <summary>One authoritative orb simulation and pickup pass. Orbs
    /// become visible only in loaded chunks, and Mending is applied only
    /// when the player actually collides with an orb.</summary>
    internal async Task TickExperienceOrbsAsync(CancellationToken ct)
    {
        if (_experienceOrbs is null || _blocks is null) return;
        _experienceOrbs.Simulate(_blocks);
        ExperienceOrb[] orbs = _experienceOrbs.Snapshot();
        ConnectedPlayer[] viewers = _players.Snapshot()
            .Where(p => p.Ready).ToArray();
        HashSet<Guid> online = viewers.Select(x => x.ConnectionId).ToHashSet();
        foreach (Guid id in _experienceViews.Keys.ToArray())
            if (!online.Contains(id)) _experienceViews.Remove(id);

        foreach (ConnectedPlayer viewer in viewers)
        {
            if (!_experienceViews.TryGetValue(viewer.ConnectionId,
                out Dictionary<int, ExperienceOrb>? seen))
            {
                seen = new Dictionary<int, ExperienceOrb>();
                _experienceViews.Add(viewer.ConnectionId, seen);
            }
            var visible = new HashSet<int>();
            foreach (ExperienceOrb orb in orbs)
            {
                PlayerPose pose = viewer.Pose;
                double dx = orb.X - pose.X, dz = orb.Z - pose.Z;
                if (dx * dx + dz * dz > 48 * 48 ||
                    Math.Abs(orb.Y - pose.Y) > 48) continue;
                int cx = (int)Math.Floor(orb.X / 16),
                    cz = (int)Math.Floor(orb.Z / 16);
                if (viewer.View is IShardChunkSubscriptions chunks &&
                    !await chunks.HasLoadedChunkAsync(cx, cz, ct)) continue;
                visible.Add(orb.EntityId);
                try
                {
                    if (!seen.TryGetValue(orb.EntityId, out var previous))
                        await ExperienceProtocol.SendOrbAsync(
                            viewer.Connection, orb, ct);
                    else if (previous.X != orb.X || previous.Y != orb.Y ||
                             previous.Z != orb.Z)
                        await ExperienceProtocol.SendPositionAsync(
                            viewer.Connection, orb, ct);
                    seen[orb.EntityId] = orb;
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            foreach (int id in seen.Keys.ToArray())
            {
                if (visible.Contains(id)) continue;
                try
                {
                    await DroppedItemProtocol.SendRemoveAsync(
                        viewer.Connection, id, ct);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                seen.Remove(id);
            }
        }

        foreach (ExperienceOrb orb in orbs)
        {
            foreach (ConnectedPlayer collector in viewers)
            {
                PlayerPose pose = collector.Pose;
                double dx = orb.X - pose.X, dz = orb.Z - pose.Z;
                if (dx * dx + dz * dz > 1.5 * 1.5 ||
                    Math.Abs(orb.Y - (pose.Y + 0.35)) > 1.6) continue;
                if (!_experienceViews.TryGetValue(collector.ConnectionId,
                    out var visible) || !visible.ContainsKey(orb.EntityId))
                    continue;
                int xp = _experienceOrbs.TryCollect(orb.EntityId);
                if (xp == 0) continue;
                var (remaining, repairedSlot) =
                    collector.Inventory.RepairFromExperience(xp);
                if (remaining > 0) collector.Experience.Award(remaining);
                foreach (ConnectedPlayer viewer in viewers)
                {
                    if (!_experienceViews.TryGetValue(viewer.ConnectionId,
                        out var seen) || !seen.Remove(orb.EntityId)) continue;
                    try
                    {
                        await ExperienceProtocol.SendPickupAsync(
                            viewer.Connection, orb, collector, ct);
                        await DroppedItemProtocol.SendRemoveAsync(
                            viewer.Connection, orb.EntityId, ct);
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }
                try
                {
                    if (repairedSlot >= 0)
                        await InventoryProtocol.SendSlotAsync(
                            collector.Connection, collector.Inventory,
                            repairedSlot, ct);
                    await ExperienceProtocol.SendExperienceAsync(
                        collector.Connection, collector.Experience, ct);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                break;
            }
        }
    }

    private static void ApplyClientInformation(ConnectedPlayer player, Packet packet)
    {
        PacketReader reader = packet.Reader();
        _ = reader.ReadString(16);
        _ = reader.ReadByte();
        player.SetChatMode(reader.ReadVarInt());
    }

    private async Task HandleChatAsync(ConnectedPlayer player, IncomingChat incoming, CancellationToken ct)
    {
        if (!_options.ChatEnabled)
        {
            await ReplyAsync(player, "Chat is disabled on this server.", ct);
            return;
        }
        if (!ChatProtocol.IsSafeMessage(incoming.Message, _options.ChatMaxLength))
        {
            await ReplyAsync(player, "Invalid or oversized chat message.", ct);
            return;
        }
        if (!player.TryConsumeChatCooldown(_options.ChatCooldownMilliseconds))
        {
            await ReplyAsync(player, "You're sending messages too quickly.", ct);
            return;
        }
        // Do not claim authenticated chat signatures: this is an unsigned system
        // message attributed to a server-authenticated account UUID.
        string text = $"<{player.Username}> {incoming.Message}";
        Console.WriteLine($"[chat] {text}");
        await BroadcastChatAsync(text, ct);
    }

    private async Task BroadcastChatAsync(string message, CancellationToken ct)
    {
        foreach (ConnectedPlayer target in _players.Snapshot().Where(p => p.Ready && p.ChatMode == 0))
        {
            try { await ChatProtocol.SendSystemAsync(target.Connection, message, ct); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private static Task ReplyAsync(ConnectedPlayer player, string message, CancellationToken ct) =>
        ChatProtocol.SendSystemAsync(player.Connection, $"[Server] {message}", ct);

    private ConnectedPlayer? FindOnline(string username) => _players.Snapshot().FirstOrDefault(p =>
        p.Ready && string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));

    private int Level(ConnectedPlayer player) => _options.OnlineMode ? _operators.Level(player.PlayerUuid) : 0;

    private async Task ExecuteCommandAsync(ConnectedPlayer player, string command, CancellationToken ct)
    {
        if (command.Length > 512 || command.Any(c => char.IsControl(c)))
        {
            await ReplyAsync(player, "Invalid command.", ct);
            return;
        }
        string[] split = command.TrimStart('/').Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 0) return;
        string verb = split[0].ToLowerInvariant();
        string argument = split.Length == 2 ? split[1].Trim() : "";
        if (!_options.ChatEnabled && (verb is "msg" or "tell" or "w" or "me" or "say"))
        {
            await ReplyAsync(player, "Chat is disabled on this server.", ct);
            return;
        }

        int level = Level(player);
        int minimum = verb switch
        {
            "gamemode" => GameModeProtocol.RequiredOpLevel,
            "say" or "kick" or "oplist" or "enchant" or "summon" => 2,
            "op" or "deop" => 4,
            _ => 0
        };
        if (level < minimum)
        {
            await ReplyAsync(player, "You don't have permission to use that command.", ct);
            return;
        }
        switch (verb)
        {
            case "help":
                await ReplyAsync(player, "Commands: /help /list /whoami /msg /me" +
                    (level >= 1 ? " /gamemode" : "") +
                    (level >= 2 ? " /say /kick /oplist /enchant /summon" : "") +
                    (level >= 4 ? " /op /deop" : ""), ct);
                break;
            case "list":
                var online = _players.Snapshot().Where(p => p.Ready).Select(p => p.Username).ToArray();
                await ReplyAsync(player, $"Online ({online.Length}): {string.Join(", ", online)}", ct);
                break;
            case "whoami":
                await ReplyAsync(player, $"{player.Username} | UUID {player.PlayerUuid} | permission level {level}", ct);
                break;
            case "gamemode":
            {
                if (string.IsNullOrWhiteSpace(argument))
                {
                    // No vanilla server packet can open this local debug screen.
                    // Advertise gamemode as executable without arguments so
                    // users can discover the F3+F4 switcher via /gamemode.
                    await ReplyAsync(player, "Hold F3 and press F4 to open the game-mode switcher. " +
                        "Tap F4 to choose, then release F3 to apply. " +
                        "Or use /gamemode <mode> [player].", ct);
                    break;
                }
                if (!GameModeProtocol.TryParseArguments(argument, out PlayerGameMode mode,
                    out string? targetName))
                {
                    await ReplyAsync(player,
                        "Usage: /gamemode <survival|creative|adventure|spectator> [online player]", ct);
                    break;
                }
                ConnectedPlayer? target = targetName is null ? player : FindOnline(targetName);
                if (target is null)
                {
                    await ReplyAsync(player, "Player not found or not fully connected.", ct);
                    break;
                }
                await ChangeGameModeAsync(target, mode, player, ct);
                break;
            }
            case "enchant":
                await ReplyAsync(player, await EnchantHeldAsync(argument, ct), ct);
                break;
            case "summon":
                await ReplyAsync(player,
                    argument == "sheep" && SummonSheepAt(player)
                        ? "Summoned a persistent sheep near you."
                        : "Usage: /summon sheep (maximum 512 per world).", ct);
                break;
            case "msg":
            case "tell":
            case "w":
            {
                string[] pm = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (pm.Length != 2 || !ChatProtocol.IsSafeMessage(pm[1], _options.ChatMaxLength))
                { await ReplyAsync(player, "Usage: /msg <player> <message>", ct); break; }
                ConnectedPlayer? target = FindOnline(pm[0]);
                if (target is null) { await ReplyAsync(player, "Player not found.", ct); break; }
                if (!player.TryConsumeChatCooldown(_options.ChatCooldownMilliseconds))
                { await ReplyAsync(player, "You're sending messages too quickly.", ct); break; }
                await ChatProtocol.SendSystemAsync(target.Connection, $"[From {player.Username}] {pm[1]}", ct);
                if (target != player)
                    await ChatProtocol.SendSystemAsync(player.Connection, $"[To {target.Username}] {pm[1]}", ct);
                break;
            }
            case "me":
                if (!ChatProtocol.IsSafeMessage(argument, _options.ChatMaxLength))
                { await ReplyAsync(player, "Usage: /me <action>", ct); break; }
                if (!player.TryConsumeChatCooldown(_options.ChatCooldownMilliseconds))
                { await ReplyAsync(player, "You're sending messages too quickly.", ct); break; }
                await BroadcastChatAsync($"* {player.Username} {argument}", ct);
                break;
            case "say":
                if (!ChatProtocol.IsSafeMessage(argument, _options.ChatMaxLength))
                { await ReplyAsync(player, "Usage: /say <message>", ct); break; }
                await BroadcastChatAsync($"[Server: {player.Username}] {argument}", ct);
                break;
            case "oplist":
                await ReplyAsync(player, "Operators: " + string.Join(", ",
                    _operators.Snapshot().Select(o => $"{o.Name} (level {o.Level})")), ct);
                break;
            case "op":
            {
                if (!TryParseOpArgument(argument, out string targetName, out int targetLevel))
                { await ReplyAsync(player, "Usage: /op <online player> [level 1-4]", ct); break; }
                await GrantOperatorAsync(targetName, targetLevel, player, ct);
                break;
            }
            case "deop":
                if (argument.Length == 0 || argument.Contains(' '))
                { await ReplyAsync(player, "Usage: /deop <name|uuid>", ct); break; }
                await RevokeOperatorAsync(argument, player, ct);
                break;
            case "kick":
            {
                string[] kick = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (kick.Length == 0) { await ReplyAsync(player, "Usage: /kick <player> [reason]", ct); break; }
                ConnectedPlayer? target = FindOnline(kick[0]);
                if (target is null) { await ReplyAsync(player, "Player not found.", ct); break; }
                if (target == player || Level(target) >= level)
                { await ReplyAsync(player, "Cannot kick yourself or an equal/higher-level operator.", ct); break; }
                string reason = kick.Length == 2 && ChatProtocol.IsSafeMessage(kick[1], 256) ? kick[1] : "Kicked by an operator.";
                await KickAsync(target, reason);
                await ReplyAsync(player, $"Kicked {target.Username}.", ct);
                break;
            }
            default:
                await ReplyAsync(player, $"Unknown command: /{verb}", ct);
                break;
        }
    }

    private async Task<string> EnchantHeldAsync(string argument, CancellationToken ct)
    {
        string[] words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 3)
            return "Usage: enchant <online-player> <unbreaking|mending> [level]";
        ConnectedPlayer? target = FindOnline(words[0]);
        if (target is null) return "Player not found or not fully connected.";
        string enchantment = words[1].ToLowerInvariant();
        if (enchantment.StartsWith("minecraft:", StringComparison.Ordinal))
            enchantment = enchantment["minecraft:".Length..];
        int level = 1;
        if (words.Length == 3 && !int.TryParse(words[2], out level))
            return "Invalid enchantment level.";
        if ((enchantment == "mending" && level != 1) ||
            (enchantment == "unbreaking" && level is < 1 or > 3) ||
            enchantment is not ("mending" or "unbreaking"))
            return "Supported enchantments: Mending I and Unbreaking I–III.";
        ItemStack held = target.Inventory.Held();
        bool applied = enchantment == "mending"
            ? target.Inventory.ConfigureHeld(
                held.Unbreaking, true, held.Unbreakable)
            : target.Inventory.ConfigureHeld(
                level, held.Mending, held.Unbreakable);
        if (!applied)
            return "Target must hold a damageable item; the requested enchantment may already be applied.";
        await InventoryProtocol.SendSlotAsync(target.Connection,
            target.Inventory, target.Inventory.SelectedSlot, ct);
        await ReplyAsync(target,
            $"Your held item received {enchantment} {level}.", ct);
        return $"Applied {enchantment} {level} to {target.Username}'s held item.";
    }

    private async Task ChangeGameModeAsync(ConnectedPlayer target, PlayerGameMode mode,
        ConnectedPlayer actor, CancellationToken ct)
    {
        if (target.GameMode == mode)
        {
            await ReplyAsync(actor, $"{target.Username} is already in {mode.ToString().ToLowerInvariant()} mode.", ct);
            return;
        }
        target.SetGameMode(mode);
        await GameModeProtocol.SendLocalChangeAsync(target.Connection, mode, ct);
        foreach (ConnectedPlayer observer in _players.Snapshot().Where(p => p.Ready))
            await GameModeProtocol.SendTabUpdateAsync(observer.Connection, target.PlayerUuid, mode, ct);
        string message = $"Set {target.Username}'s game mode to {mode.ToString().ToLowerInvariant()}.";
        await ReplyAsync(actor, message, ct);
        if (actor != target)
            await ReplyAsync(target, $"Your game mode was set to {mode.ToString().ToLowerInvariant()} by {actor.Username}.", ct);
        Console.WriteLine($"[gamemode] {actor.Username} -> {target.Username}: {mode}");
    }

    private static bool TryParseOpArgument(string raw, out string username, out int level)
    {
        string[] fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        username = fields.Length > 0 ? fields[0] : "";
        level = 4;
        return fields.Length is >= 1 and <= 2 && username.Length <= 16 &&
            (fields.Length == 1 || (int.TryParse(fields[1], out level) && level is >= 1 and <= 4));
    }

    private async Task GrantOperatorAsync(string username, int level, ConnectedPlayer? actor, CancellationToken ct)
    {
        if (!_options.OnlineMode)
        {
            if (actor is not null) await ReplyAsync(actor, "OP permissions require onlineMode: true.", ct);
            else Console.WriteLine("[op] Online authentication must be enabled to assign permissions.");
            return;
        }
        ConnectedPlayer? target = FindOnline(username);
        if (target is null)
        {
            if (actor is not null) await ReplyAsync(actor, "The player must be online and authenticated to grant OP.", ct);
            else Console.WriteLine("[op] Player must be online and authenticated before granting OP.");
            return;
        }
        bool changed = _operators.TryGrant(target.PlayerUuid, target.Username, level, out string response);
        Console.WriteLine($"[op] {response}");
        if (actor is not null) await ReplyAsync(actor, response, ct);
        if (changed)
        {
            await GameModeProtocol.SendClientPermissionsAsync(target.Connection, target.EntityId, level, ct);
            await CommandTree.SendAsync(target.Connection, level, ct);
            await ReplyAsync(target, $"Your operator level is now {level}. " +
                "Use F3+F4 to switch game modes.", ct);
        }
    }

    private async Task RevokeOperatorAsync(string userOrUuid, ConnectedPlayer? actor, CancellationToken ct)
    {
        bool changed = _operators.TryRevoke(userOrUuid, out Guid uuid, out string response);
        Console.WriteLine($"[op] {response}");
        if (actor is not null) await ReplyAsync(actor, response, ct);
        if (changed)
        {
            ConnectedPlayer? target = _players.Snapshot().FirstOrDefault(p => p.Ready && p.PlayerUuid == uuid);
            if (target is not null)
            {
                await GameModeProtocol.SendClientPermissionsAsync(target.Connection, target.EntityId, 0, ct);
                await CommandTree.SendAsync(target.Connection, 0, ct);
                await ReplyAsync(target, "Your operator privileges were removed.", ct);
            }
        }
    }

    private static async Task KickAsync(ConnectedPlayer target, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await ChatProtocol.SendDisconnectAsync(target.Connection, reason, timeout.Token); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        finally { target.RequestDisconnect(); }
    }

    public async Task ExecuteConsoleCommandAsync(string line, CancellationTokenSource shutdown,
        ServerConsole? terminal, CancellationToken stop)
    {
        // The console is trusted, but still enforce syntax/length limits.
        if (line.Length > ConsoleLineEditor.MaxCommandLength ||
            line.Any(c => char.IsControl(c)))
        {
            Console.WriteLine("[WARN] Invalid console command.");
            return;
        }
        string[] fields = line.Trim().TrimStart('/').Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0) return;
        string argument = fields.Length == 2 ? fields[1].Trim() : "";
        switch (fields[0].ToLowerInvariant())
        {
            case "op":
                if (TryParseOpArgument(argument, out string name, out int level))
                    await GrantOperatorAsync(name, level, null, stop);
                else Console.WriteLine("Usage: op <online player> [level 1-4]");
                break;
            case "deop":
                if (argument.Length > 0 && !argument.Contains(' '))
                    await RevokeOperatorAsync(argument, null, stop);
                else Console.WriteLine("Usage: deop <name|uuid>");
                break;
            case "enchant":
                Console.WriteLine("[enchant] " +
                    await EnchantHeldAsync(argument, stop));
                break;
            case "summon":
            {
                string[] words = argument.Split(' ',
                    StringSplitOptions.RemoveEmptyEntries);
                ConnectedPlayer? target = words.Length == 2 ?
                    FindOnline(words[1]) : _players.Snapshot()
                    .FirstOrDefault(p => p.Ready);
                Console.WriteLine(words.Length is >= 1 and <= 2 &&
                    words[0] == "sheep" && target is not null &&
                    SummonSheepAt(target)
                        ? "[entities] Summoned a sheep near " + target.Username
                        : "Usage: summon sheep [online player]");
                break;
            }
            case "say":
                if (ChatProtocol.IsSafeMessage(argument, _options.ChatMaxLength))
                {
                    Console.WriteLine($"[chat] [Server] {argument}");
                    await BroadcastChatAsync($"[Server] {argument}", stop);
                }
                else Console.WriteLine("Usage: say <message>");
                break;
            case "list":
                Console.WriteLine("Online: " + string.Join(", ",
                    _players.Snapshot().Where(p => p.Ready).Select(p => p.Username)));
                break;
            case "oplist":
                Console.WriteLine("Operators: " + string.Join(", ",
                    _operators.Snapshot().Select(p => $"{p.Name} (level {p.Level})")));
                break;
            case "kick":
            {
                string[] parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    Console.WriteLine("Usage: kick <online player> [reason]");
                    break;
                }
                ConnectedPlayer? target = FindOnline(parts[0]);
                if (target is null)
                {
                    Console.WriteLine("[WARN] Player not found or not ready.");
                    break;
                }
                string reason = parts.Length == 2 && ChatProtocol.IsSafeMessage(parts[1], 256)
                    ? parts[1] : "Kicked by the server console.";
                await KickAsync(target, reason);
                Console.WriteLine($"[server] Kicked {target.Username}: {reason}");
                break;
            }
            case "perf":
                Console.WriteLine(_world.GetPerformanceReport());
                Console.WriteLine($"[perf] caches: {_world.GetCacheSummary()}");
                break;
            case "clear": terminal?.Clear(); break;
            case "help":
                Console.WriteLine("Console commands: op <player> [1-4], deop <name|uuid>, " +
                    "kick <player> [reason], say <message>, list, oplist, perf, clear, stop");
                break;
            case "stop":
                Console.WriteLine("[server] Shutdown requested from console.");
                shutdown.Cancel();
                break;
            default:
                Console.WriteLine($"Unknown console command '{fields[0]}'. Type 'help'.");
                break;
        }
    }

    private async Task BroadcastMovementAsync(ConnectedPlayer subject, PlayerPose previous,
        PlayerPose current, CancellationToken ct)
    {
        foreach (ConnectedPlayer other in _players.Snapshot().Where(p => p.Ready && p != subject))
        {
            try { await PlayerPackets.SendMotionAsync(other.Connection, subject.EntityId, previous, current, ct); }
            catch (IOException) { /* peer will be removed by its handler */ }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task NotifyDepartureAsync(ConnectedPlayer departed)
    {
        if (!departed.Ready) return;
        await BroadcastChatAsync($"[Server] {departed.Username} left the game", CancellationToken.None);
        foreach (ConnectedPlayer other in _players.Snapshot().Where(p => p.Ready))
        {
            try { await PlayerPackets.SendGoneAsync(other.Connection, departed, CancellationToken.None); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    // All players observe the same clock. Each client interpolates its sun/moon
    // between syncs using the rate included in the 26.3 clock entry.
    private async Task BroadcastWorldTimeAsync(CancellationToken stop)
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(5));
        int steps = 0;
        DateTime nextPerf = DateTime.UtcNow.AddSeconds(_options.PerformanceReportSeconds);
        try
        {
            while (await ticker.WaitForNextTickAsync(stop))
            {
                foreach (ConnectedPlayer player in _players.Snapshot())
                {
                    if (!player.Ready) continue;
                    try { await _clock.SendAsync(player.Connection, stop); }
                    catch (IOException) { /* connection cleanup is handled by its owner */ }
                    catch (ObjectDisposedException) { }
                }
                if (++steps % 12 == 0) _clock.Save();
                if (_options.PerformanceReportSeconds > 0 && DateTime.UtcNow >= nextPerf)
                {
                    Console.WriteLine(_world.GetPerformanceReport());
                    nextPerf = DateTime.UtcNow.AddSeconds(_options.PerformanceReportSeconds);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    /// <summary>World item effect for Core skill executors. The Gameplay
    /// Shard continues to own dropped-item physics, pickup and persistence.</summary>
    public bool TryDropItem(int itemId, int count, double x, double y, double z) =>
        _groundItems is not null && _groundItems.TrySpawn(itemId, count,
            x, y, z, out _);

}
