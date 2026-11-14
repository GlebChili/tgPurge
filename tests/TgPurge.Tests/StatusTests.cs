using TgPurge.Telegram;

namespace TgPurge.Tests;

/// <summary>Status is process-wide; tests touching it run serially in this collection.</summary>
[CollectionDefinition("Status", DisableParallelization = true)]
public class StatusCollection;

[Collection("Status")]
public sealed class StatusTests : IDisposable
{
    readonly StringWriter _out = new();
    DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    readonly TextWriter _savedOut = Status.Out;
    readonly bool _savedInteractive = Status.Interactive;
    readonly Func<DateTime> _savedNow = Status.UtcNow;
    readonly Func<TimeSpan, Task> _savedDelay = Status.Delay;

    public StatusTests()
    {
        Status.Out = _out;
        Status.UtcNow = () => _now;
        Status.Interactive = false;
        Status.Delay = _ => Task.CompletedTask;
        Status.Reset();
        Status.Info("start"); // resets the silence clock
        Status.EndProgress();
        Clear();
    }

    void Clear() => _out.GetStringBuilder().Clear();
    string[] Lines => _out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    [Fact]
    public void Heartbeat_reports_current_activity_after_silence_only()
    {
        Status.SetActivity("downloading media of \"Family\"");
        Advance(10);
        Status.Heartbeat();
        Assert.Empty(Lines); // 10s of silence is fine

        Advance(6);
        Status.Heartbeat();
        var line = Assert.Single(Lines);
        Assert.Contains("still working: downloading media of \"Family\"", line);
        Assert.Matches(@"^\[\d\d:\d\d:\d\d \+\d+", line); // time-stamped

        Clear();
        Advance(5);
        Status.Heartbeat();
        Assert.Empty(Lines); // the heartbeat itself counts as output
    }

    [Fact]
    public void Heartbeat_includes_latest_counter()
    {
        Status.SetActivity("backing up chat 3/9");
        Status.Progress("downloading media 4/20 — 1.2 GB / 2 GB (60%)");
        Clear();
        Advance(20);
        Status.Heartbeat();
        Assert.Contains("downloading media 4/20 — 1.2 GB / 2 GB (60%)", Assert.Single(Lines));
    }

    [Fact]
    public void No_heartbeat_while_user_is_typing()
    {
        using (Status.Suspend())
        {
            Advance(120);
            Status.Heartbeat();
            Assert.Empty(Lines);
        }
        Status.Heartbeat(); // resuming resets the silence clock
        Assert.Empty(Lines);
    }

    [Fact]
    public async Task Rate_limit_wait_is_announced_and_counted_down()
    {
        var release = new TaskCompletionSource();
        Status.Delay = _ => release.Task;
        Status.SetActivity("reading history of \"Work\"");
        var wait = Status.WaitAsync(TimeSpan.FromMinutes(10), "Telegram rate limit (FLOOD_WAIT_600)");
        Assert.Contains("waiting 10m00s", Assert.Single(Lines));
        Assert.Contains("not frozen", Lines[0]);

        Clear();
        Advance(30);
        Status.Heartbeat();
        Assert.Empty(Lines); // while waiting, the heartbeat is every 60s

        Advance(35);
        Status.Heartbeat();
        var line = Assert.Single(Lines);
        Assert.Contains("still waiting (Telegram rate limit (FLOOD_WAIT_600)): 8m55s left", line);
        Assert.Contains("then: reading history of \"Work\"", line);

        release.SetResult();
        await wait;
        Assert.Contains("Resuming.", Lines[^1]);
    }

    [Fact]
    public void Redirected_output_prints_counters_as_lines_at_most_every_10s()
    {
        Status.Progress("reading history: 100 messages so far…");
        Status.Progress("reading history: 200 messages so far…");
        Advance(5);
        Status.Progress("reading history: 300 messages so far…");
        Assert.Single(Lines);
        Advance(6);
        Status.Progress("reading history: 400 messages so far…");
        Status.Progress("reading history: 412 messages so far…");
        Status.EndProgress(); // the final value is always printed
        Assert.Equal(3, Lines.Length);
        Assert.Contains("400", Lines[1]);
        Assert.Contains("412", Lines[2]);
        Assert.DoesNotContain("\r", _out.ToString());
    }

    [Fact]
    public void Interactive_counters_update_in_place_and_lines_never_collide()
    {
        Status.Interactive = true;
        Status.Progress("deleting for everyone: 100/500");
        Status.Progress("deleting for everyone: 200/500");
        Status.Info("checking that the 500 messages are gone…");
        var text = _out.ToString();
        Assert.Equal(2, text.Count(c => c == '\r'));
        Assert.Contains("200/500\n", text.Replace("\r\n", "\n").Replace(" \n", "\n").TrimEnd(' '), StringComparison.Ordinal);
        Assert.EndsWith("checking that the 500 messages are gone…" + Environment.NewLine, text);
    }

    [Theory]
    [InlineData("Connecting to DC 4...", "Connecting to Telegram (data center 4)…")]
    [InlineData("Connecting to 149.154.167.50:443...", "Connecting to Telegram…")]
    [InlineData("Could not read payload length : Connection shut down", "Connection to Telegram lost; reconnecting automatically…")]
    public void Connection_events_are_shown(string log, string shown)
    {
        Status.OnLibraryLog(2, log);
        Assert.EndsWith(shown, Assert.Single(Lines).TrimEnd());
    }

    [Theory]
    [InlineData("2>Alt DC disconnected: idle")]
    [InlineData("Received 1234 bytes")]
    public void Routine_library_noise_is_not_shown(string log)
    {
        Status.OnLibraryLog(1, log);
        Assert.Empty(Lines);
    }

    [Fact]
    public async Task Rate_limits_do_not_use_up_retry_attempts()
    {
        int calls = 0;
        var result = await Retry.RunAsync(() =>
        {
            calls++;
            if (calls <= 3) throw new TL.RpcException(420, "FLOOD_WAIT_X", 30);
            return Task.FromResult("ok");
        }, attempts: 1);
        Assert.Equal("ok", result);
        Assert.Equal(4, calls);
        Assert.Equal(3, Lines.Count(l => l.Contains("rate limit") && l.Contains("waiting 31s")));
    }

    [Fact]
    public async Task Network_retries_are_shown_on_screen()
    {
        int calls = 0;
        Status.Delay = _ => Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => Retry.RunAsync<int>(() => { calls++; throw new IOException("connection reset"); }, attempts: 2));
        Assert.Equal(2, calls);
        Assert.Contains(Lines, l => l.Contains("connection reset") && l.Contains("retry 1/1"));
    }

    public void Dispose()
    {
        Status.Out = _savedOut;
        Status.Interactive = _savedInteractive;
        Status.UtcNow = _savedNow;
        Status.Delay = _savedDelay;
    }
}
