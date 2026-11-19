using TL;
using WTelegram;

namespace TgPurge.Telegram;

public sealed class ChatPurgeResult
{
    public required ChatInfo Chat { get; init; }
    public int Deleted { get; set; }
    /// <summary>In-scope messages older than the cut-off still present after all passes. Non-empty means the purge is incomplete.</summary>
    public List<int> Remaining { get; } = [];
    public List<int> NotBackedUp { get; } = [];
    public List<int> BeyondRevokeWindow { get; } = [];
    /// <summary>Other people's messages before the cut-off left in place; null when only the user's own were read.</summary>
    public int? OthersLeftNoPermission { get; set; }
    public List<string> Errors { get; } = [];
    public bool Complete => Remaining.Count == 0 && NotBackedUp.Count == 0 && BeyondRevokeWindow.Count == 0 && Errors.Count == 0;
}

/// <summary>
/// Deletes messages of one chat. All deletion goes through <see cref="ITelegramApi.DeleteForEveryoneAsync"/>,
/// which only ever deletes for everyone. Nothing is ever deleted "for me only".
/// </summary>
public sealed class Purger(ITelegramApi api, TargetDiscovery dir, TL.Config tlConfig, Action<string> log)
{
    const int MaxPasses = 3;

    /// <summary>
    /// Purges one chat. What to delete is planned from the verified backup, which came from a full read of the chat;
    /// a message that is not in it is never deleted. Afterwards every planned message is looked up by id to confirm it
    /// is gone, and a separate, cheaper read of the chat (see <see cref="History.ReadMode.Check"/>) looks for anything
    /// in scope still there, including messages the backup read missed, which are reported, never deleted.
    /// </summary>
    /// <param name="backedUp">The messages of this chat in the verified backup.</param>
    public async Task<ChatPurgeResult> PurgeAsync(TelegramTarget target, IReadOnlyCollection<ServerMessage> backedUp, DateTime cutoffUtc, bool dryRun)
    {
        var result = new ChatPurgeResult { Chat = target.Info };
        int? revokeWindow = PurgeRule.RevokeWindowFor(target.Info.Kind, tlConfig);
        var verifiedIds = backedUp.Select(m => m.Id).ToHashSet();

        // Planned again from the backup at purge time: hours may have passed, and a message that has meanwhile left
        // Telegram's "delete for everyone" window must not be deleted (that would be "for me only").
        var plan = PurgePlanner.Plan(target.Info.Scope, backedUp, cutoffUtc, verifiedIds, revokeWindow, DateTime.UtcNow);

        var permanentlyFailed = new HashSet<int>();
        IReadOnlyList<int> leftById = [];
        if (!dryRun && plan.ToDelete.Count > 0)
        {
            IReadOnlyList<int> pending = plan.ToDelete;
            for (int pass = 1; pass <= MaxPasses; pass++)
            {
                var todo = pending.Where(id => !permanentlyFailed.Contains(id)).ToList();
                if (todo.Count == 0) break;
                if (pass > 1) log($"    pass {pass}: {todo.Count} messages still present, retrying");

                int done = 0;
                foreach (var batch in PurgePlanner.Batches(todo))
                {
                    try
                    {
                        await Retry.RunAsync(() => api.DeleteForEveryoneAsync(target.Channel, batch));
                    }
                    catch (RpcException ex)
                    {
                        // Isolate the messages that cannot be deleted so the rest of the batch still goes.
                        log($"    batch delete failed ({ex.Message}); deleting one by one");
                        foreach (var id in batch)
                        {
                            try
                            {
                                await Retry.RunAsync(() => api.DeleteForEveryoneAsync(target.Channel, [id]));
                            }
                            catch (RpcException single)
                            {
                                permanentlyFailed.Add(id);
                                result.Errors.Add($"message #{id}: {single.Message}");
                            }
                        }
                    }
                    done += batch.Length;
                    Status.Progress($"    deleting for everyone: {done}/{todo.Count}");
                }
                Status.EndProgress();
                Status.Info($"    checking that the {todo.Count} messages are gone…");
                pending = await StillPresentAsync(target, todo);
            }
            // Every planned message, looked up by id: this does not depend on how the chat is read below.
            leftById = await StillPresentAsync(target, plan.ToDelete);
            result.Deleted = plan.ToDelete.Count - leftById.Count;
        }

        // Independent check of the chat as it is now: anything in scope still on Telegram is reported. Messages the
        // backup read missed show up here as not backed up (never deleted); in a dry run this is what would be deleted.
        Status.Info(dryRun ? "    reading the chat to list what would be deleted…" : "    re-reading the chat to confirm nothing in scope is left…");
        var after = await CurrentMessagesAsync(target, cutoffUtc, result, "final re-read");
        var finalPlan = PurgePlanner.Plan(target.Info.Scope, after, cutoffUtc, verifiedIds, revokeWindow, DateTime.UtcNow);
        result.Remaining.AddRange(finalPlan.ToDelete.Union(leftById).Order());
        if (dryRun)
        {
            // What would be deleted is the plan; anything planned but not seen by the read is listed too.
            result.Remaining.Clear();
            result.Remaining.AddRange(plan.ToDelete.Union(finalPlan.ToDelete).Order());
        }
        result.NotBackedUp.AddRange(finalPlan.NotBackedUp.Order());
        result.BeyondRevokeWindow.AddRange(plan.BeyondRevokeWindow.Union(finalPlan.BeyondRevokeWindow).Order());
        result.OthersLeftNoPermission = OthersCount(target, finalPlan);
        return result;
    }

    static int? OthersCount(TelegramTarget target, PurgePlan plan) =>
        History.ReadsOwnMessagesBySearch(target.Info) ? null : plan.OthersNotDeletable.Count;

    /// <summary>The chat's messages before the cut-off currently on the server (only the user's own where only those can be deleted).
    /// A read that returns fewer messages than Telegram reports is recorded as an error, so the chat is never
    /// reported as fully purged on the strength of a history that was not read completely.</summary>
    async Task<List<ServerMessage>> CurrentMessagesAsync(TelegramTarget target, DateTime cutoffUtc, ChatPurgeResult result, string stage)
    {
        // Only id, date and ownership are kept per message, so even huge chats fit comfortably in memory.
        var (messages, stats) = await History.ScanAsync(api, target, dir,
            n => Status.Progress($"    scanning the chat: {n} messages so far…"), History.ReadMode.Check, cutoffUtc);
        Status.EndProgress();
        if (HistoryStats.IsShortRead(stats.Fetched, stats.ReportedCount, stats.Inexact, target.Info.Kind))
        {
            var error = $"{stage}: Telegram reported {stats.ReportedCount} messages but only {stats.Fetched} could be read; " +
                "messages that were not read may still be on Telegram";
            result.Errors.Add(error);
            log($"    ✗ {error}");
        }
        return messages;
    }

    /// <summary>Which of <paramref name="ids"/> still exist on the server (deleted ones come back as empty messages).</summary>
    async Task<List<int>> StillPresentAsync(TelegramTarget target, IReadOnlyList<int> ids)
    {
        var present = new List<int>();
        foreach (var batch in PurgePlanner.Batches(ids))
        {
            var res = await Retry.RunAsync(() => api.GetMessagesAsync(target.Channel, batch));
            var wanted = batch.ToHashSet();
            present.AddRange(res.Messages.Where(m => m is not MessageEmpty && wanted.Contains(m.ID)).Select(m => m.ID));
        }
        present.Sort();
        return present;
    }

}
