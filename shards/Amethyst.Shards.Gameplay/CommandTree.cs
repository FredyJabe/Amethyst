namespace Amethyst;

// Clientbound Commands for Java 26.3 (777). The core Brigadier parsers are
// stable IDs 0..5; brigadier:string is parser 5. This small tree declares only
// the commands the server actually implements. Authorization is ALSO checked
// when executing, never inferred from what the client was shown.
public static class CommandTree
{
    private sealed record Node(string Name, bool IsArgument, bool Executable, bool Greedy, int[] Children);
    private static readonly (string Name, int RequiredLevel, bool NeedsArgument)[] Specs =
    {
        ("help", 0, false), ("list", 0, false), ("whoami", 0, false),
        ("msg", 0, true), ("tell", 0, true), ("w", 0, true), ("me", 0, true),
        ("gamemode", 1, true),
        ("say", 2, true), ("kick", 2, true), ("oplist", 2, false),
        ("enchant", 2, true), ("summon", 2, true),
        ("op", 4, true), ("deop", 4, true)
    };

    public static byte[] Encode(int permissionLevel)
    {
        if (permissionLevel is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(permissionLevel));
        List<Node> nodes = new() { new Node("", false, false, false, Array.Empty<int>()) };
        List<int> rootChildren = new();
        foreach (var spec in Specs.Where(s => s.RequiredLevel <= permissionLevel))
        {
            int literalId = nodes.Count;
            rootChildren.Add(literalId);
            if (spec.NeedsArgument)
            {
                nodes.Add(new Node(spec.Name, false, spec.Name == "gamemode", false, new[] { literalId + 1 }));
                nodes.Add(new Node("args", true, true, true, Array.Empty<int>()));
            }
            else nodes.Add(new Node(spec.Name, false, true, false, Array.Empty<int>()));
        }
        nodes[0] = nodes[0] with { Children = rootChildren.ToArray() };
        var p = new PacketWriter();
        p.WriteVarInt(nodes.Count);
        foreach (Node node in nodes)
        {
            bool root = node.Name.Length == 0;
            byte flags = root ? (byte)0 : node.IsArgument ? (byte)2 : (byte)1;
            if (node.Executable) flags |= 0x04;
            p.WriteByte(flags);
            p.WriteVarInt(node.Children.Length);
            foreach (int child in node.Children) p.WriteVarInt(child);
            if (!root)
            {
                p.WriteString(node.Name);
                if (node.IsArgument)
                {
                    p.WriteVarInt(5); // brigadier:string parser (26.3)
                    p.WriteVarInt(node.Greedy ? 2 : 0); // greedy string accepts full remainder
                }
            }
        }
        p.WriteVarInt(0); // root node index
        return p.ToArray();
    }

    public static Task SendAsync(PacketChannel channel, int permissionLevel, CancellationToken ct)
    {
        byte[] body = Encode(permissionLevel);
        return channel.SendAsync(ChatProtocol.ClientboundCommands, p => p.WriteBytes(body), ct);
    }
}
