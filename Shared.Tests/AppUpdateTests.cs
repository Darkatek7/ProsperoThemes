using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class AppUpdateTests
{
    [Fact]
    public void The_store_answer_gives_the_version_and_its_page()
    {
        var release = AppUpdate.Parse("""{"version":"01.000.022","url":"https://github.com/o/r/releases/tag/01.000.022","publishedAt":"2026-10-30T10:00:00Z"}""");

        Assert.Equal("01.000.022", release!.Version);
        Assert.Equal("https://github.com/o/r/releases/tag/01.000.022", release.Url);
        Assert.Equal("https://themes.example.org/api/app/latest", AppUpdate.LatestUrl("https://themes.example.org/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"error":"the latest release is not known right now"}""")]
    [InlineData("""{"version":"v1.2.3"}""")]
    [InlineData("""{"version":"1.000.022"}""")]
    [InlineData("""{"version":"01.000.02x"}""")]
    [InlineData("<html>")]
    public void Anything_else_is_no_release(string? body) => Assert.Null(AppUpdate.Parse(body));

    [Theory]
    [InlineData("01.000.022", "01.000.021", true)]
    [InlineData("01.001.000", "01.000.099", true)]
    [InlineData("02.000.000", "01.999.999", true)]
    [InlineData("01.000.021", "01.000.021", false)]
    [InlineData("01.000.020", "01.000.021", false)]
    [InlineData("01.000.022", "", false)]
    [InlineData("01.000.022", "1.0", false)]
    public void Only_a_later_version_counts(string latest, string current, bool newer) =>
        Assert.Equal(newer, AppUpdate.IsNewer(latest, current));

    [Fact]
    public void The_notice_names_both_versions() =>
        Assert.StartsWith("Prospero Themes 01.000.022 is out (this is 01.000.021).", AppUpdate.Text("01.000.022", "01.000.021"));

    [Fact]
    public void The_notice_points_at_the_update_row_when_the_store_has_the_zip()
    {
        Assert.StartsWith("Prospero Themes 01.000.022 is out (this is 01.000.021).", AppUpdate.Text("01.000.022", "01.000.021", canUpdate: true));
        Assert.Contains("App update", AppUpdate.Text("01.000.022", "01.000.021", canUpdate: true));
        Assert.Contains("where you installed it from", AppUpdate.Text("01.000.022", "01.000.021", canUpdate: false));
    }

    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_store_answer_may_carry_the_release_zip()
    {
        var release = AppUpdate.Parse($$$"""{"version":"01.000.027","url":"https://x/y","zip":{"url":"/api/app/download/01.000.027","bytes":8576241,"sha256":"{{{Sha.ToUpperInvariant()}}}"}}""");

        Assert.Equal(new AppUpdate.ZipInfo("/api/app/download/01.000.027", 8576241, Sha), release!.Zip);
        Assert.Equal("https://themes.example.org/api/app/download/01.000.027", AppUpdate.ZipUrl("https://themes.example.org/", release.Zip!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(""","zip":null""")]
    [InlineData(""","zip":{}""")]
    [InlineData(""","zip":{"url":"/api/app/download/01.000.027","bytes":0,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/01.000.027","bytes":-5,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/01.000.027","bytes":99999999999,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/01.000.027","bytes":1.5,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/01.000.027","bytes":10,"sha256":"abc"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/01.000.027","bytes":10,"sha256":"zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""")]
    [InlineData(""","zip":{"url":"https://evil.example/PPSA90210.zip","bytes":10,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/other/path","bytes":10,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/../x","bytes":10,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download//x","bytes":10,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/","bytes":10,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/a?b=c","bytes":10,"sha256":"SHA"}""")]
    [InlineData(""","zip":{"url":"/api/app/download/a b","bytes":10,"sha256":"SHA"}""")]
    [InlineData(",\"zip\":\"nope\"")]
    public void A_zip_the_app_cannot_trust_leaves_only_the_notice(string zip)
    {
        var release = AppUpdate.Parse("{\"version\":\"01.000.027\",\"url\":\"u\"" + zip.Replace("SHA", Sha) + "}");

        Assert.NotNull(release);
        Assert.Equal("01.000.027", release!.Version);
        Assert.Null(release.Zip);
    }

    [Fact]
    public void The_question_and_the_row_say_what_will_happen()
    {
        string text = AppUpdate.DialogText("01.000.027", "01.000.026", 8_576_241);

        Assert.Contains("01.000.027", text);
        Assert.Contains("8.2 MB", text);
        Assert.Contains("close the app and start it again", text);
        Assert.Equal("340 KB", AppUpdate.FormatSize(348_160));
        Assert.Contains("01.000.027", AppUpdate.RowText(AppUpdate.State.Ready, "01.000.027", "01.000.026"));
        Assert.Contains("start it again", AppUpdate.RowText(AppUpdate.State.Installed, "01.000.027", "01.000.026"));
    }
}
