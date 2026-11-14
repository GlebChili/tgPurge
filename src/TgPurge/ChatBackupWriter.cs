using System.Text.Json;

namespace TgPurge;

/// <summary>
/// Writes one chat's backup without holding the chat in memory. Pages of messages are appended to small temporary
/// files as they arrive from Telegram (newest page first); <see cref="Complete"/> then streams them, oldest first, into
/// messages.jsonl and into one or more Markdown files. Every disk failure becomes a <see cref="BackupStorageException"/>.
/// </summary>
public sealed class ChatBackupWriter
{
    public const int DefaultMessagesPerPart = 50_000;
    const string PagesDir = ".pages";

    readonly BackupStore _store;
    readonly ChatBackup _chat;
    readonly int _messagesPerPart;
    readonly string _dir;
    readonly string _pagesDir;
    readonly List<string> _pages = [];
    long _pageBytes;

    public int Count { get; private set; }

    public ChatBackupWriter(BackupStore store, ChatBackup chat, int messagesPerPart = DefaultMessagesPerPart)
    {
        _store = store;
        _chat = chat;
        _messagesPerPart = Math.Max(1, messagesPerPart);
        _dir = store.ChatDir(chat);
        _pagesDir = Path.Combine(_dir, PagesDir);
        Storage.Guard($"folder for \"{chat.Title}\"", () =>
        {
            Directory.CreateDirectory(Path.Combine(_dir, BackupStore.MediaDir));
            if (Directory.Exists(_pagesDir)) Directory.Delete(_pagesDir, recursive: true);
            Directory.CreateDirectory(_pagesDir);
        });
    }

    /// <summary>Stores one page of messages (any order) in a temporary file.</summary>
    public void AddPage(IEnumerable<MessageRecord> records)
    {
        var ordered = records.OrderBy(r => r.Id).ToList();
        if (ordered.Count == 0) return;
        var path = Path.Combine(_pagesDir, $"{_pages.Count:D7}.jsonl");
        Storage.Guard($"backup of \"{_chat.Title}\"", () =>
        {
            var bytes = 0L;
            BackupStore.WriteFileAtomic(path, fs =>
            {
                using var w = BackupStore.TextWriterFor(fs);
                foreach (var r in ordered) w.WriteLine(JsonSerializer.Serialize(r, BackupJsonLine.Default.MessageRecord));
                w.Flush();
                bytes = fs.Length;
            });
            _pageBytes += bytes;
        });
        _pages.Add(path);
        Count += ordered.Count;
    }

    /// <summary>All stored messages, oldest first: pages arrived newest-first, each page is sorted ascending.</summary>
    IEnumerable<string> LinesOldestFirst()
    {
        for (int i = _pages.Count - 1; i >= 0; i--)
            foreach (var line in BackupStore.ReadLines(_pages[i]))
                if (line.Length > 0) yield return line;
    }

    /// <summary>Assembles messages.jsonl and the Markdown history, records them in the chat entry, removes temp files.</summary>
    public void Complete(DateTime exportedUtc)
    {
        // The final files are about the size of the stored pages (Markdown a bit more); the pages are freed afterwards.
        Storage.EnsureRoom(_pageBytes * 3, Storage.AvailableBytes(_dir), $"the text backup of \"{_chat.Title}\"");
        Storage.Guard($"backup of \"{_chat.Title}\"", () =>
        {
            _chat.MessageCount = Count;
            _chat.MessagesJsonSha256 = BackupStore.WriteFileAtomic(Path.Combine(_dir, BackupStore.MessagesFile), fs =>
            {
                using var w = BackupStore.TextWriterFor(fs);
                foreach (var line in LinesOldestFirst()) w.WriteLine(line);
            });

            int parts = Math.Max(1, (Count + _messagesPerPart - 1) / _messagesPerPart);
            _chat.HistoryParts = [];
            using var records = LinesOldestFirst()
                .Select(l => JsonSerializer.Deserialize(l, BackupJsonLine.Default.MessageRecord)!)
                .GetEnumerator();
            bool more = records.MoveNext();
            for (int part = 1; part <= parts; part++)
            {
                var file = BackupStore.HistoryPartFile(part, parts);
                var info = new HistoryPart { File = file };
                info.Sha256 = BackupStore.WriteFileAtomic(Path.Combine(_dir, file), fs =>
                {
                    using var w = BackupStore.TextWriterFor(fs);
                    MarkdownRenderer.WriteHeader(w, _chat, exportedUtc, Count, part, parts,
                        part > 1 ? BackupStore.HistoryPartFile(part - 1, parts) : null);
                    for (int n = 0; n < _messagesPerPart && more; n++)
                    {
                        var m = records.Current;
                        if (info.Messages == 0) info.FirstId = m.Id;
                        info.LastId = m.Id;
                        info.Messages++;
                        MarkdownRenderer.WriteMessage(w, m);
                        more = records.MoveNext();
                    }
                    MarkdownRenderer.WriteFooter(w, part < parts ? BackupStore.HistoryPartFile(part + 1, parts) : null);
                });
                _chat.HistoryParts.Add(info);
            }
        });
        try { Directory.Delete(_pagesDir, recursive: true); } catch { /* temporary data only */ }
    }
}
