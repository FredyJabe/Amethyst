using Amethyst.Shards;

namespace Amethyst.BuiltinShards;

/// <summary>The built-in world provider. Can be replaced by a third-party
/// implementation of IShardWorld without modifying Amethyst's network core.</summary>
public sealed class OverworldShard : IShard
{
    private IShardContext? _context;
    private IShardWorld? _world;

    public Task OnLoadAsync(IShardContext context, CancellationToken ct)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnEnableAsync(CancellationToken ct)
    {
        if (_context is null) throw new InvalidOperationException("Shard not loaded.");
        var world = new TerrainWorld(_context.Options);
        _world = world;
        _context.RegisterService<IShardWorld>(world);
        _context.RegisterService<IShardBlockWorld>(world);
        _context.Log($"World initialized: seed={world.Seed}, generatorVersion={_context.Options.GeneratorVersion}, sea={world.SeaLevel}");
        return Task.CompletedTask;
    }

    public async Task OnDisableAsync(CancellationToken ct)
    {
        if (_world is not null)
        {
            await _world.DisposeAsync();
            _world = null;
        }
        _context?.Log("World generation workers stopped; queued saves drained.");
    }
}
