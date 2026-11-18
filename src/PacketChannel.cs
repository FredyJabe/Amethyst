using System.Buffers;
using System.Security.Cryptography;

namespace Amethyst;

// Protocol writes a complete packet frame with one WriteAsync call. The gate
// serializes encrypted frames, preserving the CFB8 stream's feedback state.
public sealed class PacketChannel : Stream
{
    private readonly Stream _network;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private MinecraftCfb8Cipher? _encrypt;
    private MinecraftCfb8Cipher? _decrypt;

    public PacketChannel(Stream network) => _network = network;

    /// <summary>
    /// Called after receiving and validating the unencrypted Encryption Response.
    /// ALL subsequent frame bytes (including VarInt lengths) are encrypted.
    /// </summary>
    public void EnableEncryption(byte[] sharedSecret)
    {
        ArgumentNullException.ThrowIfNull(sharedSecret);
        if (sharedSecret.Length != 16 || _encrypt is not null || _decrypt is not null)
            throw new InvalidOperationException("Invalid or duplicate AES session key.");

        _encrypt = new MinecraftCfb8Cipher(sharedSecret, encrypt: true);
        _decrypt = new MinecraftCfb8Cipher(sharedSecret, encrypt: false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_decrypt is null)
            return await _network.ReadAsync(buffer, cancellationToken);
        if (buffer.IsEmpty) return 0;

        // Socket reads may return 1 byte, 7 bytes, or any other length.
        // A different chunk size must not change the AES/CFB8 byte stream.
        byte[] encrypted = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int count = await _network.ReadAsync(encrypted.AsMemory(0, buffer.Length), cancellationToken);
            if (count == 0) return 0;
            _decrypt.Transform(encrypted.AsSpan(0, count), buffer.Span.Slice(0, count));
            return count;
        }
        finally { ArrayPool<byte>.Shared.Return(encrypted, clearArray: true); }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_encrypt is null)
            {
                await _network.WriteAsync(buffer, cancellationToken);
                return;
            }

            if (buffer.IsEmpty) return;
            byte[] encrypted = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                _encrypt.Transform(buffer.Span, encrypted.AsSpan(0, buffer.Length));
                await _network.WriteAsync(encrypted.AsMemory(0, buffer.Length), cancellationToken);
            }
            finally { ArrayPool<byte>.Shared.Return(encrypted, clearArray: true); }
        }
        finally { _writeGate.Release(); }
    }

    public Task SendAsync(int id, Action<PacketWriter>? fields, CancellationToken ct) =>
        Protocol.WritePacketAsync(this, id, fields, ct);

    public Task<Packet?> ReceiveAsync(CancellationToken ct) => Protocol.ReadPacketAsync(this, ct);

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _network.Flush();
    public override Task FlushAsync(CancellationToken ct) => _network.FlushAsync(ct);
    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] b, int o, int c) => WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _encrypt?.Dispose();
            _decrypt?.Dispose();
            _writeGate.Dispose();
        }
        base.Dispose(disposing);
    }
}
