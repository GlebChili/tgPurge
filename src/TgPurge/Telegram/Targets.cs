using TL;
using WTelegram;

namespace TgPurge.Telegram;

/// <summary>A chat plus the MTProto handles needed to read and delete in it.</summary>
public sealed record TelegramTarget(ChatInfo Info, InputPeer Peer, InputChannelBase? Channel);

public sealed class TargetDiscovery
{
    public List<TelegramTarget> Targets { get; } = [];
    /// <summary>Chats seen in the dialog list that cannot be read at all (kicked, banned, deleted).</summary>
    public List<string> Inaccessible { get; } = [];
    public Dictionary<long, User> Users { get; } = [];
    public Dictionary<long, ChatBase> Chats { get; } = [];
    /// <summary>Channels the user owns: Telegram lets members post in groups "as" such a channel.</summary>
    public List<Channel> SendAsChannels { get; } = [];
}

public static class TargetEnumerator
{
    public static async Task<TargetDiscovery> DiscoverAsync(ITelegramApi api, User me, Action<string> progress)
    {
        var result = new TargetDiscovery();
        var peers = new List<Peer>();
        var seen = new HashSet<(string, long)>();

        void AddPeer(Peer p)
        {
            var key = p switch
            {
                PeerUser u => ("u", u.user_id),
                PeerChat c => ("c", c.chat_id),
                PeerChannel ch => ("ch", ch.channel_id),
                _ => ("?", 0L),
            };
            if (key.Item1 != "?" && seen.Add(key)) peers.Add(p);
        }

        // All dialogs, then the main list and the Archive folder separately, so no chat is missed if one listing stops early.
        foreach (int? folder in new int?[] { null, 0, 1 })
        {
            progress(folder switch { null => "Fetching all dialogs…", 0 => "Fetching main chat list…", _ => "Fetching archived chats…" });
            var dialogs = await Retry.RunAsync(() => api.GetAllDialogsAsync(folder));
            dialogs.CollectUsersChats(result.Users, result.Chats);
            foreach (var d in dialogs.Dialogs)
                if (d is Dialog dlg) AddPeer(dlg.peer);
        }
        // Saved Messages is always processed, even if it is not shown as a dialog.
        result.Users[me.id] = me;
        AddPeer(new PeerUser { user_id = me.id });

        var channelsToInspect = new List<Channel>();

        foreach (var peer in peers)
        {
            switch (peer)
            {
                case PeerUser pu when result.Users.TryGetValue(pu.user_id, out var user):
                    result.Targets.Add(new TelegramTarget(
                        new ChatInfo(user.id, ChatKind.Private,
                            user.id == me.id ? "Saved Messages" : DisplayName(user),
                            user.MainUsername, DeleteScope.Everyone,
                            "private chat: every message from BOTH sides is deleted for BOTH sides"),
                        user, null));
                    break;

                case PeerChat pc when result.Chats.TryGetValue(pc.chat_id, out var cb):
                    if (cb is Chat chat) result.Targets.Add(BasicGroupTarget(chat, chat.title));
                    else result.Inaccessible.Add($"group \"{cb.Title}\" (id {cb.ID}): no access (removed or banned)");
                    break;

                case PeerChannel pch when result.Chats.TryGetValue(pch.channel_id, out var cb):
                    if (cb is Channel channel)
                    {
                        result.Targets.Add(ChannelTarget(channel));
                        if (channel.IsChannel && channel.flags.HasFlag(Channel.Flags.creator)) result.SendAsChannels.Add(channel);
                        channelsToInspect.Add(channel);
                    }
                    else result.Inaccessible.Add($"channel/supergroup \"{cb.Title}\" (id {cb.ID}): no access (removed or banned)");
                    break;

                default:
                    result.Inaccessible.Add($"{peer}: peer details not returned by Telegram");
                    break;
            }
        }

        // Two kinds of history live outside the dialog list and must be found through the channel's full info:
        //  * a supergroup upgraded from a basic group keeps every pre-upgrade message in the old, deactivated basic group;
        //  * comments the user left under posts of followed channels live in the channel's linked discussion group.
        var known = result.Targets.Select(t => t.Info.Key).ToHashSet();
        foreach (var channel in channelsToInspect)
        {
            progress(channel.IsGroup
                ? $"Checking \"{channel.title}\" for history from before it became a supergroup…"
                : $"Checking comments group of channel \"{channel.title}\"…");
            try
            {
                var full = await Retry.RunAsync(() => api.GetFullChannelAsync(channel));
                full.CollectUsersChats(result.Users, result.Chats);
                if (full.full_chat is not ChannelFull cf) continue;

                if (cf.migrated_from_chat_id != 0 && known.Add(ChatKey.For(ChatKind.BasicGroup, cf.migrated_from_chat_id)))
                {
                    if (!result.Chats.TryGetValue(cf.migrated_from_chat_id, out var old))
                    {
                        var got = await Retry.RunAsync(() => api.GetChatsAsync(cf.migrated_from_chat_id));
                        foreach (var (id, c) in got.chats) result.Chats[id] = c;
                        result.Chats.TryGetValue(cf.migrated_from_chat_id, out old);
                    }
                    if (old is Chat oldChat)
                    {
                        var t = BasicGroupTarget(oldChat, $"{channel.title} (history before it became a supergroup)");
                        result.Targets.Add(t with { Info = t.Info with { Parent = ChatKey.For(ChatKind.Supergroup, channel.id) } });
                    }
                    else
                        result.Inaccessible.Add($"pre-supergroup history of \"{channel.title}\" (group id {cf.migrated_from_chat_id}): no access");
                }

                if (channel.IsChannel && cf.linked_chat_id != 0
                    && known.Add(ChatKey.For(ChatKind.Supergroup, cf.linked_chat_id))
                    && result.Chats.TryGetValue(cf.linked_chat_id, out var lc) && lc is Channel discussion)
                {
                    result.Targets.Add(new TelegramTarget(
                        new ChatInfo(discussion.id, ChatKind.DiscussionComments,
                            $"{discussion.title} (comments of \"{channel.title}\")", discussion.MainUsername,
                            DeleteScope.OwnOnly, "comments group you are not a member of: only YOUR comments deleted for everyone")
                        { Parent = ChatKey.For(ChatKind.Channel, channel.id) },
                        discussion, discussion));
                }
            }
            catch (Exception ex)
            {
                result.Inaccessible.Add($"\"{channel.title}\": cannot inspect for older history / comments ({ex.Message})");
            }
        }

        result.Targets.Sort((a, b) =>
        {
            int k = a.Info.Kind.CompareTo(b.Info.Kind);
            return k != 0 ? k : string.Compare(a.Info.Title, b.Info.Title, StringComparison.CurrentCultureIgnoreCase);
        });
        return result;
    }

    static TelegramTarget BasicGroupTarget(Chat chat, string title)
    {
        bool canDeleteAll = chat.flags.HasFlag(Chat.Flags.creator)
            || (chat.admin_rights?.flags.HasFlag(ChatAdminRights.Flags.delete_messages) ?? false);
        return new TelegramTarget(
            new ChatInfo(chat.id, ChatKind.BasicGroup, title, null,
                canDeleteAll ? DeleteScope.Everyone : DeleteScope.OwnOnly,
                canDeleteAll
                    ? "group where you are creator/admin: ALL members' messages deleted for everyone"
                    : "group without delete rights: only YOUR messages deleted for everyone"),
            chat, null);
    }

    static TelegramTarget ChannelTarget(Channel channel)
    {
        bool creator = channel.flags.HasFlag(Channel.Flags.creator);
        bool canDeleteAll = creator || (channel.admin_rights?.flags.HasFlag(ChatAdminRights.Flags.delete_messages) ?? false);
        if (channel.IsGroup)
        {
            return new TelegramTarget(
                new ChatInfo(channel.id, ChatKind.Supergroup, channel.title, channel.MainUsername,
                    canDeleteAll ? DeleteScope.Everyone : DeleteScope.OwnOnly,
                    canDeleteAll
                        ? "supergroup where you are creator/admin with delete rights: ALL members' messages deleted for everyone"
                        : "supergroup without delete rights: only YOUR messages deleted for everyone"),
                channel, channel);
        }

        bool canPost = channel.admin_rights?.flags.HasFlag(ChatAdminRights.Flags.post_messages) ?? false;
        if (canDeleteAll || canPost)
        {
            return new TelegramTarget(
                new ChatInfo(channel.id, ChatKind.Channel, channel.title, channel.MainUsername,
                    canDeleteAll ? DeleteScope.Everyone : DeleteScope.OwnOnly,
                    canDeleteAll
                        ? "channel you own/administer with delete rights: ALL posts deleted for everyone"
                        : "channel where you can post: only YOUR posts deleted for everyone"),
                channel, channel);
        }
        // A broadcast channel the user only follows contains nothing the user wrote; its comments group is handled separately.
        return new TelegramTarget(
            new ChatInfo(channel.id, ChatKind.Channel, channel.title, channel.MainUsername, DeleteScope.None,
                "channel you only follow: contains no messages of yours; not backed up, nothing deleted (its comments group is checked separately)"),
            channel, channel);
    }

    public static string DisplayName(User u)
    {
        if (u.flags.HasFlag(User.Flags.deleted)) return $"Deleted Account {u.id}";
        var name = $"{u.first_name} {u.last_name}".Trim();
        return name.Length > 0 ? name : u.MainUsername is { } un ? "@" + un : $"user {u.id}";
    }
}
