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
    public ConnectedPlayer(Guid id, string name, ProfileProperty[] properties, int entityId,
        PacketChannel connection, IShardWorld world)
    {
        PlayerUuid = id; Username = name; Properties = properties;
        EntityId = entityId; Connection = connection; View = world.CreateView();
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
