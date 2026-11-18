using Amethyst;
using System.Collections.Generic;

namespace Amethyst.Shards;

/// <summary>Public ABI for Amethyst 1.x Shards. A Shard is loaded once; enable/disable
/// is a lifecycle transition, not an assembly unload. Keep all game state in Shards.</summary>
public interface IShard
{
    Task OnLoadAsync(IShardContext context, CancellationToken ct);
    Task OnEnableAsync(CancellationToken ct);
    Task OnDisableAsync(CancellationToken ct);
}

public interface IShardContext
{
    string Id { get; }
    ServerOptions Options { get; }
    string DataDirectory { get; }
    void RegisterService<T>(T service) where T : class;
    T GetRequiredService<T>() where T : class;
    T? GetService<T>() where T : class;
    void Log(string message);
    void RegisterPlayPacketHandler(int packetId, ShardPlayPacketHandler handler);
    void RegisterConsoleCommand(string name, ShardConsoleCommand handler);
}

/// <summary>Safe view of an authenticated Play-state session for Shards.</summary>
public interface IShardPlayer
{
    Guid PlayerUuid { get; }
    string Username { get; }
    PacketChannel Connection { get; }
}

/// <returns>True if the packet was consumed by the Shard.</returns>
public delegate Task<bool> ShardPlayPacketHandler(IShardPlayer player, Packet packet, CancellationToken ct);
public delegate Task ShardConsoleCommand(string arguments, CancellationToken ct);

public interface IShardPacketBus
{
    Task<bool> DispatchAsync(IShardPlayer player, Packet packet, CancellationToken ct);
}

public readonly record struct ShardPlayerSummary(string Username, Guid Uuid);

/// <summary>The world's public integration surface. Alternative world-generation
/// Shards can implement this without changing Amethyst's network core.</summary>
public interface IShardWorld : IAsyncDisposable
{
    string DimensionId { get; }
    long Seed { get; }
    int SeaLevel { get; }
    int ViewDistance { get; }
    int MinY { get; }
    bool UsesDensity { get; }
    double SpawnXPosition { get; }
    double SpawnYPosition { get; }
    double SpawnZPosition { get; }
    int GetGroundHeight(int x, int z);
    /// <summary>Read the authoritative global state ID (air = 0).</summary>
    ushort GetBlockState(int x, int y, int z);
    /// <summary>Atomically replace a block, returning false for no-op/out-of-bounds.</summary>
    bool TrySetBlockState(int x, int y, int z, ushort state, out ushort previousState);
    IShardChunkView CreateView();
    string GetPerformanceReport();
    string GetCacheSummary();
}

/// <summary>Chunk streaming remains protocol-agnostic from the server host's
/// perspective. The world Shard owns generation, lighting, and streaming.</summary>
public interface IShardChunkView
{
    Task UpdateAsync(PacketChannel connection, PlayerPose pose, CancellationToken ct);
    Task OnBatchReceivedAsync(PacketChannel connection, float chunksPerTick, CancellationToken ct);
    /// <summary>Whether this connection has received the target chunk.</summary>
    bool HasLoadedChunk(int chunkX, int chunkZ);
}

/// <summary>One enabled gameplay Shard is responsible for Play-state behavior.
/// Core exclusively owns socket accept, auth, handshake, login, configuration,
/// packet framing, and encryption.</summary>
public interface IShardGameplay
{
    ShardPlayerSummary[] OnlinePlayers { get; }
    bool TryAdmit(VerifiedProfile profile, PacketChannel channel, int chatMode, out string reason);
    void SetChatMode(Guid uuid, int chatMode);
    Task RunPlayAsync(VerifiedProfile profile, CancellationToken ct);
    Task OnDisconnectAsync(Guid uuid);
    Task ExecuteConsoleAsync(string line, CancellationTokenSource shutdown, ServerConsole? terminal, CancellationToken ct);
    IReadOnlyList<string> CompleteConsole(string prefix);
}
