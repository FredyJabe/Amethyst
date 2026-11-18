#!/usr/bin/env python3
"""Offline 26.3 chunk fixture tests. No server or .NET needed.

Checks that the independent protocol-777 decoder accepts correctly sized
section palettes and rejects the previously shipped extra-length framing.
"""
import struct
from probe import varint, verify_chunk


def encode(chunk_x: int, chunk_z: int, *, incorrect_lengths: bool = False) -> tuple[bytes, int]:
    def uniform(state: int) -> bytes:
        # 26.3 has NO packed-array length field for a uniform palette.
        return b'\0' + varint(state) + (b'\0' if incorrect_lengths else b'')

    def block_section(section: int) -> bytes:
        y0 = section * 16 - 64
        states = []
        for y in range(y0, y0 + 16):
            for z in range(chunk_z * 16, chunk_z * 16 + 16):
                for x in range(chunk_x * 16, chunk_x * 16 + 16):
                    pad = y == 79 and -2 <= x <= 2 and -2 <= z <= 2
                    states.append(1 if y <= 63 or pad else 0)
        non_air = sum(states)
        if min(states) == max(states):
            blocks = uniform(states[0])
        else:
            # A two-color block palette is 4 bits wide: 16 indices per long.
            palette = [0, 1]
            packed = []
            for start in range(0, 4096, 16):
                value = sum(s << (i * 4) for i, s in enumerate(states[start:start + 16]))
                packed.append(struct.pack('>Q', value))
            blocks = b'\x04' + varint(2) + bytes(palette)
            if incorrect_lengths:
                blocks += varint(256)
            blocks += b''.join(packed)
        return struct.pack('>hh', non_air, 0) + blocks + uniform(40)

    sections = b''.join(block_section(s) for s in range(24))
    header = struct.pack('>ii', chunk_x, chunk_z) + varint(3)
    height_words = [0] * 37
    for z in range(16):
        for x in range(16):
            wx = chunk_x * 16 + x
            wz = chunk_z * 16 + z
            top = 79 if -2 <= wx <= 2 and -2 <= wz <= 2 else 63
            index = x + 16 * z
            height_words[index // 7] |= (top + 65) << (9 * (index % 7))
    for height_type in (1, 4, 5):
        header += varint(height_type) + varint(37)
        header += b''.join(struct.pack('>Q', v) for v in height_words)
    header += varint(len(sections)) + sections + varint(0)  # no block entities
    def bitset(bits: int) -> bytes:
        encoded = bits.to_bytes((bits.bit_length() + 7) // 8, "little")
        return varint(len(encoded)) + encoded

    # Correct 26.3 sky/empty sky and no block-light masks. Underground
    # sections 0..8 are explicitly dark, and above-ground layers 9..25
    # carry 2048-byte skylight arrays.
    header += bitset(((1 << 26) - 1) & ~((1 << 9) - 1))
    header += bitset(0)
    header += bitset((1 << 9) - 1)
    header += bitset((1 << 26) - 1)
    header += varint(17)
    for layer in range(9, 26):
        data = bytearray(b'\xff' * 2048)
        if layer == 9:
            for z in range(16):
                for x in range(16):
                    wx, wz = chunk_x * 16 + x, chunk_z * 16 + z
                    if not (-2 <= wx <= 2 and -2 <= wz <= 2):
                        continue
                    for y in range(16):
                        index = (y << 8) | (z << 4) | x
                        data[index // 2] &= ~(15 << ((index % 2) * 4))
        header += varint(2048) + data
    header += varint(0)
    return header, len(sections)


for cx, cz, section_size in ((0, 0, 2242), (-1, 0, 2242),
                             (0, -1, 2242), (-1, -1, 2242),
                             (4, 4, 192)):
    packet, size = encode(cx, cz)
    assert size == section_size, (cx, cz, size)
    assert verify_chunk(packet) == (cx, cz)
    print(f'PASS: valid chunk ({cx},{cz}) with {size} section bytes')

bad, bad_size = encode(0, 0, incorrect_lengths=True)
assert bad_size == 2291, bad_size
try:
    verify_chunk(bad)
except (AssertionError, ValueError, IndexError, struct.error):
    print('PASS: reject old 2291-byte chunk with redundant palette lengths')
else:
    raise AssertionError('Protocol decoder incorrectly accepted legacy length fields')
