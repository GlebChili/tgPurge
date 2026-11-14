using System.Text.Json.Serialization;

namespace TgPurge;

/// <summary>What kind of Telegram conversation a target is.</summary>
public enum ChatKind
{
    /// <summary>One-to-one private chat (includes bots and Saved Messages).</summary>
    Private,
    /// <summary>Legacy "basic" group.</summary>
    BasicGroup,
    /// <summary>Supergroup / megagroup / forum.</summary>
    Supergroup,
    /// <summary>Broadcast channel where the user can post or administer.</summary>
    Channel,
    /// <summary>
    /// Discussion (comments) group linked to a channel the user follows, which is not itself in the dialog list.
    /// Only the user's own comments are backed up and deleted.
    /// </summary>
    DiscussionComments,
}

/// <summary>Which messages older than the cut-off the tool removes in a chat.</summary>
public enum DeleteScope
{
    /// <summary>
    /// Every message, from every participant, removed for all sides.
    /// Private chats always use this scope.
    /// </summary>
    Everyone,
    /// <summary>
    /// Only the authenticated user's own messages, removed for all sides.
    /// Used only where Telegram does not allow deleting other people's messages.
    /// </summary>
    OwnOnly,
    /// <summary>Nothing to delete (e.g. a broadcast channel the user merely follows).</summary>
    None,
}

public enum MediaStatus
{
    None,
    Downloaded,
    /// <summary>Telegram has no file for this media any more (expired self-destructing media, empty photo, etc.).</summary>
    Unavailable,
    /// <summary>The media is not a file (poll, location, contact, ...) and is fully described in the text backup.</summary>
    NotAFile,
    Failed,
}

/// <summary>Telegram's three independent id spaces: the same number can be both a user id and a basic-group id.</summary>
public enum PeerSpace { User, Chat, Channel }

/// <summary>Unique identity of a chat. A bare id is ambiguous across <see cref="PeerSpace"/>s.</summary>
public readonly record struct ChatKey(PeerSpace Space, long Id)
{
    public static ChatKey For(ChatKind kind, long id) => new(kind switch
    {
        ChatKind.Private => PeerSpace.User,
        ChatKind.BasicGroup => PeerSpace.Chat,
        _ => PeerSpace.Channel,
    }, id);

    public override string ToString() => $"{Space.ToString().ToLowerInvariant()}:{Id}";
}

/// <summary>A chat the tool will process. Telegram-specific handles live in <see cref="TelegramTarget"/>.</summary>
public sealed record ChatInfo(
    long Id,
    ChatKind Kind,
    string Title,
    string? Username,
    DeleteScope Scope,
    string ScopeReason)
{
    public ChatKey Key => ChatKey.For(Kind, Id);

    /// <summary>
    /// The chat this one belongs to: the supergroup for its pre-upgrade basic group, or the channel for its comments
    /// group. An exception on the parent also covers this chat.
    /// </summary>
    public ChatKey? Parent { get; init; }

    public string KindLabel => Kind switch
    {
        ChatKind.Private => "private",
        ChatKind.BasicGroup => "group",
        ChatKind.Supergroup => "supergroup",
        ChatKind.Channel => "channel",
        ChatKind.DiscussionComments => "comments",
        _ => Kind.ToString(),
    };

    /// <summary>Bot-API style id: users positive, basic groups negative, channels -100 prefixed.</summary>
    public long BotApiId => Kind switch
    {
        ChatKind.Private => Id,
        ChatKind.BasicGroup => -Id,
        _ => -1_000_000_000_000 - Id,
    };
}

/// <summary>One backed-up message, as written to messages.jsonl (one per line).</summary>
public sealed class MessageRecord
{
    public int Id { get; set; }
    public DateTime DateUtc { get; set; }
    public bool IsOwn { get; set; }
    public bool IsService { get; set; }
    public long? SenderId { get; set; }
    public string? SenderName { get; set; }
    public string Text { get; set; } = "";
    public int? ReplyToId { get; set; }
    public string? ForwardedFrom { get; set; }
    public DateTime? EditDateUtc { get; set; }
    public string? MediaDescription { get; set; }
    public MediaStatus MediaStatus { get; set; }
    /// <summary>Path of the media file relative to the chat folder.</summary>
    public string? MediaFile { get; set; }
    /// <summary>Size Telegram declared for the file, when known in advance.</summary>
    public long? MediaExpectedSize { get; set; }
    public long? MediaSize { get; set; }
    public string? MediaSha256 { get; set; }
    public string? MediaError { get; set; }
}

public sealed class ChatBackup
{
    public long Id { get; set; }
    public ChatKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? Username { get; set; }
    public DeleteScope Scope { get; set; }
    /// <summary>Folder name, relative to the backup root.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Total messages Telegram reported for the chat when the backup started.</summary>
    public int? ServerReportedCount { get; set; }
    public bool ServerCountInexact { get; set; }
    /// <summary>Messages in this backup: exactly the ones this run deletes.</summary>
    public int MessageCount { get; set; }
    /// <summary>All messages read from Telegram for this chat (backed up or not); compared with Telegram's count.</summary>
    public int MessagesScanned { get; set; }
    /// <summary>Human description of what the backup contains, e.g. "your own messages sent before …".</summary>
    public string? ContentNote { get; set; }
    /// <summary>The readable history: one file (history.md) or, for very large chats, several parts.</summary>
    public List<HistoryPart> HistoryParts { get; set; } = [];
    /// <summary>SHA-256 of messages.jsonl at the time it was written.</summary>
    public string? MessagesJsonSha256 { get; set; }
    public List<string> Errors { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public ChatKey Key => ChatKey.For(Kind, Id);
}

/// <summary>One Markdown file of a chat's history, oldest messages first.</summary>
public sealed class HistoryPart
{
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int Messages { get; set; }
    public int FirstId { get; set; }
    public int LastId { get; set; }
}

public sealed class BackupManifest
{
    public string Tool { get; set; } = "tgPurge";
    /// <summary>2: messages.jsonl (one message per line) and history split into parts.</summary>
    public int FormatVersion { get; set; } = 2;
    public DateTime CreatedUtc { get; set; }
    public DateTime CutoffUtc { get; set; }
    public long AccountUserId { get; set; }
    public string AccountName { get; set; } = "";
    public List<ChatBackup> Chats { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(List<MessageRecord>))]
public partial class BackupJson : JsonSerializerContext;

/// <summary>Compact (single-line) JSON for messages.jsonl.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MessageRecord))]
public partial class BackupJsonLine : JsonSerializerContext;
