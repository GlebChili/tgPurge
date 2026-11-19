using TgPurge.Telegram;
using TL;

namespace TgPurge.Tests;

/// <summary>
/// The backup reads every chat in full; the check after the purge reads only from the cut-off back, and in groups
/// where only your messages can be deleted, only yours (by searching by sender).
/// </summary>
[Collection("Status")]
public sealed class ReadOptimizationTests : IDisposable
{
    static readonly DateTime Cutoff = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly TL.Config Unlimited = new() { revoke_pm_time_limit = int.MaxValue, revoke_time_limit = int.MaxValue };
    readonly TextWriter _savedOut = Status.Out;
    readonly FakeTelegram _tg = new();
    readonly TargetDiscovery _dir = new();

    public ReadOptimizationTests() => Status.Out = TextWriter.Null;
    public void Dispose() => Status.Out = _savedOut;

    /// <summary>Messages 1..total, one hour apart; the first <paramref name="before"/> are sent before the cut-off.</summary>
    static List<MessageBase> Timeline(int total, int before, Func<int, bool>? own = null) =>
        Enumerable.Range(1, total)
            .Select(i => (MessageBase)FakeTelegram.Msg(i, Cutoff.AddHours(i - before - 1), own?.Invoke(i) ?? i % 2 == 0))
            .ToList();

    static TelegramTarget Private(long id) =>
        new(new ChatInfo(id, ChatKind.Private, "Bob", null, DeleteScope.Everyone, ""), new InputPeerUser(id, 0), null);

    static TelegramTarget Supergroup(long id, DeleteScope scope) =>
        new(new ChatInfo(id, ChatKind.Supergroup, "Group", null, scope, ""), new InputPeerChannel(id, 0), new InputChannel(id, 0));

    static Message From(Message m, long channelId)
    {
        m.from_id = new PeerChannel { channel_id = channelId };
        m.flags |= Message.Flags.has_from_id;
        return m;
    }

    // ---- Backup read: always complete ------------------------------------------------------------------------

    [Fact]
    public async Task The_backup_read_covers_the_whole_history_including_out_of_order_dates()
    {
        // #1001 has an id after the cut-off messages but an old date, as imported histories do.
        var msgs = Timeline(total: 1000, before: 150);
        msgs.Add(FakeTelegram.Msg(1001, Cutoff.AddYears(-5), own: false));
        _tg.History["channel:9"] = msgs;

        var (messages, stats) = await History.ScanAsync(_tg, Supergroup(9, DeleteScope.Everyone), _dir, cutoffUtc: Cutoff);

        Assert.Equal(1001, messages.Count);
        Assert.Contains(messages, m => m.Id == 1001);
        Assert.All(_tg.HistoryDates, d => Assert.Null(d));
        Assert.Empty(_tg.SearchCalls);
        Assert.False(HistoryStats.IsShortRead(stats.Fetched, stats.ReportedCount, stats.Inexact, ChatKind.Supergroup));
    }

    [Fact]
    public async Task The_backup_read_of_an_own_only_group_reads_every_message_not_a_search()
    {
        _tg.History["channel:9"] = Timeline(total: 300, before: 300);

        var (messages, _) = await History.ScanAsync(_tg, Supergroup(9, DeleteScope.OwnOnly), _dir);

        Assert.Equal(300, messages.Count);
        Assert.Empty(_tg.SearchCalls);
    }

    // ---- Checking read after the purge -----------------------------------------------------------------------

    [Fact]
    public async Task The_check_starts_at_the_cutoff_and_skips_newer_messages()
    {
        _tg.History["channel:9"] = Timeline(total: 1000, before: 150);

        var (messages, stats) = await History.ScanAsync(_tg, Supergroup(9, DeleteScope.Everyone), _dir,
            mode: History.ReadMode.Check, cutoffUtc: Cutoff);

        Assert.Equal(Enumerable.Range(1, 150), messages.Select(m => m.Id).Order());
        Assert.Equal(2, _tg.HistoryCalls.Count);                 // 150 messages = 2 pages, not 10
        Assert.Equal(Cutoff, _tg.HistoryDates[0]);              // first page positioned at the cut-off…
        Assert.Null(_tg.HistoryDates[1]);                       // …then paged by id
        Assert.Equal(150, stats.ReportedCount);                 // expected = count - newer messages
        Assert.Equal(1000, stats.RawCount);
        Assert.Equal(850, stats.Position);
        Assert.False(HistoryStats.IsShortRead(stats.Fetched, stats.ReportedCount, stats.Inexact, ChatKind.Supergroup));
    }

    [Fact]
    public async Task A_short_check_read_is_detected()
    {
        _tg.History["channel:9"] = Timeline(total: 1000, before: 250);
        _tg.CountedButNeverReturned.Add(120);

        var (_, stats) = await History.ScanAsync(_tg, Supergroup(9, DeleteScope.Everyone), _dir,
            mode: History.ReadMode.Check, cutoffUtc: Cutoff);

        Assert.True(HistoryStats.IsShortRead(stats.Fetched, stats.ReportedCount, stats.Inexact, ChatKind.Supergroup));
    }

    [Fact]
    public async Task Without_the_position_of_the_cutoff_the_check_reads_the_whole_history()
    {
        _tg.History["user:5"] = Timeline(total: 300, before: 120);
        _tg.OmitOffsetPosition = true;

        var (messages, stats) = await History.ScanAsync(_tg, Private(5), _dir, mode: History.ReadMode.Check, cutoffUtc: Cutoff);

        Assert.Equal(300, messages.Count);
        Assert.Equal(300, stats.ReportedCount);
    }

    [Fact]
    public async Task Checking_a_chat_with_nothing_before_the_cutoff_costs_one_request()
    {
        _tg.History["user:5"] = Timeline(total: 500, before: 0);

        var (messages, _) = await History.ScanAsync(_tg, Private(5), _dir, mode: History.ReadMode.Check, cutoffUtc: Cutoff);

        Assert.Empty(messages);
        Assert.Single(_tg.HistoryCalls);
    }

    [Fact]
    public async Task A_future_cutoff_is_checked_from_the_newest_message()
    {
        _tg.History["user:5"] = Timeline(total: 50, before: 50);

        var (messages, _) = await History.ScanAsync(_tg, Private(5), _dir, mode: History.ReadMode.Check,
            cutoffUtc: DateTime.UtcNow.AddDays(1));

        Assert.Equal(50, messages.Count);
        Assert.All(_tg.HistoryDates, d => Assert.Null(d));
    }

    [Fact]
    public void The_cutoff_sent_to_Telegram_is_rounded_up_to_a_whole_second()
    {
        Assert.Equal(Cutoff, History.TelegramBound(Cutoff));
        Assert.Equal(Cutoff.AddSeconds(1), History.TelegramBound(Cutoff.AddMilliseconds(250)));
    }

    [Fact]
    public async Task A_message_in_the_cutoffs_own_second_is_still_checked()
    {
        // --cutoff now carries a fraction of a second; a message sent earlier in that same second is before it.
        var cutoff = Cutoff.AddMilliseconds(700);
        _tg.History["channel:9"] = [FakeTelegram.Msg(1, Cutoff, own: true)];

        var (messages, _) = await History.ScanAsync(_tg, Supergroup(9, DeleteScope.OwnOnly), _dir,
            mode: History.ReadMode.Check, cutoffUtc: cutoff);

        Assert.Single(messages);
    }

    // ---- Purge: planned from the full backup, checked by id and by search -------------------------------------

    [Fact]
    public async Task Own_only_purge_deletes_what_the_backup_found_and_checks_with_searches()
    {
        var myChannel = new Channel { id = 500, title = "My channel", flags = Channel.Flags.broadcast | Channel.Flags.creator };
        _dir.SendAsChannels.Add(myChannel);
        _dir.Chats[9] = new Channel { id = 9, title = "Group", flags = Channel.Flags.megagroup }; // not anonymous (any more)

        var msgs = Timeline(total: 40, before: 30, own: i => i % 4 == 0);
        var asChannel = From(FakeTelegram.Msg(41, Cutoff.AddDays(-1), own: true), 500);    // sent as your channel
        var pastAnonymous = From(FakeTelegram.Msg(42, Cutoff.AddDays(-1), own: true), 9);  // you, once an anonymous admin
        var othersAnonymous = From(FakeTelegram.Msg(43, Cutoff.AddDays(-1), own: false), 9);
        _tg.History["channel:9"] = [.. msgs, asChannel, pastAnonymous, othersAnonymous];

        // The backup is a full read classified by "outgoing", so it has #42 although no search would find it now.
        var (backupRead, _) = await History.ScanAsync(_tg, Supergroup(9, DeleteScope.OwnOnly), _dir);
        var backedUp = backupRead.Where(m => m.IsOwn && m.DateUtc < Cutoff).ToList();
        _tg.HistoryCalls.Clear();

        var r = await new Purger(_tg, _dir, Unlimited, _ => { })
            .PurgeAsync(Supergroup(9, DeleteScope.OwnOnly), backedUp, Cutoff, dryRun: false);

        Assert.Equal([4, 8, 12, 16, 20, 24, 28, 41, 42], _tg.Deletions.SelectMany(d => d.Ids).Order());
        Assert.True(r.Complete, string.Join("; ", r.Errors));
        Assert.Equal(9, r.Deleted);
        Assert.Empty(_tg.HistoryCalls);                                       // the check searched instead of reading
        Assert.Equal([typeof(InputPeerSelf), typeof(InputPeerChannel)], _tg.SearchCalls.Select(c => c.From.GetType()));
    }

    [Fact]
    public async Task A_planned_message_that_was_not_deleted_is_reported_even_if_the_check_cannot_see_it()
    {
        // #42 (a past anonymous post) is in the backup but Telegram refuses to delete it; searches cannot see it,
        // the lookup by id does.
        _dir.Chats[9] = new Channel { id = 9, title = "Group", flags = Channel.Flags.megagroup };
        var pastAnonymous = From(FakeTelegram.Msg(42, Cutoff.AddDays(-1), own: true), 9);
        _tg.History["channel:9"] = [pastAnonymous];
        _tg.Undeletable.Add(42);

        var r = await new Purger(_tg, _dir, Unlimited, _ => { })
            .PurgeAsync(Supergroup(9, DeleteScope.OwnOnly), [History.ToServerMessage(pastAnonymous)], Cutoff, dryRun: false);

        Assert.Equal([42], r.Remaining);
        Assert.False(r.Complete);
    }

    [Fact]
    public async Task An_empty_backup_is_still_checked_and_old_messages_beyond_the_revoke_window_are_reported()
    {
        // Every old message is past the "delete for everyone" window, so none was backed up; the check must still
        // run and say so, instead of the chat passing as clean.
        _tg.History["user:5"] = Timeline(total: 10, before: 10);
        var limited = new TL.Config { revoke_pm_time_limit = 48 * 3600, revoke_time_limit = int.MaxValue };

        var r = await new Purger(_tg, _dir, limited, _ => { }).PurgeAsync(Private(5), [], Cutoff, dryRun: false);

        Assert.Empty(_tg.Deletions);
        Assert.Equal(10, r.BeyondRevokeWindow.Count);
        Assert.False(r.Complete);
    }

    [Fact]
    public async Task A_partly_verified_backup_deletes_only_the_verified_messages_and_reports_the_rest()
    {
        // Messages 1..10 are before the cut-off; #4 and #7 failed to back up, so they are not in the verified set.
        _tg.History["user:5"] = Timeline(total: 12, before: 10);
        var (read, _) = await History.ScanAsync(_tg, Private(5), _dir);
        var verified = read.Where(m => m.DateUtc < Cutoff && m.Id is not (4 or 7)).ToList();

        var r = await new Purger(_tg, _dir, Unlimited, _ => { }).PurgeAsync(Private(5), verified, Cutoff, dryRun: false);

        Assert.Equal([1, 2, 3, 5, 6, 8, 9, 10], _tg.Deletions.SelectMany(d => d.Ids).Order());
        Assert.Equal(8, r.Deleted);
        Assert.Equal([4, 7], r.NotBackedUp);
        Assert.Empty(r.Remaining);
        Assert.False(r.Complete); // the chat is reported, never passed as clean
    }

    // ---- Discovery -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Discovery_fetches_all_dialogs_and_both_folders_and_collects_send_as_channels()
    {
        var me = new User { id = 1, first_name = "Me" };
        var mine = new Channel { id = 500, title = "Mine", flags = Channel.Flags.broadcast | Channel.Flags.creator };
        var followed = new Channel { id = 600, title = "News", flags = Channel.Flags.broadcast };
        _tg.Dialogs = new Messages_Dialogs
        {
            dialogs = [new Dialog { peer = new PeerChannel { channel_id = 500 } }, new Dialog { peer = new PeerChannel { channel_id = 600 } }],
            messages = [], users = [], chats = new() { [500] = mine, [600] = followed },
        };
        _tg.FullChannels[500] = new Messages_ChatFull { full_chat = new ChannelFull { id = 500 }, chats = [], users = [] };
        _tg.FullChannels[600] = new Messages_ChatFull { full_chat = new ChannelFull { id = 600 }, chats = [], users = [] };

        var d = await TargetEnumerator.DiscoverAsync(_tg, me, _ => { });

        Assert.Equal([null, 0, 1], _tg.DialogFolders);
        Assert.Equal([500L], d.SendAsChannels.Select(c => c.id));
    }
}
