using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amethyst;

public sealed record VerifiedProfile(Guid Uuid, string Username, ProfileProperty[] Properties);

// Only the actual Mojang sessionserver response is trusted. Never use the
// client's UUID or a name->UUID lookup as an authentication substitute.
public sealed class OnlineAuthentication : IDisposable
{
    private readonly RSA _rsa = RSA.Create(1024);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly byte[] _publicKey;

    public OnlineAuthentication() => _publicKey = _rsa.ExportSubjectPublicKeyInfo();

    public async Task<VerifiedProfile?> AuthenticateAsync(PacketChannel channel, string username, CancellationToken ct)
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(4);
        await channel.SendAsync(0x01, writer =>
        {
            writer.WriteString("");
            writer.WriteByteArray(_publicKey);
            writer.WriteByteArray(challenge);
            writer.WriteBool(true);
        }, ct);

        Packet? answer = await channel.ReceiveAsync(ct);
        if (answer is null || answer.Value.Id != 0x01)
            throw new InvalidDataException("Missing Login Encryption Response.");
        var r = answer.Value.Reader();
        byte[] secretBytes = r.ReadByteArray(512);
        byte[] tokenBytes = r.ReadByteArray(512);
        r.RequireEnd();
        byte[] secret = _rsa.Decrypt(secretBytes, RSAEncryptionPadding.Pkcs1);
        byte[] token = _rsa.Decrypt(tokenBytes, RSAEncryptionPadding.Pkcs1);
        if (secret.Length != 16 || !CryptographicOperations.FixedTimeEquals(token, challenge))
            throw new InvalidDataException("Invalid login encryption challenge.");

        // All subsequent packets (including an auth failure Disconnect) are encrypted.
        channel.EnableEncryption(secret);
        string serverHash = MinecraftSignedSha1(secret, _publicKey);
        string endpoint = "https://sessionserver.mojang.com/session/minecraft/hasJoined" +
                          "?username=" + Uri.EscapeDataString(username) +
                          "&serverId=" + Uri.EscapeDataString(serverHash);

        using var reply = await _http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, ct);
        if (reply.StatusCode == HttpStatusCode.NoContent || reply.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!reply.IsSuccessStatusCode)
            throw new HttpRequestException($"Mojang session server replied {(int)reply.StatusCode}.");

        await using var jsonStream = await reply.Content.ReadAsStreamAsync(ct);
        using JsonDocument json = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
        var root = json.RootElement;
        string id = root.GetProperty("id").GetString() ?? "";
        string name = root.GetProperty("name").GetString() ?? "";
        if (!Guid.TryParseExact(id, "N", out Guid playerUuid) ||
            !string.Equals(name, username, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mojang session profile does not match login username.");

        var props = new List<ProfileProperty>();
        if (root.TryGetProperty("properties", out var properties))
        {
            foreach (var item in properties.EnumerateArray())
            {
                if (props.Count >= 16) throw new InvalidDataException("Too many profile properties.");
                string propertyName = item.GetProperty("name").GetString() ?? "";
                string value = item.GetProperty("value").GetString() ?? "";
                string? signature = item.TryGetProperty("signature", out var sig) ? sig.GetString() : null;
                if (propertyName.Length > 64 || value.Length > 32767 || signature?.Length > 1024)
                    throw new InvalidDataException("Invalid authenticated profile property length.");
                props.Add(new ProfileProperty(propertyName, value, signature));
            }
        }
        return new VerifiedProfile(playerUuid, name, props.ToArray());
    }

    internal static string MinecraftSignedSha1(byte[] secret, byte[] publicKey)
    {
        // SHA-1("" + shared secret + DER public key), written as a signed
        // two's-complement BigInteger, with no redundant leading zeroes.
        byte[] data = new byte[secret.Length + publicKey.Length];
        secret.CopyTo(data, 0);
        publicKey.CopyTo(data, secret.Length);
        var digest = SHA1.HashData(data);
        bool negative = (digest[0] & 0x80) != 0;
        if (negative)
        {
            for (int i = 0; i < digest.Length; i++) digest[i] = (byte)~digest[i];
            for (int i = digest.Length - 1; i >= 0; i--)
            {
                if (++digest[i] != 0) break;
            }
        }
        string hex = Convert.ToHexString(digest).TrimStart('0').ToLowerInvariant();
        return (negative ? "-" : "") + (hex.Length == 0 ? "0" : hex);
    }

    public void Dispose() { _rsa.Dispose(); _http.Dispose(); }
}
