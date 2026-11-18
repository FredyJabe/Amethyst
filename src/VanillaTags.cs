using System.Security.Cryptography;
using System.Text;

namespace Amethyst;

/// <summary>
/// Complete vanilla Java 26.3 tags are mandatory. Sending an empty tags update
/// can invalidate the client's required tag bindings at Finish Configuration.
/// This file is a pinned capture of the exact vanilla packet payload.
/// </summary>
public static class VanillaTags
{
    private const string TagUrl =
        "https://raw.githubusercontent.com/ezchr/go-mcjava/25ac9cf2bbbd/v777/vanilla/35_update_tags.bin";
    private const string ExpectedBlobSha = "ff949c6ed65cef786ea41d17f993242d89f5d68f";
    private static byte[]? _packetBody;

    public static async Task EnsureAvailableAsync(CancellationToken ct)
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "VanillaRegistries", "35_update_tags.bin");
        byte[] body;
        if (File.Exists(path))
        {
            body = await File.ReadAllBytesAsync(path, ct);
        }
        else
        {
            Console.WriteLine("[startup] Vanilla 26.3 tag data missing; downloading verified snapshot...");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            try
            {
                body = await http.GetByteArrayAsync(TagUrl, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidDataException(
                    "The mandatory vanilla 26.3 tag dataset is missing and cannot be downloaded. " +
                    "With internet access, run 'python3 tools/fetch_vanilla_tags.py' " +
                    "from the project folder, rebuild with 'dotnet build src/Amethyst.csproj', " +
                    "and restart. The server no longer sends invalid empty tags.", ex);
            }
            Validate(body);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, body, ct);
        }

        Validate(body);
        _packetBody = body;
        Console.WriteLine($"[startup] Verified vanilla 26.3 tags: {body.Length:N0} bytes.");
    }

    private static void Validate(byte[] body)
    {
        if (body.Length is < 10000 or > 200000)
            throw new InvalidDataException("Invalid vanilla 26.3 tags length.");
        byte[] prefix = Encoding.ASCII.GetBytes($"blob {body.Length}\0");
        byte[] input = new byte[prefix.Length + body.Length];
        prefix.CopyTo(input, 0);
        body.CopyTo(input, prefix.Length);
        string sha = Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant();
        if (sha != ExpectedBlobSha)
            throw new InvalidDataException(
                $"Vanilla 26.3 tag data checksum mismatch; expected {ExpectedBlobSha}, got {sha}. " +
                "Delete the corrupt 35_update_tags.bin and download the pinned copy again.");
    }

    public static Task SendAsync(Stream stream, CancellationToken ct)
    {
        byte[] body = _packetBody ??
            throw new InvalidOperationException("Vanilla tags are not loaded; call EnsureAvailableAsync at startup.");
        return Protocol.WritePacketAsync(stream, 0x0E, p => p.WriteBytes(body), ct);
    }
}
