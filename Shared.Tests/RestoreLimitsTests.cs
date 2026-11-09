using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class RestoreLimitsTests
{
    private const string Hub = "vsh_asset/bg_hub_default.dds.zst", TileBg = "vsh_asset/bg_NPXS40037.dds.zst",
        PlainTileBg = "vsh_asset/bg_NPXS40047.dds", Rco = "vsh_asset/Sce.PlayStation.PUI_UI3.rco", Music = "vsh_asset/bgm_home.at9",
        TileIcon = "rnps/apps/NPXS40047/icon0.png", AppPic = "appmeta/CUSA01127/pic0.dds", Texture = "vsh_asset/some_glow.dds.zst";

    [Theory]
    [InlineData(Hub, NoOriginalKind.Backgrounds)]
    [InlineData(TileBg, NoOriginalKind.Backgrounds)]
    [InlineData(PlainTileBg, NoOriginalKind.Backgrounds)]
    [InlineData(Rco, NoOriginalKind.IconsAndSounds)]
    [InlineData(Music, NoOriginalKind.Music)]
    [InlineData(TileIcon, NoOriginalKind.TileIcons)]
    [InlineData(AppPic, NoOriginalKind.AppBackgrounds)]
    [InlineData("userapp/PPSA01234/sce_sys/pic0.dds", NoOriginalKind.AppBackgrounds)]
    [InlineData(Texture, NoOriginalKind.Other)]
    public void Each_file_has_its_kind(string logical, NoOriginalKind kind) => Assert.Equal(kind, RestoreLimits.Of(logical));

    [Fact]
    public void Only_the_icons_and_sounds_come_back_without_a_backup()
    {
        Assert.Equal(NoOriginalKind.IconsAndSounds, RestoreLimits.ComesBack);
        NoOriginalKind all = RestoreLimits.Of([Hub, Rco, Music, TileIcon, AppPic, Texture]);
        Assert.Equal(NoOriginalKind.Backgrounds | NoOriginalKind.Music | NoOriginalKind.TileIcons | NoOriginalKind.AppBackgrounds | NoOriginalKind.Other,
            RestoreLimits.Lost(all));
        Assert.Equal(NoOriginalKind.None, RestoreLimits.Lost(NoOriginalKind.IconsAndSounds));
    }

    [Fact]
    public void Thirteen_tile_backgrounds_are_one_kind_and_no_path_is_named()
    {
        var tiles = Enumerable.Range(40030, 13).Select(n => $"vsh_asset/bg_NPXS{n}.dds.zst").Append(Hub).ToList();
        NoOriginalKind kinds = RestoreLimits.Of(tiles);

        Assert.Equal(NoOriginalKind.Backgrounds, kinds);
        foreach (string text in new[] { RestoreLimits.NoticeText(kinds), RestoreLimits.ResultLine(kinds), RestoreLimits.RowText(kinds),
                     RestoreLimits.DialogText(kinds), RestoreLimits.PartsDialogText(kinds) })
        {
            Assert.DoesNotContain("vsh_asset", text);
            Assert.DoesNotContain("NPXS", text);
        }
    }

    [Fact]
    public void The_notice_says_what_stays_and_what_still_comes_back()
    {
        string text = RestoreLimits.NoticeText(NoOriginalKind.Backgrounds | NoOriginalKind.IconsAndSounds | NoOriginalKind.Music);

        Assert.Equal("Another tool changed this console before Prospero Themes, so some originals are gone. " +
            "Restore defaults can’t bring back the old backgrounds and home music: they stay as that theme left them. " +
            "Icons and sounds can still come back with a theme that has the originals.", text);
    }

    [Fact]
    public void The_notice_for_icons_and_sounds_alone_names_no_backgrounds()
    {
        string text = RestoreLimits.NoticeText(NoOriginalKind.IconsAndSounds);

        Assert.DoesNotContain("background", text);
        Assert.Contains("a theme with the original icons and sounds can", text);
    }

    [Fact]
    public void Nothing_without_an_original_says_nothing_and_keeps_the_usual_texts()
    {
        Assert.Equal("", RestoreLimits.NoticeText(NoOriginalKind.None));
        Assert.Equal("", RestoreLimits.ResultLine(NoOriginalKind.None));
        Assert.Equal("Put every background, icon and sound back to the original", RestoreLimits.RowText(NoOriginalKind.None));
        Assert.Equal("Puts the original backgrounds, icons and sounds back on this console. Your theme library stays as it is.",
            RestoreLimits.DialogText(NoOriginalKind.None));
    }

    [Fact]
    public void The_restore_result_leads_with_the_kinds_that_stay()
    {
        Assert.Equal("Backgrounds stay as another tool’s theme left them. ",
            RestoreLimits.ResultLine(NoOriginalKind.Backgrounds));
        Assert.Equal("Home music stays as another tool’s theme left it. ",
            RestoreLimits.ResultLine(NoOriginalKind.Music));
        Assert.Equal("Backgrounds and home music stay as another tool’s theme left them. " +
            "Icons and sounds need a theme with the originals. ",
            RestoreLimits.ResultLine(NoOriginalKind.Backgrounds | NoOriginalKind.Music | NoOriginalKind.IconsAndSounds));
    }

    [Fact]
    public void Many_kinds_are_named_short_in_one_line_and_in_full_in_the_notice()
    {
        NoOriginalKind kinds = NoOriginalKind.Backgrounds | NoOriginalKind.TileIcons | NoOriginalKind.Music;

        Assert.Equal("backgrounds and other files", RestoreLimits.ShortNames(kinds));
        Assert.Equal("backgrounds and home music", RestoreLimits.ShortNames(NoOriginalKind.Backgrounds | NoOriginalKind.Music));
        Assert.StartsWith("Backgrounds and other files stay", RestoreLimits.ResultLine(kinds));
        Assert.Contains("the old backgrounds, tile icons and home music:", RestoreLimits.NoticeText(kinds));
    }

    [Fact]
    public void The_system_row_and_the_restore_question_say_it_on_such_a_console()
    {
        NoOriginalKind kinds = NoOriginalKind.Backgrounds | NoOriginalKind.IconsAndSounds;

        Assert.Contains("backgrounds", RestoreLimits.RowText(kinds));
        Assert.DoesNotContain("Put every background", RestoreLimits.RowText(kinds));
        Assert.Equal("Puts back what Prospero Themes changed; your theme library stays as it is. The old backgrounds can’t come back: " +
            "another tool’s theme left them. Icons and sounds need a theme with the originals.", RestoreLimits.DialogText(kinds));
        Assert.Contains("The old backgrounds can’t come back", RestoreLimits.PartsDialogText(kinds));
    }

    [Theory]
    [InlineData(NoOriginalKind.Backgrounds)]
    [InlineData(NoOriginalKind.Music)]
    [InlineData(NoOriginalKind.Backgrounds | NoOriginalKind.Music)]
    [InlineData(NoOriginalKind.IconsAndSounds)]
    [InlineData(NoOriginalKind.Backgrounds | NoOriginalKind.IconsAndSounds | NoOriginalKind.TileIcons | NoOriginalKind.AppBackgrounds | NoOriginalKind.Other)]
    public void Every_text_is_short(NoOriginalKind kinds)
    {
        // The notice is a dialog body (five lines of about 60 characters), the result leads a two-line toast, the row is one line.
        Assert.InRange(RestoreLimits.NoticeText(kinds).Length, 1, 300);
        Assert.InRange(RestoreLimits.ResultLine(kinds).Length, 1, 150);
        Assert.InRange(RestoreLimits.RowText(kinds).Length, 1, 80);
        // The Restore questions: five lines of the dialog, the parts one with its first sentences.
        Assert.InRange(RestoreLimits.DialogText(kinds).Length, 1, 260);
        Assert.InRange(RestoreLimits.PartsDialogText(kinds).Length, 1, 260);
    }

    [Fact]
    public void The_notice_shows_on_the_first_job_that_finds_such_files()
    {
        Assert.True(RestoreLimits.ShouldTell(NoOriginalKind.Backgrounds, NoOriginalKind.None));
        Assert.False(RestoreLimits.ShouldTell(NoOriginalKind.None, NoOriginalKind.None));
    }

    [Fact]
    public void The_notice_does_not_come_back_on_every_apply()
    {
        NoOriginalKind told = NoOriginalKind.Backgrounds | NoOriginalKind.IconsAndSounds;

        Assert.False(RestoreLimits.ShouldTell(NoOriginalKind.Backgrounds, told));
        Assert.False(RestoreLimits.ShouldTell(NoOriginalKind.IconsAndSounds, told));
        Assert.False(RestoreLimits.ShouldTell(told, told));
    }

    [Fact]
    public void A_kind_found_for_the_first_time_tells_once_more()
    {
        Assert.True(RestoreLimits.ShouldTell(NoOriginalKind.Backgrounds | NoOriginalKind.Music, NoOriginalKind.Backgrounds));
    }

    [Fact]
    public void An_apply_adds_what_it_found_and_clears_what_it_wrote_with_an_original()
    {
        // The record knew the hub background and the icon container; a theme with the original icons and sounds put the
        // container back and wrote a tile background that has no original.
        List<string> after = RestoreLimits.AfterApply([Hub, Rco], written: [Rco, TileBg], noOriginal: [TileBg]);

        Assert.Equal(new[] { TileBg, Hub }, after);   // sorted ordinally: "N" before "h"
    }

    [Fact]
    public void An_apply_keeps_a_file_it_wrote_without_an_original()
    {
        Assert.Equal(new[] { Hub }, RestoreLimits.AfterApply([Hub], written: [Hub], noOriginal: [Hub]));
    }

    [Fact]
    public void A_full_restore_replaces_the_record()
    {
        Assert.Equal(new[] { TileBg }, RestoreLimits.AfterRestore([Hub, Rco, Music], ThemePart.All, [TileBg]));
        Assert.Empty(RestoreLimits.AfterRestore([Hub, Rco], ThemePart.All, []));
    }

    [Fact]
    public void A_restore_of_some_parts_only_replaces_those_parts()
    {
        // Icons were restored: the container (icons and sounds, one file) was looked at, so did the music (a sound).
        List<string> after = RestoreLimits.AfterRestore([Hub, Rco, Music], ThemePart.Icons, []);

        Assert.Equal(new[] { Hub }, after);
    }

    [Fact]
    public void The_record_reads_back_what_it_wrote()
    {
        string text = RestoreLimits.Serialize([TileBg, Hub, Hub]);

        Assert.Equal(TileBg + "\n" + Hub + "\n", text);
        Assert.Equal(new[] { TileBg, Hub }, RestoreLimits.Parse(text));
        Assert.Empty(RestoreLimits.Parse(null));
        Assert.Equal(new[] { Hub }, RestoreLimits.Parse("\r\n" + Hub + "\r\n\n"));
    }

    [Fact]
    public void The_told_kinds_survive_settings_json()
    {
        var settings = new AppSettings(false, "", "abcdefgh1234") { NoOriginalTold = NoOriginalKind.Backgrounds | NoOriginalKind.Music };

        string json = settings.Serialize();
        AppSettings back = AppSettings.Parse(json, () => "unused-id-0000");

        Assert.Contains("\"no_original_told\": \"backgrounds,music\"", json);
        Assert.Equal(NoOriginalKind.Backgrounds | NoOriginalKind.Music, back.NoOriginalTold);
        Assert.Equal(NoOriginalKind.None, AppSettings.Parse("{}", () => "unused-id-0000").NoOriginalTold);
        Assert.DoesNotContain("no_original_told", new AppSettings(false, "", "abcdefgh1234").Serialize());
    }

    [Fact]
    public void Unknown_words_in_the_told_kinds_are_ignored()
    {
        Assert.Equal(NoOriginalKind.TileIcons, RestoreLimits.ParseKinds("tile_icons, wallpapers,"));
        Assert.Equal(NoOriginalKind.None, RestoreLimits.ParseKinds(null));
    }
}
