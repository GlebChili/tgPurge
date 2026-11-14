using System.Globalization;

namespace TgPurge;

public enum LoginMethod { Qr, Code }

public sealed class Options
{
    public LoginMethod? Login { get; set; }
    public DateTime? CutoffUtc { get; set; }
    public string? CutoffInput { get; set; }
    public List<string> Exceptions { get; } = [];
    public bool ExceptionsGiven { get; set; }
    public int? ApiId { get; set; }
    public string? ApiHash { get; set; }
    public string? Phone { get; set; }
    public bool AssumeYes { get; set; }
    public bool DryRun { get; set; }
    public bool KeepSession { get; set; }
    public string? SessionPath { get; set; }
    public bool ShowHelp { get; set; }

    public const string ConfirmFlag = "--yes-i-understand-this-is-irreversible";

    public static string Usage => $"""
        tgPurge — back up every Telegram chat, verify the backup, then erase all messages older than a cut-off date
                  from Telegram for ALL participants.

        Usage:
          tgPurge --cutoff <date|now> [--except <chat>]... [--except-file <path>] [options]

        Cut-off:
          --cutoff <date|now>     Messages sent strictly before this moment are deleted.
                                  Accepts yyyy-MM-dd (local midnight), yyyy-MM-ddTHH:mm[:ss] (local time),
                                  an ISO-8601 time with offset/Z, or "now".
                                  Prompted for if omitted.

        Exceptions (chats left completely untouched: neither backed up nor purged):
          --except <chat>         Numeric chat id (as listed by the tool, or Bot-API style -100… id),
                                  @username, or exact chat title. Repeatable.
          --except-file <path>    File with one exception per line (# starts a comment).
                                  If no exceptions are passed, you are asked interactively after the chat list is shown.

        Telegram credentials (from https://my.telegram.org → API development tools):
          --api-id <id>           or env TGPURGE_API_ID
          --api-hash <hash>       or env TGPURGE_API_HASH
          --phone <+number>       or env TGPURGE_PHONE (otherwise prompted)

        Login:
          --login qr              Show a QR code to scan in the Telegram app (Settings → Devices → Link Desktop Device).
                                  No login code is sent, so this works when the code never arrives.
          --login code            Enter your phone number and the login code Telegram sends you.
                                  If --login is omitted you are asked (QR is the default; code if --phone is given
                                  or input is not a terminal).

        Other options:
          --dry-run               Back up and verify, show exactly what would be deleted, delete nothing.
          {ConfirmFlag}
                                  Skip the interactive confirmations (the disclaimer is still printed).
                                  Intended for emergency scripted use.
          --session <path>        Keep the Telegram session in this file (default: temporary file, deleted at exit).
          --keep-session          Do not log the session out of Telegram at exit.
          -h, --help              Show this help.
        """;

    /// <summary>Parses arguments. Throws <see cref="ArgumentException"/> with a user-facing message on bad input.</summary>
    public static Options Parse(string[] args, Func<string, string?>? env = null, DateTime? nowUtc = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Option {a} requires a value.");
                return args[++i];
            }
            switch (a)
            {
                case "-h" or "--help": o.ShowHelp = true; break;
                case "--cutoff": o.CutoffInput = Next(); break;
                case "--except":
                    o.Exceptions.Add(Next());
                    o.ExceptionsGiven = true;
                    break;
                case "--except-file":
                    var path = Next();
                    if (!File.Exists(path)) throw new ArgumentException($"Exception file not found: {path}");
                    o.Exceptions.AddRange(ReadExceptionFile(File.ReadAllLines(path)));
                    o.ExceptionsGiven = true;
                    break;
                case "--api-id":
                    o.ApiId = int.TryParse(Next(), out var id) ? id : throw new ArgumentException("--api-id must be a number.");
                    break;
                case "--api-hash": o.ApiHash = Next(); break;
                case "--phone": o.Phone = Next(); break;
                case "--login":
                    o.Login = Next().ToLowerInvariant() switch
                    {
                        "qr" => LoginMethod.Qr,
                        "code" => LoginMethod.Code,
                        _ => throw new ArgumentException("--login must be \"qr\" or \"code\"."),
                    };
                    break;
                case "--dry-run": o.DryRun = true; break;
                case ConfirmFlag: o.AssumeYes = true; break;
                case "--session": o.SessionPath = Next(); break;
                case "--keep-session": o.KeepSession = true; break;
                default: throw new ArgumentException($"Unknown argument: {a}");
            }
        }

        if (o.ApiId is null && env("TGPURGE_API_ID") is { Length: > 0 } envId)
            o.ApiId = int.TryParse(envId, out var id) ? id : throw new ArgumentException("TGPURGE_API_ID must be a number.");
        o.ApiHash ??= NullIfEmpty(env("TGPURGE_API_HASH"));
        o.Phone ??= NullIfEmpty(env("TGPURGE_PHONE"));

        if (o.CutoffInput is not null)
            o.CutoffUtc = ParseCutoff(o.CutoffInput, nowUtc ?? DateTime.UtcNow);
        return o;
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static IEnumerable<string> ReadExceptionFile(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

    static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
    ];

    /// <summary>Parses a cut-off. Dates without an offset are interpreted in the local time zone.</summary>
    public static DateTime ParseCutoff(string input, DateTime nowUtc)
    {
        input = input.Trim();
        if (input.Equals("now", StringComparison.OrdinalIgnoreCase))
            return DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        if (DateTime.TryParseExact(input, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return local.ToUniversalTime();

        if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            && (input.EndsWith('Z') || input.EndsWith("z") || HasOffset(input)))
            return dto.UtcDateTime;

        throw new ArgumentException(
            $"Cannot parse cut-off \"{input}\". Use yyyy-MM-dd, yyyy-MM-ddTHH:mm[:ss], an ISO-8601 time with offset, or \"now\".");
    }

    static bool HasOffset(string s)
    {
        int t = s.IndexOf('T');
        if (t < 0) t = s.IndexOf(' ');
        if (t < 0) return false;
        var timePart = s[t..];
        return timePart.Contains('+') || timePart.LastIndexOf('-') > 0;
    }
}
