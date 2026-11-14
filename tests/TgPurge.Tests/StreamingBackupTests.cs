using TgPurge.Telegram;
using TL;

namespace TgPurge.Tests;

[Collection("Status")]
public sealed class StreamingBackupTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "tgPurgeStream-" + Guid.NewGuid().ToString("N"));
    readonly BackupStore _store;
    readonly TextWriter _savedOut = Status.Out;
    static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public StreamingBackupTests()
    {
        _store = BackupStore.CreateNew(_dir, new DateTime(2026, 11, 14, 9, 0, 0));
        Storage.CreatePrivateDirectory(_store.Root);
        Status.Out = TextWriter.Null;
    }

    public void Dispose()
    {
        Status.Out = _savedOut;
        try { Directory.Delete(_dir, true); } catch { }
    }

    static readonly TL.Config Unlimited = new() { revoke_pm_time_limit = int.MaxValue, revoke_time_limit = int.MaxValue };

    static ChatBackup Chat(long id = 42) =>
        new() { Id = id, Kind = ChatKind.Private, Title = "Alice", Folder = $"private_{id}_Alice" };

    static MessageRecord Rec(int id, string text = "") =>
        new() { Id = id, DateUtc = T0.AddMinutes(id), SenderName = "Alice", Text = text.Length > 0 ? text : $"message {id}" };

    ChatVerification Verify(ChatBackup chat)
    {
        _store.WriteManifest(new BackupManifest { Chats = [chat] });
        var results = BackupVerifier.Verify(_store, out var global);
        Assert.Empty(global);
        return Assert.Single(results);
    }

    [Fact]
    public void Pages_arriving_newest_first_are_assembled_oldest_first_and_split_into_parts()
    {
        var chat = Chat();
        var w = new ChatBackupWriter(_store, chat, messagesPerPart: 3);
        w.AddPage([Rec(10), Rec(8), Rec(9)]);   // Telegram sends the newest page first…
        w.AddPage([Rec(7), Rec(5), Rec(6), Rec(4)]);
        w.AddPage([Rec(1), Rec(3), Rec(2)]);   // …and the oldest last
        w.Complete(DateTime.UtcNow);

        var dir = _store.ChatDir(chat);
        Assert.False(Directory.Exists(Path.Combine(dir, ".pages")));                       // temp pages removed
        Assert.Equal(Enumerable.Range(1, 10), _store.ReadMessages(chat).Select(m => m.Id)); // oldest first
        Assert.Equal(["history-0001.md", "history-0002.md", "history-0003.md", "history-0004.md"], chat.HistoryParts.Select(p => p.File));
        Assert.Equal([3, 3, 3, 1], chat.HistoryParts.Select(p => p.Messages));
        Assert.Equal((4, 6), (chat.HistoryParts[1].FirstId, chat.HistoryParts[1].LastId));
        var part2 = File.ReadAllText(Path.Combine(dir, "history-0002.md"));
        Assert.Contains("part 2 of 4", part2);
        Assert.Contains("[history-0001.md](history-0001.md)", part2);
        Assert.Contains("Continued in [history-0003.md](history-0003.md)", part2);
        Assert.True(part2.IndexOf("message 4", StringComparison.Ordinal) < part2.IndexOf("message 6", StringComparison.Ordinal));

        var v = Verify(chat);
        Assert.True(v.Ok, string.Join("; ", v.Problems));
        Assert.Equal(10, v.VerifiedIds.Count);
    }

    [Fact]
    public void Small_chat_keeps_a_single_history_md()
    {
        var chat = Chat();
        _store.WriteChat(chat, [Rec(1), Rec(2)], DateTime.UtcNow);
        Assert.Equal(["history.md"], chat.HistoryParts.Select(p => p.File));
        Assert.True(Verify(chat).Ok);
    }

    [Fact]
    public void Tampered_or_missing_part_fails_verification()
    {
        var chat = Chat();
        var w = new ChatBackupWriter(_store, chat, messagesPerPart: 2);
        w.AddPage(Enumerable.Range(1, 6).Select(i => Rec(i)));
        w.Complete(DateTime.UtcNow);
        var dir = _store.ChatDir(chat);

        File.AppendAllText(Path.Combine(dir, "history-0002.md"), "x");
        Assert.Contains(Verify(chat).Problems, p => p.Contains("history-0002.md checksum mismatch"));

        File.Delete(Path.Combine(dir, "history-0003.md"));
        var v = Verify(chat);
        Assert.False(v.Ok);
        Assert.Contains(v.Problems, p => p.Contains("history-0003.md missing"));
        Assert.Contains(v.Problems, p => p.Contains("#5 missing from the Markdown history"));
    }

    [Fact]
    public async Task Backup_runner_streams_a_chat_from_Telegram_to_disk()
    {
        var tg = new FakeTelegram();
        tg.History["user:5"] = Enumerable.Range(1, 2345)
            .Select(i => (MessageBase)FakeTelegram.Msg(i, T0.AddMinutes(i), own: i % 3 == 0, text: $"hello {i}"))
            .ToList();
        var target = new TelegramTarget(new ChatInfo(5, ChatKind.Private, "Bob", null, DeleteScope.Everyone, ""), new InputPeerUser(5, 0), null);

        var chat = await new BackupRunner(tg, new TargetDiscovery(), _store, Unlimited, _ => { })
            .BackupChatAsync(target, DateTime.UtcNow, cutoffUtc: T0.AddYears(10));

        Assert.Empty(chat.Errors);
        Assert.Equal(2345, chat.MessageCount);
        Assert.Equal(2345, chat.ServerReportedCount);
        var v = Verify(chat);
        Assert.True(v.Ok, string.Join("; ", v.Problems));
        Assert.Equal(Enumerable.Range(1, 2345), _store.ReadMessages(chat).Select(m => m.Id));
        Assert.True(_store.ReadMessages(chat).Single(m => m.Id == 3).IsOwn);
    }

    static (TelegramTarget Target, FakeTelegram Tg) ChatWithOnePhoto()
    {
        var tg = new FakeTelegram();
        var m = FakeTelegram.Msg(1, T0, own: true, text: "a photo");
        m.media = new MessageMediaPhoto { photo = new Photo { id = 77, dc_id = 4 } };
        m.flags |= Message.Flags.has_media;
        tg.History["user:5"] = [m];
        var target = new TelegramTarget(new ChatInfo(5, ChatKind.Private, "Bob", null, DeleteScope.Everyone, ""), new InputPeerUser(5, 0), null);
        return (target, tg);
    }

    BackupRunner FastRunner(FakeTelegram tg) => new(tg, new TargetDiscovery(), _store, Unlimited, _ => { })
    {
        StallTimeout = TimeSpan.FromMilliseconds(200),
        AbortGrace = TimeSpan.FromMilliseconds(200),
        RetryDelay = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public async Task A_stalled_media_download_is_aborted_and_retried()
    {
        // Regression: a download that never received a byte kept the backup waiting forever.
        var (target, tg) = ChatWithOnePhoto();
        tg.HangingPhotoDownloads = 1;

        var backup = FastRunner(tg).BackupChatAsync(target, DateTime.UtcNow, cutoffUtc: T0.AddDays(1));
        Assert.Same(backup, await Task.WhenAny(backup, Task.Delay(TimeSpan.FromSeconds(20))));
        var chat = await backup;

        Assert.Equal([4], tg.AbortedDcs);
        var rec = _store.ReadMessages(chat).Single();
        Assert.Equal(MediaStatus.Downloaded, rec.MediaStatus);
        Assert.True(Verify(chat).Ok);
        Assert.Empty(Directory.GetFiles(Path.Combine(_store.ChatDir(chat), BackupStore.MediaDir), "*.download"));
    }

    [Fact]
    public async Task A_download_that_never_recovers_fails_the_chat_instead_of_hanging()
    {
        var (target, tg) = ChatWithOnePhoto();
        tg.HangingPhotoDownloads = int.MaxValue;
        tg.HangsSurviveAbort = true; // even closing the connection does not end it: the download is abandoned

        var backup = FastRunner(tg).BackupChatAsync(target, DateTime.UtcNow, cutoffUtc: T0.AddDays(1));
        Assert.Same(backup, await Task.WhenAny(backup, Task.Delay(TimeSpan.FromSeconds(20))));
        var chat = await backup;

        var rec = _store.ReadMessages(chat).Single();
        Assert.Equal(MediaStatus.Failed, rec.MediaStatus);
        Assert.Contains("stalled", rec.MediaError);
        Assert.False(Verify(chat).Ok); // so the chat is not purged
        Assert.Equal(4, tg.AbortedDcs.Count); // every attempt was aborted
    }

    [Fact]
    public async Task Only_messages_this_run_deletes_are_backed_up()
    {
        // A supergroup where you may delete only your own messages: others' messages (with media) and anything after
        // the cut-off must never reach the disk.
        var cutoff = T0.AddDays(10);
        var tg = new FakeTelegram();
        var msgs = new List<MessageBase>();
        for (int i = 1; i <= 20; i++)
        {
            var m = FakeTelegram.Msg(i, T0.AddDays(i - 1), own: i % 2 == 0, text: $"text {i}");
            if (i % 2 == 1) { m.media = new MessageMediaPhoto { photo = new Photo { id = i } }; m.flags |= Message.Flags.has_media; }
            msgs.Add(m);
        }
        tg.History["channel:9"] = msgs;
        var target = new TelegramTarget(new ChatInfo(9, ChatKind.Supergroup, "Big group", null, DeleteScope.OwnOnly, ""),
            new InputPeerChannel(9, 0), new InputChannel(9, 0));

        var chat = await new BackupRunner(tg, new TargetDiscovery(), _store, Unlimited, _ => { }).BackupChatAsync(target, DateTime.UtcNow, cutoff);

        // Own messages are the even ids; before the cut-off are days 0..9, i.e. ids 1..10.
        Assert.Equal([2, 4, 6, 8, 10], _store.ReadMessages(chat).Select(m => m.Id));
        Assert.Equal(20, chat.MessagesScanned);
        Assert.Empty(Directory.GetFiles(Path.Combine(_store.ChatDir(chat), BackupStore.MediaDir))); // others' photos never downloaded
        var md = File.ReadAllText(Path.Combine(_store.ChatDir(chat), BackupStore.HistoryFile));
        Assert.DoesNotContain("> text 1\n", md.Replace("\r", ""));
        Assert.DoesNotContain("text 12", md);
        Assert.Contains("only your own messages", md);
        Assert.True(Verify(chat).Ok);
    }

    [Fact]
    public async Task Private_chat_backs_up_both_sides_but_only_before_cutoff()
    {
        var cutoff = T0.AddDays(5);
        var tg = new FakeTelegram();
        tg.History["user:5"] = Enumerable.Range(1, 8)
            .Select(i => (MessageBase)FakeTelegram.Msg(i, T0.AddDays(i - 1), own: i % 2 == 0)).ToList();
        var target = new TelegramTarget(new ChatInfo(5, ChatKind.Private, "Bob", null, DeleteScope.Everyone, ""), new InputPeerUser(5, 0), null);

        var chat = await new BackupRunner(tg, new TargetDiscovery(), _store, Unlimited, _ => { }).BackupChatAsync(target, DateTime.UtcNow, cutoff);

        Assert.Equal([1, 2, 3, 4, 5], _store.ReadMessages(chat).Select(m => m.Id)); // yours and Bob's
        Assert.Equal(8, chat.MessagesScanned);
        Assert.True(Verify(chat).Ok);
    }

    [Fact]
    public void Backup_rule_and_purge_rule_are_the_same()
    {
        var cutoff = T0.AddDays(10);
        var now = T0.AddDays(100);
        var limited = 60 * 86400; // 60-day delete-for-everyone window
        foreach (var scope in new[] { DeleteScope.Everyone, DeleteScope.OwnOnly, DeleteScope.None })
        foreach (var own in new[] { true, false })
        foreach (var day in new[] { 0, 5, 9, 10, 11, 50 })
        foreach (int? window in new int?[] { null, int.MaxValue, limited })
        {
            var date = T0.AddDays(day);
            bool backedUp = PurgeRule.IsPurgeable(scope, own, date, cutoff, window, now);
            var plan = PurgePlanner.Plan(scope, [new ServerMessage(1, date, own)], cutoff,
                backedUp ? new HashSet<int> { 1 } : [], window, now);
            Assert.Equal(backedUp, plan.ToDelete.Contains(1));
            Assert.Empty(plan.NotBackedUp); // nothing the purge wants is ever missing from the backup
        }
    }

    [Fact]
    public async Task Large_chat_streams_with_flat_memory()
    {
        // 100,000 messages of ~1 KB each: ~100 MB of text that the old design held in memory several times over.
        const int total = 100_000, pageSize = 100;
        var text = new string('x', 1000);
        var chat = Chat(7);
        long peak = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
                await Task.Delay(50);
            }
        });
        long baseline = GC.GetTotalMemory(true);

        var w = new ChatBackupWriter(_store, chat);
        for (int top = total; top > 0; top -= pageSize) // newest page first, like Telegram
            w.AddPage(Enumerable.Range(top - pageSize + 1, pageSize).Select(i => Rec(i, text)));
        w.Complete(DateTime.UtcNow);
        _store.WriteManifest(new BackupManifest { Chats = [chat] });
        var v = BackupVerifier.Verify(_store, out _).Single();

        stop.Cancel();
        await sampler;
        Assert.True(v.Ok, string.Join("; ", v.Problems.Take(5)));
        Assert.Equal(total, v.VerifiedIds.Count);
        Assert.Equal(2, chat.HistoryParts.Count); // 50,000 messages per Markdown file
        var growthMb = (peak - baseline) / (1024.0 * 1024.0);
        Assert.True(growthMb < 40, $"live heap grew by {growthMb:0.0} MB while streaming ~100 MB of messages");
    }
}
