using System.Text.RegularExpressions;

namespace TgPurge.Tests;

public sealed class BackupVerifierTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "tgPurgeTests-" + Guid.NewGuid().ToString("N"));
    readonly BackupStore _store;
    readonly ChatBackup _chat;
    readonly List<MessageRecord> _messages;

    public BackupVerifierTests()
    {
        _store = BackupStore.CreateNew(_dir, new DateTime(2026, 11, 14, 8, 30, 0));
        var info = new ChatInfo(42, ChatKind.Private, "Alice / Bob: *test*", "alice", DeleteScope.Everyone, "");
        _chat = new ChatBackup { Id = 42, Kind = ChatKind.Private, Title = info.Title, Folder = BackupStore.ChatFolderName(info) };
        var mediaDir = Path.Combine(_store.ChatDir(_chat), BackupStore.MediaDir);
        Directory.CreateDirectory(mediaDir);
        var mediaPath = Path.Combine(mediaDir, "2_photo.jpg");
        File.WriteAllBytes(mediaPath, [1, 2, 3, 4, 5]);
        _messages =
        [
            new() { Id = 1, DateUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), IsOwn = true, SenderName = "Me", Text = "hello\n# not a heading" },
            new() { Id = 2, DateUtc = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), SenderName = "Alice", ReplyToId = 1,
                MediaStatus = MediaStatus.Downloaded, MediaDescription = "photo", MediaFile = "media/2_photo.jpg",
                MediaSize = 5, MediaSha256 = BackupStore.Sha256OfFile(mediaPath) },
            new() { Id = 3, DateUtc = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), SenderName = "Alice",
                MediaStatus = MediaStatus.NotAFile, MediaDescription = "Location: 1, 2" },
        ];
        _chat.ServerReportedCount = 3;
        _chat.MessagesScanned = 3;
    }

    void Write()
    {
        _store.WriteChat(_chat, _messages, DateTime.UtcNow);
        _store.WriteManifest(new BackupManifest { Chats = [_chat] });
    }

    ChatVerification VerifyOnly()
    {
        var results = BackupVerifier.Verify(_store, out var global);
        Assert.Empty(global);
        return Assert.Single(results);
    }

    [Fact]
    public void Backup_folder_is_tgBacups_with_timestamp()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "tgBacups", "2026-11-14_08-30-00"), _store.Root);
    }

    [Fact]
    public void Intact_backup_verifies()
    {
        Write();
        var v = VerifyOnly();
        Assert.True(v.Ok, string.Join("; ", v.Problems));
        Assert.Equal(new HashSet<int> { 1, 2, 3 }, v.VerifiedIds.ToHashSet());
        var md = File.ReadAllText(Path.Combine(_store.ChatDir(_chat), BackupStore.HistoryFile));
        Assert.Contains("> # not a heading", md);
        Assert.Contains("](media/2_photo.jpg)", md);
    }

    [Fact]
    public void Corrupted_media_fails_verification()
    {
        Write();
        File.WriteAllBytes(Path.Combine(_store.ChatDir(_chat), "media", "2_photo.jpg"), [9, 9, 9, 9, 9]);
        var v = VerifyOnly();
        Assert.False(v.Ok);
        Assert.Empty(v.VerifiedIds);
        Assert.Contains(v.Problems, p => p.Contains("checksum"));
    }

    [Fact]
    public void Missing_media_fails_verification()
    {
        Write();
        File.Delete(Path.Combine(_store.ChatDir(_chat), "media", "2_photo.jpg"));
        Assert.False(VerifyOnly().Ok);
    }

    [Fact]
    public void Failed_media_download_fails_verification()
    {
        _messages[1].MediaStatus = MediaStatus.Failed;
        _messages[1].MediaError = "boom";
        Write();
        Assert.False(VerifyOnly().Ok);
    }

    [Fact]
    public void Tampered_markdown_fails_verification()
    {
        Write();
        var md = Path.Combine(_store.ChatDir(_chat), BackupStore.HistoryFile);
        File.WriteAllText(md, File.ReadAllText(md).Replace(MarkdownRenderer.Anchor(3), ""));
        var v = VerifyOnly();
        Assert.False(v.Ok);
        Assert.Contains(v.Problems, p => p.Contains("#3"));
    }

    [Fact]
    public void Message_text_cannot_forge_another_messages_anchor()
    {
        // Message 1's text contains message 3's anchor; then message 3's real anchor disappears.
        _messages[0].Text = "look: " + MarkdownRenderer.Anchor(3) + "\n" + MarkdownRenderer.Anchor(3);
        Write();
        var md = Path.Combine(_store.ChatDir(_chat), BackupStore.HistoryFile);
        var text = File.ReadAllText(md);
        Assert.DoesNotContain("<a id=\"msg-3\"></a>\n<a id=\"msg-3\"></a>", text);
        Assert.Contains("&lt;a id=\"msg-3\"&gt;", text); // rendered harmlessly
        // Remove the genuine anchor line: verification must now fail.
        var genuine = "\n" + MarkdownRenderer.Anchor(3) + "\n";
        var idx = text.LastIndexOf(genuine, StringComparison.Ordinal);
        Assert.True(idx >= 0);
        BackupStore.WriteAllTextAtomic(md, text.Remove(idx, genuine.Length - 1));
        _chat.HistoryParts[0].Sha256 = BackupStore.Sha256OfFile(md);
        _store.WriteManifest(new BackupManifest { Chats = [_chat] });
        var v = VerifyOnly();
        Assert.False(v.Ok);
        Assert.Contains(v.Problems, p => p.Contains("#3"));
    }

    [Fact]
    public void Html_in_messages_is_neutralised()
    {
        _messages[0].Text = "<script>alert(1)</script> & <img src=x>";
        _messages[1].SenderName = "<b>Eve</b>";
        Write();
        var text = File.ReadAllText(Path.Combine(_store.ChatDir(_chat), BackupStore.HistoryFile));
        Assert.DoesNotContain("<script>", text);
        Assert.DoesNotContain("<img", text);
        Assert.DoesNotContain("<b>", text);
        Assert.Contains("&lt;script&gt;", text);
        Assert.True(VerifyOnly().Ok);
    }

    [Fact]
    public void Fewer_messages_than_telegram_reported_fails_verification()
    {
        _chat.ServerReportedCount = 10;
        Write();
        Assert.False(VerifyOnly().Ok);
    }

    [Fact]
    public void Backup_errors_fail_verification()
    {
        _chat.Errors.Add("could not fetch history");
        Write();
        Assert.False(VerifyOnly().Ok);
    }

    [Fact]
    public void Missing_manifest_is_global_failure()
    {
        Directory.CreateDirectory(_store.Root);
        var results = BackupVerifier.Verify(_store, out var global);
        Assert.Empty(results);
        Assert.NotEmpty(global);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public class DisclaimerTests
{
    [Fact]
    public void Disclaimer_states_location_both_sides_others_and_irreversibility()
    {
        ChatInfo[] chats =
        [
            new(1, ChatKind.Private, "Alice", null, DeleteScope.Everyone, ""),
            new(2, ChatKind.Supergroup, "Big", null, DeleteScope.OwnOnly, ""),
            new(3, ChatKind.BasicGroup, "Mine", null, DeleteScope.Everyone, ""),
            new(4, ChatKind.Private, "Kept", null, DeleteScope.Everyone, ""),
        ];
        var text = ConsoleUi.Disclaimer("/x/tgBacups/2026-11-14_08-30-00", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, chats,
            new HashSet<ChatKey> { chats[3].Key }, dryRun: false);
        Assert.Contains("/x/tgBacups/2026-11-14_08-30-00", text);
        Assert.Contains("FOR BOTH SIDES", text);
        Assert.Contains("the other person's", text);
        Assert.Contains("Other members lose their own messages", text);
        Assert.Contains("CANNOT BE UNDONE", text);
        Assert.Contains("IRRECOVERABLE", text);
        Assert.Contains("NOT encrypted", text);
        Assert.Contains("Kept", text);
        // Accurate coverage statements.
        Assert.Contains("Settings → Devices", text);
        Assert.Contains("Stories, profile photos", text);
        Assert.Contains("NOT DONE BY THIS TOOL", text);
        Assert.Contains("only for me", text);
        Assert.Contains("before the upgrade", text);
        Assert.Contains("single media file", text);
        Assert.DoesNotContain("does not expose these to any client", text);
        Assert.DoesNotContain("from every\n", text);
    }
}

/// <summary>
/// Static guard: the tool must never delete "for me only". Every deletion call in the source must revoke.
/// </summary>
public class NoDeleteForMeOnlyTests
{
    static string SourceDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "TgPurge"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "TgPurge");
    }

    static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(SourceDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)));

    [Fact]
    public void Only_revoking_delete_apis_are_used()
    {
        var forbidden = new Regex(@"Messages_DeleteHistory|Channels_DeleteHistory|Messages_DeleteChat\b|DeleteParticipantHistory|just_clear|revoke\s*:\s*false|Messages_DeleteScheduled|Messages_DeleteSavedHistory");
        foreach (var (file, text) in Sources())
            Assert.False(forbidden.IsMatch(text), $"forbidden deletion API in {file}: {forbidden.Match(text).Value}");
    }

    [Fact]
    public void Every_messages_delete_call_passes_revoke_true()
    {
        var calls = Sources().SelectMany(s => Regex.Matches(s.Text, @"Messages_DeleteMessages\(([^;]*)\)").Select(m => m.Groups[1].Value)).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, args => Assert.Matches(@"revoke\s*:\s*true", args));
    }

    [Fact]
    public void Deletion_happens_in_exactly_one_place()
    {
        var files = Sources().Where(s => Regex.IsMatch(s.Text, @"Messages_DeleteMessages\(|Channels_DeleteMessages\(")).Select(s => Path.GetFileName(s.File)).ToList();
        Assert.Equal(["TelegramApi.cs"], files);
    }
}
