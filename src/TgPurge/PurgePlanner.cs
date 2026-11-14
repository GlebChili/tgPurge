namespace TgPurge;

/// <summary>A message currently present on the server, as seen during the purge phase.</summary>
public readonly record struct ServerMessage(int Id, DateTime DateUtc, bool IsOwn);

public sealed record PurgePlan(
    IReadOnlyList<int> ToDelete,
    /// <summary>Older than the cut-off and in scope, but not in the verified backup: never deleted.</summary>
    IReadOnlyList<int> NotBackedUp,
    /// <summary>
    /// Older than Telegram's "delete for everyone" window: deleting would only remove them for the user,
    /// which this tool must never do, so they are left untouched and reported as critical.
    /// </summary>
    IReadOnlyList<int> BeyondRevokeWindow,
    /// <summary>Other people's messages in a chat where only own messages may be deleted.</summary>
    IReadOnlyList<int> OthersNotDeletable);

/// <summary>
/// The single definition of which messages a run deletes. The backup copies exactly these messages and nothing else,
/// and the purge deletes only these (and only once they are in the verified backup).
/// </summary>
public static class PurgeRule
{
    /// <summary>Sent before the cut-off.</summary>
    public static bool BeforeCutoff(DateTime dateUtc, DateTime cutoffUtc) => dateUtc < cutoffUtc;

    /// <summary>Telegram lets the user delete it for everyone in this chat (by ownership).</summary>
    public static bool UserMayDelete(DeleteScope scope, bool isOwn) => scope switch
    {
        DeleteScope.Everyone => true,
        DeleteScope.OwnOnly => isOwn,
        _ => false,
    };

    /// <summary>Within Telegram's "delete for everyone" age limit (int.MaxValue or null means no limit).</summary>
    public static bool WithinRevokeWindow(int? revokeWindowSeconds, DateTime dateUtc, DateTime nowUtc) =>
        revokeWindowSeconds is not int limit || limit >= int.MaxValue || (nowUtc - dateUtc).TotalSeconds < limit;

    /// <summary>This run deletes the message (given it is in the verified backup) — and therefore backs it up.</summary>
    public static bool IsPurgeable(DeleteScope scope, bool isOwn, DateTime dateUtc, DateTime cutoffUtc, int? revokeWindowSeconds, DateTime nowUtc) =>
        BeforeCutoff(dateUtc, cutoffUtc) && UserMayDelete(scope, isOwn) && WithinRevokeWindow(revokeWindowSeconds, dateUtc, nowUtc);

    /// <summary>
    /// The age limit for "delete for everyone", where exceeding it could silently turn a deletion into "for me only".
    /// Per the API docs revoke_pm_time_limit covers private chats and revoke_time_limit covers channels/supergroups;
    /// basic groups are not documented, so the stricter of the two is applied there.
    /// Channels and supergroups get no guard: their messages are stored once for everyone, so Telegram either deletes
    /// them for everyone or refuses with an error, which is reported.
    /// </summary>
    public static int? RevokeWindowFor(ChatKind kind, TL.Config config) => kind switch
    {
        ChatKind.Private => config.revoke_pm_time_limit,
        ChatKind.BasicGroup => Math.Min(config.revoke_pm_time_limit, config.revoke_time_limit),
        _ => null,
    };
}

public static class PurgePlanner
{
    /// <param name="revokeWindowSeconds">
    /// Telegram's revoke time limit for this chat kind (Config.revoke_pm_time_limit / revoke_time_limit),
    /// or null when deletion is always for everyone (supergroups and channels).
    /// </param>
    public static PurgePlan Plan(
        DeleteScope scope,
        IEnumerable<ServerMessage> serverMessages,
        DateTime cutoffUtc,
        IReadOnlySet<int> verifiedBackupIds,
        int? revokeWindowSeconds,
        DateTime nowUtc)
    {
        var toDelete = new List<int>();
        var notBackedUp = new List<int>();
        var beyondWindow = new List<int>();
        var others = new List<int>();
        if (scope == DeleteScope.None)
            return new PurgePlan(toDelete, notBackedUp, beyondWindow, others);

        foreach (var m in serverMessages)
        {
            if (!PurgeRule.BeforeCutoff(m.DateUtc, cutoffUtc)) continue;
            if (!PurgeRule.UserMayDelete(scope, m.IsOwn))
            {
                others.Add(m.Id);
                continue;
            }
            if (!PurgeRule.WithinRevokeWindow(revokeWindowSeconds, m.DateUtc, nowUtc))
            {
                beyondWindow.Add(m.Id);
                continue;
            }
            if (!verifiedBackupIds.Contains(m.Id))
            {
                notBackedUp.Add(m.Id);
                continue;
            }
            toDelete.Add(m.Id);
        }
        toDelete.Sort();
        return new PurgePlan(toDelete, notBackedUp, beyondWindow, others);
    }

    public static IEnumerable<int[]> Batches(IReadOnlyList<int> ids, int size = 100)
    {
        for (int i = 0; i < ids.Count; i += size)
            yield return ids.Skip(i).Take(size).ToArray();
    }
}
