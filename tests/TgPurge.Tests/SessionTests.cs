namespace TgPurge.Tests;

/// <summary>Local-only checks of session handling (constructing a WTelegram client does not touch the network).</summary>
[Collection("Status")]
public class SessionTests
{
    [Fact]
    public async Task Throwaway_session_lives_in_private_temp_folder_and_is_removed()
    {
        string workDir;
        using (var s = new SessionLifecycle(persistentSessionPath: null, keepSession: false))
        {
            workDir = s.WorkDir;
            Assert.True(Directory.Exists(workDir));
            Assert.StartsWith(workDir, s.SessionPath);
            Assert.NotNull(s.SessionKey);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(workDir));
            File.WriteAllText(s.SessionPath, "x");
            await s.CleanupAsync();
        }
        Assert.False(Directory.Exists(workDir));
    }

    [Fact]
    public async Task Random_session_key_is_accepted_by_WTelegram_and_required_to_reopen()
    {
        using var s = new SessionLifecycle(persistentSessionPath: null, keepSession: false);
        try
        {
            string? Config(string what, string? key) => what switch
            {
                "api_id" => "12345",
                "api_hash" => "0123456789abcdef0123456789abcdef",
                "session_pathname" => s.SessionPath,
                "session_key" => key,
                _ => null,
            };
            // WTelegram only writes session content after a login; force a save (via reflection, test-only)
            // so there is real encrypted content to test against.
            using (var c = new WTelegram.Client(w => Config(w, s.SessionKey))) SaveSession(c);
            Assert.True(new FileInfo(s.SessionPath).Length > 0, "session file was not written");

            // Same key: loads fine.
            using (var c = new WTelegram.Client(w => Config(w, s.SessionKey))) { }

            // Without the per-run key (i.e. with only the api_hash, which may be in shell history) the file is unreadable.
            Assert.ThrowsAny<Exception>(() => { using var c = new WTelegram.Client(w => Config(w, null)); });
        }
        finally
        {
            await s.CleanupAsync();
        }
    }

    static void SaveSession(WTelegram.Client c)
    {
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        var session = typeof(WTelegram.Client).GetFields(all).First(f => f.FieldType.Name == "Session").GetValue(c)!;
        session.GetType().GetMethods(all).First(m => m.Name == "Save" && m.GetParameters().Length == 0).Invoke(session, null);
    }

    [Fact]
    public void Private_files_are_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "tgPurgePriv-" + Guid.NewGuid().ToString("N"));
        try
        {
            Storage.CreatePrivateDirectory(dir);
            var file = Path.Combine(dir, "history.md");
            BackupStore.WriteAllTextAtomic(file, "secret");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
            Storage.SyncFileSystem(); // must not throw
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
