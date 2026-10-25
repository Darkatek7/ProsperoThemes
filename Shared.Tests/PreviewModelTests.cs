using System;
using System.IO;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using Xunit;
using static ProsperoThemes.Shared.Tests.PreviewKit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// What the console's preview shows for a pack, worked out from its manifest (<see cref="PreviewModel"/>), and the small
/// readers it uses to take the pack's pictures, sounds and music in: image sizes from headers, MP3 headers, the files.
/// </summary>
public class PreviewModelTests
{
    private static string[] Names(System.Collections.Generic.IReadOnlyList<PreviewTile> tiles) => tiles.Select(t => t.Name).ToArray();

    // ---- The tiles --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pack_with_nothing_still_has_the_systems_tiles_and_neutral_stand_ins()
    {
        PreviewModel model = PreviewModel.Of(Manifest());

        Assert.Equal(["PlayStation Store", "PlayStation Plus", "Game Library", "App Library", "Welcome", "Demo game", "Demo racing game", "Demo adventure"], Names(model.Games));
        Assert.Equal(["Discover", "Music Home", "All Apps", "Live TV on PS5", "Media Gallery", "Prospero Themes", "Demo video app", "Demo music app"], Names(model.Media));
        Assert.Contains(model.Media, t => t.Id == "NPXS40075" && t.Name == "Media Gallery" && t.Kind == PreviewTileKind.System);
        Assert.All(model.Games.Concat(model.Media).Where(t => t.Kind == PreviewTileKind.System), t => Assert.Null(t.IconFile));
        Assert.All(model.Games.Concat(model.Media).Where(t => t.Kind == PreviewTileKind.System), t => Assert.StartsWith("NPXS", t.Id));
        Assert.Equal(8, model.Tiles(PreviewTab.Games).Count);
        Assert.Equal(8, model.Tiles(PreviewTab.Media).Count);
        Assert.Null(model.MusicFile);
        Assert.Null(model.AllAppsFile);
        Assert.Empty(model.SoundFiles);
        Assert.Empty(model.Files());
    }

    [Fact]
    public void A_tile_icon_goes_to_the_tile_of_its_full_path_and_the_media_hubs_tiles_are_in_media()
    {
        // App Library's icon sits under Game Library's folder: the tile is found by the whole path, not by its first folder.
        PreviewModel model = PreviewModel.Of(Manifest([
            Target("rnps/NPXS40071/appdb/NPXS40139/icon0.png", "assets/library.png"),
            Target("rnps/NPXS40016/appdb/NPXS40056/icon0.png", "assets/all.png"),
            Target("rnps/NPXS40047/appdb/default/icon0.png", "assets/store.png"),
            Target("rnps/NPXS40037/appdb/default/icon0.dds", "assets/plus.dds"),   // not a PNG: no icon
        ]));

        Assert.Equal("assets/library.png", model.Games.Single(t => t.Id == "NPXS40139").IconFile);
        Assert.Null(model.Games.Single(t => t.Id == "NPXS40071").IconFile);
        Assert.Equal("assets/store.png", model.Games.Single(t => t.Id == "NPXS40047").IconFile);
        Assert.Null(model.Games.Single(t => t.Id == "NPXS40037").IconFile);
        Assert.Equal("assets/all.png", model.Media.Single(t => t.Id == "NPXS40056").IconFile);
        Assert.DoesNotContain(model.Games, t => t.Id == "NPXS40056");
    }

    [Fact]
    public void A_tile_the_list_does_not_know_comes_after_the_known_ones_named_by_its_title_id()
    {
        PreviewModel model = PreviewModel.Of(Manifest([
            Target("rnps/NPXS40099/appdb/default/icon0.png", "assets/new.png"),
            Target("rnps/NPXS40016/appdb/NPXS40777/icon0.png", "assets/hub.png"),
            Target("rnps/NPXS40099/appdb/default/icon0.png", "assets/again.png"),   // the same tile twice: once
        ]));

        PreviewTile added = model.Games[5];
        Assert.Equal(("NPXS40099", "NPXS40099", PreviewTileKind.System), (added.Id, added.Name, added.Kind));
        Assert.Equal("assets/again.png", added.IconFile);   // the later target of a path wins, as in the applier
        Assert.Equal(1, model.Games.Count(t => t.Id == "NPXS40099"));
        Assert.Equal("NPXS40777", model.Media[5].Id);
        Assert.Equal("assets/hub.png", model.Media[5].IconFile);
        Assert.Equal("Demo game", model.Games[6].Name);   // the stand-ins follow
    }

    [Fact]
    public void Apps_with_a_background_of_their_own_get_tiles_after_the_system_ones_pictures_first()
    {
        PreviewModel model = PreviewModel.Of(Manifest(
            [Target("apps/PPSA22222/pic0.dds", layer: "background"), Target("appmeta/PPSA33333/pic0.dds", layer: "background"), Target("apps/PPSA11111/pic0.dds", layer: "background")],
            Previews(apps: [("PPSA11111", "previews/apps/PPSA11111.jpg"), ("PPSA44444", "previews/apps/PPSA44444.jpg")])));

        PreviewTile[] apps = model.Games.Where(t => t.Kind == PreviewTileKind.App).ToArray();
        Assert.Equal(["PPSA11111", "PPSA44444", "PPSA22222", "PPSA33333"], apps.Select(t => t.Id).ToArray());
        Assert.Equal(new string?[] { "previews/apps/PPSA11111.jpg", "previews/apps/PPSA44444.jpg", null, null }, apps.Select(t => t.PictureFile).ToArray());
        var games = model.Games.ToList();
        Assert.Equal(games.IndexOf(apps[0]) + 1, games.IndexOf(apps[1]));
        Assert.True(games.IndexOf(apps[0]) > games.FindLastIndex(t => t.Kind == PreviewTileKind.System));
        Assert.True(games.IndexOf(apps[^1]) < games.FindIndex(t => t.Kind == PreviewTileKind.Demo));
        Assert.All(apps, t => Assert.Equal(t.Id, t.Name));
    }

    [Fact]
    public void An_apps_own_picture_for_a_system_tile_goes_to_that_tile_and_makes_no_new_one()
    {
        PreviewModel model = PreviewModel.Of(Manifest(previews: Previews(apps: [("NPXS40071", "previews/apps/NPXS40071.jpg")])));

        Assert.Equal("previews/apps/NPXS40071.jpg", model.Games.Single(t => t.Id == "NPXS40071").PictureFile);
        Assert.DoesNotContain(model.Games, t => t.Kind == PreviewTileKind.App);
    }

    [Fact]
    public void No_more_than_twelve_app_tiles_are_made()
    {
        PreviewModel model = PreviewModel.Of(Manifest(previews: Previews(apps: Enumerable.Range(0, 30).Select(i => ($"PPSA{20000 + i}", $"previews/apps/{i}.jpg")))));

        Assert.Equal(PreviewModel.MaxAppTiles, model.Games.Count(t => t.Kind == PreviewTileKind.App));
    }

    // ---- The picture behind a tile ----------------------------------------------------------------------------------

    [Fact]
    public void A_tiles_picture_is_its_own_then_the_all_apps_one_for_anything_but_a_system_tile_then_the_cover()
    {
        PreviewModel model = PreviewModel.Of(Manifest(previews: Previews(
            apps: [("PPSA11111", "previews/a.jpg"), ("NPXS40071", "previews/lib.jpg")], allApps: "previews/all.jpg")));
        PreviewTile system = model.Games.Single(t => t.Id == "NPXS40047"), own = model.Games.Single(t => t.Id == "PPSA11111");
        PreviewTile ownSystem = model.Games.Single(t => t.Id == "NPXS40071"), demo = model.Games.First(t => t.Kind == PreviewTileKind.Demo);

        Assert.Equal(["cover"], model.PictureKeys(system).ToArray());                    // the home background is the theme's own
        Assert.Equal(["app:NPXS40071", "cover"], model.PictureKeys(ownSystem).ToArray());
        Assert.Equal(["app:PPSA11111", "allapps", "cover"], model.PictureKeys(own).ToArray());
        Assert.Equal(["allapps", "cover"], model.PictureKeys(demo).ToArray());
        Assert.Equal("previews/a.jpg", model.PictureFile("app:PPSA11111"));
        Assert.Equal("previews/lib.jpg", model.PictureFile("app:NPXS40071"));
        Assert.Equal("previews/all.jpg", model.PictureFile("allapps"));
        Assert.Null(model.PictureFile("cover"));
        Assert.Null(model.PictureFile("app:PPSA99999"));
    }

    [Fact]
    public void Without_an_all_apps_picture_every_tile_without_its_own_has_the_cover()
    {
        PreviewModel model = PreviewModel.Of(Manifest());

        Assert.All(model.Games.Concat(model.Media), t => Assert.Equal(["cover"], model.PictureKeys(t).ToArray()));
    }

    // ---- The control centre, the sounds, the files ------------------------------------------------------------------

    [Fact]
    public void The_control_centre_has_twelve_buttons_in_the_consoles_order_with_the_packs_renders()
    {
        PreviewModel model = PreviewModel.Of(Manifest(previews: Previews(icons:
            [("iconid_game_base", "previews/icons/iconid_game_base.png"), ("iconid_search", "previews/icons/iconid_search.png"), ("iconid_power", "previews/icons/iconid_power.png")])));

        Assert.Equal(["Home", "Switcher", "Notifications", "Game Base", "Music", "Downloads", "Share Screen", "Sound", "Mic", "Accessories", "Profile", "Power"],
            model.Controls.Select(c => c.Name).ToArray());
        Assert.Equal(["iconid_home", "iconid_switcher", "iconid_notification", "iconid_game_base", "iconid_music", "iconid_download", "iconid_share_screen",
            "iconid_sound", "iconid_mic", "iconid_ps5_wireless_controller", "iconid_ps_user", "iconid_power"], model.Controls.Select(c => c.Slot).ToArray());
        Assert.Equal("previews/icons/iconid_game_base.png", model.Controls[3].IconFile);
        Assert.Equal("previews/icons/iconid_power.png", model.Controls[11].IconFile);
        Assert.Equal(2, model.Controls.Count(c => c.IconFile is not null));   // iconid_search is not one of the twelve
        Assert.Equal(PreviewModel.ControlSlots.Count, model.Controls.Count);
    }

    [Fact]
    public void The_packs_own_ui_sounds_are_its_vag_targets_named_by_clip_and_only_the_four_clips_the_preview_plays()
    {
        PreviewModel model = PreviewModel.Of(Manifest([
            Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/move.vag", "vag", "psfx_focus_move"),
            Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/enter.vag", "vag", "psfx_enter"),
            Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/cancel.vag", "vag", "psfx_cancel"),
            Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/end.vag", "vag", "psfx_cannot_move"),
            Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/error.vag", "vag", "psfx_error"),       // a clip the preview does not play
            Target("vsh_asset/other.bin", "sounds/sneaky.vag", "sound", "psfx_enter"),                       // not the vag layer
        ]));

        Assert.Equal(4, model.SoundFiles.Count);
        Assert.Equal("sounds/move.vag", model.SoundFiles["psfx_focus_move"]);
        Assert.Equal("sounds/enter.vag", model.SoundFiles["psfx_enter"]);
        Assert.Equal("psfx_focus_move", PreviewModel.Clips[PreviewSound.Move]);
        Assert.Equal("psfx_enter", PreviewModel.Clips[PreviewSound.Confirm]);
        Assert.Equal("psfx_cancel", PreviewModel.Clips[PreviewSound.Back]);
        Assert.Equal("psfx_cannot_move", PreviewModel.Clips[PreviewSound.End]);
    }

    [Fact]
    public void The_files_the_model_names_are_all_the_preview_reads()
    {
        PreviewModel model = PreviewModel.Of(Manifest(
            [Target("rnps/NPXS40047/appdb/default/icon0.png", "assets/store.png"), Target("apps/PPSA22222/pic0.dds", "assets/PPSA22222/pic0.dds", "background"),
             Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/move.vag", "vag", "psfx_focus_move")],
            Previews(apps: [("PPSA11111", "previews/a.jpg")], allApps: "previews/all.jpg", music: "previews/music.mp3", icons: [("iconid_home", "previews/home.png")])));

        Assert.Equal(new[] { "assets/store.png", "previews/a.jpg", "previews/all.jpg", "previews/music.mp3", "previews/home.png", "sounds/move.vag" }.Order().ToArray(),
            model.Files().Order().ToArray());
    }

    [Fact]
    public void Every_word_the_preview_writes_is_in_the_font_atlases()
    {
        // tools/bake-assets.py bakes printable ASCII and a few more; a name outside them would show as a question mark.
        string dir = Path.Combine(AppContext.BaseDirectory, "renderer");
        AtlasFont[] fonts = new[] { "inter-22.p5font", "inter-28.p5font", "inter-36.p5font" }.Select(f => new AtlasFont(File.ReadAllBytes(Path.Combine(dir, f)))).ToArray();
        PreviewModel model = PreviewModel.Of(Manifest(previews: Previews(apps: [("PPSA11111", "a.jpg")])));
        string[] words = [.. Names(model.Games), .. Names(model.Media), .. model.Controls.Select(c => c.Name),
            "Games", "Media", "Control centre", "PREVIEW", "Hide overlay", "Home screen", "Stop music", "Music", "Back"];

        foreach (string word in words)
            foreach (AtlasFont font in fonts)
                foreach (char c in word)
                    Assert.True(font.HasGlyph(c), $"'{c}' of \"{word}\" is not in the atlas");
    }

    // ---- Image sizes --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_png_is_measured_from_its_header_and_nothing_else_is_taken_for_one()
    {
        byte[] png = Png(512, 384, 1, 2, 3);

        Assert.Equal((512, 384), ImageHeader.PngSize(png));
        Assert.Null(ImageHeader.PngSize(png.AsSpan(0, 30)));                        // cut inside the header
        Assert.Null(ImageHeader.PngSize(FakeJpeg(10, 10, 0, 0, 0)));
        Assert.Null(ImageHeader.PngSize(Array.Empty<byte>()));
        byte[] zero = (byte[])png.Clone();
        zero[19] = 0;   // width 0
        zero[18] = zero[17] = zero[16] = 0;
        Assert.Null(ImageHeader.PngSize(zero));
        byte[] notIhdr = (byte[])png.Clone();
        notIhdr[12] = (byte)'X';
        Assert.Null(ImageHeader.PngSize(notIhdr));
    }

    [Fact]
    public void A_jpeg_is_measured_from_its_first_frame_header_baseline_or_progressive()
    {
        Assert.Equal((300, 200), ImageHeader.JpegSize(FakeJpeg(300, 200, 0, 0, 0)));
        // The creator fixture's picture is progressive (SOF2) and 1280x720.
        var pack = ProsperoThemes.Shared.ThemePack.OpenFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "creator-previews.p5theme"));
        Assert.Equal((1280, 720), ImageHeader.JpegSize(pack.Find("previews/apps/PPSA99169.jpg")!));
    }

    [Fact]
    public void A_thumbnail_inside_an_exif_block_is_not_the_picture()
    {
        byte[] thumbnail = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9];
        byte[] exif = [0xFF, 0xE1, (byte)((thumbnail.Length + 2) >> 8), (byte)(thumbnail.Length + 2), .. thumbnail];
        byte[] dht = [0xFF, 0xC4, 0x00, 0x05, 0x00, 0xC0, 0xC0];   // a Huffman table, whose marker is between SOF0 and SOF15
        byte[] real = FakeJpeg(640, 480, 0, 0, 0);
        byte[] jpeg = [0xFF, 0xD8, .. exif, .. dht, .. real[2..]];

        Assert.Equal((640, 480), ImageHeader.JpegSize(jpeg));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00 })]                   // cut inside the frame header
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x00, 0x00 })]                   // a scan before any frame
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0, 0, 0, 5, 1, 1, 0x11, 0 })]   // no height
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 })]
    public void Anything_else_has_no_jpeg_size(byte[] bytes) => Assert.Null(ImageHeader.JpegSize(bytes));

    // ---- MP3 headers --------------------------------------------------------------------------------------------------

    private static byte[] Mp3Frame(byte b1, byte b2, byte b3, int length, string? tagAt36 = null)
    {
        var frame = new byte[length];
        (frame[0], frame[1], frame[2], frame[3]) = (0xFF, b1, b2, b3);
        if (tagAt36 is not null) System.Text.Encoding.ASCII.GetBytes(tagAt36).CopyTo(frame, 36);
        return frame;
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void An_mp3_gives_its_rate_channels_and_samples_per_frame_from_the_first_frame()
    {
        byte[] stereo = Mp3Frame(0xFB, 0x90, 0x00, 417);   // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, stereo
        Assert.Equal(new Mp3Info(0, 44100, 2, 1152), Mp3Frames.Find(Join(stereo, stereo, stereo)));

        byte[] mono = Mp3Frame(0xFB, 0x94, 0xC0, 384);     // 48 kHz, single channel: 144 * 128000 / 48000 bytes
        Assert.Equal(new Mp3Info(0, 48000, 1, 1152), Mp3Frames.Find(Join(mono, mono)));

        byte[] half = Mp3Frame(0xF3, 0x80, 0x00, 208);     // MPEG-2 Layer III, 64 kbit/s, 22.05 kHz: 576 samples a frame
        Assert.Equal(new Mp3Info(0, 22050, 2, 576), Mp3Frames.Find(Join(half, half, half)));
    }

    [Fact]
    public void An_id3_tag_and_a_xing_frame_in_front_are_skipped()
    {
        byte[] tag = Join([(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 20], new byte[20]);   // 10-byte header, 20 bytes of body
        byte[] xing = Mp3Frame(0xFB, 0x90, 0x00, 417, "Xing");
        byte[] frame = Mp3Frame(0xFB, 0x90, 0x00, 417);

        Mp3Info info = Mp3Frames.Find(Join(tag, xing, frame, frame, frame))!.Value;

        Assert.Equal(30 + 417, info.Start);
        Assert.Equal(30, Mp3Frames.Id3v2Length(tag));
        Assert.Equal(0, Mp3Frames.Id3v2Length(frame));
        Assert.Equal(30 + 417, Mp3Frames.Find(Join(tag, Mp3Frame(0xFB, 0x90, 0x00, 417, "Info"), frame, frame))!.Value.Start);
    }

    [Fact]
    public void Junk_before_the_first_frame_is_skipped_and_a_lone_sync_pattern_is_not_a_frame()
    {
        byte[] frame = Mp3Frame(0xFB, 0x90, 0x00, 417);
        byte[] junk = [1, 2, 3, 0xFF, 0xFB, 0x90, 0x00, 9, 9, 9, 9];   // looks like a header but no frame follows 417 bytes later

        Assert.Equal(junk.Length, Mp3Frames.Find(Join(junk, frame, frame, frame))!.Value.Start);
        Assert.Null(Mp3Frames.Find(junk));
    }

    [Fact]
    public void Only_layer_three_and_a_fixed_bitrate_are_decoded()
    {
        byte[] layer2 = Mp3Frame(0xFD, 0x90, 0x00, 417);
        Assert.Null(Mp3Frames.Find(Join(layer2, layer2, layer2)));
        byte[] free = Mp3Frame(0xFB, 0x00, 0x00, 417);   // free format: no length to follow
        Assert.Null(Mp3Frames.Find(Join(free, free)));
        Assert.Null(Mp3Frames.Find([]));
        Assert.Null(Mp3Frames.Find(new byte[2000]));
    }

    [Fact]
    public void The_music_of_the_creator_fixture_is_a_layer_three_stream_the_decoder_can_take()
    {
        var pack = ProsperoThemes.Shared.ThemePack.OpenFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "creator-previews.p5theme"));
        byte[] mp3 = pack.Find("previews/music.mp3")!;

        Mp3Info info = Mp3Frames.Find(mp3)!.Value;

        Assert.Contains(info.SampleRate, new[] { 22050, 24000, 32000, 44100, 48000 });
        Assert.InRange(info.Channels, 1, 2);
        Assert.InRange(info.Start, 0, mp3.Length - 1);
        Assert.Equal(0xFF, mp3[info.Start]);
    }

    // ---- The files of a pack ----------------------------------------------------------------------------------------

    [Fact]
    public void The_assets_are_the_files_the_model_names_and_that_are_within_the_limits_of_their_kind()
    {
        byte[] icon = Png(512, 512, 10, 20, 30), jpeg = FakeJpeg(64, 36, 1, 2, 3), music = [1, 2, 3, 4], vag = [5, 6, 7];
        byte[] tooBig = new byte[PreviewAssets.MaxPicture + 1];
        PreviewModel model = PreviewModel.Of(Manifest(
            [Target("rnps/NPXS40047/appdb/default/icon0.png", "assets/store.png"), Target("rnps/NPXS40037/appdb/default/icon0.png", "assets/plus.png"),
             Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/move.vag", "vag", "psfx_focus_move")],
            Previews(apps: [("PPSA11111", "previews/a.jpg")], allApps: "previews/all.jpg", music: "previews/music.mp3", icons: [("iconid_home", "previews/home.png")])));
        byte[] archive = Archive(Manifest(),
            ("assets/store.png", icon), ("assets/plus.png", icon), ("sounds/move.vag", vag), ("previews/a.jpg", jpeg), ("previews/all.jpg", tooBig),
            ("previews/music.mp3", music), ("previews/home.png", Png(128, 128, 255, 255, 255)), ("assets/unrelated.bin", new byte[300_000]));

        PreviewAssets assets = PreviewAssets.Load(model, archive);

        Assert.Equal(icon, assets.Get("assets/store.png"));
        Assert.Equal(vag, assets.Get("sounds/move.vag"));
        Assert.Equal(jpeg, assets.Get("previews/a.jpg"));
        Assert.Equal(music, assets.Get("previews/music.mp3"));
        Assert.NotNull(assets.Get("previews/home.png"));
        Assert.Null(assets.Get("previews/all.jpg"));        // over the limit for a picture
        Assert.Null(assets.Get("assets/unrelated.bin"));    // not named by the model
        Assert.Null(assets.Get("assets/missing.png"));
        Assert.Null(assets.Get(null));
        Assert.Equal(6, assets.Count);
    }

    [Fact]
    public void An_archive_that_cannot_be_read_raises_the_packs_own_exception()
    {
        Assert.Throws<ThemePackException>(() => PreviewAssets.Load(PreviewModel.Of(Manifest()), [1, 2, 3, 4, 5]));
        Assert.Equal(0, PreviewAssets.Empty().Count);
    }
}
