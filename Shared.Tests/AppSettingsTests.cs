using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class AppSettingsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("http://192.168.0.5:8080")]
    [InlineData("https://themes.example.de")]
    [InlineData("https://themes.example.de/store/v2?x=1#top")]
    [InlineData("HTTP://Example.COM")]
    [InlineData("http://10.0.0.22:1")]
    [InlineData("http://store.local:65535/")]
    public void Usable_addresses_have_no_problem(string url) => Assert.Null(AppSettings.StoreUrlProblem(url));

    [Theory]
    [InlineData("example.com", "http://")]
    [InlineData("ftp://example.com", "http://")]
    [InlineData("http:/example.com", "http://")]
    [InlineData("http://", "server name")]
    [InlineData("https://", "server name")]
    [InlineData("http:///path", "server name")]
    [InlineData("http://:8080", "server name")]
    [InlineData("http://host:", "port")]
    [InlineData("http://host:0", "port")]
    [InlineData("http://host:65536", "port")]
    [InlineData("http://host:80a", "port")]
    [InlineData("http://host:+80", "port")]
    [InlineData("http://host name", "spaces")]
    [InlineData("http://host/ path", "spaces")]
    [InlineData("http://host\t", "spaces")]
    [InlineData(" http://host", "spaces")]
    public void Unusable_addresses_say_why(string url, string mentions)
    {
        string? problem = AppSettings.StoreUrlProblem(url);
        Assert.NotNull(problem);
        Assert.Contains(mentions, problem);
    }

    [Fact]
    public void Length_is_limited_to_200_characters()
    {
        string ok = "http://" + new string('a', AppSettings.MaxStoreUrlLength - 7);
        Assert.Equal(200, ok.Length);
        Assert.Null(AppSettings.StoreUrlProblem(ok));
        Assert.Contains("too long", AppSettings.StoreUrlProblem(ok + "a"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("http://host", "http://host")]
    [InlineData("  https://host:8080/  ", "https://host:8080")]
    [InlineData("https://host//", "https://host")]
    [InlineData("http://host/api/", "http://host/api")]
    [InlineData("http://", "http://")]
    [InlineData("https:///", "https://")]
    public void Normalizing_trims_and_drops_trailing_slashes(string url, string expected) =>
        Assert.Equal(expected, AppSettings.NormalizeStoreUrl(url));

    [Fact]
    public void A_normalized_bare_scheme_is_refused_for_its_missing_host_not_its_scheme()
    {
        string? problem = AppSettings.StoreUrlProblem(AppSettings.NormalizeStoreUrl("http://"));
        Assert.NotNull(problem);
        Assert.Contains("server name", problem);
    }

    [Fact]
    public void Normalizing_makes_the_address_the_community_list_builds_on()
    {
        string url = AppSettings.NormalizeStoreUrl("http://10.0.0.22:8080/");
        Assert.Null(AppSettings.StoreUrlProblem(url));
        Assert.StartsWith("http://10.0.0.22:8080/api/themes?", CommunityList.ListUrl(url, CommunitySort.Top));
    }

    [Fact]
    public void Earlier_themes_are_kept_unless_the_file_says_replace()
    {
        Assert.False(AppSettings.Parse(null, () => "abcdef0123456789").ReplaceEarlierThemes);
        var s = AppSettings.Parse("""{"device_id":"abcdef0123456789","earlier_themes":"replace"}""", () => "unused");
        Assert.True(s.ReplaceEarlierThemes);
        Assert.True(AppSettings.Parse(s.Serialize(), () => "unused").ReplaceEarlierThemes);
        Assert.False(AppSettings.Parse((s with { ReplaceEarlierThemes = false }).Serialize(), () => "unused").ReplaceEarlierThemes);
    }

    [Fact]
    public void A_home_background_key_from_an_older_build_is_dropped()
    {
        // Builds with the BgRotator tile rotation wrote "home_background"; it is ignored and not written back.
        var s = AppSettings.Parse("""{"device_id":"abcdef0123456789","home_background":"rotate"}""", () => "unused");
        Assert.Equal("abcdef0123456789", s.DeviceId);
        Assert.DoesNotContain("home_background", s.Serialize());
    }
}
