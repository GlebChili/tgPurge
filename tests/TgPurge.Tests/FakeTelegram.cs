using TgPurge.Telegram;
using TL;

namespace TgPurge.Tests;

/// <summary>
/// In-memory Telegram that behaves like the real API in the ways tgPurge depends on: history pages newest-first
/// below an offset id, private/basic-group pages come back as Messages_MessagesSlice (a subclass of
/// Messages_Messages in WTelegram) when there are more than fit, channels as Messages_ChannelMessages,
/// and deleted messages come back as MessageEmpty.
/// </summary>
public sealed class FakeTelegram : ITelegramApi
{
    public long UserId { get; set; } = 1;

    /// <summary>Messages per chat, keyed like "user:5", "chat:7", "channel:9".</summary>
    public Dictionary<string, List<MessageBase>> History { get; } = [];
    public List<(InputChannelBase? Channel, int[] Ids)> Deletions { get; } = [];
    public List<int> HistoryCalls { get; } = [];
    /// <summary>Ids hidden from the first full scan of their chat (to simulate a scan that misses messages).</summary>
    public HashSet<int> HiddenOnFirstScan { get; } = [];
    readonly Dictionary<string, int> _scans = [];
    /// <summary>Ids never returned by history reads but still included in the reported count (a short read).</summary>
    public HashSet<int> CountedButNeverReturned { get; } = [];
    /// <summary>Ids whose deletion Telegram refuses.</summary>
    public HashSet<int> Undeletable { get; } = [];

    public Messages_Dialogs Dialogs { get; set; } = EmptyDialogs();
    public Messages_Dialogs ArchivedDialogs { get; set; } = EmptyDialogs();
    public Dictionary<long, Messages_ChatFull> FullChannels { get; } = [];
    public Dictionary<long, ChatBase> ExtraChats { get; } = [];

    public static Messages_Dialogs EmptyDialogs() => new() { dialogs = [], messages = [], chats = [], users = [] };

    public static string KeyOf(InputPeer peer) => peer switch
    {
        InputPeerUser u => $"user:{u.user_id}",
        InputPeerChat c => $"chat:{c.chat_id}",
        InputPeerChannel ch => $"channel:{ch.channel_id}",
        _ => throw new ArgumentException($"unsupported peer {peer}"),
    };

    public static Message Msg(int id, DateTime utc, bool own, long peerUserId = 2, string text = "") => new()
    {
        id = id,
        date = utc,
        peer_id = new PeerUser { user_id = peerUserId },
        flags = own ? Message.Flags.out_ : 0,
        message = text,
    };

    public Task<Messages_Dialogs> GetAllDialogsAsync(int? folderId) =>
        Task.FromResult(folderId == 1 ? ArchivedDialogs : folderId == 0 ? Dialogs : Merge(Dialogs, ArchivedDialogs));

    static Messages_Dialogs Merge(Messages_Dialogs a, Messages_Dialogs b) => new()
    {
        dialogs = [.. a.dialogs, .. b.dialogs],
        messages = [],
        users = a.users.Concat(b.users).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value),
        chats = a.chats.Concat(b.chats).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value),
    };

    public Task<Messages_ChatFull> GetFullChannelAsync(Channel channel) =>
        FullChannels.TryGetValue(channel.id, out var full)
            ? Task.FromResult(full)
            : throw new RpcException(400, "CHANNEL_PRIVATE", -1);

    public Task<Messages_Chats> GetChatsAsync(long chatId) =>
        Task.FromResult(new Messages_Chats { chats = ExtraChats.Where(kv => kv.Key == chatId).ToDictionary() });

    public Task<Messages_MessagesBase> GetHistoryAsync(InputPeer peer, int offsetId, int limit)
    {
        var key = KeyOf(peer);
        HistoryCalls.Add(offsetId);
        if (offsetId == 0) _scans[key] = _scans.GetValueOrDefault(key) + 1; // a new full scan starts at offset 0
        bool firstScan = _scans.GetValueOrDefault(key) <= 1;
        var all = History.GetValueOrDefault(key) ?? [];
        var visible = all.Where(m => !(firstScan && HiddenOnFirstScan.Contains(m.ID))).OrderByDescending(m => m.ID).ToList();
        int reported = visible.Count;
        visible.RemoveAll(m => CountedButNeverReturned.Contains(m.ID));
        var page = visible.Where(m => offsetId == 0 || m.ID < offsetId).Take(limit).ToArray();
        return Task.FromResult(Wrap(key, page, reported, limit));
    }

    public Task<Messages_MessagesBase> SearchOwnAsync(InputPeer peer, int offsetId, int limit)
    {
        var key = KeyOf(peer);
        var own = (History.GetValueOrDefault(key) ?? []).Where(Telegram.History.IsOwn).OrderByDescending(m => m.ID).ToList();
        var page = own.Where(m => offsetId == 0 || m.ID < offsetId).Take(limit).ToArray();
        return Task.FromResult(Wrap(key, page, own.Count, limit));
    }

    static Messages_MessagesBase Wrap(string key, MessageBase[] page, int total, int limit)
    {
        if (key.StartsWith("channel:", StringComparison.Ordinal))
            return new Messages_ChannelMessages { messages = page, count = total, chats = [], users = [], topics = [] };
        // Like Telegram: the whole history in one answer when it fits, otherwise a slice with the total count.
        if (total <= limit)
            return new Messages_Messages { messages = page, chats = [], users = [] };
        return new Messages_MessagesSlice { messages = page, count = total, chats = [], users = [] };
    }

    public Task<Messages_MessagesBase> GetMessagesAsync(InputChannelBase? channel, int[] ids)
    {
        var pool = Pool(channel);
        var found = ids.Select(id => pool.FirstOrDefault(m => m.ID == id) ?? (MessageBase)new MessageEmpty { id = id }).ToArray();
        return Task.FromResult<Messages_MessagesBase>(channel is null
            ? new Messages_Messages { messages = found, chats = [], users = [] }
            : new Messages_ChannelMessages { messages = found, chats = [], users = [], topics = [] });
    }

    public Task DeleteForEveryoneAsync(InputChannelBase? channel, int[] ids)
    {
        if (ids.Any(Undeletable.Contains))
            throw new RpcException(403, "MESSAGE_DELETE_FORBIDDEN", -1);
        Deletions.Add((channel, ids));
        foreach (var list in Lists(channel)) list.RemoveAll(m => ids.Contains(m.ID));
        return Task.CompletedTask;
    }

    /// <summary>Private chats and basic groups share one message-id space per account; each channel has its own.</summary>
    IEnumerable<List<MessageBase>> Lists(InputChannelBase? channel) => channel switch
    {
        null => History.Where(kv => !kv.Key.StartsWith("channel:", StringComparison.Ordinal)).Select(kv => kv.Value),
        InputChannel ch => History.Where(kv => kv.Key == $"channel:{ch.channel_id}").Select(kv => kv.Value),
        _ => [],
    };

    List<MessageBase> Pool(InputChannelBase? channel) => Lists(channel).SelectMany(l => l).ToList();

    public Task<Storage_FileType> DownloadPhotoAsync(Photo photo, Stream output, WTelegram.Client.ProgressCallback progress) =>
        throw new NotSupportedException();

    public Task DownloadDocumentAsync(Document document, Stream output, WTelegram.Client.ProgressCallback progress) =>
        throw new NotSupportedException();
}
