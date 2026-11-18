namespace Amethyst;

/// <summary>
/// Completes full console command lines without looking at network state.
/// Player and operator names are passed in by the server at completion time.
/// </summary>
public static class ConsoleCommandCompletion
{
    public static readonly string[] Commands =
    ["help", "list", "oplist", "op", "deop", "kick", "say", "perf", "stop", "clear"];

    public static IReadOnlyList<string> Suggest(string prefix,
        IEnumerable<string> onlinePlayers, IEnumerable<string> operatorNames)
    {
        if (prefix.Length == 0) return Array.Empty<string>();
        bool slash = prefix.StartsWith("/", StringComparison.Ordinal);
        string input = slash ? prefix[1..] : prefix;
        int separator = input.IndexOf(' ');
        if (separator < 0)
        {
            return Commands.Where(c => c.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                .Select(c => (slash ? "/" : "") + c + " ")
                .ToArray();
        }

        string verb = input[..separator].ToLowerInvariant();
        string argument = input[(separator + 1)..];
        if (argument.Contains(' ')) return Array.Empty<string>();
        IEnumerable<string> candidates = verb switch
        {
            "op" or "kick" => onlinePlayers,
            "deop" => operatorNames.Concat(onlinePlayers),
            _ => Array.Empty<string>()
        };
        return candidates.Where(name => name.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => (slash ? "/" : "") + verb + " " + name + " ")
            .ToArray();
    }
}
