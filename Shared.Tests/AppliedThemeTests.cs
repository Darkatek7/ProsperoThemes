using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class AppliedThemeTests
{
    [Fact]
    public void An_apply_names_the_pack_that_is_on_the_console()
    {
        const string json = """
            {
              "pack": { "id": "night-drive", "name": "Night Drive", "version": "1.2" },
              "action": "apply",
              "when_utc": "2026-10-13T12:00:00Z",
              "firmware": "13.000.000",
              "targets": []
            }
            """;
        Assert.Equal("night-drive", AppliedTheme.Read(json));
    }

    [Fact]
    public void A_restore_means_nothing_is_applied()
    {
        const string restore = """{"pack":{"id":"night-drive","name":"N","version":"1"},"action":"restore","when_utc":"2026-10-13T12:00:00Z","targets":[]}""";
        Assert.Null(AppliedTheme.Read(restore));
        const string originals = """{"pack":null,"action":"restore-originals","when_utc":"2026-10-13T12:00:00Z","targets":[]}""";
        Assert.Null(AppliedTheme.Read(originals));
    }

    [Fact]
    public void The_real_restore_fixture_state_reads_as_nothing_applied()
    {
        // Written by the applier itself (a restore of the icon-layer pack).
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "icon-layer.p5theme.state.json"));
        Assert.Null(AppliedTheme.Read(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"apply\"")]
    [InlineData("{}")]
    [InlineData("""{"action":"apply"}""")]
    [InlineData("""{"action":"apply","pack":null}""")]
    [InlineData("""{"action":"apply","pack":{}}""")]
    [InlineData("""{"action":"apply","pack":{"id":""}}""")]
    [InlineData("""{"action":"apply","pack":{"id":7}}""")]
    [InlineData("""{"action":"apply","pack":"night-drive"}""")]
    [InlineData("""{"pack":{"id":"x"}}""")]
    public void A_missing_or_unusable_file_means_nothing_is_applied(string? json) => Assert.Null(AppliedTheme.Read(json));
}
