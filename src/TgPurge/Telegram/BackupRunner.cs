using System.Globalization;
using TL;
using WTelegram;

namespace TgPurge.Telegram;

/// <summary>
/// Backs up exactly what the purge will delete (see <see cref="PurgeRule"/>): messages sent before the cut-off that the
/// user may delete for everyone. Other people's messages in chats where the user may only delete their own, newer
/// messages, and their media, are read to be counted but never written to disk.
/// </summary>
public sealed class BackupRunner(ITelegramApi api, TargetDiscovery dir, BackupStore store, TL.Config tlConfig, Action<string> log)
{
    /// <summary>A download that receives no data for this long is aborted and counts as a failed attempt.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>How long an aborted download may take to wind down before it is abandoned.</summary>
    public TimeSpan AbortGrace { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Pause before retry n of a failed media download is n times this.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<ChatBackup> BackupChatAsync(TelegramTarget target, DateTime exportedUtc, DateTime cutoffUtc)
    {
        var info = target.Info;
        var chat = new ChatBackup
        {
            Id = info.Id,
            Kind = info.Kind,
            Title = info.Title,
            Username = info.Username,
            Scope = info.Scope,
            Folder = BackupStore.ChatFolderName(info),
        };
        var chatDir = store.ChatDir(chat);
        var writer = new ChatBackupWriter(store, chat);
        int mediaDone = 0;
        int? revokeWindow = PurgeRule.RevokeWindowFor(info.Kind, tlConfig);
        var now = DateTime.UtcNow;
        bool Purgeable(MessageBase m) =>
            PurgeRule.IsPurgeable(info.Scope, History.IsOwn(m), DateTime.SpecifyKind(m.Date, DateTimeKind.Utc), cutoffUtc, revokeWindow, now);
        var before = $"sent before {cutoffUtc:yyyy-MM-dd HH:mm} UTC";
        chat.ContentNote = info.Scope == DeleteScope.Everyone
            ? $"every message {before} (all of them are deleted for everyone)"
            : $"only your own messages {before} (only those can be deleted; other people's messages are not copied)";

        // Page by page: convert, download the page's media, write the page to disk, forget it. Memory stays flat
        // however large the chat is.
        async Task OnPage(IReadOnlyList<MessageBase> page)
        {
            // Only what this run deletes is backed up; everything else is skipped without touching the disk.
            var kept = page.Where(Purgeable).ToList();
            if (kept.Count == 0) return;
            // Rough room check for this page's media before downloading any of it.
            Storage.EnsureRoom(EstimateMediaBytes(kept), Storage.AvailableBytes(chatDir), $"the media of \"{info.Title}\"");
            var records = new List<MessageRecord>(kept.Count);
            foreach (var m in kept.OrderBy(m => m.ID))
            {
                var rec = ToRecord(m);
                if (m is Message { media: not null } msg)
                {
                    if (msg.media is MessageMediaPhoto or MessageMediaDocument)
                    {
                        mediaDone++;
                        _mediaLabel = $"    {writer.Count + records.Count} messages saved; downloading media file {mediaDone}";
                        Status.Progress(_mediaLabel + "…");
                    }
                    await BackupMediaAsync(target, msg, rec, chatDir);
                }
                records.Add(rec);
            }
            writer.AddPage(records);
        }

        try
        {
            var stats = await History.StreamAsync(api, target, dir, OnPage,
                n => Status.Progress($"    {n} messages read, {writer.Count} to back up…"));
            chat.MessagesScanned = stats.Fetched;
            chat.ServerReportedCount = stats.ReportedCount;
            chat.ServerCountInexact = stats.Inexact;
            Status.EndProgress();
        }
        catch (Exception ex) when (ex is not BackupStorageException)
        {
            // Keep what was saved; the recorded error makes this chat fail verification, so it is not purged.
            Status.EndProgress();
            Status.Warn($"    could not read the whole history of \"{info.Title}\": {ex.Message}");
            chat.Errors.Add($"could not fetch history: {ex.Message}");
        }

        Status.Info($"    writing {BackupStore.MessagesFile} and the Markdown history ({writer.Count} messages)…");
        writer.Complete(exportedUtc);
        return chat;
    }

    string _mediaLabel = "    downloading media";

    void ReportBytes(long transmitted, long total) =>
        Status.Progress(total > 0
            ? $"{_mediaLabel} — {Storage.Human(transmitted)} / {Storage.Human(total)} ({transmitted * 100 / total}%)"
            : $"{_mediaLabel} — {Storage.Human(transmitted)}");

    /// <summary>
    /// Runs a download, aborting it when no data arrives for <see cref="StallTimeout"/>. Without this, a request that
    /// Telegram never answers keeps the backup waiting forever. The abort throws <see cref="TimeoutException"/>, which
    /// the caller treats like any failed attempt (retried, and the chat does not verify if every attempt fails).
    /// </summary>
    async Task<T> WithStallTimeoutAsync<T>(Func<WTelegram.Client.ProgressCallback, Task<T>> download, int dcId, int messageId)
    {
        long lastBytes = -1;
        long lastChangeTicks = Environment.TickCount64;
        void OnProgress(long transmitted, long total)
        {
            if (Interlocked.Exchange(ref lastBytes, transmitted) != transmitted)
                Interlocked.Exchange(ref lastChangeTicks, Environment.TickCount64);
            ReportBytes(transmitted, total);
        }

        var task = download(OnProgress);
        var check = TimeSpan.FromMilliseconds(Math.Clamp(StallTimeout.TotalMilliseconds / 4, 10, 5000));
        while (true)
        {
            if (await Task.WhenAny(task, Task.Delay(check)) == task) return await task;
            var quiet = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastChangeTicks));
            if (quiet < StallTimeout) continue;

            log($"media download for message #{messageId} received no data for {quiet.TotalSeconds:0}s; aborting it");
            try
            {
                await api.AbortMediaDownloadsAsync(dcId);
            }
            catch (Exception ex)
            {
                log($"could not close the media connection to DC {dcId}: {ex.Message}");
            }
            // Give the aborted download a moment to fail so it stops writing; if it still hangs, abandon it.
            if (await Task.WhenAny(task, Task.Delay(AbortGrace)) != task)
                log($"download for message #{messageId} did not stop after the abort; abandoning it");
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"no data received for {Status.Human(quiet)} (download stalled)");
        }
    }

    MessageRecord ToRecord(MessageBase m)
    {
        var rec = new MessageRecord
        {
            Id = m.ID,
            DateUtc = DateTime.SpecifyKind(m.Date, DateTimeKind.Utc),
            IsOwn = History.IsOwn(m),
        };
        var sender = rec.IsOwn ? new PeerUser { user_id = api.UserId } : (m.From ?? m.Peer);
        if (sender is not null)
        {
            rec.SenderId = sender.ID;
            rec.SenderName = PeerName(sender);
        }
        if (m.ReplyTo is MessageReplyHeader rh && rh.reply_to_msg_id != 0) rec.ReplyToId = rh.reply_to_msg_id;

        switch (m)
        {
            case Message msg:
                rec.Text = msg.message ?? "";
                if (msg.post_author is { Length: > 0 } author) rec.SenderName = $"{rec.SenderName} ({author})";
                if (msg.fwd_from is { } fwd)
                    rec.ForwardedFrom = fwd.from_name ?? (fwd.from_id is { } fp ? PeerName(fp) : "hidden user");
                if (msg.edit_date != default) rec.EditDateUtc = DateTime.SpecifyKind(msg.edit_date, DateTimeKind.Utc);
                break;
            case MessageService svc:
                rec.IsService = true;
                rec.Text = DescribeAction(svc.action);
                break;
        }
        return rec;
    }

    string PeerName(Peer p)
    {
        switch (p)
        {
            case PeerUser pu when dir.Users.TryGetValue(pu.user_id, out var u):
                var name = TargetEnumerator.DisplayName(u);
                return u.MainUsername is { } un ? $"{name} (@{un})" : name;
            case PeerChat pc when dir.Chats.TryGetValue(pc.chat_id, out var c):
                return c.Title;
            case PeerChannel pch when dir.Chats.TryGetValue(pch.channel_id, out var ch):
                return ch.Title;
            default:
                return $"{p.GetType().Name} {p.ID}";
        }
    }

    static string DescribeAction(MessageAction? action)
    {
        if (action is null) return "service message";
        var name = action.GetType().Name.Replace("MessageAction", "");
        return action switch
        {
            MessageActionChatEditTitle t => $"{name}: {t.title}",
            MessageActionChatCreate c => $"{name}: {c.title}",
            MessageActionChannelCreate c => $"{name}: {c.title}",
            MessageActionPhoneCall call => $"{name}: {call.duration}s",
            _ => name,
        };
    }

    async Task BackupMediaAsync(TelegramTarget target, Message msg, MessageRecord rec, string chatDir)
    {
        switch (msg.media)
        {
            case MessageMediaPhoto { photo: Photo }:
            case MessageMediaDocument { document: Document }:
                await DownloadWithRefreshAsync(target, msg, rec, chatDir);
                break;
            case MessageMediaPhoto mp:
                rec.MediaStatus = MediaStatus.Unavailable;
                rec.MediaDescription = "photo";
                rec.MediaError = mp.flags.HasFlag(MessageMediaPhoto.Flags.has_ttl_seconds) ? "self-destructing photo already expired" : "photo removed by Telegram";
                break;
            case MessageMediaDocument md:
                rec.MediaStatus = MediaStatus.Unavailable;
                rec.MediaDescription = "file";
                rec.MediaError = md.flags.HasFlag(MessageMediaDocument.Flags.has_ttl_seconds) ? "self-destructing file already expired" : "file removed by Telegram";
                break;
            default:
                rec.MediaStatus = MediaStatus.NotAFile;
                rec.MediaDescription = DescribeNonFileMedia(msg.media);
                break;
        }
    }

    async Task DownloadWithRefreshAsync(TelegramTarget target, Message msg, MessageRecord rec, string chatDir)
    {
        const int attempts = 4;
        for (int i = 1; i <= attempts; i++)
        {
            try
            {
                await DownloadAsync(msg, rec, chatDir);
                return;
            }
            catch (Exception ex) when (Storage.IsLocalWriteFailure(ex))
            {
                // Retrying cannot help when the disk is full or unwritable: stop the whole backup now.
                throw ex as BackupStorageException
                    ?? new BackupStorageException($"cannot write media of message #{msg.id}: {ex.Message}", ex);
            }
            catch (RpcException flood) when (Retry.IsFloodWait(flood))
            {
                // A rate limit is not a failure: wait it out and retry without using up an attempt.
                await Retry.WaitOutFloodAsync(flood);
                i--;
            }
            catch (Exception ex)
            {
                rec.MediaStatus = MediaStatus.Failed;
                rec.MediaError = ex.Message;
                log($"media download for message #{msg.id} failed (attempt {i}/{attempts}): {ex.Message}");
                Status.Warn($"    media of message #{msg.id} failed ({ex.Message}); " +
                    (i < attempts ? $"retry {i}/{attempts - 1} shortly…" : "giving up — this chat will not verify"));
                if (i == attempts) return;
                await Task.Delay(RetryDelay * i);
                // File references expire; re-fetching the message yields a fresh one.
                if (ex is RpcException)
                {
                    try
                    {
                        if (await History.RefetchAsync(api, target, msg.id) is Message fresh) msg = fresh;
                    }
                    catch (Exception refetchEx)
                    {
                        log($"could not refresh message #{msg.id}: {refetchEx.Message}");
                    }
                }
            }
        }
    }

    async Task DownloadAsync(Message msg, MessageRecord rec, string chatDir)
    {
        string relDir = BackupStore.MediaDir;
        string tmp = Path.Combine(chatDir, relDir, $"{msg.id}.download");
        string finalName;
        string? finalPath = null;
        long? expected = null;
        long size;

        try
        {
            switch (msg.media)
            {
                case MessageMediaPhoto { photo: Photo photo }:
                {
                    Storage.EnsureRoom(PhotoEstimateBytes, Storage.AvailableBytes(chatDir), $"photo of message #{msg.id}");
                    Storage_FileType type;
                    await using (var fs = Storage.RetryOnTransientLock(() => Storage.CreatePrivateFile(tmp)))
                    {
                        type = await WithStallTimeoutAsync(p => api.DownloadPhotoAsync(photo, fs, p), photo.dc_id, msg.id);
                        fs.Flush(flushToDisk: true);
                    }
                    finalName = BackupStore.MediaFileName(msg.id, "photo", ExtensionFor(type));
                    rec.MediaDescription = "photo";
                    break;
                }
                case MessageMediaDocument { document: Document doc }:
                {
                    Storage.EnsureRoom(doc.size, Storage.AvailableBytes(chatDir), $"file of message #{msg.id}");
                    await using (var fs = Storage.RetryOnTransientLock(() => Storage.CreatePrivateFile(tmp)))
                    {
                        await WithStallTimeoutAsync(async p => { await api.DownloadDocumentAsync(doc, fs, p); return 0; },
                            doc.dc_id, msg.id);
                        fs.Flush(flushToDisk: true);
                    }
                    expected = doc.size;
                    var original = doc.Filename;
                    // The file name comes from the sender, so it may be huge, contain separators or a bogus extension.
                    var ext = BackupStore.SanitizeExtension(Path.GetExtension(original ?? ""));
                    if (ext.Length == 0) ext = ExtensionForMime(doc.mime_type);
                    var baseName = string.IsNullOrEmpty(original) ? KindOf(doc) : Path.GetFileNameWithoutExtension(original);
                    finalName = BackupStore.MediaFileName(msg.id, baseName, ext);
                    rec.MediaDescription = original is { Length: > 0 } ? $"{KindOf(doc)}: {original}" : KindOf(doc);
                    break;
                }
                default:
                    throw new InvalidOperationException("not a downloadable media");
            }

            finalPath = Path.Combine(chatDir, relDir, finalName);
            BackupStore.MoveWithRetry(tmp, finalPath);
            size = new FileInfo(finalPath).Length;
            if (expected is long e && e > 0 && size != e)
                throw new InvalidDataException($"downloaded {size} bytes but Telegram declared {e}");
            if (size == 0)
                throw new InvalidDataException("downloaded file is empty");
        }
        catch
        {
            // Never leave partial or bad files behind: they waste space and must not look like a backup.
            TryDelete(tmp);
            if (finalPath is not null) TryDelete(finalPath);
            throw;
        }

        rec.MediaFile = $"{relDir}/{finalName}";
        rec.MediaExpectedSize = expected;
        rec.MediaSize = size;
        rec.MediaSha256 = BackupStore.Sha256OfFile(finalPath);
        rec.MediaStatus = MediaStatus.Downloaded;
        rec.MediaError = null;
    }

    /// <summary>Photo sizes are not known before download; Telegram's largest photo version stays well below this.</summary>
    const long PhotoEstimateBytes = 10L * 1024 * 1024;

    static long EstimateMediaBytes(IEnumerable<MessageBase> messages)
    {
        long total = 0;
        foreach (var m in messages)
            total += m switch
            {
                Message { media: MessageMediaDocument { document: Document d } } => d.size,
                Message { media: MessageMediaPhoto { photo: Photo } } => 1024 * 1024, // typical full-size photo
                _ => 0,
            };
        return total;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    static string KindOf(Document doc)
    {
        foreach (var a in doc.attributes ?? [])
            switch (a)
            {
                case DocumentAttributeSticker: return "sticker";
                case DocumentAttributeVideo v: return v.flags.HasFlag(DocumentAttributeVideo.Flags.round_message) ? "video message" : "video";
                case DocumentAttributeAudio au: return au.flags.HasFlag(DocumentAttributeAudio.Flags.voice) ? "voice message" : "audio";
                case DocumentAttributeAnimated: return "animation";
            }
        return "file";
    }

    static string ExtensionFor(Storage_FileType type) => type switch
    {
        Storage_FileType.jpeg => ".jpg",
        Storage_FileType.png => ".png",
        Storage_FileType.gif => ".gif",
        Storage_FileType.webp => ".webp",
        Storage_FileType.mp4 => ".mp4",
        Storage_FileType.mov => ".mov",
        Storage_FileType.mp3 => ".mp3",
        Storage_FileType.pdf => ".pdf",
        _ => ".jpg",
    };

    static string ExtensionForMime(string? mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        "audio/ogg" => ".ogg",
        "audio/mpeg" => ".mp3",
        "audio/mp4" or "audio/x-m4a" => ".m4a",
        "application/pdf" => ".pdf",
        "application/x-tgsticker" => ".tgs",
        "application/zip" => ".zip",
        "text/plain" => ".txt",
        _ => ".bin",
    };

    static string DescribeNonFileMedia(MessageMedia media) => media switch
    {
        MessageMediaGeoLive gl when gl.geo is GeoPoint gp => $"Live location: {F(gp.lat)}, {F(gp.lon)}",
        MessageMediaVenue v when v.geo is GeoPoint gp => $"Venue: {v.title}, {v.address} ({F(gp.lat)}, {F(gp.lon)})",
        MessageMediaGeo g when g.geo is GeoPoint gp => $"Location: {F(gp.lat)}, {F(gp.lon)}",
        MessageMediaContact c => $"Contact: {c.first_name} {c.last_name} {c.phone_number}".Trim(),
        MessageMediaPoll p => $"Poll: {p.poll.question?.text} — options: {string.Join(" | ", (p.poll.answers ?? []).Select(a => a.Text?.text))}",
        MessageMediaWebPage { webpage: WebPage wp } => $"Link preview: {wp.url} {wp.title}".Trim(),
        MessageMediaWebPage => "Link preview",
        MessageMediaDice d => $"Dice {d.emoticon}: {d.value}",
        MessageMediaGame gm => $"Game: {gm.game?.title}",
        MessageMediaInvoice inv => $"Invoice: {inv.title} {inv.total_amount} {inv.currency}",
        MessageMediaStory => "Shared story",
        _ => media.GetType().Name.Replace("MessageMedia", "Media: "),
    };

    static string F(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
