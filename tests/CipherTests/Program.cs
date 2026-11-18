using Amethyst;

// Independent AES-128/CFB8 known-answer vector generated with OpenSSL-backed
// Python cryptography (key == IV, as required by Minecraft).
byte[] referenceKey = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
byte[] referencePlain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172a");
byte[] referenceCipher = Convert.FromHexString("614622e1d1e39ca6e652427481aab2ca");

using (var enc = new MinecraftCfb8Cipher(referenceKey, encrypt: true))
{
    byte[] actual = new byte[referencePlain.Length];
    enc.Transform(referencePlain.AsSpan(0, 1), actual.AsSpan(0, 1));
    enc.Transform(referencePlain.AsSpan(1, 7), actual.AsSpan(1, 7));
    enc.Transform(referencePlain.AsSpan(8), actual.AsSpan(8));
    Check(actual.SequenceEqual(referenceCipher), "AES/CFB8 known ciphertext");
}
using (var dec = new MinecraftCfb8Cipher(referenceKey, encrypt: false))
{
    byte[] actual = new byte[referenceCipher.Length];
    for (int i = 0; i < actual.Length; i++)
        dec.Transform(referenceCipher.AsSpan(i, 1), actual.AsSpan(i, 1));
    Check(actual.SequenceEqual(referencePlain), "AES/CFB8 bytewise decrypt");
}

// PacketChannel sends the entire packet frame as a continuing encrypted stream.
byte[] secret = Convert.FromHexString("00112233445566778899aabbccddeeff");
using var wire = new MemoryStream();
using (var sender = new PacketChannel(wire))
{
    sender.EnableEncryption(secret);
    await sender.SendAsync(0x02, p => p.WriteString("A"), CancellationToken.None);
    await sender.SendAsync(0x03, null, CancellationToken.None);
    await sender.SendAsync(0x04, p => p.WriteString(new string('q', 257)), CancellationToken.None);
}
wire.Position = 0;
using (var receiver = new PacketChannel(new FragmentedStream(wire)))
{
    receiver.EnableEncryption(secret);
    var p1 = await receiver.ReceiveAsync(CancellationToken.None);
    Check(p1 is { Id: 2 }, "first packet ID");
    Check(p1!.Value.Reader().ReadString(128) == "A", "first packet contents");
    var p2 = await receiver.ReceiveAsync(CancellationToken.None);
    Check(p2 is { Id: 3, Fields.Length: 0 }, "second packet");
    var p3 = await receiver.ReceiveAsync(CancellationToken.None);
    Check(p3 is { Id: 4 }, "third packet ID");
    Check(p3!.Value.Reader().ReadString(1024) == new string('q', 257), "third packet contents");
    Check(await receiver.ReceiveAsync(CancellationToken.None) is null, "clean EOF");
}

Console.WriteLine("PASS: CFB8 ciphertext, read/write fragmentation, multi-packet round-trip");

static void Check(bool result, string what)
{
    if (!result) throw new Exception("FAILED: " + what);
}

// Forces ReceiveAsync to return arbitrarily small chunks even if more data is available.
sealed class FragmentedStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, Math.Min(count, 3));
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
