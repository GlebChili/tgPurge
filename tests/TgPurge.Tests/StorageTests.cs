namespace TgPurge.Tests;

public sealed class StorageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "tgPurgeStorage-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData(28)]                            // ENOSPC (macOS, Linux)
    [InlineData(69)]                            // EDQUOT (macOS)
    [InlineData(122)]                           // EDQUOT (Linux)
    [InlineData(unchecked((int)0x80070070))]    // ERROR_DISK_FULL (Windows)
    [InlineData(unchecked((int)0x80070027))]    // ERROR_HANDLE_DISK_FULL (Windows)
    public void Out_of_space_error_codes_are_recognised(int hresult) =>
        Assert.True(Storage.IsOutOfSpace(new IOException("write failed", hresult)));

    [Fact]
    public void Out_of_space_message_and_inner_exception_are_recognised()
    {
        Assert.True(Storage.IsOutOfSpace(new IOException("No space left on device : '/x'")));
        Assert.True(Storage.IsOutOfSpace(new InvalidOperationException("wrapped", new IOException("x", 28))));
    }

    [Fact]
    public void Network_style_io_errors_are_not_storage_failures()
    {
        var net = new IOException("Unable to read data from the transport connection");
        Assert.False(Storage.IsOutOfSpace(net));
        Assert.False(Storage.IsLocalWriteFailure(net));
        Assert.False(Storage.IsLocalWriteFailure(new InvalidDataException("size mismatch")));
        Assert.True(Storage.IsLocalWriteFailure(new UnauthorizedAccessException()));
    }

    [Fact]
    public void EnsureRoom_reserves_a_safety_margin()
    {
        Storage.EnsureRoom(100, Storage.SafetyMarginBytes + 100, "x");            // exactly fits
        Storage.EnsureRoom(long.MaxValue / 2, null, "x");                           // unknown free space: no check
        var ex = Assert.Throws<BackupStorageException>(() => Storage.EnsureRoom(101, Storage.SafetyMarginBytes + 100, "the file"));
        Assert.Contains("the file", ex.Message);
    }

    [Fact]
    public void AvailableBytes_works_for_existing_and_not_yet_created_paths()
    {
        Assert.True(Storage.AvailableBytes(_dir) > 0);
        Assert.True(Storage.AvailableBytes(Path.Combine(_dir, "tgBacups", "2026-01-01_00-00-00", "chat")) > 0);
    }

    [Fact]
    public void Backup_store_turns_write_failures_into_BackupStorageException()
    {
        if (OperatingSystem.IsWindows()) return; // relies on POSIX permissions
        var store = BackupStore.CreateNew(_dir, DateTime.Now);
        Directory.CreateDirectory(store.Root);
        File.SetUnixFileMode(store.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var chat = new ChatBackup { Id = 1, Title = "x", Folder = "private_1_x" };
            Assert.Throws<BackupStorageException>(() => store.WriteChat(chat, [], DateTime.UtcNow));
            Assert.Throws<BackupStorageException>(() => store.WriteManifest(new BackupManifest()));
        }
        finally
        {
            File.SetUnixFileMode(store.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Failed_atomic_write_leaves_previous_file_and_no_partial()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_dir, "history.md");
        BackupStore.WriteAllTextAtomic(target, "original");
        // Make the .part path a directory so the write fails half-way through the atomic sequence.
        Directory.CreateDirectory(target + ".part");
        Assert.ThrowsAny<Exception>(() => BackupStore.WriteAllTextAtomic(target, "new"));
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public void Report_save_failure_does_not_throw()
    {
        var bad = Path.Combine(_dir, "missing-dir", "purge-report.md");
        Assert.False(Report.TrySave(bad, "report", out var error));
        Assert.NotNull(error);
        Assert.True(Report.TrySave(Path.Combine(_dir, "purge-report.md"), "report", out _));
    }

    [Fact]
    public void SafeLog_never_throws_after_its_file_becomes_unwritable()
    {
        var path = Path.Combine(_dir, "mtproto.log");
        var log = new SafeLog(path);
        log.Write(1, "hello");
        Assert.True(log.Enabled);
        log.Dispose();
        log.Write(1, "after dispose"); // must be a silent no-op
        Assert.False(log.Enabled);

        var unopenable = new SafeLog(Path.Combine(_dir, "no", "such", "dir", "x.log"));
        unopenable.Write(1, "ignored");
        Assert.False(unopenable.Enabled);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
