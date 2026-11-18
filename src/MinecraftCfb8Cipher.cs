using System.Security.Cryptography;

namespace Amethyst;

/// <summary>
/// Minecraft Java's AES-128/CFB8 cipher. Unlike ICryptoTransform in CFB mode,
/// this can transform any number of bytes without padding or finalizing between
/// packets. The AES primitive is ECB encrypt of the current 16-byte feedback
/// register; CFB8 supplies its own 1-byte feedback and XOR.
///
/// Each direction MUST have its own instance. This class is stateful and is not
/// thread-safe: serialize writers and use a single packet reader per channel.
/// </summary>
public sealed class MinecraftCfb8Cipher : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _aesBlockEncrypt;
    private readonly byte[] _feedback;
    private readonly byte[] _keystream = new byte[16];
    private readonly bool _encrypt;
    private bool _disposed;

    public MinecraftCfb8Cipher(ReadOnlySpan<byte> sharedSecret, bool encrypt)
    {
        if (sharedSecret.Length != 16)
            throw new ArgumentException("Minecraft requires a 16-byte AES shared secret.", nameof(sharedSecret));

        _encrypt = encrypt;
        _feedback = sharedSecret.ToArray(); // Minecraft's AES IV equals the shared secret.
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = sharedSecret.ToArray();
        _aesBlockEncrypt = _aes.CreateEncryptor();
    }

    public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (output.Length < input.Length)
            throw new ArgumentException("Output buffer is shorter than input.", nameof(output));

        for (int i = 0; i < input.Length; i++)
        {
            // Encrypt exactly one AES block (16 bytes), not the arbitrarily
            // sized network payload that caused the original exception.
            int written = _aesBlockEncrypt.TransformBlock(_feedback, 0, 16, _keystream, 0);
            if (written != 16) throw new CryptographicException("AES ECB produced an invalid block.");

            byte transformed = (byte)(input[i] ^ _keystream[0]);
            // CFB8 always feeds back the ciphertext, NOT the plaintext.
            byte ciphertext = _encrypt ? transformed : input[i];
            output[i] = transformed;
            Buffer.BlockCopy(_feedback, 1, _feedback, 0, 15);
            _feedback[15] = ciphertext;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aesBlockEncrypt.Dispose();
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_feedback);
        CryptographicOperations.ZeroMemory(_keystream);
        GC.SuppressFinalize(this);
    }
}
