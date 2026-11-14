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
    public int OthersLeftNoPermission { get; set; }
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

    public async Task<ChatPurgeResult> PurgeAsync(TelegramTarget target, IReadOnlySet<int> verifiedIds, DateTime cutoffUtc, bool dryRun)
    {
        var result = new ChatPurgeResult { Chat = target.Info };
        int? revokeWindow = PurgeRule.RevokeWindowFor(target.Info.Kind, tlConfig);

        // One full scan of the chat decides what to delete. Own messages are recognised by Telegram's "outgoing" flag
        // on each message, not by a search on the sender, because channel posts and some group messages carry no sender.
        var server = await CurrentMessagesAsync(target);
        var plan = PurgePlanner.Plan(target.Info.Scope, server, cutoffUtc, verifiedIds, revokeWindow, DateTime.UtcNow);
        result.NotBackedUp.AddRange(plan.NotBackedUp);
        result.BeyondRevokeWindow.AddRange(plan.BeyondRevokeWindow);
        result.OthersLeftNoPermission = plan.OthersNotDeletable.Count;
        if (dryRun)
        {
            result.Remaining.AddRange(plan.ToDelete);
            return result;
        }

        var permanentlyFailed = new HashSet<int>();
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
            // Re-check only the messages we tried to delete instead of re-reading the whole history.
            pending = await StillPresentAsync(target, todo);
        }

        // Independent final check: re-read the whole chat from Telegram and plan again. This also catches anything
        // the first scan missed, not just the messages that were planned.
        Status.Info("    re-reading the chat to confirm nothing in scope is left…");
        var after = await CurrentMessagesAsync(target);
        var finalPlan = PurgePlanner.Plan(target.Info.Scope, after, cutoffUtc, verifiedIds, revokeWindow, DateTime.UtcNow);
        result.Remaining.AddRange(finalPlan.ToDelete);
        result.NotBackedUp.Clear(); result.NotBackedUp.AddRange(finalPlan.NotBackedUp);
        result.BeyondRevokeWindow.Clear(); result.BeyondRevokeWindow.AddRange(finalPlan.BeyondRevokeWindow);
        result.OthersLeftNoPermission = finalPlan.OthersNotDeletable.Count;
        var stillThere = after.Select(m => m.Id).ToHashSet();
        result.Deleted = plan.ToDelete.Count(id => !stillThere.Contains(id));
        return result;
    }

    /// <summary>Every message of the chat currently on the server (own comments only, for comments groups).</summary>
    async Task<List<ServerMessage>> CurrentMessagesAsync(TelegramTarget target)
    {
        // Only id, date and ownership are kept per message, so even huge chats fit comfortably in memory.
        var (messages, _) = await History.ScanAsync(api, target, dir,
            n => Status.Progress($"    scanning the chat: {n} messages so far…"));
        Status.EndProgress();
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
