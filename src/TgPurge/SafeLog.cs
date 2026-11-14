namespace TgPurge;

/// <summary>
/// Diagnostic log that can never throw. WTelegram calls the logger from inside its network code, so a write
/// failure (e.g. disk full) must not propagate there and break the Telegram connection in the middle of a purge.
/// After the first failure the log simply stops.
/// </summary>
public sealed class SafeLog : IDisposable
{
    readonly object _lock = new();
    StreamWriter? _writer;

    public SafeLog(string path)
    {
        try { _writer = new StreamWriter(path) { AutoFlush = true }; }
        catch { _writer = null; }
    }

    public bool Enabled
    {
        get { lock (_lock) return _writer is not null; }
    }

    public void Write(int level, string message)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine($"{DateTime.UtcNow:O} [{level}] {message}");
            }
            catch
            {
                Close();
            }
        }
    }

    void Close()
    {
        try { _writer?.Dispose(); } catch { /* the stream may be unflushable on a full disk */ }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_lock) Close();
    }
}
