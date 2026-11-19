using System.Text;
using TgPurge;
using TgPurge.Telegram;
using TL;
using WTelegram;

Console.OutputEncoding = Encoding.UTF8;

Options opts;
try
{
    opts = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run with --help for usage.");
    return 2;
}
if (opts.ShowHelp)
{
    Console.WriteLine(Options.Usage);
    return 0;
}

try
{
    return await App.RunAsync(opts);
}
catch (OperationCanceledException ex)
{
    Console.Error.WriteLine($"Aborted: {ex.Message}");
    Console.Error.WriteLine(App.DeletionStarted ? App.DeletionInterruptedNote : "Nothing was deleted from Telegram.");
    return App.ExitAborted;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"tgPurge stopped because of an unexpected error: {ex.Message}");
    Console.Error.WriteLine(App.DeletionStarted ? App.DeletionInterruptedNote : "Nothing was deleted from Telegram.");
    Console.Error.WriteLine($"Details: {ex}");
    return App.ExitError;
}

static class App
{
    public const int ExitOk = 0, ExitAborted = 1, ExitUsage = 2, ExitIncomplete = 3, ExitStorage = 4, ExitError = 5;

    /// <summary>Set the moment the first deletion phase begins; used to tell the user what state a crash left them in.</summary>
    public static bool DeletionStarted { get; private set; }
    public static string DeletionInterruptedNote =>
        "Deletion had already started: some messages may be deleted, others not. Your verified backup is intact. " +
        "Re-run tgPurge with the same cut-off to finish.";

    public static async Task<int> RunAsync(Options opts)
    {
        ConsoleUi.Banner("tgPurge — Telegram emergency backup & purge", ConsoleColor.Yellow);

        // ---- Inputs --------------------------------------------------------------------------------------------
        if (opts.ApiId is null || opts.ApiHash is null)
            Console.WriteLine("Telegram API credentials are needed. Create them at https://my.telegram.org → API development tools.");
        while (opts.ApiId is null)
            if (int.TryParse(ConsoleUi.Prompt("api_id: "), out var id)) opts.ApiId = id;
        while (string.IsNullOrWhiteSpace(opts.ApiHash))
            opts.ApiHash = ConsoleUi.ReadSecret("api_hash (hidden while typing): ").Trim();
        while (opts.CutoffUtc is null)
        {
            try
            {
                opts.CutoffUtc = Options.ParseCutoff(
                    ConsoleUi.Prompt("Cut-off (delete everything sent before), e.g. 2025-06-01 or \"now\": "), DateTime.UtcNow);
            }
            catch (ArgumentException ex) { Console.WriteLine(ex.Message); }
        }
        var cutoff = opts.CutoffUtc.Value;
        var startedLocal = DateTime.Now;
        var store = BackupStore.CreateNew(Directory.GetCurrentDirectory(), startedLocal);

        // ---- Login ---------------------------------------------------------------------------------------------
        using var session = new SessionLifecycle(opts.SessionPath, opts.KeepSession);
        session.KeepLogIn = store.Root;
        session.InterruptNote = () => DeletionStarted ? DeletionInterruptedNote : "Nothing was deleted from Telegram.";

        var client = new Client(what => what switch
        {
            "api_id" => opts.ApiId.ToString(),
            "api_hash" => opts.ApiHash,
            "session_pathname" => session.SessionPath,
            "session_key" => session.SessionKey,
            // When resuming a kept session without a phone number, accept whichever account it belongs to
            // ("Logged in as …" is printed right after) instead of asking for the number just to compare it.
            "user_id" => opts.Phone is null ? "-1" : null,
            "phone_number" => opts.Phone ??= ConsoleUi.Prompt("Phone number in international format (e.g. +15551234567): "),
            "verification_code" => ConsoleUi.Prompt("Login code Telegram sent you: "),
            "email" => ConsoleUi.Prompt("Login email: "),
            "email_verification_code" => ConsoleUi.Prompt("Code sent to your email: "),
            "password" => ConsoleUi.ReadSecret("Two-step verification password: "),
            "first_name" or "last_name" => throw new OperationCanceledException("This number has no Telegram account; tgPurge will not create one."),
            _ => null,
        });
        session.Client = client;
        // WTelegram silently waits out only short rate limits; longer ones come back to tgPurge, which announces
        // them, counts them down and waits (Retry.RunAsync), so the user can see the tool is not frozen.
        client.FloodRetryThreshold = 10;

        try
        {
            Status.StartHeartbeat();
            Status.SetActivity("connecting and logging in to Telegram");
            var me = await LoginAsync(client, opts);
            Status.Info($"Logged in as {TargetEnumerator.DisplayName(me)}{(me.MainUsername is { } u ? " @" + u : "")} (id {me.id}).");
            var tlConfig = client.TLConfig ?? await Retry.RunAsync(() => client.Help_GetConfig());
            return await RunLoggedInAsync(client, me, tlConfig, opts, cutoff, store);
        }
        finally
        {
            // Cleanup never throws, so it cannot hide the real outcome of the run.
            await session.CleanupAsync();
        }
    }

    /// <summary>The verified messages of one chat as recorded in its backup (id, date, whether it is the user's).</summary>
    static List<ServerMessage> BackedUpMessages(BackupStore store, BackupManifest manifest, ChatKey key, IReadOnlySet<int> verifiedIds)
    {
        var chat = manifest.Chats.FirstOrDefault(c => c.Key == key);
        if (chat is null) return [];
        return store.ReadMessages(chat).Where(m => verifiedIds.Contains(m.Id))
            .Select(m => new ServerMessage(m.Id, DateTime.SpecifyKind(m.DateUtc, DateTimeKind.Utc), m.IsOwn)).ToList();
    }

    static async Task<User> LoginAsync(Client client, Options opts)
    {
        await client.ConnectAsync();
        if (client.UserId != 0)
            return await client.LoginUserIfNeeded(); // a kept session (--session) is resumed, no new login

        var method = opts.Login ?? (opts.Phone is not null || Console.IsInputRedirected
            ? LoginMethod.Code
            : ConsoleUi.AskLoginMethod());
        if (method == LoginMethod.Code)
            return await client.LoginUserIfNeeded();

        // No heartbeat lines while the QR code is on screen: they would scroll it away.
        using var quiet = Status.Suspend();
        Status.Info("Logging in with a QR code. Waiting for you to scan it…");
        var me = await client.LoginWithQRCode(ConsoleUi.ShowQr, logoutFirst: false)
            ?? throw new OperationCanceledException("QR login was cancelled.");
        return me;
    }

    static async Task<int> RunLoggedInAsync(Client client, User me, TL.Config tlConfig, Options opts, DateTime cutoff, BackupStore store)
    {
        // ---- Discover chats ------------------------------------------------------------------------------------
        ConsoleUi.Banner("Step 1/5 — Discovering all chats, groups and channels");
        var discovery = await TargetEnumerator.DiscoverAsync(new TelegramApi(client), me, Status.Info);
        var chats = discovery.Targets.Select(t => t.Info).ToList();
        Status.Info($"Found {chats.Count} chats.");
        foreach (var i in discovery.Inaccessible) ConsoleUi.Color($"  ! cannot access: {i}", ConsoleColor.Yellow);

        // ---- Exceptions ----------------------------------------------------------------------------------------
        IReadOnlySet<ChatKey> excluded = new HashSet<ChatKey>();
        ConsoleUi.PrintChatTable(chats, excluded);
        var tokens = opts.Exceptions.ToList();
        bool interactive = !opts.AssumeYes;
        while (true)
        {
            if (!opts.ExceptionsGiven && interactive)
            {
                var line = ConsoleUi.Prompt(
                    "\nChats to KEEP (exceptions): ids as shown (e.g. user:123), @usernames, exact titles or # row numbers, comma-separated. Empty = none: ");
                tokens = ExceptionMatcher.SplitInteractive(line)
                    .Select(t => t.StartsWith('#') && int.TryParse(t[1..], out var row) && row >= 1 && row <= chats.Count
                        ? chats[row - 1].Key.ToString() : t)
                    .ToList();
            }
            var resolution = ExceptionMatcher.Resolve(tokens, chats);
            if (resolution.IsValid) { excluded = resolution.Excluded; break; }
            foreach (var u in resolution.Unmatched) ConsoleUi.Color($"  exception \"{u}\" matches no chat", ConsoleColor.Red);
            foreach (var (t, m) in resolution.Ambiguous)
                ConsoleUi.Color($"  exception \"{t}\" matches {m.Count} chats ({string.Join(", ", m.Select(c => $"{c.Title} id {c.Id}"))}); use the id",
                    ConsoleColor.Red);
            if (opts.ExceptionsGiven || !interactive)
            {
                Console.Error.WriteLine("Refusing to continue: every exception must match exactly one chat, so nothing you meant to keep is purged.");
                return ExitUsage;
            }
        }
        if (excluded.Count > 0)
        {
            Console.WriteLine();
            ConsoleUi.PrintChatTable(chats, excluded);
        }

        // ---- Disclaimer ----------------------------------------------------------------------------------------
        ConsoleUi.Banner("WARNING — IRREVERSIBLE DELETION", ConsoleColor.Red);
        Console.WriteLine(ConsoleUi.Disclaimer(store.Root, cutoff, DateTime.UtcNow, chats, excluded, opts.DryRun));
        if (!opts.AssumeYes && !opts.DryRun)
        {
            var answer = ConsoleUi.Prompt($"Type {ConsoleUi.ConfirmPhrase} (in capitals) to back up and then permanently delete, anything else to abort: ");
            if (answer != ConsoleUi.ConfirmPhrase) { Console.WriteLine("Aborted. Nothing was backed up or deleted."); return ExitAborted; }
        }
        else if (opts.AssumeYes)
            Status.Info($"Confirmation given via {Options.ConfirmFlag}.");

        // ---- Backup --------------------------------------------------------------------------------------------
        ConsoleUi.Banner($"Step 2/5 — Backing up to {store.Root}");
        var free = Storage.AvailableBytes(store.Root);
        Status.Info(free is long f ? $"Free disk space: {Storage.Human(f)}" : "Free disk space: unknown");
        if (free is long low && low < Storage.LowSpaceWarningBytes)
        {
            ConsoleUi.Color($"  ! Only {Storage.Human(low)} free. Media backups are often much larger; the backup will stop " +
                "cleanly (deleting nothing) if the disk fills up.", ConsoleColor.Yellow);
            if (!opts.AssumeYes && ConsoleUi.Prompt("Continue anyway? [y/N]: ").ToLowerInvariant() is not ("y" or "yes"))
            {
                Console.WriteLine("Aborted. Nothing was deleted.");
                return ExitAborted;
            }
        }
        try
        {
            return await BackupThenPurgeAsync(client, me, tlConfig, opts, cutoff, store, discovery, excluded);
        }
        catch (BackupStorageException ex)
        {
            Console.WriteLine();
            ConsoleUi.Banner("BACKUP STOPPED — could not write to disk", ConsoleColor.Red);
            ConsoleUi.Color($"  {ex.Message}", ConsoleColor.Red);
            ConsoleUi.Color(DeletionStarted
                ? "  " + DeletionInterruptedNote
                : "  NOTHING was deleted from Telegram: deletion only starts after a complete, verified backup.", ConsoleColor.Red);
            Status.Info($"  Free up space (or choose a working directory on a bigger/writable disk) and re-run tgPurge.");
            Status.Info($"  The partial backup in {store.Root} can be deleted; a new run makes a fresh one.");
            return ExitStorage;
        }
    }

    static async Task<int> BackupThenPurgeAsync(Client client, User me, TL.Config tlConfig, Options opts, DateTime cutoff,
        BackupStore store, TargetDiscovery discovery, IReadOnlySet<ChatKey> excluded)
    {
        Storage.Guard("backup folder", () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.Root)!);
            Storage.CreatePrivateDirectory(store.Root); // only this user can read the backup
        });
        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            CutoffUtc = cutoff,
            AccountUserId = me.id,
            AccountName = TargetEnumerator.DisplayName(me),
        };
        var backupLog = new List<string>();
        ITelegramApi api = new TelegramApi(client);
        var runner = new BackupRunner(api, discovery, store, tlConfig, s => { backupLog.Add(s); });
        // Only chats that will be purged are backed up: never exceptions, never chats with nothing of yours to delete.
        var toBackUp = discovery.Targets.Where(t => t.Info.Scope != DeleteScope.None && !excluded.Contains(t.Info.Key)).ToList();
        int n = 0;
        foreach (var target in toBackUp)
        {
            Status.SetActivity($"backing up chat {n + 1}/{toBackUp.Count}: {target.Info.KindLabel} \"{target.Info.Title}\"");
            Status.Info($"  [{++n}/{toBackUp.Count}] {target.Info.KindLabel} \"{target.Info.Title}\"");
            var cb = await runner.BackupChatAsync(target, DateTime.UtcNow, cutoff);
            manifest.Chats.Add(cb);
            store.WriteManifest(manifest); // keep the manifest current so a crash leaves a usable backup
            Status.Info($"    {cb.MessageCount} messages saved{(cb.Errors.Count > 0 ? $", {cb.Errors.Count} error(s)" : "")}");
        }
        store.WriteManifest(manifest);
        Storage.Guard("backup-log.txt", () => BackupStore.WriteAllTextAtomic(Path.Combine(store.Root, "backup-log.txt"), string.Join(Environment.NewLine, backupLog)));

        // ---- Verify --------------------------------------------------------------------------------------------
        ConsoleUi.Banner("Step 3/5 — Verifying the backup from disk");
        var verifications = BackupVerifier.Verify(store, out var globalProblems, Status.Progress);
        Status.EndProgress();
        foreach (var g in globalProblems) ConsoleUi.Color($"  ✗ {g}", ConsoleColor.Red);
        if (globalProblems.Count > 0)
        {
            ConsoleUi.Color("Backup could not be verified. NOTHING will be deleted.", ConsoleColor.Red);
            return ExitIncomplete;
        }
        var verifiedByKey = verifications.ToDictionary(v => v.Key);
        foreach (var v in verifications)
        {
            if (v.Ok) ConsoleUi.Color($"  ✓ {v.Title}: {v.VerifiedIds.Count} messages verified", ConsoleColor.Green);
            else
            {
                ConsoleUi.Color($"  ✗ {v.Title}: backup NOT verified — this chat will NOT be purged", ConsoleColor.Red);
                foreach (var p in v.Problems.Take(10)) Status.Info($"      - {p}");
                if (v.Problems.Count > 10) Status.Info($"      … and {v.Problems.Count - 10} more");
            }
        }
        int failedVerifications = verifications.Count(v => !v.Ok);
        // Make sure the verified backup is physically on disk before anything is deleted.
        Status.SetActivity("flushing the backup to disk");
        Status.Info("Flushing the backup to disk before deleting anything…");
        Storage.SyncFileSystem();

        // ---- Purge ---------------------------------------------------------------------------------------------
        ConsoleUi.Banner(opts.DryRun ? "Step 4/5 — DRY RUN: computing what would be deleted" : "Step 4/5 — Deleting messages for everyone");
        if (!opts.DryRun) DeletionStarted = true;
        var purger = new Purger(api, discovery, tlConfig, Status.Warn);
        var results = new List<ChatPurgeResult>();
        var skipped = new List<(ChatInfo Chat, string Reason)>();
        foreach (var target in discovery.Targets)
        {
            var info = target.Info;
            if (excluded.Contains(info.Key)) { skipped.Add((info, "exception requested by you")); continue; }
            if (info.Scope == DeleteScope.None) { skipped.Add((info, "nothing of yours to delete")); continue; }
            if (!verifiedByKey.TryGetValue(info.Key, out var ver) || !ver.Ok)
            {
                skipped.Add((info, "BACKUP NOT VERIFIED — not purged"));
                continue;
            }
            Status.SetActivity($"purging {info.KindLabel} \"{info.Title}\"");
            Status.Info($"  {info.KindLabel} \"{info.Title}\" ({(info.Scope == DeleteScope.Everyone ? "all messages" : "your messages only")})");
            try
            {
                var r = await purger.PurgeAsync(target, BackedUpMessages(store, manifest, info.Key, ver.VerifiedIds), cutoff, opts.DryRun);
                results.Add(r);
                Status.Info(opts.DryRun
                    ? $"    would delete {r.Remaining.Count} messages"
                    : $"    deleted {r.Deleted}; remaining before cut-off: {r.Remaining.Count}");
            }
            catch (Exception ex)
            {
                var r = new ChatPurgeResult { Chat = info };
                r.Errors.Add(ex.Message);
                results.Add(r);
                ConsoleUi.Color($"    ✗ failed: {ex.Message}", ConsoleColor.Red);
            }
        }

        // ---- Report --------------------------------------------------------------------------------------------
        ConsoleUi.Banner("Step 5/5 — Report");
        var report = Report.Build(store.Root, cutoff, opts.DryRun, results, skipped, discovery.Inaccessible, verifications);
        var reportPath = Path.Combine(store.Root, opts.DryRun ? "dry-run-report.md" : "purge-report.md");
        // Print first: if the disk is full the report on screen is the only copy, and saving must not abort anything.
        Status.StopHeartbeat();
        Console.WriteLine(report);
        if (Report.TrySave(reportPath, report, out var saveError))
            Status.Info($"Report saved to {reportPath}");
        else
            ConsoleUi.Color($"Could not save the report to {reportPath} ({saveError}). Copy it from the screen above.", ConsoleColor.Yellow);

        // Chats that could not be read or inspected may still hold messages: never report such a run as complete.
        bool incomplete = failedVerifications > 0
            || discovery.Inaccessible.Count > 0
            || results.Any(r => !opts.DryRun && !r.Complete)
            || results.Any(r => opts.DryRun && (r.NotBackedUp.Count > 0 || r.BeyondRevokeWindow.Count > 0));
        if (incomplete)
            ConsoleUi.Color("\nPURGE INCOMPLETE — see the CRITICAL items above. Re-run tgPurge to retry.", ConsoleColor.Red);
        else if (!opts.DryRun)
            ConsoleUi.Color("\nDone: a final re-read of every purged chat found no in-scope message before the cut-off left in your " +
                "account. Deletion was requested for everyone; Telegram gives no way to confirm the other side's copy.", ConsoleColor.Green);
        return incomplete ? ExitIncomplete : ExitOk;
    }

}
