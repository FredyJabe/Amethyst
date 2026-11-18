using System.Text;
using Amethyst;

static void Check(bool value, string message)
{
    if (!value) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS " + message);
}

string temp = Path.Combine(Path.GetTempPath(), "minicraft-ops-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    string path = Path.Combine(temp, "ops.yaml");
    var ops = new OperatorStore(path);
    Guid a = Guid.Parse("12345678-1234-4234-8234-1234567890ab");
    Guid b = Guid.NewGuid();
    Check(File.Exists(path) && ops.Level(a) == 0, "empty YAML operator store created");
    Check(ops.TryGrant(a, "TestPlayer", 4, out _), "grant level 4 to authenticated UUID");
    Check(ops.Level(a) == 4 && ops.Level(b) == 0, "UUID, not username, authorizes privileges");
    Check(new OperatorStore(path).Level(a) == 4, "YAML operator permissions persist across loads");
    Check(!ops.TryGrant(a, "TestPlayer", 5, out _), "invalid operator level rejected");
    Check(ops.TryRevoke("TestPlayer", out Guid revoked, out _) && revoked == a, "revoke stored name resolves UUID");
    Check(new OperatorStore(path).Level(a) == 0, "operator revocation persists");

    byte[] guestTree = CommandTree.Encode(0), ownerTree = CommandTree.Encode(4);
    Check(ownerTree.Length > guestTree.Length, "operator command tree includes additional commands");
    Check(HasCommand(guestTree, "help") && HasCommand(guestTree, "msg") && !HasCommand(guestTree, "op"),
        "guest command tree contains chat, but no OP commands");
    Check(HasCommand(ownerTree, "op") && HasCommand(ownerTree, "deop") && HasCommand(ownerTree, "kick"),
        "owner command tree includes permission and moderation commands");

    var chatData = new PacketWriter();
    chatData.WriteString("hello from 26.3");
    chatData.WriteInt64(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    chatData.WriteInt64(1);
    chatData.WriteBool(true);
    chatData.WriteBytes(new byte[256]);
    chatData.WriteVarInt(0);
    chatData.WriteBytes(new byte[3]);
    chatData.WriteByte(0);
    IncomingChat chat = ChatProtocol.ParseChat(new Packet(ChatProtocol.ServerboundChat, chatData.ToArray()));
    Check(chat.Message == "hello from 26.3" && chat.HasSignature, "signed-capable chat packet consumes all fields");
    Check(!ChatProtocol.IsSafeMessage("test\nspoof", 256) && ChatProtocol.IsSafeMessage("Hello, world!", 256),
        "control characters in chat rejected");
    var commandData = new PacketWriter();
    commandData.WriteString("help");
    Check(ChatProtocol.ParseCommand(new Packet(ChatProtocol.ServerboundCommand, commandData.ToArray())).Command == "help",
        "unsigned command format parsed");
    commandData = new PacketWriter();
    commandData.WriteString("say hello");
    commandData.WriteInt64(0);
    commandData.WriteInt64(0);
    commandData.WriteVarInt(0);
    commandData.WriteVarInt(0);
    commandData.WriteBytes(new byte[3]);
    commandData.WriteByte(0);
    Check(!ChatProtocol.ParseCommand(new Packet(ChatProtocol.ServerboundSignedCommand, commandData.ToArray())).HasSignedArguments,
        "signed-command envelope with no signed args parsed");

    using var stream = new MemoryStream();
    using (var channel = new PacketChannel(stream))
        await ChatProtocol.SendSystemAsync(channel, "Server hello", CancellationToken.None);
    stream.Position = 0;
    Packet output = (await Protocol.ReadPacketAsync(stream, CancellationToken.None))!.Value;
    PacketReader r = output.Reader();
    Check(output.Id == 0x7C && r.ReadByte() == 8, "system chat uses protocol 777 NBT packet");
    int count = r.ReadUInt16();
    string text = Encoding.UTF8.GetString(r.ReadFixedBytes(count));
    Check(text == "Server hello" && r.ReadByte() == 0 && r.Remaining == 0,
        "system chat text and overlay flag round-trip");
    Console.WriteLine("Chat & OP tests: PASS");
}
finally
{
    Directory.Delete(temp, true);
}

static bool HasCommand(byte[] bytes, string name)
{
    var r = new PacketReader(bytes);
    int count = r.ReadVarInt();
    bool found = false;
    for (int i = 0; i < count; i++)
    {
        byte flags = r.ReadByte();
        int n = r.ReadVarInt();
        for (int c = 0; c < n; c++) _ = r.ReadVarInt();
        if ((flags & 0x08) != 0) _ = r.ReadVarInt();
        if ((flags & 3) is 1 or 2)
        {
            string label = r.ReadString(32767);
            if ((flags & 3) == 1 && label == name) found = true;
            if ((flags & 3) == 2)
            {
                int parser = r.ReadVarInt();
                if (parser != 5 || r.ReadVarInt() != 2)
                    throw new Exception("Bad brigadier:string greedy argument payload");
            }
        }
        if ((flags & 0x10) != 0) _ = r.ReadString(32767);
    }
    Check(r.ReadVarInt() == 0 && r.Remaining == 0, "command tree fully decodable");
    return found;
}
