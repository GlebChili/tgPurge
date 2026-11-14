using System.Globalization;
using System.Text;

namespace TgPurge;

public static class ConsoleUi
{
    public const string ConfirmPhrase = "PURGE";

    public static string Prompt(string label)
    {
        using var quiet = Status.Suspend(); // no heartbeat lines while the user is typing
        Console.Write(label);
        return (Console.ReadLine() ?? throw new OperationCanceledException("Input closed.")).Trim();
    }

    public static string ReadSecret(string label)
    {
        using var quiet = Status.Suspend();
        Console.Write(label);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    /// <summary>Asks how to log in; QR is the default because it needs no login code to be delivered.</summary>
    public static LoginMethod AskLoginMethod()
    {
        while (true)
        {
            var a = Prompt("How do you want to log in?\n" +
                "  [Q] QR code: scan it with the Telegram app on your phone (no login code needed) — default\n" +
                "  [C] phone number and a login code sent by Telegram\n" +
                "Choice [Q/c]: ").ToLowerInvariant();
            if (a is "" or "q" or "qr") return LoginMethod.Qr;
            if (a is "c" or "code") return LoginMethod.Code;
        }
    }

    /// <summary>
    /// Renders <paramref name="text"/> as a QR code, two modules per character row (▀ ▄ █), including the quiet zone.
    /// Dark modules are drawn with the glyphs, so it must be printed dark-on-light (see <see cref="ShowQr"/>).
    /// </summary>
    public static IReadOnlyList<string> QrLines(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.L);
        var m = data.ModuleMatrix; // already includes the 4-module quiet zone
        int size = m.Count;
        var lines = new List<string>();
        for (int y = 0; y < size; y += 2)
        {
            var sb = new StringBuilder(size);
            for (int x = 0; x < size; x++)
            {
                bool top = m[y][x], bottom = y + 1 < size && m[y + 1][x];
                sb.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }
            lines.Add(sb.ToString());
        }
        return lines;
    }

    static int _qrShown;

    /// <summary>Prints a login QR code black-on-white, so it scans the same on dark and light terminals.</summary>
    public static void ShowQr(string loginUrl)
    {
        Status.EndProgress();
        Console.WriteLine();
        if (Interlocked.Increment(ref _qrShown) > 1)
            Console.WriteLine("The previous QR code expired. Scan this NEW one:");
        Console.WriteLine("On your phone: Telegram → Settings → Devices → Link Desktop Device, then point the camera at this code.");
        Console.WriteLine("(It is replaced by a new code every 30 seconds or so until you scan it. Ctrl+C cancels.)");
        Console.WriteLine();
        foreach (var line in QrLines(loginUrl))
        {
            Console.Write("  ");
            Console.BackgroundColor = ConsoleColor.White;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Write(line);
            Console.ResetColor();
            Console.WriteLine();
        }
        Console.WriteLine();
    }

    public static void Banner(string text, ConsoleColor color = ConsoleColor.Cyan)
    {
        Status.EndProgress();
        Status.SetActivity(text);
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{Status.Stamp()} {text}");
        Console.WriteLine(new string('=', 78));
        Console.ForegroundColor = old;
    }

    /// <summary>A coloured, time-stamped status line.</summary>
    public static void Color(string text, ConsoleColor color)
    {
        foreach (var line in text.TrimStart('\n').Split('\n'))
            switch (color)
            {
                case ConsoleColor.Red: Status.Error(line); break;
                case ConsoleColor.Yellow: Status.Warn(line); break;
                case ConsoleColor.Green: Status.Success(line); break;
                default: Status.Info(line); break;
            }
    }

    public static string Local(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        + $" (local, {TimeZoneInfo.Local.StandardName}) = {utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC";

    public static void PrintChatTable(IReadOnlyList<ChatInfo> chats, IReadOnlySet<ChatKey> excluded)
    {
        Status.EndProgress();
        Console.WriteLine($"{"#",4}  {"type",-10} {"id",-26} {"action",-26} title");
        for (int i = 0; i < chats.Count; i++)
        {
            var c = chats[i];
            string action = excluded.Contains(c.Key) ? "EXCEPTION – keep" : c.Scope switch
            {
                DeleteScope.Everyone => "delete ALL, both sides",
                DeleteScope.OwnOnly => "delete YOUR msgs, all sides",
                _ => "nothing to delete",
            };
            var user = c.Username is null ? "" : $"  @{c.Username}";
            Console.WriteLine($"{i + 1,4}  {c.KindLabel,-10} {c.Key,-26} {action,-26} {c.Title}{user}");
        }
    }

    /// <summary>The full warning shown before anything is touched.</summary>
    public static string Disclaimer(string backupRoot, DateTime cutoffUtc, DateTime nowUtc, IReadOnlyList<ChatInfo> chats,
        IReadOnlySet<ChatKey> excluded, bool dryRun)
    {
        var purge = chats.Where(c => !excluded.Contains(c.Key) && c.Scope != DeleteScope.None).ToList();
        int priv = purge.Count(c => c.Kind == ChatKind.Private);
        int all = purge.Count(c => c.Kind != ChatKind.Private && c.Scope == DeleteScope.Everyone);
        int own = purge.Count(c => c.Scope == DeleteScope.OwnOnly);
        var sb = new StringBuilder();
        sb.AppendLine($"""
            READ THIS CAREFULLY. {(dryRun ? "(DRY RUN: the backup is made, but NOTHING will be deleted.)" : "THIS OPERATION CANNOT BE UNDONE.")}

            1. BACKUP LOCATION
               Exactly the messages that will be deleted are first downloaded (text as Markdown, plus their
               photos, videos, voice messages, documents and other files) into:

                   {backupRoot}

               The backup is NOT encrypted. On Linux/macOS the folder is readable only by your user account, but anyone
               who gets this folder (or your logged-in computer) can read all of your conversations.
               Store it somewhere safe (encrypted disk / encrypted archive) or destroy it if that is safer for you.

               Nothing that will NOT be deleted is copied: other people's messages in groups where you may only
               delete your own, messages sent after the cut-off, and your exceptions are not part of the backup.
               Note: the report saved in that folder lists, by name, the chats that were NOT purged (your exceptions,
               channels you only follow, chats that could not be accessed), so you can check what was covered.

            2. WHAT WILL BE DELETED
               Every message sent before the cut-off:
                   {Local(cutoffUtc)}
               {(cutoffUtc > nowUtc ? "   (the cut-off is in the future, so EVERY existing message is affected)\n" : "")}
               • {priv} private chat(s): ALL messages are deleted — yours AND the other person's — and they are
                 deleted FOR BOTH SIDES. The other person will permanently lose their copy of the conversation,
                 including the messages they wrote themselves. Nothing is removed "only for you".
               • {all} group(s)/channel(s) where you are owner or have the right to delete messages: ALL messages,
                 from ALL members, are deleted for EVERYONE in the group. Other members lose their own messages too.
               • {own} group(s) where you may not delete other people's messages: ONLY YOUR OWN messages are
                 deleted, for everyone. Other members' messages stay; Telegram does not allow removing them.

               Groups that were upgraded to supergroups are covered including their history from before the upgrade.

               Deletion happens only after the backup has been re-read from disk and verified (message counts,
               Markdown completeness, file sizes and SHA-256 checksums). A chat whose backup does not fully verify is
               NOT purged at all — even a single media file that failed to download keeps that whole chat on Telegram.
               Such chats are listed as CRITICAL in the report; re-run the tool to retry them.

            3. THIS IS IRRECOVERABLE
               Telegram has no recycle bin. Deleted messages are removed from Telegram's servers, and Telegram apps
               remove them from devices when they next sync. Neither you, the other participants, nor Telegram support
               can restore them. Copies that already left Telegram can survive: exports, screenshots, forwards,
               notification history, and devices that stay offline. The local backup made in step 1 will be the
               only copy you have.
               In supergroups and channels, Telegram's "Recent actions" log shows messages deleted in the last
               48 hours to the group's admins, so they can still see them for up to two days.

            4. NOT DONE BY THIS TOOL (do these yourself in the Telegram app if they matter to you)
               • Other logged-in devices/sessions: end them in Settings → Devices, ideally BEFORE running this tool,
                 because anyone logged in can keep reading while the backup runs.
               • Stories, profile photos, bio and username.
               • Scheduled messages and drafts.
               • Groups and channels you have already left: your old messages there are not processed.
               • Contacts synced to Telegram.

            5. NOT POSSIBLE FOR ANY TOOL
               • Secret chats exist only on devices; delete them in the Telegram app on each device.
               • Messages you earlier deleted "only for me": they are gone from your account, so they can no longer be
                 deleted for the other person, who still has them.
               • Telegram cannot confirm that the other side deleted its copy; the tool always asks for deletion for
                 everyone and checks that the messages are gone from your account.
               • Bots: Telegram keeps no bot-side history, but a bot's owner may have stored what you sent it.
            """);
        if (excluded.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"   EXCEPTIONS: {excluded.Count} chat(s) are left completely untouched — neither backed up nor purged:");
            foreach (var c in chats.Where(c => excluded.Contains(c.Key)))
                sb.AppendLine($"     - {c.Title} ({c.KindLabel}, id {c.Key})");
        }
        return sb.ToString();
    }
}
