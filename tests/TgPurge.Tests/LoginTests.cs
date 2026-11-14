namespace TgPurge.Tests;

public class LoginTests
{
    static Options Parse(params string[] a) => Options.Parse(a, _ => null, DateTime.UtcNow);

    [Theory]
    [InlineData("qr", LoginMethod.Qr)]
    [InlineData("QR", LoginMethod.Qr)]
    [InlineData("code", LoginMethod.Code)]
    public void Login_option_is_parsed(string value, LoginMethod expected) =>
        Assert.Equal(expected, Parse("--login", value).Login);

    [Fact]
    public void Login_option_defaults_to_asking() => Assert.Null(Parse().Login);

    [Fact]
    public void Unknown_login_method_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Parse("--login", "sms"));

    const string SampleUrl = "tg://login?token=AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKw";

    [Fact]
    public void Qr_rendering_reproduces_every_module_of_the_code()
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(SampleUrl, QRCoder.QRCodeGenerator.ECCLevel.L);
        var m = data.ModuleMatrix;
        var lines = ConsoleUi.QrLines(SampleUrl);

        Assert.Equal((m.Count + 1) / 2, lines.Count);
        for (int y = 0; y < m.Count; y++)
            for (int x = 0; x < m.Count; x++)
            {
                char c = lines[y / 2][x];
                bool dark = y % 2 == 0 ? c is '█' or '▀' : c is '█' or '▄';
                Assert.True(dark == m[y][x], $"module ({x},{y}) rendered as '{c}'");
            }
    }

    [Fact]
    public void Qr_rendering_keeps_the_quiet_zone_and_a_fixed_width()
    {
        var lines = ConsoleUi.QrLines(SampleUrl);
        Assert.All(lines, l => Assert.Equal(lines[0].Length, l.Length));
        Assert.True(string.IsNullOrWhiteSpace(lines[0]), "top quiet zone");
        Assert.True(string.IsNullOrWhiteSpace(lines[^1]), "bottom quiet zone");
        Assert.All(lines, l => Assert.True(l[0] == ' ' && l[^1] == ' ', "side quiet zone"));
    }
}
