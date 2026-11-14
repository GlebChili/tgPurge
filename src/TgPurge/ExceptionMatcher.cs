namespace TgPurge;

public sealed record ExceptionResolution(
    IReadOnlySet<ChatKey> Excluded,
    IReadOnlyList<string> Unmatched,
    IReadOnlyList<(string Token, IReadOnlyList<ChatInfo> Matches)> Ambiguous)
{
    public bool IsValid => Unmatched.Count == 0 && Ambiguous.Count == 0;
}

/// <summary>
/// Resolves user-supplied exception tokens to chats. Fails closed: any token that matches no chat,
/// or matches more than one chat by title, makes the resolution invalid so nothing is purged by mistake.
/// </summary>
public static class ExceptionMatcher
{
    public static IEnumerable<string> SplitInteractive(string line) =>
        line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static ExceptionResolution Resolve(IEnumerable<string> tokens, IReadOnlyList<ChatInfo> chats)
    {
        var excluded = new HashSet<ChatKey>();
        var unmatched = new List<string>();
        var ambiguous = new List<(string, IReadOnlyList<ChatInfo>)>();

        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            var matches = Match(token, chats);
            if (matches.Count == 0) unmatched.Add(token);
            else if (matches.Count > 1) ambiguous.Add((token, matches));
            else excluded.Add(matches[0].Key);
        }
        // Keeping a chat also keeps what belongs to it (pre-upgrade history of a supergroup, comments under a channel).
        bool added;
        do
        {
            added = false;
            foreach (var c in chats)
                if (c.Parent is { } parent && excluded.Contains(parent) && excluded.Add(c.Key)) added = true;
        } while (added);
        return new ExceptionResolution(excluded, unmatched, ambiguous);
    }

    static List<ChatInfo> Match(string token, IReadOnlyList<ChatInfo> chats)
    {
        // Unambiguous "user:123" / "chat:123" / "channel:123", as shown in the chat table.
        var colon = token.IndexOf(':');
        if (colon > 0 && Enum.TryParse<PeerSpace>(token[..colon], ignoreCase: true, out var space)
            && long.TryParse(token[(colon + 1)..], out var keyId))
            return chats.Where(c => c.Key == new ChatKey(space, keyId)).ToList();

        // Numeric: raw MTProto id or Bot-API style id. Ids may collide across kinds, so all matches are returned.
        if (long.TryParse(token, out var num))
        {
            var byId = chats.Where(c => c.Id == num || c.BotApiId == num).ToList();
            if (byId.Count > 0) return byId;
        }

        var name = token.StartsWith('@') ? token[1..] : token;
        var byUsername = chats
            .Where(c => c.Username is not null && c.Username.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byUsername.Count > 0) return byUsername;

        var byExactTitle = chats.Where(c => c.Title == token).ToList();
        if (byExactTitle.Count > 0) return byExactTitle;

        return chats.Where(c => c.Title.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
