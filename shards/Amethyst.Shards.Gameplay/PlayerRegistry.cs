using System.Security.Cryptography;
using System.Text;
using Amethyst.Shards;

namespace Amethyst;

public sealed class ConnectedPlayer : IShardPlayer
{
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public Guid PlayerUuid { get; }
    public string Username { get; }
    public ProfileProperty[] Properties { get; }
    public int EntityId { get; }
    public PacketChannel Connection { get; }
    public IShardChunkView View { get; }
    private int _gameMode = (int)PlayerGameMode.Creative; // historical default for this prototype
    public PlayerGameMode GameMode => (PlayerGameMode)Volatile.Read(ref _gameMode);
    public void SetGameMode(PlayerGameMode mode)
    {
        if ((int)mode is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(mode));
        Volatile.Write(ref _gameMode, (int)mode);
        CancelMining();
    }
    public PlayerInventory Inventory { get; }
    private long _editWindowStarted;
    private int _editCount;

    public void SelectHotbar(int index) => Inventory.SelectHotbar(index);

    public bool SetCreativeSlot(int slot, int itemId, int count, bool plainItem) =>
        GameMode == PlayerGameMode.Creative &&
        Inventory.TryCreativeSet(slot, itemId, count, plainItem);

    public bool TryGetSelectedBlock(int face, out ushort blockState) =>
        CreativeBlockPalette.TryGetState(Inventory.Held().ItemId, face, out blockState);

    // A vanilla 26.3 client emits 0x0D at the end of every gameplay tick.
    // Count these in stream order so stalled packet processing does not make
    // an entire real dig look like START and STOP in the same millisecond.
    private long _clientTicks;
    public void AdvanceClientTick() => _clientTicks++;
    private (int X, int Y, int Z, ushort State, int HeldItem, long Started,
        int RequiredMs, long ClientTicksAtStart)? _mining;
    public void StartMining(int x, int y, int z, ushort state, int itemId, int requiredMs) =>
        _mining = (x, y, z, state, itemId, Environment.TickCount64, requiredMs, _clientTicks);
    public void CancelMining() => _mining = null;
    public bool FinishMining(int x, int y, int z, ushort state, int heldItem,
        out string reason)
    {
        var mining = _mining;
        _mining = null;
        if (mining is not { } started)
        {
            reason = "no START recorded";
            return false;
        }
        if (started.X != x || started.Y != y || started.Z != z)
        {
            reason = $"different target ({started.X},{started.Y},{started.Z})";
            return false;
        }
        if (started.State != state)
        {
            reason = $"block changed (started={started.State}, now={state})";
            return false;
        }
        if (started.HeldItem != heldItem)
        {
            reason = $"held item changed (started={started.HeldItem}, now={heldItem})";
            return false;
        }
        // Vanilla accepts STOP at 70% progress. Leave one 50 ms tick of
        // allowance for the client's first mining tick and packet scheduling.
        long elapsed = Math.Max(0, Environment.TickCount64 - started.Started);
        long ticks = Math.Max(0, _clientTicks - started.ClientTicksAtStart);
        long threshold = ((long)started.RequiredMs * 7 + 9) / 10;
        // The stream is ordered, even if CPU/IO blocked the receive loop for
        // an entire dig. The client has already counted these 50ms ticks.
        // Include the initial mining tick, exactly as the vanilla client does.
        // Retain wall-clock progress for protocol clients without tick markers.
        long progressed = Math.Max(elapsed + 50, (ticks + 1) * 50);
        if (progressed < threshold)
        {
            reason = $"too early (wall={elapsed}ms, clientTicks={ticks}, threshold={threshold}ms, expected={started.RequiredMs}ms)";
            return false;
        }
        reason = $"complete (wall={elapsed}ms, clientTicks={ticks}, expected={started.RequiredMs}ms)";
        return true;
    }

    public bool TryConsumeEditBudget()
    {
        long now = Environment.TickCount64;
        if (now - _editWindowStarted >= 1000)
        {
            _editWindowStarted = now;
            _editCount = 0;
        }
        return ++_editCount <= 64;
    }

    private int _chatMode;
    private long _lastChatAt;
    private readonly CancellationTokenSource _disconnect = new();
    public CancellationToken DisconnectToken => _disconnect.Token;
    public void RequestDisconnect() => _disconnect.Cancel();
    public int ChatMode => Volatile.Read(ref _chatMode);
    public void SetChatMode(int value) { if (value is >= 0 and <= 2) Volatile.Write(ref _chatMode, value); }
    public bool TryConsumeChatCooldown(int milliseconds)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastChatAt);
        if (now - previous < milliseconds) return false;
        Interlocked.Exchange(ref _lastChatAt, now);
        return true;
    }
    private int _ready;
    private readonly object _poseLock = new();
    private PlayerPose _pose = new(0.5, 80, 0.5, 0, 0, true);
    public PlayerPose Pose { get { lock (_poseLock) return _pose; } }
    public bool Ready => Volatile.Read(ref _ready) != 0;
    public void MarkReady() => Interlocked.Exchange(ref _ready, 1);
    public PlayerPose SetPose(PlayerPose pose)
    {
        lock (_poseLock) { PlayerPose previous = _pose; _pose = pose; return previous; }
    }

    // Serialize against movement updates so background saves can never write
    // an older snapshot after a newer one has been committed on logout.
    public void PersistPosition(PlayerPositionStore positions)
    {
        lock (_poseLock)
        {
            if (Ready) positions.Save(PlayerUuid, _pose);
        }
    }
    public ConnectedPlayer(Guid id, string name, ProfileProperty[] properties, int entityId,
        PacketChannel connection, IShardWorld world, string? inventoryDirectory = null)
    {
        PlayerUuid = id; Username = name; Properties = properties;
        EntityId = entityId; Connection = connection; View = world.CreateView();
        Inventory = new PlayerInventory(inventoryDirectory, id);
    }
}

public sealed class PlayerRegistry
{
    private readonly Dictionary<Guid, ConnectedPlayer> _players = new();
    private readonly object _sync = new();
    private readonly int _maxPlayers;
    private int _nextEntityId;
    public PlayerRegistry(int maxPlayers) => _maxPlayers = maxPlayers;
    public int AllocateEntityId() => Interlocked.Increment(ref _nextEntityId);
    public bool TryAdd(ConnectedPlayer player, out string reason)
    {
        lock (_sync)
        {
            if (_players.Count >= _maxPlayers) { reason = "Server is full."; return false; }
            if (_players.Values.Any(p => p.PlayerUuid == player.PlayerUuid ||
                string.Equals(p.Username, player.Username, StringComparison.OrdinalIgnoreCase)))
            { reason = "That player is already connected."; return false; }
            _players.Add(player.ConnectionId, player); reason = ""; return true;
        }
    }
    public void Remove(Guid id) { lock (_sync) _players.Remove(id); }
    public ConnectedPlayer[] Snapshot() { lock (_sync) return _players.Values.ToArray(); }

    public static Guid OfflineUuid(string username)
    {
        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest, bigEndian: true);
    }
}
