using System.Globalization;

namespace TgPurge;

/// <summary>Writes the readable history one message at a time, so a chat of any size streams straight to disk.</summary>
public static class MarkdownRenderer
{
    /// <summary>The anchor written for every message, alone on its line. The verifier requires it exactly once per message.</summary>
    public static string Anchor(int messageId) => $"<a id=\"msg-{messageId}\"></a>";

    /// <summary>Parses a line written by <see cref="Anchor"/>; anything else (including escaped message text) is not an anchor.</summary>
    public static bool TryParseAnchor(string line, out int messageId)
    {
        messageId = 0;
        const string prefix = "<a id=\"msg-", suffix = "\"></a>";
        line = line.TrimEnd('\r');
        return line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith(suffix, StringComparison.Ordinal)
            && int.TryParse(line.AsSpan(prefix.Length, line.Length - prefix.Length - suffix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out messageId);
    }

    static string Ts(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public static void WriteHeader(TextWriter w, ChatBackup chat, DateTime exportedUtc, int totalMessages, int part, int parts,
        string? previousPart)
    {
        w.WriteLine(parts > 1 ? $"# {EscapeInline(chat.Title)} — part {part} of {parts}" : $"# {EscapeInline(chat.Title)}");
        w.WriteLine();
        w.WriteLine($"- Type: {chat.Kind}");
        w.WriteLine($"- Telegram id: {chat.Id}");
        if (chat.Username is not null) w.WriteLine($"- Username: @{chat.Username}");
        w.WriteLine($"- Exported: {Ts(exportedUtc)}");
        w.WriteLine($"- Messages in this backup: {totalMessages}{(parts > 1 ? $" (in {parts} files, oldest first)" : "")}");
        if (chat.ContentNote is not null) w.WriteLine($"- Contents: {EscapeInline(chat.ContentNote)}");
        if (previousPart is not null) w.WriteLine($"- Previous part: [{previousPart}]({previousPart})");
        w.WriteLine();
        w.WriteLine("---");
        w.WriteLine();
    }

    public static void WriteFooter(TextWriter w, string? nextPart)
    {
        if (nextPart is not null) w.WriteLine($"Continued in [{nextPart}]({nextPart}).");
    }

    public static void WriteMessage(TextWriter w, MessageRecord m)
    {
        w.WriteLine(Anchor(m.Id));
        var header = new List<string>
        {
            $"**{EscapeInline(m.SenderName ?? (m.SenderId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"))}**",
            Ts(m.DateUtc),
            $"#{m.Id}",
        };
        if (m.ReplyToId is int r) header.Add($"reply to [#{r}](#msg-{r})");
        if (m.ForwardedFrom is not null) header.Add($"forwarded from {EscapeInline(m.ForwardedFrom)}");
        if (m.EditDateUtc is DateTime e) header.Add($"edited {Ts(e)}");
        w.WriteLine(string.Join(" · ", header));
        w.WriteLine();

        if (m.IsService)
            w.WriteLine($"_[service] {EscapeInline(m.Text)}_");
        else if (m.Text.Length > 0)
            foreach (var line in m.Text.Replace("\r\n", "\n").Split('\n'))
                w.WriteLine(line.Length == 0 ? ">" : "> " + EscapeHtml(line));

        if (m.MediaStatus != MediaStatus.None)
        {
            if (m.Text.Length > 0 || m.IsService) w.WriteLine();
            w.WriteLine(m.MediaStatus switch
            {
                MediaStatus.Downloaded => $"📎 [{EscapeInline(m.MediaDescription ?? "file")}]({EscapeLink(m.MediaFile!)})",
                MediaStatus.NotAFile => $"📎 {EscapeInline(m.MediaDescription ?? "")}",
                MediaStatus.Unavailable => $"⚠️ {EscapeInline(m.MediaDescription ?? "media")} — no longer available on Telegram ({EscapeInline(m.MediaError ?? "")})",
                MediaStatus.Failed => $"❌ {EscapeInline(m.MediaDescription ?? "media")} — DOWNLOAD FAILED: {EscapeInline(m.MediaError ?? "")}",
                _ => "",
            });
        }
        w.WriteLine();
        w.WriteLine("---");
        w.WriteLine();
    }

    static string EscapeInline(string s) =>
        EscapeHtml(s).Replace("\r", " ").Replace("\n", " ").Replace("*", "\\*").Replace("_", "\\_");

    /// <summary>
    /// Neutralises HTML in message content. Markdown renders entities as the original characters, but raw markup
    /// can no longer run in a viewer, and no message can contain another message's anchor (which the verifier checks).
    /// </summary>
    public static string EscapeHtml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static string EscapeLink(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
