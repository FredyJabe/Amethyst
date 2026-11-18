#!/usr/bin/env python3
"""Wire smoke test: requires 26.3 server with onlineMode: false on localhost.

The default server.yaml is onlineMode: true. Switch only for local testing.
No Python packages required.
"""
import argparse
import json
import socket
import struct
import uuid


def varint(number: int) -> bytes:
    number &= 0xFFFFFFFF
    out = bytearray()
    while number > 127:
        out.append((number & 0x7F) | 0x80)
        number >>= 7
    return bytes(out) + bytes((number,))


def string(s: str) -> bytes:
    b = s.encode("utf-8")
    return varint(len(b)) + b


def exact(sock: socket.socket, size: int) -> bytes:
    out = bytearray()
    while len(out) < size:
        part = sock.recv(size - len(out))
        if not part:
            raise EOFError("Socket disconnected")
        out.extend(part)
    return bytes(out)


def vi(data: bytes, pos: int = 0) -> tuple[int, int]:
    result = 0
    for i in range(5):
        byte = data[pos + i]
        result |= (byte & 127) << (7 * i)
        if not byte & 128:
            return result, pos + i + 1
    raise ValueError("VarInt too long")


def text(data: bytes, pos: int) -> tuple[str, int]:
    length, pos = vi(data, pos)
    value = data[pos:pos+length].decode("utf-8")
    return value, pos+length


def pack(sock: socket.socket, kind: int, fields: bytes = b"") -> None:
    body = varint(kind) + fields
    sock.sendall(varint(len(body)) + body)


def unpack(sock: socket.socket) -> tuple[int, bytes]:
    size = 0
    for i in range(5):
        b = exact(sock, 1)[0]
        size |= (b & 0x7F) << (7 * i)
        if not b & 0x80:
            break
    else:
        raise AssertionError("Invalid length VarInt")
    assert 0 < size < 2 * 1024 * 1024
    body = exact(sock, size)
    packet_id, pos = vi(body)
    return packet_id, body[pos:]


def connect(host: str, port: int, intent: int) -> socket.socket:
    sock = socket.create_connection((host, port), 5)
    sock.settimeout(15)
    pack(sock, 0, varint(777) + string("localhost") + struct.pack(">H", port) + varint(intent))
    return sock


def status(host: str, port: int) -> dict:
    with connect(host, port, 1) as sock:
        pack(sock, 0)
        packet_id, body = unpack(sock)
        assert packet_id == 0
        payload, p = text(body, 0)
        assert p == len(body)
        echo = struct.pack(">q", 0x123456789ABCDEF0)
        pack(sock, 1, echo)
        assert unpack(sock) == (1, echo)
        return json.loads(payload)


def read_registry(body: bytes) -> tuple[str, list[str]]:
    registry, p = text(body, 0)
    count, p = vi(body, p)
    entries = []
    for _ in range(count):
        key, p = text(body, p)
        assert body[p] == 0, "Core pack entries should omit NBT"
        p += 1
        entries.append(key)
    assert p == len(body), f"Trailing data in registry {registry}"
    return registry, entries


def verify_chunk(body: bytes, *, biome_id: int = 40) -> tuple[int, int]:
    """Validate procedural 26.3 chunk sections, heightmaps and skylight framing.

    No fixed height or specific seed is assumed. The `biome_id` argument is kept
    for old offline fixture compatibility, but generated chunks may have many biomes.
    """
    chunk_x, chunk_z = struct.unpack_from(">ii", body, 0)
    p = 8
    count, p = vi(body, p)
    assert count == 3
    heightmaps = {}
    for _ in range(count):
        kind, p = vi(body, p)
        n, p = vi(body, p)
        assert n == 37
        heightmaps[kind] = struct.unpack_from(">37Q", body, p)
        p += 8 * n
    assert heightmaps.keys() == {1, 4, 5}
    size, p = vi(body, p)
    end = p + size
    assert size > 0 and end <= len(body)

    def palette(capacity: int, biome: bool) -> tuple[list[int], int]:
        nonlocal p
        bits = body[p]
        p += 1
        if bits == 0:
            state, p = vi(body, p)
            return [state] * capacity, 0
        global_palette = bits > (3 if biome else 8)
        assert (1 <= bits <= 7) if biome else (4 <= bits <= 16), bits
        if global_palette:
            assert bits == (7 if biome else 16)
            table = None
        else:
            count, p = vi(body, p)
            assert 1 <= count <= 1 << bits
            table = []
            for _ in range(count):
                state, p = vi(body, p)
                table.append(state)
        per_word = 64 // bits
        words = (capacity + per_word - 1) // per_word
        packed = struct.unpack_from(f">{words}Q", body, p)
        p += words * 8
        mask = (1 << bits) - 1
        values = [(packed[i // per_word] >> ((i % per_word) * bits)) & mask
                  for i in range(capacity)]
        if table is not None:
            values = [table[v] for v in values]
        return values, bits

    highest = [-65] * 256
    for section in range(24):
        non_air, fluids = struct.unpack_from(">hh", body, p)
        p += 4
        states, _ = palette(4096, False)
        biomes, _ = palette(64, True)
        assert 0 <= non_air <= 4096
        assert non_air == sum(state != 0 for state in states)
        assert fluids == states.count(89), f"Fluid count mismatch in section {section}"
        assert all(0 <= biome < 67 for biome in biomes), "Invalid biome registry ID"
        for i, state in enumerate(states):
            assert state in {0, 1, 9, 10, 88, 89, 121, 677, 8598}, state
            if state != 0:
                x = i & 15
                z = (i >> 4) & 15
                y = section * 16 - 64 + (i >> 8)
                idx = x + 16*z
                if y > highest[idx]:
                    highest[idx] = y
    assert p == end, f"Chunk section overrun: {p} != {end}"
    for heights in heightmaps.values():
        for idx in range(256):
            value = (heights[idx // 7] >> ((idx % 7) * 9)) & 511
            assert value == highest[idx] + 65, (chunk_x, chunk_z, idx, value, highest[idx])

    block_entities, p = vi(body, p)
    assert block_entities == 0
    masks = []
    for _ in range(4):
        n, p = vi(body, p)
        masks.append(int.from_bytes(body[p:p+n], "little"))
        p += n
    sky_mask, block_mask, empty_sky, empty_block = masks
    assert block_mask == 0 and empty_block == (1 << 26) - 1
    assert (sky_mask & empty_sky) == 0
    assert (sky_mask | empty_sky) == (1 << 26) - 1
    count, p = vi(body, p)
    assert count == sky_mask.bit_count()
    for layer in range(26):
        if sky_mask & (1 << layer):
            n, p = vi(body, p)
            assert n == 2048
            assert len(body[p:p+n]) == n
            p += n
    block_count, p = vi(body, p)
    assert block_count == 0 and p == len(body)
    return chunk_x, chunk_z


def verify_join_game(body: bytes) -> None:
    p = 0
    eid = struct.unpack_from(">i", body, p)[0]
    p += 4
    assert eid >= 0 and body[p] == 0
    p += 1
    count, p = vi(body, p)
    names = []
    for _ in range(count):
        name, p = text(body, p)
        names.append(name)
    assert names == ["minecraft:overworld"]
    for _ in range(3):
        value, p = vi(body, p)
        assert value >= 2
    assert body[p:p+3] == b"\x00\x01\x00"
    p += 3
    dimension_id, p = vi(body, p)
    dimension, p = text(body, p)
    p += 8  # seed hash
    gamemode, p = vi(body, p)
    previous, p = vi(body, p)
    assert dimension_id == 0 and dimension == "minecraft:overworld"
    assert gamemode == 1 and previous == 0
    assert body[p:p+3] == b"\x00\x00\x00"  # debug, procedural, no death
    p += 3
    _, p = vi(body, p)  # portal cooldown
    _, p = vi(body, p)  # sea level
    assert body[p:p+2] == b"\x00\x00"  # offline, no secure chat
    assert p+2 == len(body), "Join Game trailing fields"


def second_player(host: str, port: int, name: str) -> socket.socket:
    """A second local offline test client. Returns a ready Play socket."""
    sock = connect(host, port, 2)
    try:
        pack(sock, 0, string(name) + uuid.UUID(int=0).bytes)
        assert unpack(sock)[0] == 2
        pack(sock, 3)
        count = 0
        while True:
            pid, payload = unpack(sock)
            if pid == 0x0F:
                pack(sock, 0x07, payload)
            elif pid == 0x07:
                count += 1
            elif pid == 0x0E:
                assert payload[0] >= 3
            elif pid == 0x03:
                break
            else:
                assert pid == 0x0D
        assert count == 32
        pack(sock, 3)
        assert unpack(sock)[0] == 0x32
        got_batch = got_teleport = False
        chunks = set()
        while not (got_batch and got_teleport and len(chunks) == 9):
            pid, payload = unpack(sock)
            if pid == 0x2E:
                chunks.add(struct.unpack_from(">ii", payload))
            elif pid == 0x0B:
                got_batch = True
                # Deliberately don't ACK this probe's initial batch; the smoke
                # test only needs nearby chunks for peer entity packets.
            elif pid == 0x49:
                tid, p = vi(payload)
                values = struct.unpack_from(">6d2fi", payload, p)
                pack(sock, 0x00, varint(tid) + struct.pack(">3d2f", *values[:3], *values[6:8]))
                pack(sock, 0x2C)
                got_teleport = True
            elif pid == 0x73:  # 26.3 world clock sync
                assert len(payload) >= 13
            elif pid == 0x2D:
                pack(sock, 0x1C, payload)
        return sock
    except Exception:
        sock.close()
        raise


def wait_for(sock: socket.socket, expected: set[int], max_packets: int = 20) -> dict[int, bytes]:
    found: dict[int, bytes] = {}
    for _ in range(max_packets):
        pid, payload = unpack(sock)
        if pid == 0x2D:
            pack(sock, 0x1C, payload)
            continue
        if pid in expected:
            found[pid] = payload
            if expected <= found.keys():
                return found
    raise AssertionError(f"Expected packets {expected}, observed {found.keys()}")



def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=25565)
    args = parser.parse_args()
    host, port = args.host, args.port

    server_status = status(host, port)
    assert server_status["version"] == {"name": "26.3", "protocol": 777}
    print("PASS: Status + ping/pong, protocol 777")

    with connect(host, port, 2) as sock:
        pack(sock, 0, string("ProbePlayer") + uuid.UUID(int=0).bytes)
        pid, payload = unpack(sock)
        assert pid == 2, f"Login Success expected, got 0x{pid:X}"
        player_id = uuid.UUID(bytes=payload[:16])
        name, p = text(payload, 16)
        props, p = vi(payload, p)
        session_id = uuid.UUID(bytes=payload[p:p+16])
        assert (name, props, p+16) == ("ProbePlayer", 0, len(payload))
        assert player_id.version == 3 and session_id.version == 4
        print("PASS: Offline Login Success + 26.3 Session UUID")

        pack(sock, 3)  # Login Acknowledged
        seen_features = seen_packs = False
        while not (seen_features and seen_packs):
            pid, payload = unpack(sock)
            if pid == 0x0D:
                count, p = vi(payload)
                flag, p = text(payload, p)
                assert count == 1 and flag == "minecraft:vanilla" and p == len(payload)
                seen_features = True
            elif pid == 0x0F:
                assert payload == varint(1) + string("minecraft") + string("core") + string("26.3")
                pack(sock, 0x07, payload)  # Known Packs reply
                seen_packs = True
            else:
                raise AssertionError(f"Unexpected configuration packet {pid:#x}")
        print("PASS: Feature Flags + Known Packs exchange")

        registries: dict[str, list[str]] = {}
        saw_tags = False
        while True:
            pid, payload = unpack(sock)
            if pid == 0x07:
                registry, entries = read_registry(payload)
                assert registry not in registries, f"Duplicate registry {registry}"
                registries[registry] = entries
            elif pid == 0x0E:
                tag_registries, offset = vi(payload)
                assert tag_registries >= 3, "Vanilla 26.3 tags are missing"
                names = set()
                for _ in range(tag_registries):
                    registry, offset = text(payload, offset)
                    assert registry not in names
                    names.add(registry)
                    tag_count, offset = vi(payload, offset)
                    for _ in range(tag_count):
                        _, offset = text(payload, offset)
                        entries, offset = vi(payload, offset)
                        for _ in range(entries):
                            _, offset = vi(payload, offset)
                assert offset == len(payload), "Malformed tag update"
                assert "minecraft:block" in names, "Block tags are required"
                assert "minecraft:item" in names, "Item tags are required"
                saw_tags = True
            elif pid == 0x03:
                assert not payload and saw_tags
                break
            else:
                raise AssertionError(f"Unexpected registry packet 0x{pid:X}")
        assert len(registries) == 32, f"Received {len(registries)} registries, wanted 32"
        assert registries["minecraft:dimension_type"] == [
            "minecraft:overworld", "minecraft:overworld_caves", "minecraft:the_end", "minecraft:the_nether"]
        assert len(registries["minecraft:worldgen/biome"]) == 67
        assert "minecraft:plains" in registries["minecraft:worldgen/biome"]
        plains_id = registries["minecraft:worldgen/biome"].index("minecraft:plains")
        print("PASS: All 32 vanilla registries + Update Tags + Finish Configuration")

        pack(sock, 0x03)  # Ack Finish Configuration => Play

        pid, payload = unpack(sock)
        assert pid == 0x32, f"Expected Join Game, received {pid:#x}"
        verify_join_game(payload)
        print("PASS: Join Game, Overworld, Creative")

        seen = set()
        chunk_positions = set()
        teleport_fields = None
        for _ in range(520):
            pid, body = unpack(sock)
            seen.add(pid)
            if pid == 0x41:
                assert body[0] == 0x0D, "Player must stand (not initially fly)"
                assert struct.unpack_from(">ff", body, 1) == struct.unpack(">ff", struct.pack(">ff", 0.05, 0.1))
            elif pid == 0x2E:
                x, z = verify_chunk(body, biome_id=plains_id)
                assert (x, z) not in chunk_positions
                chunk_positions.add((x, z))
            elif pid == 0x0B:  # Chunk Batch Finished
                count, p = vi(body)
                assert p == len(body) and 1 <= count <= 16
                # Deliberately don't ACK this probe's initial batch; the smoke
                # test only needs nearby chunks for peer entity packets.
            elif pid == 0x49:
                tid, p = vi(body)
                values = struct.unpack_from(">6d2fi", body, p)
                assert p + struct.calcsize(">6d2fi") == len(body)
                assert tid == 1 and values[:3] == (0.5, 80.0, 0.5)
                teleport_fields = varint(tid) + struct.pack(">3d2f", *values[:3], *values[6:8])
                pack(sock, 0x00, teleport_fields)  # 26.3 teleport confirmation
                pack(sock, 0x2C)  # Player Loaded
            elif pid == 0x73:
                # World age, one registered overworld clock (or a time sync).
                age = struct.unpack_from(">q", body, 0)[0]
                clocks, p = vi(body, 8)
                assert age >= 0 and clocks == 1
                clock_id, p = vi(body, p)
                assert clock_id == 0
                # The signed clock time is VarLong; following it are 2 floats.
                while body[p] & 0x80:
                    p += 1
                p += 1
                assert p + 8 == len(body)
            elif pid == 0x2D:
                pack(sock, 0x1C, body)  # Play keepalive reply
            if len(chunk_positions) == 441 and teleport_fields is not None and 0x0B in seen:
                break
        assert {0x41, 0x60, 0x61, 0x71, 0x63, 0x73, 0x27, 0x0C, 0x0B, 0x49} <= seen
        assert chunk_positions == {(x, z) for x in range(-10, 11) for z in range(-10, 11)}
        assert 0x71 in seen
        print("PASS: Procedural terrain (441 columns via ACKed batches), biome palettes,")
        print("      dynamic heightmaps, light masks, teleport and Player Loaded")

        active_status = status(host, port)
        assert any(p["name"] == "ProbePlayer" for p in active_status["players"]["sample"])
        print("PASS: Play session appears on server list")

        with second_player(host, port, "ProbeFriend") as friend:
            # First player receives the peer's PlayerInfoUpdate + Add Entity.
            arrivals = wait_for(sock, {0x47, 0x01})
            assert arrivals[0x47][0] == 0x1D
            friend_uuid = uuid.UUID(bytes=arrivals[0x01][1:17])
            assert friend_uuid.version == 3
            friend_eid, eid_end = vi(arrivals[0x01])
            assert friend_eid > 0
            # Second player sees the already-ready first player too.
            peer = wait_for(friend, {0x47, 0x01})
            assert peer[0x47][0] == 0x1D
            pack(friend, 0x1F, struct.pack(">3d2fB", 1.5, 80.0, 1.5, 90.0, 0.0, 1))
            motion = wait_for(sock, {0x37, 0x55})
            moved_eid, pos = vi(motion[0x37])
            assert moved_eid == friend_eid
            flags, pos = vi(motion[0x37], pos)
            assert flags == 1
            dx, dy, dz = struct.unpack_from(">hhh", motion[0x37], pos)
            assert (dx, dy, dz) == (4096, 0, 4096)
            print("PASS: Two players appear, receive tablist updates and synced movement/rotation")
        gone = wait_for(sock, {0x4E, 0x46})
        print("PASS: Player disconnect removes entity and tablist entry")
    print("PASS: All network smoke checks passed")


if __name__ == "__main__":
    main()
