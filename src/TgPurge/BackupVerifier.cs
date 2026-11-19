namespace TgPurge;

/// <param name="ChatProblems">
/// Problems with the chat as a whole. Integrity problems (the backup's own files disagree with each other) leave
/// <paramref name="VerifiedIds"/> empty; a history read that failed or came up short does not.
/// </param>
/// <param name="MessageProblems">Problems with single messages; each keeps only its own message on Telegram.</param>
/// <param name="VerifiedIds">
/// Messages whose own backup verified (record, Markdown entry and media file). Only these may be deleted.
/// Empty when the backup of the chat cannot be trusted as a whole.
/// </param>
/// <param name="BackedUp">Distinct message ids found in messages.jsonl.</param>
public sealed record ChatVerification(ChatKey Key, string Title, IReadOnlyList<string> ChatProblems,
    IReadOnlyList<string> MessageProblems, IReadOnlySet<int> VerifiedIds, int BackedUp)
{
    /// <summary>The whole chat verified cleanly: every message, file and count.</summary>
    public bool Ok => ChatProblems.Count == 0 && MessageProblems.Count == 0;
    /// <summary>Chat-level problems first, then per-message ones.</summary>
    public IReadOnlyList<string> Problems => [.. ChatProblems, .. MessageProblems];
    /// <summary>Records in the backup that did not verify and must therefore stay on Telegram.</summary>
    public int Unverified => Math.Max(0, BackedUp - VerifiedIds.Count);
}

/// <summary>
/// Independently re-reads a finished backup from disk and checks it is complete and intact, message by message.
/// A message is only eligible for deletion if its own backup verifies here.
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
    /// Problems with one message (its Markdown entry, its media file) exclude only that message from
    /// <see cref="ChatVerification.VerifiedIds"/>; problems with the chat as a whole (read errors, counts) are reported
    /// but do not affect messages that verified on their own. If messages.jsonl itself is missing, unreadable or
    /// altered, no message is trusted.
    /// </summary>
    public static ChatVerification VerifyChat(BackupStore store, ChatBackup chat, Action<string>? progress = null)
    {
        // Chat-level problems that do not undermine the saved messages themselves (a read that failed or came up
        // short: what was saved is still good, what was not read stays on Telegram).
        var chatProblems = new List<string>(chat.Errors.Select(e => "backup error: " + e));
        // Integrity problems: the backup's files disagree with each other, which no correct run produces.
        // Nothing in the chat is trusted then, because deletion of private and basic-group messages names no chat.
        var integrity = new List<string>();
        var messageProblems = new List<string>();
        var dir = store.ChatDir(chat);
        var jsonPath = Path.Combine(dir, BackupStore.MessagesFile);

        if (!Directory.Exists(dir)) return Fail("chat folder missing");
        if (!File.Exists(jsonPath)) return Fail($"{BackupStore.MessagesFile} missing");
        if (chat.HistoryParts.Count == 0) return Fail("no Markdown history recorded");
        if (chat.MessagesJsonSha256 is null || !BackupStore.Sha256OfFile(jsonPath).Equals(chat.MessagesJsonSha256, StringComparison.OrdinalIgnoreCase))
            return Fail($"{BackupStore.MessagesFile} checksum mismatch");

        // 1. Every Markdown part: present, unchanged, and each message entry (anchor line) counted.
        //    Entries in a part that was altered are not trusted.
        var anchorCounts = new Dictionary<int, int>();
        var inAlteredPart = new HashSet<int>();
        foreach (var part in chat.HistoryParts)
        {
            var path = Path.Combine(dir, part.File);
            if (!File.Exists(path)) { messageProblems.Add($"{part.File} missing"); continue; }
            bool altered = !BackupStore.Sha256OfFile(path).Equals(part.Sha256, StringComparison.OrdinalIgnoreCase);
            if (altered) messageProblems.Add($"{part.File} checksum mismatch");
            progress?.Invoke($"reading {part.File}");
            foreach (var line in BackupStore.ReadLines(path))
                if (MarkdownRenderer.TryParseAnchor(line, out var id))
                {
                    anchorCounts[id] = anchorCounts.GetValueOrDefault(id) + 1;
                    if (altered) inAlteredPart.Add(id);
                }
        }

        // 2. messages.jsonl, one record at a time; each message is accepted or rejected on its own.
        var ids = new HashSet<int>();
        var verified = new HashSet<int>();
        var duplicates = new HashSet<int>();
        int count = 0, mediaDone = 0;
        try
        {
            foreach (var m in store.ReadMessages(chat))
            {
                count++;
                if (!ids.Add(m.Id)) { messageProblems.Add($"duplicate message #{m.Id}"); duplicates.Add(m.Id); continue; }
                bool good = true;
                switch (anchorCounts.GetValueOrDefault(m.Id))
                {
                    case 0: messageProblems.Add($"message #{m.Id} missing from the Markdown history"); good = false; break;
                    case > 1: messageProblems.Add($"message #{m.Id} appears more than once in the Markdown history"); good = false; break;
                }
                if (inAlteredPart.Contains(m.Id)) good = false;
                switch (m.MediaStatus)
                {
                    case MediaStatus.Failed:
                        messageProblems.Add($"message #{m.Id}: media download failed ({m.MediaError})");
                        good = false;
                        break;
                    case MediaStatus.Downloaded:
                        progress?.Invoke($"checking media file {++mediaDone}");
                        if (!VerifyMedia(dir, m, messageProblems)) good = false;
                        break;
                }
                if (good) verified.Add(m.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return Fail($"{BackupStore.MessagesFile} unreadable: {ex.Message}");
        }
        verified.ExceptWith(duplicates);

        if (count != chat.MessageCount)
            integrity.Add($"manifest says {chat.MessageCount} messages, {BackupStore.MessagesFile} has {count}");
        if (chat.HistoryParts.Sum(p => p.Messages) != chat.MessageCount)
            integrity.Add($"Markdown parts hold {chat.HistoryParts.Sum(p => p.Messages)} messages, manifest says {chat.MessageCount}");
        foreach (var extra in anchorCounts.Keys.Where(id => !ids.Contains(id)).Take(5))
            integrity.Add($"Markdown history has an entry #{extra} that is not in {BackupStore.MessagesFile}");
        if (count > chat.MessagesScanned && chat.MessagesScanned > 0)
            integrity.Add($"backup holds {count} messages but only {chat.MessagesScanned} were read");

        // A chat read that came up short means messages may be missing from the backup. Those are simply not deleted
        // (only backed-up ids ever are), but the chat cannot count as fully backed up.
        if (Telegram.HistoryStats.IsShortRead(chat.MessagesScanned, chat.ServerReportedCount, chat.ServerCountInexact, chat.Kind))
            chatProblems.Add($"Telegram reported {chat.ServerReportedCount} messages but only {chat.MessagesScanned} could be read: " +
                $"{chat.ServerReportedCount - chat.MessagesScanned} message(s) were not read; any of them that are in scope stay on Telegram");

        if (integrity.Count > 0)
        {
            chatProblems.InsertRange(0, integrity.Select(p => "backup inconsistent: " + p));
            verified.Clear();
        }
        return new ChatVerification(chat.Key, chat.Title, chatProblems, messageProblems, verified, ids.Count);

        ChatVerification Fail(string reason)
        {
            chatProblems.Insert(0, reason);
            return new ChatVerification(chat.Key, chat.Title, chatProblems, messageProblems, new HashSet<int>(), chat.MessageCount);
        }
    }

    /// <returns>True when the media file is present and intact.</returns>
    static bool VerifyMedia(string chatDir, MessageRecord m, List<string> problems)
    {
        int before = problems.Count;
        if (string.IsNullOrEmpty(m.MediaFile)) { problems.Add($"message #{m.Id}: media path missing"); return false; }
        var full = Path.GetFullPath(Path.Combine(chatDir, m.MediaFile));
        if (!full.StartsWith(Path.GetFullPath(chatDir), StringComparison.Ordinal))
        { problems.Add($"message #{m.Id}: media path escapes chat folder"); return false; }
        if (!File.Exists(full)) { problems.Add($"message #{m.Id}: media file missing ({m.MediaFile})"); return false; }

        var len = new FileInfo(full).Length;
        if (m.MediaSize is null || len != m.MediaSize)
            problems.Add($"message #{m.Id}: media size {len} differs from recorded {m.MediaSize}");
        if (m.MediaExpectedSize is long expected && expected > 0 && len != expected)
            problems.Add($"message #{m.Id}: media size {len} differs from Telegram's declared size {expected}");
        if (len == 0)
            problems.Add($"message #{m.Id}: media file is empty");
        if (m.MediaSha256 is null || !BackupStore.Sha256OfFile(full).Equals(m.MediaSha256, StringComparison.OrdinalIgnoreCase))
            problems.Add($"message #{m.Id}: media checksum mismatch");
        return problems.Count == before;
    }
}
