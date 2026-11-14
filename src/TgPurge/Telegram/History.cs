using TL;
using WTelegram;

namespace TgPurge.Telegram;

/// <summary>What Telegram said about a chat's size, and how many messages were actually read.</summary>
public sealed record HistoryStats(int Fetched, int? ReportedCount, bool Inexact)
{
    /// <summary>
    /// True when fewer messages were read than Telegram said the chat holds, so the read cannot be trusted as complete.
    /// An approximate ("inexact") count is not evidence of a short read. Comments groups are read with a search for
    /// the user's own comments, whose count is not compared (the same rule the backup verifier has always used).
    /// </summary>
    public static bool IsShortRead(int fetched, int? reported, bool inexact, ChatKind kind) =>
        reported is int r && !inexact && fetched < r && kind != ChatKind.DiscussionComments;
}

public static class History
{
    const int PageSize = 100;

    /// <summary>
    /// Reads the complete history of a chat, newest to oldest, one page at a time, handing each page to
    /// <paramref name="onPage"/> as it arrives so a chat of any size never has to fit in memory.
    /// For comments groups only the user's own messages are read.
    /// </summary>
    public static Task<HistoryStats> StreamAsync(ITelegramApi api, TelegramTarget target, TargetDiscovery dir,
        Func<IReadOnlyList<MessageBase>, Task> onPage, Action<int>? progress = null)
    {
        Func<int, Task<Messages_MessagesBase>> fetch = target.Info.Kind == ChatKind.DiscussionComments
            ? offsetId => api.SearchOwnAsync(target.Peer, offsetId, PageSize)
            : offsetId => api.GetHistoryAsync(target.Peer, offsetId, PageSize);
        return PageAsync(fetch, dir, onPage, progress);
    }

    /// <summary>The id, date and ownership of every message in a chat (about 16 bytes each in memory).</summary>
    public static async Task<(List<ServerMessage> Messages, HistoryStats Stats)> ScanAsync(ITelegramApi api, TelegramTarget target,
        TargetDiscovery dir, Action<int>? progress = null)
    {
        var list = new List<ServerMessage>();
        var stats = await StreamAsync(api, target, dir, page =>
        {
            foreach (var m in page) list.Add(ToServerMessage(m));
            return Task.CompletedTask;
        }, progress);
        return (list, stats);
    }

    /// <summary>
    /// Pages backwards through a chat. Only a plain <see cref="Messages_Messages"/> is a complete answer;
    /// <see cref="Messages_MessagesSlice"/> derives from it in WTelegram but is just one page, so the type must be
    /// compared exactly. Otherwise paging continues until an empty page.
    /// </summary>
    public static async Task<HistoryStats> PageAsync(Func<int, Task<Messages_MessagesBase>> fetch, TargetDiscovery dir,
        Func<IReadOnlyList<MessageBase>, Task> onPage, Action<int>? progress)
    {
        var ids = new HashSet<int>();
        int? count = null;
        bool inexact = false;
        int offsetId = 0;
        while (true)
        {
            var res = await Retry.RunAsync(() => fetch(offsetId));
            res.CollectUsersChats(dir.Users, dir.Chats);
            if (count is null)
                (count, inexact) = res switch
                {
                    Messages_ChannelMessages cm => (cm.count, cm.flags.HasFlag(Messages_ChannelMessages.Flags.inexact)),
                    Messages_MessagesSlice ms => (ms.count, ms.flags.HasFlag(Messages_MessagesSlice.Flags.inexact)),
                    _ => (res.Messages.Length, false),
                };
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
        return new HistoryStats(ids.Count, count, inexact);
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
