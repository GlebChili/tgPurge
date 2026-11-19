using TL;
using WTelegram;

namespace TgPurge.Telegram;

/// <summary>What Telegram said about a chat's size, and how many messages were actually read.</summary>
public sealed record HistoryStats(int Fetched, int? ReportedCount, bool Inexact)
{
    /// <summary>Telegram's count as returned, before any adjustment for a read that starts at the cut-off.</summary>
    public int? RawCount { get; init; }
    /// <summary>Telegram's offset_id_offset for the first page, when it sent one.</summary>
    public int? Position { get; init; }

    /// <summary>
    /// True when fewer messages were read than Telegram said the chat holds, so the read cannot be trusted as complete.
    /// An approximate ("inexact") count is not evidence of a short read. In comments groups the count is not compared
    /// (the same rule the backup verifier has always used).
    /// </summary>
    public static bool IsShortRead(int fetched, int? reported, bool inexact, ChatKind kind) =>
        reported is int r && !inexact && fetched < r && kind != ChatKind.DiscussionComments;
}

public static class History
{
    const int PageSize = 100;

    /// <summary>How thoroughly a chat is read.</summary>
    public enum ReadMode
    {
        /// <summary>
        /// The whole history from the newest message, every message classified by Telegram's "outgoing" flag. Used for
        /// the backup, which decides what can ever be deleted, so nothing that could be deleted is missed.
        /// </summary>
        Full,
        /// <summary>
        /// A cheaper read used to check a chat after the purge: only messages before the cut-off, and in groups where
        /// only the user's own messages can be deleted, only those (found by searching by sender). It can only report
        /// problems, never cause a deletion, because the purge deletes nothing that is not in the backup.
        /// </summary>
        Check,
    }

    /// <summary>
    /// Whether a <see cref="ReadMode.Check"/> read of this chat searches for the user's own messages instead of reading
    /// every member's. Broadcast channels are always read: their posts carry no sender to search by.
    /// </summary>
    public static bool ReadsOwnMessagesBySearch(ChatInfo chat) =>
        chat.Kind == ChatKind.DiscussionComments
        || (chat.Scope == DeleteScope.OwnOnly && chat.Kind is ChatKind.Supergroup or ChatKind.BasicGroup);

    /// <summary>
    /// Everyone the user's own messages in this chat can appear as: the user, the channels the user owns (Telegram
    /// lets a member "send as" such a channel), and the group itself when the user is an anonymous admin there.
    /// </summary>
    public static IEnumerable<InputPeer> OwnSenders(TelegramTarget target, TargetDiscovery dir)
    {
        yield return InputPeer.Self;
        foreach (var channel in dir.SendAsChannels)
            if (channel.id != target.Info.Id) yield return channel;
        if (target.Info.Kind == ChatKind.Supergroup && dir.Chats.TryGetValue(target.Info.Id, out var c) && c is Channel group
            && (group.admin_rights?.flags.HasFlag(ChatAdminRights.Flags.anonymous) ?? false))
            yield return group;
    }

    /// <summary>
    /// Telegram takes dates in whole seconds and treats them as "strictly before". Rounding the cut-off up to the next
    /// whole second keeps every message sent before it; the few in the cut-off's own second that are not before it are
    /// removed by the client-side cut-off rule.
    /// </summary>
    public static DateTime TelegramBound(DateTime cutoffUtc)
    {
        var ticks = cutoffUtc.Ticks;
        var rem = ticks % TimeSpan.TicksPerSecond;
        return new DateTime(rem == 0 ? ticks : ticks - rem + TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    /// <summary>
    /// Reads a chat newest to oldest, one page at a time, handing each page to <paramref name="onPage"/> as it arrives
    /// so a chat of any size never has to fit in memory. See <see cref="ReadMode"/> for what each mode reads.
    /// Comments groups (where the user may not be a member) are always read by searching for the user's messages.
    /// </summary>
    public static async Task<HistoryStats> StreamAsync(ITelegramApi api, TelegramTarget target, TargetDiscovery dir,
        Func<IReadOnlyList<MessageBase>, Task> onPage, Action<int>? progress = null, ReadMode mode = ReadMode.Full,
        DateTime? cutoffUtc = null)
    {
        // A cut-off that is not in the past covers the whole history (and new messages): no date bound at all.
        DateTime? before = mode == ReadMode.Check && cutoffUtc is DateTime b && b < DateTime.UtcNow ? TelegramBound(b) : null;

        bool search = target.Info.Kind == ChatKind.DiscussionComments || (mode == ReadMode.Check && ReadsOwnMessagesBySearch(target.Info));
        if (search)
        {
            int fetched = 0, reported = 0;
            bool anyCount = false, inexact = false;
            var seen = new HashSet<int>();
            foreach (var from in OwnSenders(target, dir))
            {
                int baseCount = seen.Count;
                var s = await PageAsync(offsetId => api.SearchFromAsync(target.Peer, from, offsetId, PageSize, before), dir,
                    page => onPage(page.Where(m => seen.Add(m.ID)).ToList()),
                    n => progress?.Invoke(baseCount + n), datePositioned: false);
                fetched += s.Fetched;
                if (s.ReportedCount is int r) { reported += r; anyCount = true; }
                inexact |= s.Inexact;
            }
            return new HistoryStats(fetched, anyCount ? reported : null, inexact);
        }

        if (before is DateTime bound)
        {
            try
            {
                var s = await PageAsync(offsetId => api.GetHistoryAsync(target.Peer, offsetId, PageSize, offsetId == 0 ? bound : null),
                    dir, onPage, progress, datePositioned: true);
                // Recorded so a real run shows how Telegram's position value relates to what was read (see PageAsync).
                Helpers.Log(3, $"tgPurge: {target.Info.Key} read from the cut-off: Telegram count {s.RawCount}, " +
                    $"position {s.Position}, expected {s.ReportedCount}, read {s.Fetched}");
                return s;
            }
            catch (PositionUnknownException)
            {
                // Telegram did not say where the cut-off falls, so the completeness check would have nothing to compare
                // with. Nothing was handed on yet: read the whole history instead.
                Helpers.Log(3, $"tgPurge: {target.Info.Key}: no position for the cut-off; reading the whole history");
            }
        }
        return await PageAsync(offsetId => api.GetHistoryAsync(target.Peer, offsetId, PageSize, null), dir, onPage, progress,
            datePositioned: false);
    }

    /// <summary>The id, date and ownership of every message read (about 16 bytes each in memory).</summary>
    public static async Task<(List<ServerMessage> Messages, HistoryStats Stats)> ScanAsync(ITelegramApi api, TelegramTarget target,
        TargetDiscovery dir, Action<int>? progress = null, ReadMode mode = ReadMode.Full, DateTime? cutoffUtc = null)
    {
        var list = new List<ServerMessage>();
        var stats = await StreamAsync(api, target, dir, page =>
        {
            foreach (var m in page) list.Add(ToServerMessage(m));
            return Task.CompletedTask;
        }, progress, mode, cutoffUtc);
        return (list, stats);
    }

    sealed class PositionUnknownException : Exception;

    /// <summary>
    /// Pages backwards through a chat. Only a plain <see cref="Messages_Messages"/> is a complete answer;
    /// <see cref="Messages_MessagesSlice"/> derives from it in WTelegram but is just one page, so the type must be
    /// compared exactly. Otherwise paging continues until an empty page.
    /// </summary>
    /// <param name="datePositioned">
    /// The first page starts at a date rather than at the newest message. Telegram's count is still the whole chat's,
    /// so the number expected from here is the count minus the position of the first message returned
    /// (<c>offset_id_offset</c>). Telegram documents that position without saying whether it counts from 0 or 1.
    /// It is read as 0-based: if it is 1-based the expected number is one too low, so a gap of exactly one message
    /// would go unnoticed here (whole lost pages are still caught); the other reading would flag every complete read.
    /// This is only used by the checking read after the purge, and the raw values are logged to settle it.
    /// </param>
    public static async Task<HistoryStats> PageAsync(Func<int, Task<Messages_MessagesBase>> fetch, TargetDiscovery dir,
        Func<IReadOnlyList<MessageBase>, Task> onPage, Action<int>? progress, bool datePositioned = false)
    {
        var ids = new HashSet<int>();
        int? count = null, rawCount = null, position = null;
        bool inexact = false;
        int offsetId = 0;
        while (true)
        {
            var res = await Retry.RunAsync(() => fetch(offsetId));
            res.CollectUsersChats(dir.Users, dir.Chats);
            if (count is null)
            {
                (rawCount, position) = res switch
                {
                    Messages_ChannelMessages cm => (cm.count, cm.flags.HasFlag(Messages_ChannelMessages.Flags.has_offset_id_offset) ? cm.offset_id_offset : (int?)null),
                    Messages_MessagesSlice ms => (ms.count, ms.flags.HasFlag(Messages_MessagesSlice.Flags.has_offset_id_offset) ? ms.offset_id_offset : (int?)null),
                    _ => (res.Messages.Length, (int?)null),
                };
                (count, inexact) = res switch
                {
                    Messages_ChannelMessages cm when datePositioned =>
                        cm.flags.HasFlag(Messages_ChannelMessages.Flags.has_offset_id_offset)
                            ? (Math.Max(0, cm.count - cm.offset_id_offset), cm.flags.HasFlag(Messages_ChannelMessages.Flags.inexact))
                            : cm.messages.Length == 0 ? (0, false) : throw new PositionUnknownException(),
                    Messages_MessagesSlice ms when datePositioned =>
                        ms.flags.HasFlag(Messages_MessagesSlice.Flags.has_offset_id_offset)
                            ? (Math.Max(0, ms.count - ms.offset_id_offset), ms.flags.HasFlag(Messages_MessagesSlice.Flags.inexact))
                            : ms.messages.Length == 0 ? (0, false) : throw new PositionUnknownException(),
                    Messages_ChannelMessages cm => (cm.count, cm.flags.HasFlag(Messages_ChannelMessages.Flags.inexact)),
                    Messages_MessagesSlice ms => (ms.count, ms.flags.HasFlag(Messages_MessagesSlice.Flags.inexact)),
                    _ => (res.Messages.Length, false),
                };
            }
            var page = res.Messages;
            if (page.Length == 0) break;
            int minId = int.MaxValue;
            var fresh = new List<MessageBase>(page.Length);
            foreach (var m in page)
            {
                minId = Math.Min(minId, m.ID);
                if (m is MessageEmpty) continue;
                if (ids.Add(m.ID)) fresh.Add(m);
            }
            if (fresh.Count > 0) await onPage(fresh);
            progress?.Invoke(ids.Count);
            if (res.GetType() == typeof(Messages_Messages)) break; // the whole history in one answer
            if (offsetId != 0 && minId >= offsetId) break;        // defensive: no progress
            offsetId = minId;
            if (offsetId <= 1) break;                              // message ids start at 1
        }
        return new HistoryStats(ids.Count, count, inexact) { RawCount = rawCount, Position = position };
    }

    public static bool IsOwn(MessageBase m) => m switch
    {
        Message msg => msg.flags.HasFlag(Message.Flags.out_),
        MessageService svc => svc.flags.HasFlag(MessageService.Flags.out_),
        _ => false,
    };

    public static ServerMessage ToServerMessage(MessageBase m) => new(m.ID, DateTime.SpecifyKind(m.Date, DateTimeKind.Utc), IsOwn(m));

    /// <summary>Re-fetches a single message, e.g. to refresh an expired file reference before downloading media.</summary>
    public static async Task<MessageBase?> RefetchAsync(ITelegramApi api, TelegramTarget target, int id)
    {
        var res = await Retry.RunAsync(() => api.GetMessagesAsync(target.Channel, [id]));
        return res.Messages.FirstOrDefault(m => m.ID == id && m is not MessageEmpty);
    }
}

public static class Retry
{
    /// <summary>
    /// Runs a Telegram call. Rate limits (FLOOD_WAIT) are waited out exactly as long as Telegram demands, announced
    /// and counted down on screen, and never count as a failed attempt. Transient network/server errors are retried
    /// with back-off, each retry shown on screen.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> action, int attempts = 5)
    {
        for (int i = 1; ; )
        {
            try
            {
                return await action();
            }
            catch (RpcException flood) when (IsFloodWait(flood))
            {
                await WaitOutFloodAsync(flood);
            }
            catch (Exception ex) when (i < attempts && IsTransient(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, 2 << i));
                Status.Warn($"Telegram/network problem ({ex.Message}); retry {i}/{attempts - 1} in {Status.Human(delay)}…");
                await Status.Delay(delay);
                i++;
            }
        }
    }

    public static async Task RunAsync(Func<Task> action, int attempts = 5) =>
        await RunAsync(async () => { await action(); return 0; }, attempts);

    public static bool IsFloodWait(Exception ex) => ex is RpcException { Code: 420 };

    public static Task WaitOutFloodAsync(RpcException flood) =>
        Status.WaitAsync(TimeSpan.FromSeconds(Math.Max(flood.X, 1) + 1), $"Telegram rate limit ({flood.Message})");

    static bool IsTransient(Exception ex) => ex switch
    {
        RpcException rpc => rpc.Code is 500 || rpc.Message.Contains("TIMEOUT", StringComparison.Ordinal),
        IOException or TimeoutException or System.Net.Sockets.SocketException => true,
        _ => false,
    };
}
