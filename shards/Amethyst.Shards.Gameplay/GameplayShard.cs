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
        IShardItemRegistry items = context.GetRequiredService<IShardItemRegistry>();
        IShardRecipeRegistry recipes = context.GetRequiredService<IShardRecipeRegistry>();
        IShardEntityManager entities = context.GetRequiredService<IShardEntityManager>();
        IShardEntityRegistry entityTypes = context.GetRequiredService<IShardEntityRegistry>();
        IShardEntityInteractions interactions = context.GetRequiredService<IShardEntityInteractions>();
        IShardLivingEntities livestock = context.GetRequiredService<IShardLivingEntities>();
        IShardExperienceOrbs experienceOrbs = context.GetRequiredService<IShardExperienceOrbs>();
        IShardFishingWorld fishing = context.GetRequiredService<IShardFishingWorld>();
        IShardGroundItems groundItems = context.GetRequiredService<IShardGroundItems>();
        IShardProjectileWorld projectiles = context.GetRequiredService<IShardProjectileWorld>();
        var game = new MinecraftServer(context.Options, world,
            context.GetRequiredService<IShardPacketBus>(), context.GetService<IShardBlockWorld>(),
            context.DataDirectory, items, recipes,
            entities, entityTypes, interactions,
            context.GetService<IShardEnchantmentRegistry>(),
            context.GetService<IShardSkillEngine>(),
            context.GetService<IShardLootRegistry>(),
            context.GetService<IShardVanillaLootTables>(),
            context.GetService<IShardVanillaBlockStates>(),
            context.GetService<IShardConsumableRegistry>(), livestock,
            experienceOrbs, fishing, groundItems, projectiles);
        game.Start();
        _gameplay = game;
        context.RegisterService<IShardGameplay>(this);
        context.RegisterService<IShardItemDropSink>(game);
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
