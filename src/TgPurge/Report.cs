using System.Text;
using TgPurge.Telegram;

namespace TgPurge;

public static class Report
{
    public static string Build(string backupRoot, DateTime cutoffUtc, bool dryRun,
        IReadOnlyList<ChatPurgeResult> results, IReadOnlyList<(ChatInfo Chat, string Reason)> skipped,
        IReadOnlyList<string> inaccessible, IReadOnlyList<ChatVerification> verifications)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# tgPurge {(dryRun ? "dry-run " : "")}report");
        sb.AppendLine();
        sb.AppendLine($"- Backup: `{backupRoot}`");
        sb.AppendLine($"- Cut-off: {ConsoleUi.Local(cutoffUtc)}");
        sb.AppendLine($"- Finished: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- Chats backed up: {verifications.Count}, fully verified: {verifications.Count(v => v.Ok)}, " +
            $"partly verified: {verifications.Count(v => !v.Ok && v.VerifiedIds.Count > 0)}, " +
            $"not verified: {verifications.Count(v => !v.Ok && v.VerifiedIds.Count == 0)}");
        if (!dryRun)
            sb.AppendLine($"- Messages deleted for everyone: {results.Sum(r => r.Deleted)}");
        sb.AppendLine();

        var critical = new List<string>();
        foreach (var i in inaccessible)
            critical.Add($"not processed — {i}");
        var resultByKey = results.GroupBy(r => r.Chat.Key).ToDictionary(g => g.Key, g => g.Last());
        foreach (var v in verifications.Where(v => !v.Ok))
        {
            if (v.VerifiedIds.Count == 0)
                critical.Add($"{v.Title}: backup not verified, chat NOT purged");
            else
            {
                // The outcome is what the purge measured, not what verification allowed.
                var outcome = !resultByKey.TryGetValue(v.Key, out var pr) ? "the chat was not purged"
                    : dryRun ? $"{pr.Remaining.Count} message(s) would be deleted"
                    : $"{pr.Deleted} message(s) were deleted";
                var kept = v.Unverified > 0 ? $"; {v.Unverified} backed-up message(s) that did not verify stay on Telegram" : "";
                critical.Add($"{v.Title}: backup only partly verified ({v.VerifiedIds.Count} of {v.BackedUp} backed-up message(s) verified); {outcome}{kept}");
            }
            // Chat-level problems are listed in full: they are how unread or untrusted messages become visible.
            foreach (var p in v.ChatProblems) critical.Add($"{v.Title}: {p}");
            if (v.MessageProblems.Count > 0)
                critical.Add($"{v.Title}: {v.MessageProblems.Count} message problem(s), listed under \"Backup problems\" below");
        }
        foreach (var r in results)
        {
            var t = r.Chat.Title;
            if (!dryRun && r.Remaining.Count > 0)
                critical.Add($"{t}: {r.Remaining.Count} in-scope message(s) before the cut-off are STILL on Telegram (ids {Ids(r.Remaining)})");
            if (r.NotBackedUp.Count > 0)
                critical.Add($"{t}: {r.NotBackedUp.Count} message(s) are not in the verified backup (their backup failed or they were not read) and were NOT deleted (ids {Ids(r.NotBackedUp)})");
            if (r.BeyondRevokeWindow.Count > 0)
                critical.Add($"{t}: {r.BeyondRevokeWindow.Count} message(s) are older than Telegram's delete-for-everyone window; they were NOT deleted because that would only remove them for you (ids {Ids(r.BeyondRevokeWindow)})");
            // Errors about the chat (e.g. a short checking read) always; per-message delete errors capped, their ids are listed above.
            foreach (var e in r.Errors.Where(e => !e.StartsWith("message #", StringComparison.Ordinal))) critical.Add($"{t}: {e}");
            var perMessage = r.Errors.Where(e => e.StartsWith("message #", StringComparison.Ordinal)).ToList();
            foreach (var e in perMessage.Take(5)) critical.Add($"{t}: {e}");
            if (perMessage.Count > 5) critical.Add($"{t}: … and {perMessage.Count - 5} more message deletion error(s)");
        }

        sb.AppendLine("## CRITICAL");
        sb.AppendLine();
        if (critical.Count == 0) sb.AppendLine("None.");
        foreach (var c in critical) sb.AppendLine($"- ❌ {c}");
        sb.AppendLine();

        sb.AppendLine("## Chats");
        sb.AppendLine();
        sb.AppendLine(dryRun
            ? "| chat | type | scope | would delete | others' msgs left (no permission) |"
            : "| chat | type | scope | deleted | remaining | others' msgs left (no permission) |");
        sb.AppendLine(dryRun ? "|---|---|---|---|---|" : "|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var scope = r.Chat.Scope == DeleteScope.Everyone ? "all, both sides" : "own only";
            sb.AppendLine(dryRun
                ? $"| {Cell(r.Chat.Title)} | {r.Chat.KindLabel} | {scope} | {r.Remaining.Count} | {Others(r)} |"
                : $"| {Cell(r.Chat.Title)} | {r.Chat.KindLabel} | {scope} | {r.Deleted} | {r.Remaining.Count} | {Others(r)} |");
        }
        sb.AppendLine();

        var withMessageProblems = verifications.Where(v => v.MessageProblems.Count > 0).ToList();
        if (withMessageProblems.Count > 0)
        {
            sb.AppendLine("## Backup problems");
            sb.AppendLine();
            sb.AppendLine("_None of these messages was deleted; they are still on Telegram._");
            sb.AppendLine();
            foreach (var v in withMessageProblems)
            {
                sb.AppendLine($"### {v.Title}");
                sb.AppendLine();
                foreach (var p in v.MessageProblems) sb.AppendLine($"- {p}");
                sb.AppendLine();
            }
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine("## Not purged");
            sb.AppendLine();
            sb.AppendLine("_This list names chats that were left on Telegram, including your exceptions._");
            sb.AppendLine();
            foreach (var (chat, reason) in skipped) sb.AppendLine($"- {chat.Title} ({chat.KindLabel}, id {chat.Id}): {reason}");
            sb.AppendLine();
        }
        if (inaccessible.Count > 0)
        {
            sb.AppendLine("## Inaccessible");
            sb.AppendLine();
            foreach (var i in inaccessible) sb.AppendLine($"- {i}");
            sb.AppendLine();
        }
        sb.AppendLine("## Not done by this tool");
        sb.AppendLine();
        sb.AppendLine("- Other logged-in sessions (Settings → Devices), stories, profile photos, scheduled messages, drafts,");
        sb.AppendLine("  groups/channels you already left, synced contacts.");
        sb.AppendLine();
        sb.AppendLine("## Not possible for any tool");
        sb.AppendLine();
        sb.AppendLine("- Secret chats (device-only; delete them in the Telegram app on each device).");
        sb.AppendLine("- Messages earlier deleted \"only for me\" (the other side keeps them), and copies already exported,");
        sb.AppendLine("  forwarded, screenshotted or cached on offline devices.");
        sb.AppendLine("- Telegram does not let a client confirm the other side's copy is gone; deletion was always requested for everyone.");
        return sb.ToString();
    }

    /// <summary>Saves the report without ever throwing; deletion has already happened, so failing to save must not lose the outcome.</summary>
    public static bool TrySave(string path, string report, out string? error)
    {
        try
        {
            BackupStore.WriteAllTextAtomic(path, report);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    static string Ids(IEnumerable<int> ids)
    {
        var list = ids.ToList();
        return string.Join(", ", list.Take(20)) + (list.Count > 20 ? $", … (+{list.Count - 20})" : "");
    }

    static string Cell(string s) => s.Replace("|", "\\|");

    // In groups where only your messages can be deleted, only yours are read, so others' are not counted.
    static string Others(Telegram.ChatPurgeResult r) => r.OthersLeftNoPermission?.ToString() ?? "not read";
}
