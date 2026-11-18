using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

public sealed class GameplayShard : IShard, IShardGameplay
{
    private IShardContext? _context;
    private MinecraftServer? _gameplay;

    public Task OnLoadAsync(IShardContext context, CancellationToken ct)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnEnableAsync(CancellationToken ct)
    {
        var context = _context ?? throw new InvalidOperationException("Shard not loaded.");
        IShardWorld world = context.GetRequiredService<IShardWorld>();
        var game = new MinecraftServer(context.Options, world,
            context.GetRequiredService<IShardPacketBus>(), context.GetService<IShardBlockWorld>());
        game.Start();
        _gameplay = game;
        context.RegisterService<IShardGameplay>(this);
        context.Log("Gameplay handlers, roles, chat, movement, and clock enabled.");
        return Task.CompletedTask;
    }

    public async Task OnDisableAsync(CancellationToken ct)
    {
        if (_gameplay is not null)
        {
            await _gameplay.StopAsync();
            _gameplay = null;
        }
    }

    private MinecraftServer Active => _gameplay ??
        throw new InvalidOperationException("Gameplay Shard is not enabled.");

    public ShardPlayerSummary[] OnlinePlayers => Active.OnlinePlayers;
    public bool TryAdmit(VerifiedProfile profile, PacketChannel channel, int chatMode, out string reason) =>
        Active.TryAdmit(profile, channel, chatMode, out reason);
    public void SetChatMode(Guid uuid, int chatMode) => Active.SetChatMode(uuid, chatMode);
    public Task RunPlayAsync(VerifiedProfile profile, CancellationToken ct) => Active.RunPlayAsync(profile, ct);
    public Task OnDisconnectAsync(Guid uuid) => Active.OnDisconnectedAsync(uuid);
    public Task ExecuteConsoleAsync(string line, CancellationTokenSource shutdown, ServerConsole? terminal,
        CancellationToken ct) => Active.ExecuteConsoleCommandAsync(line, shutdown, terminal, ct);
    public IReadOnlyList<string> CompleteConsole(string prefix) => Active.CompleteConsole(prefix);
}
