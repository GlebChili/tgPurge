using TgPurge.Telegram;

namespace TgPurge.Tests;

public class ReportTests
{
    static readonly DateTime Cutoff = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly ChatInfo Bob = new(5, ChatKind.Private, "Bob", null, DeleteScope.Everyone, "");

    static ChatVerification Partly(int mediaFailures) => new(Bob.Key, Bob.Title,
        ["Telegram reported 100 messages but only 90 could be read: 10 message(s) were not read; any of them that are in scope stay on Telegram"],
        [.. Enumerable.Range(1, mediaFailures).Select(i => $"message #{i}: media download failed (boom)")],
        new HashSet<int>(Enumerable.Range(mediaFailures + 1, 50)), 50 + mediaFailures);

    [Fact]
    public void A_partly_verified_chat_reports_what_was_actually_deleted()
    {
        var r = new ChatPurgeResult { Chat = Bob, Deleted = 12 };
        r.Errors.Add("purge failed half-way");
        var text = Report.Build("/b", Cutoff, dryRun: false, [r], [], [], [Partly(4)]);

        Assert.Contains("50 of 54 backed-up message(s) verified); 12 message(s) were deleted; 4 backed-up message(s) that did not verify", text);
        Assert.DoesNotContain("50 verified message(s) were deleted", text);
        Assert.Contains("purge failed half-way", text);
    }

    [Fact]
    public void Chat_level_problems_and_every_message_problem_reach_the_report()
    {
        var text = Report.Build("/b", Cutoff, dryRun: false, [new ChatPurgeResult { Chat = Bob }], [], [], [Partly(30)]);

        Assert.Contains("10 message(s) were not read", text);
        Assert.Contains("## Backup problems", text);
        Assert.Contains("message #1: media download failed", text);
        Assert.Contains("message #30: media download failed", text);
    }

    [Fact]
    public void A_short_check_read_after_many_delete_errors_is_still_reported()
    {
        var r = new ChatPurgeResult { Chat = Bob };
        for (int i = 1; i <= 10; i++) r.Errors.Add($"message #{i}: MESSAGE_DELETE_FORBIDDEN");
        r.Errors.Add("final re-read: Telegram reported 9 messages but only 3 could be read");
        var text = Report.Build("/b", Cutoff, dryRun: false, [r], [], [], []);

        Assert.Contains("final re-read: Telegram reported 9", text);
        Assert.Contains("5 more message deletion error(s)", text);
    }

    [Fact]
    public void A_dry_run_says_would_be_deleted()
    {
        var r = new ChatPurgeResult { Chat = Bob };
        r.Remaining.AddRange([10, 11]);
        var text = Report.Build("/b", Cutoff, dryRun: true, [r], [], [], [Partly(1)]);
        Assert.Contains("2 message(s) would be deleted", text);
    }
}
