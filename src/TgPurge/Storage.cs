namespace TgPurge;

/// <summary>
/// The backup could not be written to disk (disk full, quota, permissions, …).
/// Always fatal for the backup phase: the tool stops before anything is deleted.
/// </summary>
public sealed class BackupStorageException(string message, Exception? inner = null) : Exception(message, inner);

public static class Storage
{
    /// <summary>Space kept free on top of what a write needs, so the OS and the final files still fit.</summary>
    public const long SafetyMarginBytes = 256L * 1024 * 1024;
    /// <summary>Below this much free space the user is warned before the backup starts.</summary>
    public const long LowSpaceWarningBytes = 2L * 1024 * 1024 * 1024;

    // errno: ENOSPC = 28 (macOS, Linux), EDQUOT = 69 (macOS) / 122 (Linux).
    // Windows: ERROR_DISK_FULL (0x70), ERROR_HANDLE_DISK_FULL (0x27) as HRESULTs.
    static readonly HashSet<int> OutOfSpaceCodes = [28, 69, 122, unchecked((int)0x80070070), unchecked((int)0x80070027)];

    public static bool IsOutOfSpace(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is BackupStorageException) return true;
            if (e is not IOException io) continue;
            if (OutOfSpaceCodes.Contains(io.HResult)) return true;
            var m = io.Message;
            if (m.Contains("No space left", StringComparison.OrdinalIgnoreCase)
                || m.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                || m.Contains("disk full", StringComparison.OrdinalIgnoreCase)
                || m.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>A failure that means "we cannot write here", as opposed to a network or Telegram error.</summary>
    public static bool IsLocalWriteFailure(Exception ex) =>
        IsOutOfSpace(ex) || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException || ex is PathTooLongException;

    /// <summary>Free bytes on the volume holding <paramref name="path"/> (or its nearest existing parent); null if unknown.</summary>
    public static long? AvailableBytes(string path)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            while (dir is not null && !dir.Exists) dir = dir.Parent;
            if (dir is null) return null;
            return new DriveInfo(dir.FullName).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Throws <see cref="BackupStorageException"/> if <paramref name="neededBytes"/> plus the safety margin does not fit.</summary>
    public static void EnsureRoom(long neededBytes, long? availableBytes, string what)
    {
        if (availableBytes is not long free) return;
        if (neededBytes + SafetyMarginBytes > free)
            throw new BackupStorageException(
                $"not enough disk space for {what}: needs about {Human(neededBytes)} (+{Human(SafetyMarginBytes)} reserve), only {Human(free)} free");
    }

    /// <summary>Runs a local file-system write and turns any I/O failure into a <see cref="BackupStorageException"/>.</summary>
    public static T Guard<T>(string what, Func<T> write)
    {
        try
        {
            return write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not BackupStorageException)
        {
            throw new BackupStorageException($"cannot write {what}: {ex.Message}", ex);
        }
    }

    public static void Guard(string what, Action write) => Guard(what, () => { write(); return 0; });

    // ERROR_SHARING_VIOLATION (0x20) and ERROR_LOCK_VIOLATION (0x21) as HRESULTs.
    static readonly HashSet<int> LockCodes = [unchecked((int)0x80070020), unchecked((int)0x80070021)];

    /// <summary>
    /// A short-lived lock, typically antivirus (Defender) or the search indexer scanning a file we just wrote on Windows.
    /// "Access denied" is only treated as transient on Windows, where scanners cause it during renames;
    /// on Unix it always means a real permission problem.
    /// </summary>
    public static bool IsTransientLock(Exception ex) =>
        ex is IOException io && LockCodes.Contains(io.HResult)
        || ex is UnauthorizedAccessException && OperatingSystem.IsWindows();

    /// <summary>Delays between attempts; about 6 seconds in total before the error is treated as real.</summary>
    public static readonly TimeSpan[] LockRetryDelays =
        [.. new[] { 50, 100, 200, 400, 800, 1500, 3000 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    /// <summary>Runs a file operation, retrying while it fails with a transient lock. Other errors are thrown immediately.</summary>
    public static T RetryOnTransientLock<T>(Func<T> op, IReadOnlyList<TimeSpan>? delays = null, Func<Exception, bool>? isTransient = null)
    {
        delays ??= LockRetryDelays;
        isTransient ??= IsTransientLock;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return op();
            }
            catch (Exception ex) when (attempt < delays.Count && isTransient(ex))
            {
                Thread.Sleep(delays[attempt]);
            }
        }
    }

    public static void RetryOnTransientLock(Action op, IReadOnlyList<TimeSpan>? delays = null, Func<Exception, bool>? isTransient = null) =>
        RetryOnTransientLock(() => { op(); return 0; }, delays, isTransient);

    const UnixFileMode OwnerOnlyDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates a directory only the current user can open (0700 on Linux/macOS). On Windows the folder inherits the
    /// ACL of its parent; the user's temp folder and profile are already private to the user.
    /// </summary>
    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, OwnerOnlyDir);
        File.SetUnixFileMode(path, OwnerOnlyDir); // also tighten it if it already existed
    }

    /// <summary>Creates (or truncates) a file readable only by the current user (0600 on Linux/macOS).</summary>
    public static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;
        return new FileStream(path, options);
    }

    delegate int SyncFn();

    /// <summary>
    /// Asks the OS to write all cached file data and directory entries to disk (sync(2) on Linux/macOS).
    /// Called before deleting anything, so a power loss or forced shutdown right after the purge cannot lose a backup
    /// that verified only from the page cache. On Windows each file is already flushed with FlushFileBuffers.
    /// </summary>
    public static void SyncFileSystem()
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var lib = OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6";
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad(lib, out var handle)
                && System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, "sync", out var fn))
                System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<SyncFn>(fn)();
        }
        catch { /* best effort: every backup file was already flushed individually */ }
    }

    public static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }
}
