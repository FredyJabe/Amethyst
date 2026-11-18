using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>The Amethyst kernel: status, handshake, online authentication,
/// login, Configuration, encrypted packet transport and connection lifetime.
/// No terrain, game modes, entities or gameplay rules are implemented here.</summary>
public sealed class AmethystNetworkServer
{
    private readonly ServerOptions _options;
    private readonly ShardHost _shards;
    private static readonly Regex UsernameRegex =
        new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AmethystNetworkServer(ServerOptions options, ShardHost shards)
    {
        _options = options;
        _shards = shards;
    }

    public async Task RunAsync(CancellationToken stop, ServerConsole? terminal = null)
    {
        using var serverStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        stop = serverStop.Token;
        await VanillaTags.EnsureAvailableAsync(stop);
        var listener = new TcpListener(_options.ListenAddress, _options.Port);
        listener.Start();
        using var listenerShutdown = stop.Register(listener.Stop);
        Console.WriteLine($"[core] Listening on {_options.BindAddress}:{_options.Port}; " +
            $"protocol={Protocol.Version}, onlineMode={_options.OnlineMode}");
        if (!_options.OnlineMode && !IPAddress.IsLoopback(_options.ListenAddress))
            Console.WriteLine("[WARN] Offline login permits username spoofing. Do not expose this listener publicly.");
        Console.WriteLine("[core] Commands: shards, shard enable/disable <id>, help, stop");

        if (terminal is not null)
        {
            _ = terminal.StartInputAsync(
                (line, ct) => ExecuteConsoleAsync(line, serverStop, terminal, ct),
                CompleteConsole, stop);
        }
        else
            _ = Task.Run(() => ConsoleInputAsync(serverStop, stop), CancellationToken.None);

        var active = new HashSet<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (SocketException) when (stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
                Task connection = HandleConnectionAsync(client, stop);
                lock (active) active.Add(connection);
                _ = connection.ContinueWith(t =>
                {
                    lock (active) active.Remove(t);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            Task[] pending;
            lock (active) pending = active.ToArray();
            if (pending.Length != 0) await Task.WhenAll(pending);
        }
    }

    private IReadOnlyList<string> CompleteConsole(string prefix)
    {
        string[] systemCommands = ["shards ", "shard enable ", "shard disable ", "stop ", "help "];
        var result = systemCommands.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (prefix.StartsWith("shard enable ", StringComparison.OrdinalIgnoreCase) ||
            prefix.StartsWith("shard disable ", StringComparison.OrdinalIgnoreCase))
        {
            string begin = prefix[..(prefix.LastIndexOf(' ') + 1)];
            string match = prefix[begin.Length..];
            result.AddRange(_shards.Snapshot().Where(e => e.Id.StartsWith(match,
                StringComparison.OrdinalIgnoreCase)).Select(e => begin + e.Id + " "));
        }
        IShardGameplay? gameplay = _shards.GetService<IShardGameplay>();
        if (gameplay is not null) result.AddRange(gameplay.CompleteConsole(prefix));
        result.AddRange(_shards.RegisteredConsoleCommands
            .Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(x => x + " "));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task ConsoleInputAsync(CancellationTokenSource shutdown, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await Console.In.ReadLineAsync(ct);
                if (line is null) return;
                await ExecuteConsoleAsync(line, shutdown, null, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private async Task ExecuteConsoleAsync(string command, CancellationTokenSource shutdown,
        ServerConsole? terminal, CancellationToken ct)
    {
        if (command.Length > ConsoleLineEditor.MaxCommandLength || command.Any(char.IsControl))
        {
            Console.WriteLine("[WARN] Invalid command."); return;
        }
        string line = command.Trim().TrimStart('/');
        if (line.Length == 0) return;
        string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "stop":
                Console.WriteLine("[core] Shutdown requested.");
                shutdown.Cancel();
                break;
            case "clear":
                terminal?.Clear();
                break;
            case "shards":
                foreach (ShardInfo info in _shards.Snapshot())
                    Console.WriteLine($"[shards] {info.Id}: {info.State} " +
                        $"requires=[{string.Join(",", info.Dependencies)}]");
                break;
            case "shard":
                if (parts.Length != 3 || parts[1] is not ("enable" or "disable"))
                {
                    Console.WriteLine("Usage: shard <enable|disable> <id>"); break;
                }
                try
                {
                    if (parts[1] == "enable") await _shards.EnableAsync(parts[2], ct);
                    else await _shards.DisableAsync(parts[2], ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                { Console.WriteLine($"[shards] {ex.Message}"); }
                break;
            case "help":
                Console.WriteLine("Core commands: shards, shard enable <id>, shard disable <id>, stop, clear");
                if (_shards.GetService<IShardGameplay>() is IShardGameplay gameplayHelp)
                    await gameplayHelp.ExecuteConsoleAsync(line, shutdown, terminal, ct);
                break;
            default:
                IShardGameplay? gameplay = _shards.GetService<IShardGameplay>();
                if (await _shards.TryExecuteConsoleAsync(line, ct)) break;
                if (gameplay is null) Console.WriteLine("[core] Gameplay Shard is disabled; only core commands are available.");
                else await gameplay.ExecuteConsoleAsync(line, shutdown, terminal, ct);
                break;
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            try
            {
                using var stream = client.GetStream();
                using var channel = new PacketChannel(stream);
                using var first = CancellationTokenSource.CreateLinkedTokenSource(stop);
                first.CancelAfter(TimeSpan.FromSeconds(15));
                Packet? handshake = await channel.ReceiveAsync(first.Token);
                if (handshake is null || handshake.Value.Id != 0) return;
                PacketReader reader = handshake.Value.Reader();
                int version = reader.ReadVarInt();
                string address = reader.ReadString(255);
                ushort port = reader.ReadUInt16();
                int intent = reader.ReadVarInt();
                reader.RequireEnd();
                Console.WriteLine($"[{remote}] Handshake v={version} {address}:{port} intent={intent}");
                switch (intent)
                {
                    case 1: await HandleStatusAsync(channel, stop); break;
                    case 2: await HandleLoginAsync(channel, version, stop); break;
                    case 3: await SendLoginDisconnectAsync(channel, "Transfers are not supported.", stop); break;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (OperationCanceledException) { Console.WriteLine($"[{remote}] Timed out"); }
            catch (IOException ex) { Console.WriteLine($"[{remote}] Connection closed: {ex.Message}"); }
            catch (SocketException ex) { Console.WriteLine($"[{remote}] Network: {ex.Message}"); }
            catch (Exception ex) { Console.Error.WriteLine($"[{remote}] Protocol/auth error: {ex}"); }
        }
    }

    private async Task HandleStatusAsync(PacketChannel channel, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Packet? request = await channel.ReceiveAsync(timeout.Token);
        if (request is null || request.Value.Id != 0 || request.Value.Fields.Length != 0) return;
        ShardPlayerSummary[] players = _shards.GetService<IShardGameplay>()?.OnlinePlayers ?? [];
        // Minecraft status JSON is mandated by the Java network protocol;
        // Shard manifests and all project settings are YAML.
        string json = JsonSerializer.Serialize(new
        {
            version = new { name = Protocol.VersionName, protocol = Protocol.Version },
            players = new
            {
                max = _options.MaxPlayers,
                online = players.Length,
                sample = players.Take(12).Select(p => new { name = p.Username, id = p.Uuid.ToString() }).ToArray()
            },
            description = new { text = _options.Motd },
            enforcesSecureChat = false
        });
        await channel.SendAsync(0, p => p.WriteString(json), timeout.Token);
        Packet? ping = await channel.ReceiveAsync(timeout.Token);
        if (ping is null || ping.Value.Id != 1) return;
        PacketReader reader = ping.Value.Reader();
        long timestamp = reader.ReadInt64();
        reader.RequireEnd();
        await channel.SendAsync(1, p => p.WriteInt64(timestamp), timeout.Token);
    }

    private async Task HandleLoginAsync(PacketChannel channel, int version, CancellationToken stop)
    {
        if (version != Protocol.Version)
        {
            await SendLoginDisconnectAsync(channel, $"Use Minecraft Java {Protocol.VersionName}.", stop);
            return;
        }
        IShardGameplay? gameplay = _shards.GetService<IShardGameplay>();
        if (gameplay is null)
        {
            await SendLoginDisconnectAsync(channel, "No gameplay Shard is currently enabled.", stop);
            return;
        }
        using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        loginTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        CancellationToken ct = loginTimeout.Token;
        Packet? login = await channel.ReceiveAsync(ct);
        if (login is null || login.Value.Id != 0) return;
        PacketReader start = login.Value.Reader();
        string username = start.ReadString(16);
        _ = start.ReadUuid(); // never trust the supplied UUID
        start.RequireEnd();
        if (!UsernameRegex.IsMatch(username))
        {
            await SendLoginDisconnectAsync(channel, "Invalid username.", ct); return;
        }
        VerifiedProfile profile;
        if (_options.OnlineMode)
        {
            try
            {
                using var authentication = new OnlineAuthentication();
                VerifiedProfile? verified = await authentication.AuthenticateAsync(channel, username, ct);
                if (verified is null)
                {
                    await SendLoginDisconnectAsync(channel, "Mojang could not verify this Minecraft Java account.", ct);
                    return;
                }
                profile = verified;
            }
            catch (Exception ex) when (ex is HttpRequestException or
                System.Security.Cryptography.CryptographicException or InvalidDataException or JsonException)
            {
                Console.WriteLine($"[auth] {username}: rejected ({ex.Message})");
                await SendLoginDisconnectAsync(channel, "Minecraft account authentication failed.", ct);
                return;
            }
            Console.WriteLine($"[auth] {profile.Username} authenticated through Mojang ({profile.Uuid}).");
        }
        else
        {
            profile = new VerifiedProfile(OfflineUuid(username), username, []);
            Console.WriteLine($"[auth] {username} using offline local-test mode.");
        }

        await channel.SendAsync(0x02, p =>
        {
            p.WriteUuid(profile.Uuid); p.WriteString(profile.Username);
            p.WriteVarInt(profile.Properties.Length);
            foreach (ProfileProperty prop in profile.Properties)
            {
                p.WriteString(prop.Name); p.WriteString(prop.Value);
                p.WriteBool(prop.Signature is not null);
                if (prop.Signature is not null) p.WriteString(prop.Signature);
            }
            p.WriteUuid(Guid.NewGuid()); // 26.3 session UUID
        }, ct);
        Packet? ack = await channel.ReceiveAsync(ct);
        if (ack is null || ack.Value.Id != 3 || ack.Value.Fields.Length != 0) return;

        int chatMode = 0;
        bool admitted = false;
        try
        {
            // Admission is owned by the gameplay Shard, not the protocol core.
            if (!gameplay.TryAdmit(profile, channel, chatMode, out string reason))
            {
                await SendConfigurationDisconnectAsync(channel, reason, ct);
                return;
            }
            admitted = true;
            Console.WriteLine($"[login] {profile.Username} entered Configuration");
            bool configured = await HandleConfigurationAsync(channel, mode => chatMode = mode, ct);
            if (!configured) return;
            gameplay.SetChatMode(profile.Uuid, chatMode);
            loginTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            using var playStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Task keepAlives = SendPlayKeepAlivesAsync(channel, playStop);
            try { await gameplay.RunPlayAsync(profile, playStop.Token); }
            finally
            {
                await playStop.CancelAsync();
                try { await keepAlives; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            if (admitted) await gameplay.OnDisconnectAsync(profile.Uuid);
        }
    }

    private static async Task<bool> HandleConfigurationAsync(PacketChannel channel,
        Action<int> onChatMode, CancellationToken ct)
    {
        await channel.SendAsync(0x0D, p => { p.WriteVarInt(1); p.WriteString("minecraft:vanilla"); }, ct);
        await channel.SendAsync(0x0F, p =>
        {
            p.WriteVarInt(1);
            p.WriteString("minecraft"); p.WriteString("core"); p.WriteString(Protocol.VersionName);
        }, ct);
        bool corePack = false;
        while (true)
        {
            Packet? packet = await channel.ReceiveAsync(ct);
            if (packet is null) return false;
            if (packet.Value.Id == 0x07)
            {
                PacketReader reader = packet.Value.Reader();
                int count = reader.ReadVarInt();
                if (count is < 0 or > 64) throw new InvalidDataException("Bad known pack count.");
                for (int i = 0; i < count; i++)
                {
                    string ns = reader.ReadString(32767), id = reader.ReadString(32767), ver = reader.ReadString(32767);
                    corePack |= ns == "minecraft" && id == "core" && ver == Protocol.VersionName;
                }
                reader.RequireEnd();
                break;
            }
            if (packet.Value.Id == 0x00) { ApplyClientInformation(packet.Value, onChatMode); continue; }
            if (packet.Value.Id == 0x02) continue;
            if (packet.Value.Id == 0x04)
            {
                PacketReader reader = packet.Value.Reader(); _ = reader.ReadInt64(); reader.RequireEnd();
                continue;
            }
            throw new InvalidDataException($"Unexpected Configuration packet 0x{packet.Value.Id:X}.");
        }
        if (!corePack)
        {
            await SendConfigurationDisconnectAsync(channel, "Vanilla 26.3 core data pack required.", ct);
            return false;
        }
        await VanillaRegistries.SendAsync(channel, ct);
        await VanillaTags.SendAsync(channel, ct);
        await channel.SendAsync(0x03, null, ct);
        while (true)
        {
            Packet? packet = await channel.ReceiveAsync(ct);
            if (packet is null) return false;
            if (packet.Value.Id == 0x03)
            {
                if (packet.Value.Fields.Length != 0)
                    throw new InvalidDataException("Bad Finish Configuration ACK.");
                Console.WriteLine($"[config] Client accepted {VanillaRegistries.Count} registries and tags");
                return true;
            }
            if (packet.Value.Id == 0x00) { ApplyClientInformation(packet.Value, onChatMode); continue; }
            if (packet.Value.Id == 0x02) continue;
            if (packet.Value.Id == 0x04)
            {
                PacketReader reader = packet.Value.Reader(); _ = reader.ReadInt64(); reader.RequireEnd();
                continue;
            }
            throw new InvalidDataException($"Unexpected Configuration ACK 0x{packet.Value.Id:X}.");
        }
    }

    private static void ApplyClientInformation(Packet packet, Action<int> onChatMode)
    {
        PacketReader reader = packet.Reader();
        _ = reader.ReadString(16);
        _ = reader.ReadByte();
        int mode = reader.ReadVarInt();
        if (mode is >= 0 and <= 2) onChatMode(mode);
    }

    private static async Task SendPlayKeepAlivesAsync(PacketChannel channel, CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await channel.SendAsync(0x2D, p => p.WriteInt64(id), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IOException) { await lifetime.CancelAsync(); }
        catch (ObjectDisposedException) { await lifetime.CancelAsync(); }
    }

    private static Guid OfflineUuid(string username)
    {
        byte[] digest = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest, bigEndian: true);
    }

    private static Task SendLoginDisconnectAsync(PacketChannel channel, string reason, CancellationToken ct) =>
        channel.SendAsync(0, p => p.WriteString(JsonSerializer.Serialize(new { text = reason })), ct);

    private static Task SendConfigurationDisconnectAsync(PacketChannel channel, string reason, CancellationToken ct) =>
        channel.SendAsync(0x02, p => p.WritePlainTextComponent(reason), ct);
}
