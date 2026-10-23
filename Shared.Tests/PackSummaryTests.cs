using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class PackSummaryTests
{
    private static ThemePackManifest Fixture(string name) =>
        ThemeLibraryEntry.Inspect(name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name))).Manifest;

    private static ThemePackTarget Target(string path, string layer) => new(path, layer, "payloads/" + path, 1, new string('0', 64));

    private static ThemePackManifest Pack(params ThemePackTarget[] targets) =>
        new(2, "id", "Name", "Author", "1.0", "", "keep", targets);

    private static IEnumerable<ThemePackTarget> Many(int count, string pathFormat, string layer) =>
        Enumerable.Range(0, count).Select(i => Target(string.Format(pathFormat, i), layer));

    [Fact]
    public void A_store_style_pack_reads_like_the_example_line()
    {
        // The shape of the store's themes: a home background, an all-apps background, 16 svg icons, 9 tile icons.
        var targets = new List<ThemePackTarget> { Target("vsh_asset/bg_hub_default.dds.zst", "background"), Target("allapps/pic0.dds", "background") };
        targets.AddRange(Many(16, "vsh_asset/Sce.PlayStation.PUI_UI3.rco#{0}", "svg"));
        targets.AddRange(Many(9, "rnps/NPXS4004{0}/appdb/default/icon0.png", "icon"));

        PackSummary summary = PackSummary.Of(Pack(targets.ToArray()));

        Assert.Equal("Background · App background · 16 icons · 9 tile icons", summary.Text);
        Assert.Equal(new[] { ("Background", 1), ("App background", 1), ("Icons", 16), ("Tile icons", 9) }, summary.Groups);
        Assert.Equal(0, summary.Other);
        Assert.Equal(27, targets.Count);
    }

    [Fact]
    public void Every_group_is_counted_and_listed_in_display_order()
    {
        var targets = new List<ThemePackTarget>
        {
            Target("vsh_asset/sfx_warmboot.at9", "sound"), Target("vsh_asset/sfx_other.at9", "sound"), Target("vsh_asset/psbutton_press.mp4", "video"),
            Target("apps/PPSA01650/pic0.dds", "background"), Target("apps/CUSA00001/pic0.dds", "background"),
            Target("vsh_asset/bg_hub_default.dds.zst", "background"), Target("allapps/pic0.dds", "background"),
            Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco#a", "svg"), Target("rnps/NPXS40047/appdb/default/icon0.png", "icon"),
            Target("vsh_asset/function_button_circle.png", "glyph"),
        };

        PackSummary summary = PackSummary.Of(Pack(targets.ToArray()));

        Assert.True(summary.HomeBackground);
        Assert.True(summary.AllApps);
        Assert.Equal((2, 1, 1, 2, 1), (summary.PerApp, summary.Icons, summary.Tiles, summary.Sounds, summary.Video));
        Assert.Equal(1, summary.Other);
        Assert.Equal(
            new[] { ("Background", 1), ("App background", 1), ("App images", 2), ("Icon", 1), ("Tile icon", 1), ("Sounds", 2), ("Video", 1) },
            summary.Groups);
        Assert.Equal("Background · App background · 2 app images · 1 icon · 1 tile icon · 2 sounds · 1 video", summary.Text);
    }

    [Fact]
    public void Each_group_names_its_kind_and_the_missing_targets_count_in_the_same_groups()
    {
        var targets = new List<ThemePackTarget> { Target("vsh_asset/bg_hub_default.dds.zst", "background") };
        targets.AddRange(Many(16, "vsh_asset/Sce.PlayStation.PUI_UI3.rco#{0}", "svg"));
        targets.AddRange(Many(9, "rnps/NPXS4004{0}/appdb/default/icon0.png", "icon"));

        PackSummary summary = PackSummary.Of(Pack(targets.ToArray()));
        Assert.Equal(new[] { PackGroup.Background, PackGroup.Icons, PackGroup.Tiles }, summary.Kinds);
        Assert.Equal((1, 16, 9, 0), (summary.Count(PackGroup.Background), summary.Count(PackGroup.Icons), summary.Count(PackGroup.Tiles),
            summary.Count(PackGroup.Sounds)));

        // Three tile icons this console does not have.
        PackSummary missing = PackSummary.Of(targets.Skip(17).Take(3));
        Assert.Equal((0, 0, 3), (missing.Count(PackGroup.Background), missing.Count(PackGroup.Icons), missing.Count(PackGroup.Tiles)));
    }

    [Fact]
    public void Counting_follows_the_stores_rules_not_the_layer_names_of_the_console()
    {
        // pack-info.ts: icons are the svg layer only, tiles are anything under rnps/, per-app images need a real title id.
        PackSummary summary = PackSummary.Of(Pack(
            Target("vsh_asset/TrophyUAM_Gold.png", "icon"),                 // a png icon, not an svg one
            Target("apps/ppsa01650/pic0.dds", "background"),                // not a title id
            Target("apps/PPSA01650/pic1.dds", "background"),                // not pic0
            Target("rnps/NPXS40047/appdb/default/icon0.png", "icon")));
        Assert.Equal((false, false, 0, 0, 1), (summary.HomeBackground, summary.AllApps, summary.PerApp, summary.Icons, summary.Tiles));
        Assert.Equal(3, summary.Other);
        Assert.Equal("1 tile icon", summary.Text);
    }

    [Fact]
    public void An_empty_pack_has_no_groups_and_reads_as_a_theme()
    {
        PackSummary summary = PackSummary.Of(Pack());
        Assert.Empty(summary.Groups);
        Assert.Equal("Theme", summary.Text);
        Assert.Equal(0, summary.Other);
    }

    [Fact]
    public void The_background_fixtures_read_as_a_background()
    {
        PackSummary web = PackSummary.Of(Fixture("web-creator-export.p5theme"));
        Assert.Equal("Background", web.Text);
        Assert.Equal(new[] { ("Background", 1) }, web.Groups);
        Assert.Equal(0, web.Other);

        // The second target is another background file (bg_image_load_error): it is no group, so it counts as other.
        PackSummary two = PackSummary.Of(Fixture("two-screen.p5theme"));
        Assert.Equal("Background", two.Text);
        Assert.Equal(new[] { ("Background", 1) }, two.Groups);
        Assert.Equal(1, two.Other);
    }

    [Fact]
    public void The_sound_and_video_fixtures_name_their_layer()
    {
        PackSummary audio = PackSummary.Of(Fixture("audio-layer.p5theme"));
        Assert.Equal("1 sound", audio.Text);
        Assert.Equal(new[] { ("Sound", 1) }, audio.Groups);
        Assert.Equal(0, audio.Other);

        PackSummary video = PackSummary.Of(Fixture("video-layer.p5theme"));
        Assert.Equal("1 video", video.Text);
        Assert.Equal(new[] { ("Video", 1) }, video.Groups);
    }

    [Fact]
    public void The_icon_layer_fixture_has_png_files_the_store_does_not_group()
    {
        // Glyph, icon and ui targets under vsh_asset: none is an svg icon or a tile, so the store would say "Theme".
        // The local view still says how many files there are, and Detail lists them as "Other files".
        PackSummary summary = PackSummary.Of(Fixture("icon-layer.p5theme"));
        Assert.Empty(summary.Groups);
        Assert.Equal(4, summary.Other);
        Assert.Equal("4 files", summary.Text);
    }

    [Fact]
    public void The_store_card_and_the_library_line_are_written_by_the_same_formatter()
    {
        var targets = new List<ThemePackTarget> { Target("vsh_asset/bg_hub_default.dds.zst", "background"), Target("allapps/pic0.dds", "background") };
        targets.AddRange(Many(2, "apps/PPSA0165{0}/pic0.dds", "background"));
        targets.AddRange(Many(16, "vsh_asset/Sce.PlayStation.PUI_UI3.rco#{0}", "svg"));
        targets.AddRange(Many(9, "rnps/NPXS4004{0}/appdb/default/icon0.png", "icon"));
        PackSummary summary = PackSummary.Of(Pack(targets.ToArray()));

        var card = new CommunityTheme("a", "A", "", "1", "", 1, 0, 0, null, "u",
            Fallback: summary.HomeBackground, AllApps: summary.AllApps, PerApp: summary.PerApp, Icons: summary.Icons, Tiles: summary.Tiles);

        Assert.Equal("Background · App background · 2 app images · 16 icons · 9 tile icons", summary.Text);
        Assert.Equal(summary.Text, card.Contents);
        Assert.Equal(PackSummary.Format(true, true, 2, 16, 9), card.Contents);
    }

    [Fact]
    public void Singular_nouns_are_used_for_a_count_of_one_and_plurals_otherwise()
    {
        Assert.Equal("1 app image · 1 icon · 1 tile icon", PackSummary.Format(false, false, 1, 1, 1));
        Assert.Equal("2 app images · 5 icons · 3 tile icons", PackSummary.Format(false, false, 2, 5, 3));
        Assert.Equal("Theme", PackSummary.Format(false, false, 0, 0, 0));
        PackSummary one = PackSummary.FromCounts(false, false, 1, 1, 1, 1, 1);
        Assert.Equal(new[] { ("App image", 1), ("Icon", 1), ("Tile icon", 1), ("Sound", 1), ("Video", 1) }, one.Groups);
    }
}
