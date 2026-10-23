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
}
