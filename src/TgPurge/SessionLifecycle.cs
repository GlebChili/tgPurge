using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TL;
using WTelegram;

namespace TgPurge;

/// <summary>
/// Owns everything that must not outlive a run: the Telegram session (server side and file), the MTProto debug log,
/// and the private temporary folder holding them. Cleanup runs exactly once, on normal exit, on error, and on
/// Ctrl+C / SIGTERM / SIGHUP / SIGQUIT. A second signal forces an immediate exit.
/// </summary>
public sealed class SessionLifecycle : IDisposable
{
    readonly object _gate = new();
    readonly List<PosixSignalRegistration> _signals = [];
    readonly bool _keepSession;
    readonly SafeLog _log;
    int _signalCount;
    bool _cleanedUp;

    public string WorkDir { get; }
    public string SessionPath { get; }
    public string LogPath { get; }
    /// <summary>Random per-run key for the session file, or null to use WTelegram's default (the api_hash).</summary>
    public string? SessionKey { get; }
    public Client? Client { get; set; }
    /// <summary>Where the MTProto log should be kept after the run (the backup folder), if it exists by then.</summary>
    public string? KeepLogIn { get; set; }
    /// <summary>Extra lines to print when the run is interrupted (e.g. whether deletion had started).</summary>
    public Func<string>? InterruptNote { get; set; }

    public SessionLifecycle(string? persistentSessionPath, bool keepSession)
    {
        _keepSession = keepSession;
        WorkDir = Path.Combine(Path.GetTempPath(), $"tgPurge-{Guid.NewGuid():N}");
        Storage.CreatePrivateDirectory(WorkDir);
        SessionPath = persistentSessionPath ?? Path.Combine(WorkDir, "telegram.session");
        // A throw-away session is encrypted with a key that exists only in this process's memory,
        // so a session file left behind by a hard kill cannot be decrypted, not even with the api_hash.
        SessionKey = persistentSessionPath is null && !keepSession ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : null;
        LogPath = Path.Combine(WorkDir, "mtproto.log");
        _log = new SafeLog(LogPath);
        Helpers.Log = (level, message) =>
        {
            _log.Write(level, message);
            Status.OnLibraryLog(level, message);
        };

        foreach (var sig in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
        {
            try { _signals.Add(PosixSignalRegistration.Create(sig, OnSignal)); }
            catch (PlatformNotSupportedException) { /* not available on this OS */ }
        }
    }

    void OnSignal(PosixSignalContext ctx)
    {
        if (Interlocked.Increment(ref _signalCount) > 1)
        {
            Console.Error.WriteLine("\nForced exit. The Telegram session may still be active: end it in Telegram → Settings → Devices.");
            return; // let the default handler terminate the process now
        }
        ctx.Cancel = true; // keep the process alive long enough to clean up
        Console.Error.WriteLine($"\nInterrupted ({ctx.Signal}). Logging out and removing the session… (press Ctrl+C again to force quit)");
        _ = Task.Run(async () =>
        {
            await CleanupAsync();
            if (InterruptNote?.Invoke() is { Length: > 0 } note) Console.Error.WriteLine(note);
            Environment.Exit(130);
        });
    }

    /// <summary>Logs out (unless the session is kept), deletes the session and temporary files. Never throws; runs once.</summary>
    public async Task CleanupAsync()
    {
        lock (_gate)
        {
            if (_cleanedUp) return;
            _cleanedUp = true;
        }
        var client = Client;
        Status.StopHeartbeat();
        if (client is not null && !_keepSession)
        {
            Status.Info("Logging out of Telegram…");
            try
            {
                if (client.User is not null)
                {
                    var logout = client.Auth_LogOut();
                    if (await Task.WhenAny(logout, Task.Delay(TimeSpan.FromSeconds(15))) == logout)
                    {
                        await logout;
                        Status.Info("Logged this tgPurge session out of Telegram.");
                    }
                    else Status.Warn("Logging out timed out. End the session in Telegram → Settings → Devices.");
                }
            }
            catch (Exception ex)
            {
                Status.Warn($"Could not log the session out: {ex.Message}. End it in Telegram → Settings → Devices.");
            }
        }
        try { client?.Dispose(); } catch { /* best effort */ }

        Helpers.Log = (_, _) => { };
        _log.Dispose();
        try
        {
            if (KeepLogIn is { } dir && Directory.Exists(dir))
                File.Move(LogPath, Path.Combine(dir, "mtproto.log"), overwrite: true);
        }
        catch { /* best effort */ }

        if (_keepSession)
            Console.WriteLine($"Session kept active; session file: {SessionPath}. " +
                "Anyone with this file can access your account — delete it and end the session when done.");
        else
            TryDelete(SessionPath);
        if (!(_keepSession && SessionPath.StartsWith(WorkDir, StringComparison.Ordinal)))
        {
            try { Directory.Delete(WorkDir, recursive: true); } catch { /* best effort */ }
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        foreach (var s in _signals) s.Dispose();
        _signals.Clear();
    }
}
