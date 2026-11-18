using Amethyst.Shards;

namespace Amethyst.Samples;

/// <summary>A small independent Shard demonstrating lifecycle, a console
/// command, and an opt-in Play packet observer without changing Core.</summary>
public sealed class HelloShard : IShard
{
    private IShardContext? _context;

    public Task OnLoadAsync(IShardContext context, CancellationToken ct)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task OnEnableAsync(CancellationToken ct)
    {
        IShardContext ctx = _context ?? throw new InvalidOperationException("Not loaded.");
        ctx.RegisterConsoleCommand("hello", (name, token) =>
        {
            ctx.Log($"Hello{(string.IsNullOrWhiteSpace(name) ? "" : ", " + name.Trim())}! This response came from a Shard.");
            return Task.CompletedTask;
        });
        // Example observation of a Play packet without consuming it.
        // 0x2C is the Minecraft Java 26.3 Player Loaded packet.
        ctx.RegisterPlayPacketHandler(0x2C, (player, packet, token) =>
        {
            ctx.Log($"Player {player.Username} finished loading the world.");
            return Task.FromResult(false); // let normal gameplay handle it
        });
        ctx.Log("Hello Shard enabled. Try 'hello World' in the server console.");
        return Task.CompletedTask;
    }

    public Task OnDisableAsync(CancellationToken ct)
    {
        _context?.Log("Hello Shard disabled.");
        return Task.CompletedTask;
    }
}
