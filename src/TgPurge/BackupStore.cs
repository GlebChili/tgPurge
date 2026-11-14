using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TgPurge;

/// <summary>File-system layout of a backup: tgBacups/&lt;timestamp&gt;/&lt;chat folder&gt;/{history.md or history-NNNN.md, messages.jsonl, media/}.</summary>
public sealed class BackupStore
{
    public const string BackupsDirName = "tgBacups";
    public const string ManifestFile = "manifest.json";
    /// <summary>The readable history of a chat that fits in one file; larger chats use history-0001.md, history-0002.md, …</summary>
    public const string HistoryFile = "history.md";
    /// <summary>Structured history, one JSON message per line (JSON Lines), oldest first.</summary>
    public const string MessagesFile = "messages.jsonl";

    public static string HistoryPartFile(int part, int parts) => parts <= 1 ? HistoryFile : $"history-{part:D4}.md";
    public const string MediaDir = "media";

    public string Root { get; }

    public BackupStore(string root) => Root = root;

    public static string TimestampFolderName(DateTime localTime) => localTime.ToString("yyyy-MM-dd_HH-mm-ss");

    public static BackupStore CreateNew(string workingDir, DateTime localTime)
    {
        var root = Path.Combine(workingDir, BackupsDirName, TimestampFolderName(localTime));
        // Never reuse an existing backup folder.
        var candidate = root;
        for (int n = 2; Directory.Exists(candidate); n++) candidate = $"{root}_{n}";
        return new BackupStore(Path.GetFullPath(candidate));
    }

    /// <summary>
    /// Hard cap for any single file or folder name we create, in UTF-8 bytes of the decomposed (NFD) form.
    /// Linux and macOS limit a name to 255 bytes (HFS+ stores NFD, which can be up to 3x longer for e.g. Hangul);
    /// Windows limits it to 255 UTF-16 units, which is always satisfied when the UTF-8 length is within this cap.
    /// The margin leaves room for the ".part"/".download" suffixes used while writing.
    /// </summary>
    public const int MaxNameBytes = 200;

    static readonly HashSet<char> InvalidNameChars =
        Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();

    public static string ChatFolderName(ChatInfo chat)
    {
        var prefix = $"{chat.KindLabel}_{chat.Id}_";
        return prefix + SanitizeFileName(chat.Title, Math.Min(120, MaxNameBytes - Utf8Bytes(prefix)));
    }

    /// <summary>Media file name: "&lt;message id&gt;_&lt;base name&gt;&lt;extension&gt;", always within <see cref="MaxNameBytes"/>.</summary>
    public static string MediaFileName(int messageId, string baseName, string extension)
    {
        var ext = SanitizeExtension(extension);
        var prefix = $"{messageId}_";
        var budget = Math.Min(150, MaxNameBytes - Utf8Bytes(prefix) - Utf8Bytes(ext));
        return prefix + SanitizeFileName(baseName, budget) + ext;
    }

    /// <summary>Keeps an extension only if it is short and plain (".jpg", ".tar.gz"-style tails are cut to the last part).</summary>
    public static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return "";
        var ext = extension.StartsWith('.') ? extension[1..] : extension;
        if (ext.Length == 0 || ext.Length > 10 || !ext.All(char.IsAsciiLetterOrDigit)) return "";
        return "." + ext;
    }

    /// <summary>
    /// Makes a string safe as a file/folder name on Windows, macOS and Linux and at most <paramref name="maxBytes"/> long
    /// (UTF-8 of the NFD form). Truncation happens only between whole characters (grapheme clusters), so emoji and
    /// combined characters are never split.
    /// </summary>
    public static string SanitizeFileName(string name, int maxBytes = 120)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormC))
            sb.Append(InvalidNameChars.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var s = TrimForWindows(sb.ToString());

        if (Utf8Bytes(s) > maxBytes)
        {
            var cut = new StringBuilder();
            int used = 0;
            var e = StringInfo.GetTextElementEnumerator(s);
            while (e.MoveNext())
            {
                var element = (string)e.Current;
                int bytes = Utf8Bytes(element);
                if (used + bytes > maxBytes) break;
                cut.Append(element);
                used += bytes;
            }
            s = TrimForWindows(cut.ToString());
        }
        return s.Length == 0 ? "untitled" : s;
    }

    /// <summary>Windows silently drops trailing dots and spaces, which would make the name we store differ from the real one.</summary>
    static string TrimForWindows(string s) => s.Trim().TrimEnd('.', ' ').TrimStart('.');

    /// <summary>Worst-case on-disk byte length: UTF-8 of the decomposed form (what HFS+ stores).</summary>
    public static int Utf8Bytes(string s) => Encoding.UTF8.GetByteCount(s.Normalize(NormalizationForm.FormD));

    public string ChatDir(ChatBackup chat) => Path.Combine(Root, chat.Folder);

    public static string Sha256OfFile(string path) =>
        Storage.RetryOnTransientLock(() =>
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        });

    /// <summary>Reads a file, retrying while antivirus/indexers briefly hold a lock on it (Windows).</summary>
    public static string ReadAllTextWithRetry(string path) => Storage.RetryOnTransientLock(() => File.ReadAllText(path));

    /// <summary>Moves a file, retrying while antivirus/indexers briefly hold a lock on it (Windows).</summary>
    public static void MoveWithRetry(string from, string to) =>
        Storage.RetryOnTransientLock(() => File.Move(from, to, overwrite: true));

    /// <summary>
    /// Writes a file atomically (temp file + rename), flushed to disk, readable only by the user, and returns its SHA-256.
    /// <paramref name="write"/> runs exactly once (it may consume a one-pass stream); only opening the file and the final
    /// rename are retried on transient locks. Once open, the file is held exclusively, so scanners cannot interrupt it.
    /// </summary>
    public static string WriteFileAtomic(string path, Action<Stream> write)
    {
        var tmp = path + ".part";
        try
        {
            using (var fs = Storage.RetryOnTransientLock(() => Storage.CreatePrivateFile(tmp)))
            {
                write(fs);
                fs.Flush(flushToDisk: true); // durable before it can count as backed up
            }
            MoveWithRetry(tmp, path);
        }
        catch
        {
            // Leave any previous complete file untouched and free the space taken by the partial one.
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        return Sha256OfFile(path);
    }

    public static string WriteAllTextAtomic(string path, string content) =>
        WriteFileAtomic(path, fs => fs.Write(new UTF8Encoding(false).GetBytes(content)));

    /// <summary>A UTF-8 (no BOM) text writer over a stream, leaving the stream open.</summary>
    public static StreamWriter TextWriterFor(Stream s) => new(s, new UTF8Encoding(false), 1 << 16, leaveOpen: true);

    /// <summary>Reads a text file line by line (never whole), retrying the open while a scanner holds a lock.</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var fs = Storage.RetryOnTransientLock(() => File.OpenRead(path));
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>Writes a whole chat in one go (small chats and tests). Large chats stream through <see cref="ChatBackupWriter"/>.</summary>
    public void WriteChat(ChatBackup chat, IEnumerable<MessageRecord> messages, DateTime exportedUtc)
    {
        var writer = new ChatBackupWriter(this, chat);
        writer.AddPage(messages);
        writer.Complete(exportedUtc);
    }

    /// <summary>Writes manifest.json. Any disk failure throws <see cref="BackupStorageException"/>.</summary>
    public void WriteManifest(BackupManifest manifest) =>
        Storage.Guard(ManifestFile, () =>
        {
            Directory.CreateDirectory(Root);
            WriteAllTextAtomic(Path.Combine(Root, ManifestFile), JsonSerializer.Serialize(manifest, BackupJson.Default.BackupManifest));
        });

    public BackupManifest ReadManifest() =>
        JsonSerializer.Deserialize(ReadAllTextWithRetry(Path.Combine(Root, ManifestFile)), BackupJson.Default.BackupManifest)
        ?? throw new InvalidDataException("manifest.json is empty.");

    /// <summary>Streams messages.jsonl one record at a time.</summary>
    public IEnumerable<MessageRecord> ReadMessages(ChatBackup chat)
    {
        int lineNo = 0;
        foreach (var line in ReadLines(Path.Combine(ChatDir(chat), MessagesFile)))
        {
            lineNo++;
            if (line.Length == 0) continue;
            yield return JsonSerializer.Deserialize(line, BackupJsonLine.Default.MessageRecord)
                ?? throw new InvalidDataException($"{MessagesFile} line {lineNo} is empty");
        }
    }
}
