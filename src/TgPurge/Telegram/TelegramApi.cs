using TL;
using WTelegram;

namespace TgPurge.Telegram;

/// <summary>
/// Every Telegram call tgPurge makes. Kept deliberately small so the logic that reads, backs up and deletes can be
/// tested against a fake Telegram. Implementations do not retry; callers wrap calls in <see cref="Retry"/>.
/// </summary>
public interface ITelegramApi
{
    long UserId { get; }
    Task<Messages_Dialogs> GetAllDialogsAsync(int? folderId);
    Task<Messages_ChatFull> GetFullChannelAsync(Channel channel);
    Task<Messages_Chats> GetChatsAsync(long chatId);
    /// <summary>One page of history, newest first, of messages with id &lt; <paramref name="offsetId"/> (0 = from the newest).</summary>
    Task<Messages_MessagesBase> GetHistoryAsync(InputPeer peer, int offsetId, int limit);
    /// <summary>One page of the authenticated user's own messages in a chat (server-side search by sender).</summary>
    Task<Messages_MessagesBase> SearchOwnAsync(InputPeer peer, int offsetId, int limit);
    /// <summary>Messages by id; deleted ones come back as <see cref="MessageEmpty"/>.</summary>
    Task<Messages_MessagesBase> GetMessagesAsync(InputChannelBase? channel, int[] ids);
    Task<Storage_FileType> DownloadPhotoAsync(Photo photo, Stream output, Client.ProgressCallback progress);
    Task DownloadDocumentAsync(Document document, Stream output, Client.ProgressCallback progress);
    /// <summary>
    /// Ends every download in progress from data center <paramref name="dcId"/> by closing the media connection to it.
    /// The stuck downloads fail; the next download opens a new connection.
    /// </summary>
    Task AbortMediaDownloadsAsync(int dcId);
    /// <summary>
    /// Deletes messages for ALL participants. This is the only deletion entry point in tgPurge:
    /// messages.deleteMessages with revoke=true (private chats, basic groups) or channels.deleteMessages
    /// (supergroups/channels, always for everyone). Nothing is ever deleted "for me only".
    /// </summary>
    Task DeleteForEveryoneAsync(InputChannelBase? channel, int[] ids);
}

/// <summary>The real Telegram, through WTelegramClient.</summary>
public sealed class TelegramApi(Client client) : ITelegramApi
{
    public long UserId => client.UserId;

    public Task<Messages_Dialogs> GetAllDialogsAsync(int? folderId) => client.Messages_GetAllDialogs(folderId);

    public Task<Messages_ChatFull> GetFullChannelAsync(Channel channel) => client.Channels_GetFullChannel(channel);

    public Task<Messages_Chats> GetChatsAsync(long chatId) => client.Messages_GetChats(chatId);

    public Task<Messages_MessagesBase> GetHistoryAsync(InputPeer peer, int offsetId, int limit) =>
        client.Messages_GetHistory(peer, offset_id: offsetId, limit: limit);

    public Task<Messages_MessagesBase> SearchOwnAsync(InputPeer peer, int offsetId, int limit) =>
        client.Messages_Search(peer, null, null, offset_id: offsetId, limit: limit, from_id: InputPeer.Self);

    public Task<Messages_MessagesBase> GetMessagesAsync(InputChannelBase? channel, int[] ids)
    {
        var input = ids.Select(id => (InputMessage)new InputMessageID { id = id }).ToArray();
        return channel is not null ? client.Channels_GetMessages(channel, input) : client.Messages_GetMessages(input);
    }

    public Task<Storage_FileType> DownloadPhotoAsync(Photo photo, Stream output, Client.ProgressCallback progress) =>
        client.DownloadFileAsync(photo, output, (PhotoSizeBase?)null, progress);

    public Task DownloadDocumentAsync(Document document, Stream output, Client.ProgressCallback progress) =>
        client.DownloadFileAsync(document, output, (PhotoSizeBase?)null, progress);

    public async Task AbortMediaDownloadsAsync(int dcId)
    {
        // WTelegram downloads files over a separate media connection per data center (a negative DC id). Disposing it
        // fails its pending requests, which ends a download that is waiting for an answer that never comes. The library
        // replaces a client whose connection is closed the next time a file is needed from that data center.
        var media = await client.GetClientForDC(-Math.Abs(dcId), connect: false);
        if (!ReferenceEquals(media, client)) await media.DisposeAsync();
    }

    public Task DeleteForEveryoneAsync(InputChannelBase? channel, int[] ids) =>
        channel is not null
            ? client.Channels_DeleteMessages(channel, ids)
            : client.Messages_DeleteMessages(ids, revoke: true);
}
