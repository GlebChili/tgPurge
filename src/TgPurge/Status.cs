using System.Diagnostics;
using System.Globalization;

namespace TgPurge;

/// <summary>
/// Human-readable progress on stdout. Every status line is time-stamped with the clock time and the run's elapsed
/// time. Counters update in place on a terminal and are printed as separate lines (at most every few seconds) when
/// output goes to a file. A heartbeat prints what the tool is doing whenever nothing was printed for a while,
/// so a long wait never looks like a hang. All members are thread-safe.
/// </summary>
public static class Status
{
    static readonly object Gate = new();
    static readonly Stopwatch Run = Stopwatch.StartNew();
    static DateTime _lastOutputUtc = DateTime.UtcNow;
    static DateTime _lastRedirectedProgressUtc = DateTime.MinValue;
    static string _activity = "starting";
    static string? _detail;
    static string? _lastPrintedDetail;
    static bool _lineOpen;
    static int _lineLength;
    static DateTime? _waitUntilUtc;
    static string? _waitReason;
    static int _suspended;
    static Timer? _timer;

    /// <summary>Counters rewrite one line on a terminal; when redirected they are printed as separate lines.</summary>
    public static bool Interactive { get; set; } = !Console.IsOutputRedirected;
    /// <summary>Silence after which a "still working" line is printed.</summary>
    public static TimeSpan HeartbeatAfter { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Heartbeat interval while waiting out a known delay (rate limit), where nothing else can happen.</summary>
    public static TimeSpan WaitingHeartbeatAfter { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>How often counters are printed when output is redirected.</summary>
    public static TimeSpan RedirectedProgressEvery { get; set; } = TimeSpan.FromSeconds(10);
    public static TextWriter Out { get; set; } = Console.Out;
    public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    /// <summary>How waits are performed (replaceable in tests).</summary>
    public static Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Clears all progress state (used between tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _lastOutputUtc = UtcNow();
            _lastRedirectedProgressUtc = DateTime.MinValue;
            _activity = "starting";
            _detail = _lastPrintedDetail = _waitReason = null;
            _waitUntilUtc = null;
            _lineOpen = false;
            _lineLength = 0;
            _suspended = 0;
        }
    }

    public static void StartHeartbeat()
    {
        lock (Gate) _timer ??= new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static void StopHeartbeat()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
            CloseLine();
        }
    }

    /// <summary>What the tool is doing right now, shown by the heartbeat. Clears the previous progress detail.</summary>
    public static void SetActivity(string activity)
    {
        lock (Gate)
        {
            _activity = activity;
            _detail = null;
        }
    }

    public static void Info(string line) => Write(line, null);
    public static void Warn(string line) => Write(line, ConsoleColor.Yellow);
    public static void Error(string line) => Write(line, ConsoleColor.Red);
    public static void Success(string line) => Write(line, ConsoleColor.Green);

    static void Write(string line, ConsoleColor? color)
    {
        lock (Gate)
        {
            CloseLine();
            var old = Console.ForegroundColor;
            if (color is { } c && Interactive) Console.ForegroundColor = c;
            Out.WriteLine($"{Stamp()} {line}");
            if (color is not null && Interactive) Console.ForegroundColor = old;
            Out.Flush();
            Touch();
        }
    }

    /// <summary>Updates the live counter for the current activity (e.g. "media 12/80 — 3.4 / 9.1 MB").</summary>
    public static void Progress(string detail)
    {
        lock (Gate)
        {
            _detail = detail;
            if (_suspended > 0) return;
            if (Interactive)
            {
                var text = Fit($"{Stamp()} {detail}");
                Out.Write("\r" + text + new string(' ', Math.Max(0, _lineLength - text.Length)));
                Out.Flush();
                _lineLength = text.Length;
                _lineOpen = true;
                Touch();
            }
            else if (UtcNow() - _lastRedirectedProgressUtc >= RedirectedProgressEvery)
            {
                PrintRedirectedDetail();
            }
        }
    }

    /// <summary>Ends a run of <see cref="Progress"/> updates, leaving the final counter visible.</summary>
    public static void EndProgress()
    {
        lock (Gate)
        {
            if (!Interactive && _detail is not null && _detail != _lastPrintedDetail) PrintRedirectedDetail();
            CloseLine();
            _detail = null;
        }
    }

    /// <summary>Waits a known delay (e.g. Telegram's rate limit), announcing it and counting it down in the heartbeat.</summary>
    public static async Task WaitAsync(TimeSpan delay, string reason)
    {
        var until = UtcNow() + delay;
        lock (Gate)
        {
            _waitUntilUtc = until;
            _waitReason = reason;
        }
        Warn($"{reason}: waiting {Human(delay)} (until {until.ToLocalTime():HH:mm:ss}). The tool is not frozen.");
        try
        {
            await Delay(delay);
        }
        finally
        {
            lock (Gate)
            {
                _waitUntilUtc = null;
                _waitReason = null;
            }
            Info("Resuming.");
        }
    }

    /// <summary>Silences the heartbeat and counters while the user is typing an answer.</summary>
    public static IDisposable Suspend()
    {
        lock (Gate)
        {
            CloseLine();
            _suspended++;
        }
        return new Resume();
    }

    sealed class Resume : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (Gate)
            {
                _suspended--;
                Touch();
            }
        }
    }

    /// <summary>Called every second; prints a "still working" line after a period of silence.</summary>
    public static void Heartbeat()
    {
        lock (Gate)
        {
            if (_suspended > 0) return;
            var now = UtcNow();
            var quiet = now - _lastOutputUtc;
            string message;
            if (_waitUntilUtc is { } until)
            {
                if (quiet < WaitingHeartbeatAfter) return;
                var left = TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((until - now).TotalSeconds)));
                message = $"… still waiting ({_waitReason}): {Human(left)} left, then: {_activity}";
            }
            else
            {
                if (quiet < HeartbeatAfter) return;
                message = $"… still working: {_activity}{(_detail is null ? "" : " — " + _detail.Trim())} (no change for {Human(quiet)})";
            }
            CloseLine();
            Out.WriteLine($"{Stamp()} {message}");
            Out.Flush();
            Touch();
        }
    }

    static void PrintRedirectedDetail()
    {
        Out.WriteLine($"{Stamp()} {_detail}");
        Out.Flush();
        _lastPrintedDetail = _detail;
        _lastRedirectedProgressUtc = UtcNow();
        Touch();
    }

    static void CloseLine()
    {
        if (!_lineOpen) return;
        Out.WriteLine();
        _lineOpen = false;
        _lineLength = 0;
    }

    static void Touch() => _lastOutputUtc = UtcNow();

    static string Fit(string s)
    {
        try
        {
            int width = Console.WindowWidth;
            if (width > 10 && s.Length >= width) return s[..(width - 2)] + "…";
        }
        catch { /* no console size available */ }
        return s;
    }

    public static string Stamp() =>
        $"[{UtcNow().ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} +{Human(Run.Elapsed)}]";

    public static string Human(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m{t.Seconds:00}s";
        return $"{(int)t.TotalHours}h{t.Minutes:00}m";
    }

    /// <summary>Forwards connection events from WTelegram's debug log to the screen (everything else stays in the log file).</summary>
    public static void OnLibraryLog(int level, string message)
    {
        if (message.Contains("Connecting to", StringComparison.Ordinal))
            Info("Connecting to Telegram" + DcSuffix(message) + "…");
        // "Alt DC disconnected" is WTelegram closing an idle media connection: normal, not shown.
        else if (message.Contains("Connection shut down", StringComparison.Ordinal))
            Warn("Connection to Telegram lost; reconnecting automatically…");
    }

    static string DcSuffix(string message)
    {
        var i = message.IndexOf("DC ", StringComparison.Ordinal);
        if (i < 0) return "";
        var digits = new string(message.Skip(i + 3).TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? $" (data center {digits})" : "";
    }
}
