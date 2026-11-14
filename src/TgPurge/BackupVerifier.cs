namespace TgPurge;

public sealed record ChatVerification(ChatKey Key, string Title, bool Ok, IReadOnlyList<string> Problems, IReadOnlySet<int> VerifiedIds);

/// <summary>
/// Independently re-reads a finished backup from disk and checks it is complete and intact.
/// A chat is only eligible for purging if it verifies cleanly here.
/// </summary>
public static class BackupVerifier
{

    public static IReadOnlyList<ChatVerification> Verify(BackupStore store, out IReadOnlyList<string> globalProblems,
        Action<string>? progress = null)
    {
        var global = new List<string>();
        globalProblems = global;
        BackupManifest manifest;
        try
        {
            manifest = store.ReadManifest();
        }
        catch (Exception ex)
        {
            global.Add($"manifest.json unreadable: {ex.Message}");
            return [];
        }
        var results = new List<ChatVerification>();
        for (int i = 0; i < manifest.Chats.Count; i++)
        {
            var chat = manifest.Chats[i];
            var prefix = $"verifying chat {i + 1}/{manifest.Chats.Count} \"{chat.Title}\"";
            progress?.Invoke(prefix);
            results.Add(VerifyChat(store, chat, progress is null ? null : detail => progress($"{prefix}: {detail}")));
        }
        return results;
    }

    /// <summary>
    /// Verifies one chat by streaming its files: messages.jsonl record by record and each Markdown part line by line.
    /// Only message ids and counters are kept in memory.
    /// </summary>
    public static ChatVerification VerifyChat(BackupStore store, ChatBackup chat, Action<string>? progress = null)
    {
        var problems = new List<string>(chat.Errors.Select(e => "backup error: " + e));
        var dir = store.ChatDir(chat);
        var jsonPath = Path.Combine(dir, BackupStore.MessagesFile);
        var ids = new HashSet<int>();

        if (!Directory.Exists(dir)) return Fail("chat folder missing");
        if (!File.Exists(jsonPath)) return Fail($"{BackupStore.MessagesFile} missing");
        if (chat.HistoryParts.Count == 0) return Fail("no Markdown history recorded");

        if (chat.MessagesJsonSha256 is null || !BackupStore.Sha256OfFile(jsonPath).Equals(chat.MessagesJsonSha256, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{BackupStore.MessagesFile} checksum mismatch");

        // 1. Every Markdown part: present, unchanged, and each message entry (anchor line) counted.
        var anchorCounts = new Dictionary<int, int>();
        foreach (var part in chat.HistoryParts)
        {
            var path = Path.Combine(dir, part.File);
            if (!File.Exists(path)) { problems.Add($"{part.File} missing"); continue; }
            if (!BackupStore.Sha256OfFile(path).Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{part.File} checksum mismatch");
            progress?.Invoke($"reading {part.File}");
            foreach (var line in BackupStore.ReadLines(path))
                if (MarkdownRenderer.TryParseAnchor(line, out var id))
                    anchorCounts[id] = anchorCounts.GetValueOrDefault(id) + 1;
        }

        // 2. messages.jsonl, one record at a time.
        int count = 0, mediaDone = 0;
        try
        {
            foreach (var m in store.ReadMessages(chat))
            {
                count++;
                if (!ids.Add(m.Id)) { problems.Add($"duplicate message #{m.Id}"); continue; }
                switch (anchorCounts.GetValueOrDefault(m.Id))
                {
                    case 0: problems.Add($"message #{m.Id} missing from the Markdown history"); break;
                    case > 1: problems.Add($"message #{m.Id} appears more than once in the Markdown history"); break;
                }
                switch (m.MediaStatus)
                {
                    case MediaStatus.Failed:
                        problems.Add($"message #{m.Id}: media download failed ({m.MediaError})");
                        break;
                    case MediaStatus.Downloaded:
                        progress?.Invoke($"checking media file {++mediaDone}");
                        VerifyMedia(dir, m, problems);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return Fail($"{BackupStore.MessagesFile} unreadable: {ex.Message}");
        }

        if (count != chat.MessageCount)
            problems.Add($"manifest says {chat.MessageCount} messages, {BackupStore.MessagesFile} has {count}");
        if (chat.HistoryParts.Sum(p => p.Messages) != chat.MessageCount)
            problems.Add($"Markdown parts hold {chat.HistoryParts.Sum(p => p.Messages)} messages, manifest says {chat.MessageCount}");
        foreach (var extra in anchorCounts.Keys.Where(id => !ids.Contains(id)).Take(5))
            problems.Add($"Markdown history has an entry #{extra} that is not in {BackupStore.MessagesFile}");

        // Reading the chat must have been complete, or messages to delete could be missing from the backup.
        // (Only a subset is backed up, so the check is on what was read, not on what was saved.)
        // A short read is only acceptable when Telegram itself said the count was approximate.
        if (chat.ServerReportedCount is int reported && !chat.ServerCountInexact && chat.MessagesScanned < reported
            && chat.Kind != ChatKind.DiscussionComments)
            problems.Add($"Telegram reported {reported} messages but only {chat.MessagesScanned} could be read");
        if (count > chat.MessagesScanned && chat.MessagesScanned > 0)
            problems.Add($"backup holds {count} messages but only {chat.MessagesScanned} were read");

        var ok = problems.Count == 0;
        return new ChatVerification(chat.Key, chat.Title, ok, problems, ok ? ids : new HashSet<int>());

        ChatVerification Fail(string reason)
        {
            problems.Add(reason);
            return new ChatVerification(chat.Key, chat.Title, false, problems, new HashSet<int>());
        }
    }

    static void VerifyMedia(string chatDir, MessageRecord m, List<string> problems)
    {
        if (string.IsNullOrEmpty(m.MediaFile)) { problems.Add($"message #{m.Id}: media path missing"); return; }
        var full = Path.GetFullPath(Path.Combine(chatDir, m.MediaFile));
        if (!full.StartsWith(Path.GetFullPath(chatDir), StringComparison.Ordinal))
        { problems.Add($"message #{m.Id}: media path escapes chat folder"); return; }
        if (!File.Exists(full)) { problems.Add($"message #{m.Id}: media file missing ({m.MediaFile})"); return; }

        var len = new FileInfo(full).Length;
        if (m.MediaSize is null || len != m.MediaSize)
            problems.Add($"message #{m.Id}: media size {len} differs from recorded {m.MediaSize}");
        if (m.MediaExpectedSize is long expected && expected > 0 && len != expected)
            problems.Add($"message #{m.Id}: media size {len} differs from Telegram's declared size {expected}");
        if (len == 0)
            problems.Add($"message #{m.Id}: media file is empty");
        if (m.MediaSha256 is null || !BackupStore.Sha256OfFile(full).Equals(m.MediaSha256, StringComparison.OrdinalIgnoreCase))
            problems.Add($"message #{m.Id}: media checksum mismatch");
    }
}
