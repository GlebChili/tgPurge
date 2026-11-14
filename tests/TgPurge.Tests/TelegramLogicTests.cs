using TgPurge.Telegram;
using TL;

namespace TgPurge.Tests;

/// <summary>History paging, chat discovery and purging against <see cref="FakeTelegram"/>.</summary>
[Collection("Status")]
public sealed class TelegramLogicTests : IDisposable
{
    static readonly DateTime Cutoff = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly TL.Config Unlimited = new() { revoke_pm_time_limit = int.MaxValue, revoke_time_limit = int.MaxValue };
    readonly TextWriter _savedOut = Status.Out;
    readonly FakeTelegram _tg = new();
    readonly TargetDiscovery _dir = new();

    public TelegramLogicTests() => Status.Out = TextWriter.Null;
    public void Dispose() => Status.Out = _savedOut;

    static List<MessageBase> Messages(int count, Func<int, bool>? own = null) =>
        Enumerable.Range(1, count)
            .Select(i => (MessageBase)FakeTelegram.Msg(i, Cutoff.AddDays(-count + i - 1), own?.Invoke(i) ?? i % 2 == 0))
            .ToList();

    static TelegramTarget Private(long userId) =>
        new(new ChatInfo(userId, ChatKind.Private, $"user {userId}", null, DeleteScope.Everyone, ""), new InputPeerUser(userId, 0), null);

    static TelegramTarget Supergroup(long id, DeleteScope scope) =>
        new(new ChatInfo(id, ChatKind.Supergroup, $"group {id}", null, scope, ""),
            new InputPeerChannel(id, 0), new InputChannel(id, 0));

    // ---- History paging --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(250)]
    [InlineData(1234)]
    public async Task Private_chat_history_is_read_completely(int count)
    {
        // Regression: a Messages_MessagesSlice is also a Messages_Messages in WTelegram; paging used to stop after 100.
        _tg.History["user:5"] = Messages(count);
        var (messages, stats) = await History.ScanAsync(_tg, Private(5), _dir);
        Assert.Equal(count, messages.Count);
        Assert.Equal(Enumerable.Range(1, count), messages.Select(m => m.Id).Order());
        Assert.Equal(count, stats.ReportedCount);
        Assert.Equal(count, stats.Fetched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(250)]
    public async Task Channel_history_is_read_completely(int count)
    {
        _tg.History["channel:9"] = Messages(count);
        var (messages, _) = await History.ScanAsync(_tg, Supergroup(9, DeleteScope.Everyone), _dir);
        Assert.Equal(count, messages.Count);
    }

    // ---- Purge -----------------------------------------------------------------------------------------------

    static HashSet<int> All(IEnumerable<MessageBase> m) => m.Select(x => x.ID).ToHashSet();

    [Fact]
    public async Task Private_chat_purge_deletes_both_sides_before_cutoff_only()
    {
        var msgs = Messages(300);                                   // 300 days before the cut-off…
        msgs.Add(FakeTelegram.Msg(301, Cutoff.AddHours(1), own: false)); // …and one after
        _tg.History["user:5"] = msgs;
        var backup = All(msgs);

        var r = await new Purger(_tg, _dir, Unlimited, _ => { }).PurgeAsync(Private(5), backup, Cutoff, dryRun: false);

        Assert.True(r.Complete, string.Join("; ", r.Errors));
        Assert.Equal(300, r.Deleted);
        var deleted = _tg.Deletions.SelectMany(d => d.Ids).ToHashSet();
        Assert.Equal(Enumerable.Range(1, 300).ToHashSet(), deleted);          // own (even) AND counterpart (odd) messages
        Assert.All(_tg.Deletions, d => Assert.Null(d.Channel));                // messages.deleteMessages(revoke=true) path
        Assert.All(_tg.Deletions, d => Assert.InRange(d.Ids.Length, 1, 100));
        Assert.Equal([301], _tg.History["user:5"].Select(m => m.ID));          // the newer message stays
    }

    [Fact]
    public async Task Messages_not_in_verified_backup_are_never_deleted()
    {
        _tg.History["user:5"] = Messages(10);
        var backup = Enumerable.Range(1, 7).ToHashSet();

        var r = await new Purger(_tg, _dir, Unlimited, _ => { }).PurgeAsync(Private(5), backup, Cutoff, dryRun: false);

        Assert.Equal([8, 9, 10], r.NotBackedUp.Order());
        Assert.False(r.Complete);
        Assert.DoesNotContain(_tg.Deletions.SelectMany(d => d.Ids), id => id > 7);
    }

    [Fact]
    public async Task OwnOnly_group_deletes_only_own_messages_via_channel_api()
    {
        var msgs = Messages(150);
        _tg.History["channel:9"] = msgs;

        var r = await new Purger(_tg, _dir, Unlimited, _ => { }).PurgeAsync(Supergroup(9, DeleteScope.OwnOnly), All(msgs), Cutoff, dryRun: false);

        Assert.True(r.Complete);
        var deleted = _tg.Deletions.SelectMany(d => d.Ids).ToList();
        Assert.All(deleted, id => Assert.True(id % 2 == 0, $"#{id} is someone else's message"));
        Assert.Equal(75, deleted.Count);
        Assert.Equal(75, r.OthersLeftNoPermission);
        Assert.All(_tg.Deletions, d => Assert.IsType<InputChannel>(d.Channel));
    }

    [Fact]
    public async Task Final_full_rescan_catches_messages_the_first_scan_missed()
    {
        var msgs = Messages(20);
        _tg.History["user:5"] = msgs;
        _tg.HiddenOnFirstScan.Add(4);

        var r = await new Purger(_tg, _dir, Unlimited, _ => { }).PurgeAsync(Private(5), All(msgs), Cutoff, dryRun: false);

        Assert.Equal([4], r.Remaining);
        Assert.False(r.Complete);
    }

    [Fact]
    public async Task Refused_deletions_are_isolated_and_reported()
    {
        var msgs = Messages(30);
        _tg.History["channel:9"] = msgs;
        _tg.Undeletable.Add(1); // e.g. "group created" service message

        var r = await new Purger(_tg, _dir, Unlimited, _ => { }).PurgeAsync(Supergroup(9, DeleteScope.Everyone), All(msgs), Cutoff, dryRun: false);

        Assert.Equal([1], r.Remaining);
        Assert.Contains(r.Errors, e => e.Contains("#1") && e.Contains("MESSAGE_DELETE_FORBIDDEN"));
        Assert.Equal(29, r.Deleted);
    }

    [Fact]
    public async Task Dry_run_deletes_nothing()
    {
        var msgs = Messages(50);
        _tg.History["user:5"] = msgs;
        var r = await new Purger(_tg, _dir, Unlimited, _ => { }).PurgeAsync(Private(5), All(msgs), Cutoff, dryRun: true);
        Assert.Empty(_tg.Deletions);
        Assert.Equal(50, r.Remaining.Count);
    }

    [Fact]
    public async Task Finite_revoke_window_never_deletes_for_me_only()
    {
        var msgs = Messages(10);
        _tg.History["user:5"] = msgs;
        var limited = new TL.Config { revoke_pm_time_limit = 48 * 3600, revoke_time_limit = int.MaxValue };
        var r = await new Purger(_tg, _dir, limited, _ => { }).PurgeAsync(Private(5), All(msgs), Cutoff, dryRun: false);
        Assert.Empty(_tg.Deletions);
        Assert.Equal(10, r.BeyondRevokeWindow.Count);
        Assert.False(r.Complete);
    }

    // ---- Discovery -------------------------------------------------------------------------------------------

    static Dialog D(Peer p) => new() { peer = p };

    [Fact]
    public async Task Discovery_finds_archive_saved_messages_pre_upgrade_history_and_comment_groups()
    {
        var me = new User { id = 1, first_name = "Me" };
        var alice = new User { id = 2, first_name = "Alice" };
        var archived = new User { id = 3, first_name = "Old friend" };
        var family = new Channel { id = 100, title = "Family", flags = Channel.Flags.megagroup | Channel.Flags.creator };
        var news = new Channel { id = 200, title = "News", flags = Channel.Flags.broadcast };
        var newsComments = new Channel { id = 201, title = "News chat", flags = Channel.Flags.megagroup };
        var oldFamily = new Chat { id = 50, title = "Family (old)", flags = Chat.Flags.creator | Chat.Flags.deactivated };

        _tg.Dialogs = new Messages_Dialogs
        {
            dialogs = [D(new PeerUser { user_id = 2 }), D(new PeerChannel { channel_id = 100 }), D(new PeerChannel { channel_id = 200 })],
            messages = [],
            users = new() { [2] = alice },
            chats = new() { [100] = family, [200] = news },
        };
        _tg.ArchivedDialogs = new Messages_Dialogs
        {
            dialogs = [D(new PeerUser { user_id = 3 })], messages = [], users = new() { [3] = archived }, chats = [],
        };
        _tg.FullChannels[100] = new Messages_ChatFull { full_chat = new ChannelFull { id = 100, migrated_from_chat_id = 50 }, chats = [], users = [] };
        _tg.ExtraChats[50] = oldFamily;
        _tg.FullChannels[200] = new Messages_ChatFull
        {
            full_chat = new ChannelFull { id = 200, linked_chat_id = 201 }, chats = new() { [201] = newsComments }, users = [],
        };

        var d = await TargetEnumerator.DiscoverAsync(_tg, me, _ => { });
        var keys = d.Targets.Select(t => t.Info.Key).ToHashSet();

        Assert.Contains(new ChatKey(PeerSpace.User, 2), keys);   // main list
        Assert.Contains(new ChatKey(PeerSpace.User, 3), keys);   // archive
        Assert.Contains(new ChatKey(PeerSpace.User, 1), keys);   // Saved Messages
        var pre = Assert.Single(d.Targets, t => t.Info.Key == new ChatKey(PeerSpace.Chat, 50));
        Assert.Equal(DeleteScope.Everyone, pre.Info.Scope);       // you created it
        Assert.Equal(new ChatKey(PeerSpace.Channel, 100), pre.Info.Parent);
        var comments = Assert.Single(d.Targets, t => t.Info.Key == new ChatKey(PeerSpace.Channel, 201));
        Assert.Equal(ChatKind.DiscussionComments, comments.Info.Kind);
        Assert.Equal(DeleteScope.OwnOnly, comments.Info.Scope);
        Assert.Equal(new ChatKey(PeerSpace.Channel, 200), comments.Info.Parent);
        Assert.Empty(d.Inaccessible);

        // An exception on the supergroup also keeps its pre-upgrade history; one on the channel keeps your comments.
        var chats = d.Targets.Select(t => t.Info).ToList();
        var r = ExceptionMatcher.Resolve(["Family", "News"], chats);
        Assert.True(r.IsValid);
        Assert.Contains(new ChatKey(PeerSpace.Chat, 50), r.Excluded);
        Assert.Contains(new ChatKey(PeerSpace.Channel, 201), r.Excluded);
    }

    [Fact]
    public async Task Channel_that_cannot_be_inspected_is_reported_not_silently_skipped()
    {
        var me = new User { id = 1, first_name = "Me" };
        var secret = new Channel { id = 300, title = "Private group", flags = Channel.Flags.megagroup };
        _tg.Dialogs = new Messages_Dialogs
        {
            dialogs = [D(new PeerChannel { channel_id = 300 })], messages = [], users = [], chats = new() { [300] = secret },
        };
        // No FullChannels entry: the fake answers CHANNEL_PRIVATE.
        var d = await TargetEnumerator.DiscoverAsync(_tg, me, _ => { });
        Assert.Contains(d.Inaccessible, i => i.Contains("Private group") && i.Contains("CHANNEL_PRIVATE"));
    }
}
