namespace TgPurge.Tests;

public class PurgePlannerTests
{
    static readonly DateTime Cutoff = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static readonly ServerMessage[] Msgs =
    [
        new(1, Cutoff.AddDays(-10), IsOwn: true),
        new(2, Cutoff.AddDays(-9), IsOwn: false),
        new(3, Cutoff.AddSeconds(-1), IsOwn: false),
        new(4, Cutoff, IsOwn: true),            // exactly at cut-off: kept
        new(5, Cutoff.AddDays(1), IsOwn: false), // after cut-off: kept
    ];

    static HashSet<int> AllBackedUp => [1, 2, 3, 4, 5];

    [Fact]
    public void Everyone_scope_deletes_own_and_counterpart_messages_before_cutoff()
    {
        var plan = PurgePlanner.Plan(DeleteScope.Everyone, Msgs, Cutoff, AllBackedUp, int.MaxValue, Now);
        Assert.Equal([1, 2, 3], plan.ToDelete);
        Assert.Empty(plan.OthersNotDeletable);
    }

    [Fact]
    public void OwnOnly_scope_deletes_only_own_messages()
    {
        var plan = PurgePlanner.Plan(DeleteScope.OwnOnly, Msgs, Cutoff, AllBackedUp, null, Now);
        Assert.Equal([1], plan.ToDelete);
        Assert.Equal([2, 3], plan.OthersNotDeletable);
    }

    [Fact]
    public void None_scope_deletes_nothing()
    {
        var plan = PurgePlanner.Plan(DeleteScope.None, Msgs, Cutoff, AllBackedUp, null, Now);
        Assert.Empty(plan.ToDelete);
    }

    [Fact]
    public void Messages_missing_from_verified_backup_are_never_deleted()
    {
        var plan = PurgePlanner.Plan(DeleteScope.Everyone, Msgs, Cutoff, new HashSet<int> { 1, 3 }, null, Now);
        Assert.Equal([1, 3], plan.ToDelete);
        Assert.Equal([2], plan.NotBackedUp);
    }

    [Fact]
    public void Messages_beyond_revoke_window_are_not_deleted_for_me_only()
    {
        // 30-day window: messages 1-3 are a year old, so revoking is impossible and they must be left alone.
        var plan = PurgePlanner.Plan(DeleteScope.Everyone, Msgs, Cutoff, AllBackedUp, 30 * 86400, Now);
        Assert.Empty(plan.ToDelete);
        Assert.Equal([1, 2, 3], plan.BeyondRevokeWindow);
    }

    [Fact]
    public void Unlimited_revoke_window_is_int_max()
    {
        var plan = PurgePlanner.Plan(DeleteScope.Everyone, Msgs, Cutoff, AllBackedUp, int.MaxValue, Now.AddYears(80));
        Assert.Equal([1, 2, 3], plan.ToDelete);
    }

    [Fact]
    public void Batches_are_at_most_100()
    {
        var ids = Enumerable.Range(1, 250).ToList();
        var batches = PurgePlanner.Batches(ids).ToList();
        Assert.Equal([100, 100, 50], batches.Select(b => b.Length));
        Assert.Equal(ids, batches.SelectMany(b => b));
    }
}

public class OptionsTests
{
    static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    static Options Parse(params string[] a) => Options.Parse(a, _ => null, Now);

    [Fact]
    public void Date_only_cutoff_is_local_midnight()
    {
        var o = Parse("--cutoff", "2025-06-01");
        Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), o.CutoffUtc);
    }

    [Fact]
    public void Cutoff_with_offset_and_now()
    {
        Assert.Equal(new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Utc), Parse("--cutoff", "2025-06-01T12:00:00+02:00").CutoffUtc);
        Assert.Equal(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc), Parse("--cutoff", "2025-06-01T12:00:00Z").CutoffUtc);
        Assert.Equal(Now, Parse("--cutoff", "now").CutoffUtc);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("01/06/2025")]
    [InlineData("2025-13-01")]
    public void Bad_cutoff_is_rejected(string input) =>
        Assert.Throws<ArgumentException>(() => Parse("--cutoff", input));

    [Fact]
    public void Exceptions_and_flags()
    {
        var o = Parse("--except", "@alice", "--except", "-1001234", "--dry-run", Options.ConfirmFlag);
        Assert.Equal(["@alice", "-1001234"], o.Exceptions);
        Assert.True(o.ExceptionsGiven && o.DryRun && o.AssumeYes);
    }

    [Fact]
    public void Credentials_come_from_environment()
    {
        var env = new Dictionary<string, string> { ["TGPURGE_API_ID"] = "123", ["TGPURGE_API_HASH"] = "abc" };
        var o = Options.Parse([], k => env.GetValueOrDefault(k), Now);
        Assert.Equal(123, o.ApiId);
        Assert.Equal("abc", o.ApiHash);
    }

    [Fact]
    public void Unknown_argument_is_rejected() => Assert.Throws<ArgumentException>(() => Parse("--force"));

    [Fact]
    public void Exception_file_skips_comments_and_blanks() =>
        Assert.Equal(["@bob", "Family"], Options.ReadExceptionFile(["# keep these", "", " @bob ", "Family"]));
}

public class ExceptionMatcherTests
{
    static readonly ChatInfo[] Chats =
    [
        new(111, ChatKind.Private, "Alice", "alice", DeleteScope.Everyone, ""),
        new(222, ChatKind.BasicGroup, "Family", null, DeleteScope.Everyone, ""),
        new(333, ChatKind.Supergroup, "Work", "workgroup", DeleteScope.OwnOnly, ""),
        new(444, ChatKind.Supergroup, "Dup", null, DeleteScope.OwnOnly, ""),
        new(555, ChatKind.Private, "Dup", null, DeleteScope.Everyone, ""),
    ];

    [Fact]
    public void Matches_by_id_botapi_id_username_and_title()
    {
        var r = ExceptionMatcher.Resolve(["111", "-222", "-1000000000333", "@WorkGroup", "family"], Chats);
        Assert.True(r.IsValid);
        Assert.Equal(new HashSet<ChatKey> { new(PeerSpace.User, 111), new(PeerSpace.Chat, 222), new(PeerSpace.Channel, 333) },
            r.Excluded.ToHashSet());
    }

    [Fact]
    public void Same_number_in_different_id_spaces_is_ambiguous_until_qualified()
    {
        ChatInfo[] chats =
        [
            new(777, ChatKind.Private, "Bob", null, DeleteScope.Everyone, ""),
            new(777, ChatKind.BasicGroup, "Club", null, DeleteScope.Everyone, ""),
        ];
        Assert.False(ExceptionMatcher.Resolve(["777"], chats).IsValid);
        var r = ExceptionMatcher.Resolve(["user:777"], chats);
        Assert.True(r.IsValid);
        Assert.Equal([new ChatKey(PeerSpace.User, 777)], r.Excluded);
        Assert.NotEqual(chats[0].Key, chats[1].Key);
    }

    [Fact]
    public void Unmatched_exception_is_invalid()
    {
        var r = ExceptionMatcher.Resolve(["@nobody"], Chats);
        Assert.False(r.IsValid);
        Assert.Equal(["@nobody"], r.Unmatched);
    }

    [Fact]
    public void Ambiguous_title_is_invalid()
    {
        var r = ExceptionMatcher.Resolve(["Dup"], Chats);
        Assert.False(r.IsValid);
        Assert.Single(r.Ambiguous);
    }
}
