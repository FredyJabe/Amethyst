#!/usr/bin/env python3
"""Static safeguards for client chunk visibility and packet ordering.

Actual network behavior is checked in the .NET ChunkTests project.
"""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1]
spawn = (root / "shards/Amethyst.Shards.Gameplay/SpawnPackets.cs").read_text()
view = (root / "shards/Amethyst.Shards.Overworld/TerrainWorld.cs").read_text()
start = spawn.index('await SendPositionAsync(channel, TeleportId')
first_batch = spawn.index('await player.View.UpdateAsync(channel')
assert start < first_batch, "position must precede chunk batch at login"
assert "RenderHaloChunks = 1" in view
assert "ViewDistance + RenderHaloChunks" in view
unload = view.index('await channel.SendAsync(0x26')
center = view.index('await channel.SendAsync(0x60')
assert unload < center, "unloads must precede cache-center movement"
unload_block = view[unload:view.index('}, ct);', unload)]
assert unload_block.index('p.WriteInt32(old.Z)') < unload_block.index('p.WriteInt32(old.X)'), (
    "Forget Level Chunk packet must write Z then X")
assert 'AMETHYST_TRACE_CHUNKS' in view
print('PASS: spawn packet ordering, render halo, unload before cache center, Z/X order')
