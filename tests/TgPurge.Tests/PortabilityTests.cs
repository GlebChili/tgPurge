using System.Text;

namespace TgPurge.Tests;

public sealed class FileNameLengthTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "tgPurgeNames-" + Guid.NewGuid().ToString("N"));

    public FileNameLengthTests() => Directory.CreateDirectory(_dir);

    // Worst cases for the 255-byte name limit on Linux/macOS.
    public static TheoryData<string> LongNames => new()
    {
        new string('中', 300),                                  // CJK: 3 bytes each in UTF-8
        string.Concat(Enumerable.Repeat("한국어", 100)),         // Hangul: 3 bytes NFC, up to 9 bytes NFD (HFS+)
        string.Concat(Enumerable.Repeat("😀", 200)),            // emoji: surrogate pairs, 4 bytes each
        string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 50)),     // ZWJ family: one character, many code points
        string.Concat(Enumerable.Repeat("é", 300)),             // combining-sensitive Latin
        new string('a', 1000),
        "trailing dots and spaces" + new string(' ', 50) + new string('.', 50),
    };

    static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }

    [Theory]
    [MemberData(nameof(LongNames))]
    public void Chat_folder_names_fit_and_can_be_created(string title)
    {
        var name = BackupStore.ChatFolderName(new ChatInfo(1_000_000_000_123, ChatKind.Supergroup, title, null, DeleteScope.OwnOnly, ""));
        Assert.True(BackupStore.Utf8Bytes(name) <= BackupStore.MaxNameBytes, $"{BackupStore.Utf8Bytes(name)} bytes");
        Assert.False(HasLoneSurrogate(name));
        Assert.False(name.EndsWith('.') || name.EndsWith(' '));
        Directory.CreateDirectory(Path.Combine(_dir, name));
        Assert.True(Directory.Exists(Path.Combine(_dir, name)));
    }

    [Theory]
    [MemberData(nameof(LongNames))]
    public void Media_file_names_fit_and_can_be_written_atomically(string baseName)
    {
        var name = BackupStore.MediaFileName(2_147_483_647, baseName, ".jpeg");
        Assert.True(BackupStore.Utf8Bytes(name) <= BackupStore.MaxNameBytes, $"{BackupStore.Utf8Bytes(name)} bytes");
        // + ".part" (atomic write) must still be under the 255-byte OS limit.
        Assert.True(Encoding.UTF8.GetByteCount((name + ".part").Normalize(NormalizationForm.FormD)) <= 255);
        Assert.False(HasLoneSurrogate(name));
        Assert.EndsWith(".jpeg", name);
        var path = Path.Combine(_dir, name);
        BackupStore.WriteAllTextAtomic(path, "x");
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void Truncation_keeps_whole_characters()
    {
        var family = "👨‍👩‍👧‍👦";
        var s = BackupStore.SanitizeFileName(string.Concat(Enumerable.Repeat(family, 10)), 30);
        Assert.True(s.Length % family.Length == 0, "a ZWJ sequence was split");
        Assert.NotEmpty(s);
    }

    [Theory]
    [InlineData(".jpg", ".jpg")]
    [InlineData("tar", ".tar")]
    [InlineData(".verylongextension", "")]
    [InlineData(".j/p:g", "")]
    [InlineData(".中文", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Extensions_are_kept_only_when_short_and_plain(string? input, string expected) =>
        Assert.Equal(expected, BackupStore.SanitizeExtension(input));

    [Fact]
    public void Sender_supplied_names_cannot_escape_or_break_the_folder()
    {
        var name = BackupStore.MediaFileName(5, "..\\..//etc/passwd:<>|?*\"", ".txt");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.StartsWith("5_", name);
        Assert.Equal("untitled", BackupStore.SanitizeFileName("...   ..."));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public class TransientLockRetryTests
{
    static readonly TimeSpan[] FastDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
    static IOException SharingViolation() => new("in use by another process", unchecked((int)0x80070020));
    static IOException LockViolation() => new("locked", unchecked((int)0x80070021));

    [Fact]
    public void Sharing_and_lock_violations_are_transient()
    {
        Assert.True(Storage.IsTransientLock(SharingViolation()));
        Assert.True(Storage.IsTransientLock(LockViolation()));
        Assert.False(Storage.IsTransientLock(new IOException("No space left on device", 28)));
        Assert.False(Storage.IsTransientLock(new FileNotFoundException()));
        // "Access denied" during a rename is a scanner symptom on Windows only.
        Assert.Equal(OperatingSystem.IsWindows(), Storage.IsTransientLock(new UnauthorizedAccessException()));
    }

    [Fact]
    public void Retries_until_the_lock_is_released()
    {
        int calls = 0;
        var result = Storage.RetryOnTransientLock(() => ++calls < 3 ? throw SharingViolation() : "ok", FastDelays);
        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Gives_up_after_all_delays_and_rethrows_the_lock_error()
    {
        int calls = 0;
        var ex = Assert.Throws<IOException>(() => Storage.RetryOnTransientLock<int>(() => { calls++; throw LockViolation(); }, FastDelays));
        Assert.Equal(unchecked((int)0x80070021), ex.HResult);
        Assert.Equal(FastDelays.Length + 1, calls);
    }

    [Fact]
    public void Real_errors_are_not_retried()
    {
        int calls = 0;
        Assert.Throws<IOException>(() => Storage.RetryOnTransientLock<int>(() => { calls++; throw new IOException("No space left on device", 28); }, FastDelays));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Default_retry_window_is_a_few_seconds()
    {
        var total = Storage.LockRetryDelays.Aggregate(TimeSpan.Zero, (a, d) => a + d);
        Assert.InRange(total.TotalSeconds, 3, 10);
    }
}
