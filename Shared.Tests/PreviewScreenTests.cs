using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using SharpProspero.Ui;
using Xunit;
using static ProsperoThemes.Shared.Tests.PreviewKit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The preview as it is drawn and fed: the visuals it holds, the controller that loads them off the frame thread, and the
/// frames that come out (tile icons, the picture that follows the focus and fades, the control centre).
/// </summary>
public class PreviewScreenTests
{
    private static readonly UiInput Right = UiInput.None with { Right = true }, Left = UiInput.None with { Left = true },
        O = UiInput.None with { Cancel = true }, R1 = UiInput.None with { PageDown = true };

    private static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)(r << 16 | g << 8 | b);
    private static int Blue(uint pixel) => (int)(pixel & 0xFF);

    private static readonly uint CoverColour = Rgb(120, 60, 30);

    /// <summary>A preview with the pieces of the control: the renderer, a browser on one pack, a controller whose jobs wait for the test, a recording sound.</summary>
    private sealed class Scene : IDisposable
    {
        public readonly ThemeRenderer Renderer;
        public readonly PixelBuffer Canvas = new(1920, 1080);
        public readonly ThemeBrowser Browser = new();
        public readonly PreviewController Controller;
        public readonly RecordingAudio Audio = new();
        public readonly QueueWorker Worker = new();
        public ThemeLibraryEntry[] Entries;
        public double Time = 5;

        public Scene(ThemePackManifest manifest, byte[] archive, Func<byte[], PixelBuffer>? decode = null, Func<string, byte[]>? readPack = null)
        {
            decode ??= Decode;
            Renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), decode);
            Entries = [new ThemeLibraryEntry("pack", manifest, new PackReport(), Png(64, 36, 120, 60, 30))];
            Browser.SetCount(1);
            Controller = new PreviewController(Renderer, Browser, Audio, decode, readPack ?? (_ => archive), Worker);
        }

        /// <summary>One frame of buttons, as the app feeds them (Square is the music, Triangle the control centre).</summary>
        public void Press(UiInput input, bool square = false, bool triangle = false)
        {
            Controller.BeforeUpdate(Entries);
            Browser.Update(input, preview: square, triangle: triangle);
            if (Browser.TakeAction() == ThemeAction.PreviewMusic) Controller.ToggleMusic();
            Controller.Update(Entries);
        }

        public void Open()
        {
            Press(UiInput.None, square: true);
            Worker.RunAll();
        }

        /// <summary>Frames at 60 Hz; between them the controller starts what the renderer asked for and the worker answers at once.</summary>
        public void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Renderer.Draw(Canvas.AsSurface(), Browser, Entries, Time, 1.0 / 60);
                Time += 1.0 / 60;
                Controller.Update(Entries);
                Worker.RunAll();
            }
        }

        public uint Pixel(int x, int y) => PixelAt(Canvas, x, y);

        public unsafe long Hash()
        {
            Surface s = Canvas.AsSurface();
            long hash = 17;
            for (int y = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++) hash = unchecked(hash * 31 + s.Pixels[y * s.Stride + x]);
            return hash;
        }

        public void Dispose()
        {
            Controller.Dispose();
            Renderer.Dispose();
            Canvas.Dispose();
        }
    }

    private static int TileX(int index) => Theme.SafeX + index * ThemeRenderer.HomePitch;

    // The pack of the pictures: an app with its own picture (a blue one), the all-apps picture (green), a Store icon (red).
    private static (ThemePackManifest Manifest, byte[] Archive) Pictures(bool icon = false, bool allApps = true)
    {
        var targets = new List<ThemePackTarget>();
        var files = new List<(string, byte[])>();
        if (icon)
        {
            targets.Add(Target("rnps/NPXS40047/appdb/default/icon0.png", "assets/store.png"));
            files.Add(("assets/store.png", Png(256, 256, 200, 40, 40)));
        }
        ThemePackManifest manifest = Manifest(targets, Previews(apps: [("PPSA11111", "previews/a.jpg")], allApps: allApps ? "previews/all.jpg" : null));
        files.Add(("previews/a.jpg", FakeJpeg(64, 36, 0, 0, 200)));
        files.Add(("previews/all.jpg", FakeJpeg(64, 36, 0, 160, 0)));
        return (manifest, Archive(manifest, [.. files]));
    }

    // ---- The visuals ------------------------------------------------------------------------------------------------

    private static PreviewPicture Picture(byte red) => ThemeRenderer.BuildPreviewPicture(Decode(Png(16, 9, red, 0, 0)), smooth: false);

    [Fact]
    public void Only_five_pictures_are_kept_and_the_least_recently_used_goes_first()
    {
        using var visuals = new PreviewVisuals();
        PreviewPicture[] pictures = Enumerable.Range(0, 6).Select(i => Picture((byte)i)).ToArray();
        for (int i = 0; i < PreviewVisuals.MaxPictures; i++) visuals.AddPicture("p" + i, pictures[i]);
        Assert.Equal(PreviewVisuals.MaxPictures, visuals.PictureCount);

        Assert.NotNull(visuals.Picture("p0"));   // used: p1 is now the oldest
        visuals.AddPicture("p5", pictures[5]);

        Assert.Equal(PreviewVisuals.MaxPictures, visuals.PictureCount);
        Assert.False(visuals.HasPicture("p1"));
        Assert.True(visuals.HasPicture("p0") && visuals.HasPicture("p2") && visuals.HasPicture("p3") && visuals.HasPicture("p4") && visuals.HasPicture("p5"));
        Assert.Throws<ObjectDisposedException>(() => pictures[1].Full.AsSurface());   // freed with it
    }

    [Fact]
    public void The_cover_is_never_the_picture_that_makes_room_however_long_ago_it_was_used()
    {
        using var visuals = new PreviewVisuals();
        PreviewPicture cover = Picture(9);
        visuals.AddPicture(PreviewModel.CoverKey, cover);   // the oldest of all, and never touched again
        for (int i = 0; i < 20; i++) visuals.AddPicture("app:PPSA" + (10000 + i), Picture((byte)i));

        Assert.True(visuals.HasPicture(PreviewModel.CoverKey));
        Assert.Equal(PreviewVisuals.MaxPictures, visuals.PictureCount);
        Assert.Equal(Rgb(9, 0, 0), PixelAt(cover.Full, 500, 500));   // and still alive
        Assert.False(visuals.HasPicture("app:PPSA10000"));
        Assert.True(visuals.HasPicture("app:PPSA10019"));
    }

    [Fact]
    public void A_picture_that_arrives_clears_the_request_and_one_that_failed_is_remembered()
    {
        using var visuals = new PreviewVisuals();
        visuals.Wanted = "app:PPSA11111";
        visuals.AddPicture("allapps", Picture(1));
        Assert.Equal("app:PPSA11111", visuals.Wanted);   // another picture does not answer it
        visuals.AddPicture("app:PPSA11111", Picture(2));
        Assert.Null(visuals.Wanted);

        visuals.Wanted = "app:PPSA22222";
        visuals.Fail("app:PPSA22222");
        Assert.Null(visuals.Wanted);
        Assert.True(visuals.HasFailed("app:PPSA22222"));
        Assert.False(visuals.HasPicture("app:PPSA22222"));
        visuals.AddPicture("app:PPSA22222", Picture(3));   // a later success wins
        Assert.False(visuals.HasFailed("app:PPSA22222"));
    }

    [Fact]
    public void Clearing_frees_everything_and_replacing_a_picture_frees_the_old_one()
    {
        var visuals = new PreviewVisuals();
        PreviewPicture first = Picture(1), second = Picture(2);
        visuals.AddPicture("a", first);
        visuals.AddPicture("a", second);
        Assert.Throws<ObjectDisposedException>(() => first.Full.AsSurface());
        Assert.Equal(1, visuals.PictureCount);
        visuals.SetGlyph("slot", new PixelBuffer(4, 4));
        Assert.False(visuals.IsEmpty);

        visuals.Clear();

        Assert.True(visuals.IsEmpty);
        Assert.Throws<ObjectDisposedException>(() => second.Full.AsSurface());
        Assert.Null(visuals.Glyph("slot"));
    }

    [Fact]
    public void The_icons_of_a_pack_are_decoded_and_scaled_to_the_tiles_and_buttons_and_a_bad_file_is_skipped()
    {
        PreviewModel model = PreviewModel.Of(Manifest(
            [Target("rnps/NPXS40047/appdb/default/icon0.png", "assets/store.png"),     // good
             Target("rnps/NPXS40037/appdb/default/icon0.png", "assets/plus.png"),      // garbage
             Target("rnps/NPXS40071/appdb/default/icon0.png", "assets/library.png"),   // far too big for an icon
             Target("rnps/NPXS40063/appdb/default/icon0.png", "assets/welcome.png")],  // not in the pack
            Previews(apps: [("PPSA11111", "previews/a.jpg")], icons: [("iconid_home", "previews/home.png"), ("iconid_mic", "previews/mic.png"), ("iconid_power", "previews/power.png")])));
        var assets = PreviewAssets.Load(model, Archive(Manifest(),
            ("assets/store.png", Png(512, 512, 200, 40, 40)), ("assets/plus.png", [1, 2, 3, 4]), ("assets/library.png", Png(4000, 4000, 1, 1, 1)),
            ("previews/a.jpg", FakeJpeg(160, 90, 0, 0, 255)),
            ("previews/home.png", Png(128, 128, 255, 255, 255)), ("previews/mic.png", Png(2000, 2000, 255, 255, 255)), ("previews/power.png", Png(128, 128, 255, 255, 255, 0))));
        var decoded = new List<int>();

        using PreviewLoad load = PreviewVisuals.Load(model, assets, bytes => { decoded.Add(bytes.Length); return Decode(bytes); });

        Assert.Equal(["NPXS40047", "PPSA11111"], load.Tiles.Keys.Order().ToArray());   // the store icon, and the app's picture cropped square
        Assert.Equal(["iconid_home", "iconid_power"], load.Glyphs.Keys.Order().ToArray());
        Assert.All(load.Glyphs.Values, g => Assert.Equal((PreviewVisuals.GlyphSize, PreviewVisuals.GlyphSize), (g.Width, g.Height)));
        Assert.Equal(Rgb(255, 255, 255), PixelAt(load.Glyphs["iconid_home"], 32, 32));
        Assert.Equal(0u, PixelAt(load.Glyphs["iconid_power"], 32, 32) >> 24);   // a transparent render stays transparent: the alpha is kept
        Assert.DoesNotContain(Png(4000, 4000, 1, 1, 1).Length, decoded);         // a picture of the wrong size never reaches the decoder
        Assert.DoesNotContain(4, decoded);                                       // nor does a file that is not a picture
        Assert.Equal(4, decoded.Count);
    }

    [Fact]
    public void A_decoder_that_refuses_a_file_leaves_that_tile_without_art_and_nothing_throws()
    {
        PreviewModel model = PreviewModel.Of(Manifest([Target("rnps/NPXS40047/appdb/default/icon0.png", "assets/store.png")], Previews(icons: [("iconid_home", "previews/home.png")])));
        var assets = PreviewAssets.Load(model, Archive(Manifest(), ("assets/store.png", Png(64, 64, 1, 2, 3)), ("previews/home.png", Png(64, 64, 255, 255, 255))));

        using PreviewLoad load = PreviewVisuals.Load(model, assets, _ => throw new InvalidDataException("no"));

        Assert.Empty(load.Tiles);
        Assert.Empty(load.Glyphs);
    }

    [Fact]
    public void A_picture_is_cropped_to_its_middle_square()
    {
        using var wide = new PixelBuffer(6, 2);
        wide.Clear(Color.FromRgb(1, 1, 1));
        wide.AsSurface().FillRect(2, 0, 2, 2, Color.FromRgb(9, 9, 9));

        using PixelBuffer square = PreviewVisuals.CropSquare(wide);

        Assert.Equal((2, 2), (square.Width, square.Height));
        Assert.Equal(Rgb(9, 9, 9), PixelAt(square, 0, 0));
        Assert.Equal(Rgb(9, 9, 9), PixelAt(square, 1, 1));
    }

    // ---- The layers of a picture ------------------------------------------------------------------------------------

    [Fact]
    public void A_picture_is_made_at_the_screens_size_with_washed_bands_that_end_in_the_picture_itself()
    {
        using var source = new PixelBuffer(32, 18);
        source.Clear(Color.FromRgb(200, 150, 90));

        using PreviewPicture picture = ThemeRenderer.BuildPreviewPicture(source, smooth: true);

        Assert.Equal((1920, 1080), (picture.Full.Width, picture.Full.Height));
        Assert.Equal((1920, ThemeRenderer.HomeTopHeight), (picture.Top.Width, picture.Top.Height));
        Assert.Equal((1920, ThemeRenderer.HomeBottomHeight), (picture.Bottom.Width, picture.Bottom.Height));
        uint flat = Rgb(200, 150, 90);
        Assert.Equal(flat, PixelAt(picture.Full, 700, 500));
        int Red(uint p) => (int)((p >> 16) & 0xFF);
        Assert.True(Red(PixelAt(picture.Top, 700, 0)) < 150);                                // the top of the cap is washed toward the ink
        Assert.Equal(200, Red(PixelAt(picture.Top, 700, ThemeRenderer.HomeTopHeight - 1)));   // and its last row is the picture: no edge
        Assert.Equal(200, Red(PixelAt(picture.Bottom, 700, 0)));
        Assert.True(Red(PixelAt(picture.Bottom, 700, ThemeRenderer.HomeBottomHeight - 1)) < 60);
        Assert.Equal(0xFF000000u, PixelAt(picture.Top, 700, 0) & 0xFF000000u);
    }

    [Fact]
    public void Mixing_two_pictures_takes_the_share_of_each_in_every_pixel_of_the_bands_and_the_rest()
    {
        using var dark = new PixelBuffer(16, 9);
        dark.Clear(Color.FromRgb(0, 0, 0));
        using var light = new PixelBuffer(16, 9);
        light.Clear(Color.FromRgb(200, 100, 50));
        using PreviewPicture from = ThemeRenderer.BuildPreviewPicture(dark, smooth: false), to = ThemeRenderer.BuildPreviewPicture(light, smooth: false);
        using var mixed = new PixelBuffer(1920, 1080);

        ThemeRenderer.MixPictures(mixed, from, to, 128, overlay: false);
        Assert.Equal(Rgb(100, 50, 25), PixelAt(mixed, 0, 0));
        Assert.Equal(Rgb(100, 50, 25), PixelAt(mixed, 1919, 1079));
        ThemeRenderer.MixPictures(mixed, from, to, 0, overlay: false);
        Assert.Equal(Rgb(0, 0, 0), PixelAt(mixed, 960, 500));
        ThemeRenderer.MixPictures(mixed, from, to, 256, overlay: false);
        Assert.Equal(Rgb(200, 100, 50), PixelAt(mixed, 960, 500));

        // With the overlay the bands come from the washed layers: the middle is the plain mix, the top row is darker than it.
        ThemeRenderer.MixPictures(mixed, from, to, 128, overlay: true);
        Assert.Equal(Rgb(100, 50, 25), PixelAt(mixed, 960, 500));
        Assert.True(((PixelAt(mixed, 960, 0) >> 16) & 0xFF) < 100);
        Assert.True(((PixelAt(mixed, 960, 1079) >> 16) & 0xFF) < 100);
        Assert.Equal(Rgb(100, 50, 25), PixelAt(mixed, 960, ThemeRenderer.HomeTopHeight - 1));
    }

    // ---- The frames -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_packs_tile_icons_replace_the_stand_ins_once_they_are_loaded()
    {
        (ThemePackManifest manifest, byte[] archive) = Pictures(icon: true);
        using var scene = new Scene(manifest, archive);
        int x = TileX(0) + 24, y = ThemeRenderer.HomeTilesY + 24;

        scene.Press(UiInput.None, square: true);
        scene.Controller.Update(scene.Entries);
        scene.Renderer.Draw(scene.Canvas.AsSurface(), scene.Browser, scene.Entries, 5);
        uint standIn = scene.Pixel(x, y);
        Assert.NotEqual(Rgb(200, 40, 40), standIn);   // the pack is still being read on the worker: the drawn tile

        scene.Worker.RunAll();
        scene.Frames(3);

        Assert.Equal(Rgb(200, 40, 40), scene.Pixel(x, y));
        Assert.NotEqual(Rgb(200, 40, 40), scene.Pixel(TileX(1) + 24, y));   // the Plus tile has no icon in this pack
    }

    [Fact]
    public void Without_previews_every_tile_is_in_front_of_the_cover_and_nothing_is_asked_for()
    {
        ThemePackManifest manifest = Manifest();
        using var scene = new Scene(manifest, Archive(manifest));
        scene.Open();

        for (int i = 0; i < 8; i++)
        {
            scene.Frames(70);
            Assert.Equal(CoverColour, scene.Pixel(960, 500));
            Assert.Null(scene.Renderer.Preview.Wanted);
            scene.Press(Right);
        }
        scene.Press(R1);
        scene.Frames(70);
        Assert.Equal(CoverColour, scene.Pixel(960, 500));
        Assert.Equal(1, scene.Renderer.Preview.PictureCount);   // the cover is the only picture there is
    }

    [Fact]
    public void The_picture_behind_the_row_follows_the_focus_and_changes_with_a_cross_fade()
    {
        (ThemePackManifest manifest, byte[] archive) = Pictures();
        using var scene = new Scene(manifest, archive);
        scene.Open();
        scene.Frames(70);
        Assert.Equal(CoverColour, scene.Pixel(960, 500));   // the Store has no picture of its own

        // Five tiles to the right is the app with its own picture (a blue one); the picture is read on the worker, then fades in.
        for (int i = 0; i < 5; i++) scene.Press(Right);
        scene.Frames(1);
        Assert.Equal("app:PPSA11111", scene.Renderer.Preview.Wanted ?? "app:PPSA11111");
        var blues = new List<int>();
        for (int i = 0; i < 30; i++)
        {
            scene.Frames(1);
            blues.Add(Blue(scene.Pixel(960, 500)));
        }
        Assert.Null(scene.Renderer.Preview.Wanted);
        Assert.Equal(Rgb(0, 0, 200), scene.Pixel(960, 500));
        Assert.Contains(blues, b => b > 30 && b < 200);                      // in between, not a cut
        Assert.Equal(blues.Order().ToArray(), blues.ToArray());              // and always on its way, never back
        Assert.Equal(30, Blue(CoverColour));

        // The next tile is a stand-in app: it has none of its own and takes the all-apps picture (green).
        scene.Press(Right);
        scene.Frames(40);
        Assert.Equal(Rgb(0, 160, 0), scene.Pixel(960, 500));

        // Back on a system tile the theme's own cover is behind it again.
        for (int i = 0; i < 6; i++) scene.Press(Left);
        scene.Frames(40);
        Assert.Equal(CoverColour, scene.Pixel(960, 500));
    }

    [Fact]
    public void A_picture_the_decoder_refuses_falls_back_to_the_next_best_and_is_not_asked_for_again()
    {
        (ThemePackManifest manifest, byte[] archive) = Pictures();
        int attempts = 0;
        PixelBuffer Refuse(byte[] bytes)
        {
            if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[^1] == 200) { attempts++; throw new InvalidDataException("not decodable"); }   // the app's blue picture
            return Decode(bytes);
        }
        using var scene = new Scene(manifest, archive, Refuse);
        scene.Open();
        for (int i = 0; i < 5; i++) scene.Press(Right);

        scene.Frames(60);

        Assert.True(scene.Renderer.Preview.HasFailed("app:PPSA11111"));
        Assert.Equal(Rgb(0, 160, 0), scene.Pixel(960, 500));   // the all-apps picture instead of nothing
        int tried = attempts;   // the tile's own art and the picture each tried the file once
        Assert.Equal(2, tried);
        scene.Press(Left);
        scene.Press(Right);
        scene.Frames(60);
        Assert.Equal(tried, attempts);   // and it is not tried again
        Assert.Equal(Rgb(0, 160, 0), scene.Pixel(960, 500));
    }

    [Fact]
    public void Without_an_all_apps_picture_a_failed_app_picture_falls_back_to_the_cover()
    {
        (ThemePackManifest manifest, byte[] archive) = Pictures(allApps: false);
        using var scene = new Scene(manifest, archive, bytes => bytes.Length > 2 && bytes[0] == 0xFF ? throw new InvalidDataException() : Decode(bytes));
        scene.Open();
        for (int i = 0; i < 5; i++) scene.Press(Right);

        scene.Frames(60);

        Assert.Equal(CoverColour, scene.Pixel(960, 500));
        Assert.True(scene.Renderer.Preview.HasFailed("app:PPSA11111"));
    }

    [Fact]
    public void A_pack_picture_listed_but_missing_from_the_archive_falls_back_too()
    {
        ThemePackManifest manifest = Manifest(previews: Previews(apps: [("PPSA11111", "previews/gone.jpg")], allApps: "previews/all.jpg"));
        using var scene = new Scene(manifest, Archive(manifest, ("previews/all.jpg", FakeJpeg(64, 36, 0, 160, 0))));
        scene.Open();
        for (int i = 0; i < 5; i++) scene.Press(Right);

        scene.Frames(60);

        Assert.Equal(Rgb(0, 160, 0), scene.Pixel(960, 500));
    }

    [Fact]
    public void With_the_overlay_hidden_the_picture_is_bare_and_its_fade_goes_on()
    {
        (ThemePackManifest manifest, byte[] archive) = Pictures();
        using var scene = new Scene(manifest, archive);
        scene.Open();
        scene.Frames(70);
        uint withOverlay = scene.Pixel(1500, 2);
        scene.Press(UiInput.None with { Confirm = true });
        scene.Frames(5);

        Assert.Equal(CoverColour, scene.Pixel(1500, 2));       // no band over the top of it
        Assert.Equal(CoverColour, scene.Pixel(1500, 1075));
        Assert.NotEqual(withOverlay, scene.Pixel(1500, 2));
    }

    [Fact]
    public void Leaving_the_preview_lets_go_of_the_pictures_and_the_art_and_closes_the_sound()
    {
        (ThemePackManifest manifest, byte[] archive) = Pictures(icon: true);
        using var scene = new Scene(manifest, archive);
        scene.Open();
        for (int i = 0; i < 5; i++) scene.Press(Right);
        scene.Frames(70);
        Assert.False(scene.Renderer.Preview.IsEmpty);
        Assert.True(scene.Renderer.Preview.PictureCount >= 2);

        scene.Press(O);
        scene.Frames(2);   // a frame of the Library

        Assert.Equal(ThemeView.Library, scene.Browser.View);
        Assert.True(scene.Renderer.Preview.IsEmpty);
        Assert.False(scene.Controller.IsOpen);
        Assert.Equal(1, scene.Audio.Closed);
    }

    [Fact]
    public void A_preview_of_another_pack_opened_before_any_other_frame_does_not_start_with_the_first_ones_cover()
    {
        // Leaving a preview and opening one of another pack needs no frame in between for the pictures to be let go of.
        (ThemePackManifest manifest, byte[] archive) = Pictures();
        using var scene = new Scene(manifest, archive);
        scene.Open();
        scene.Frames(70);
        Assert.Equal(CoverColour, scene.Pixel(960, 500));
        scene.Press(O);
        Assert.True(scene.Renderer.Preview.IsEmpty);

        scene.Entries = [new ThemeLibraryEntry("other", Manifest(), new PackReport(), Png(64, 36, 10, 90, 200))];
        scene.Open();
        scene.Frames(70);

        Assert.Equal(Rgb(10, 90, 200), scene.Pixel(960, 500));
    }

    [Fact]
    public void The_row_slides_to_the_focused_tile_and_then_rests()
    {
        ThemePackManifest manifest = Manifest();
        using var scene = new Scene(manifest, Archive(manifest));
        scene.Open();
        scene.Frames(80);
        long rest = scene.Hash();

        scene.Press(Right);
        scene.Frames(1);
        long sliding = scene.Hash();
        scene.Frames(90);
        long settled = scene.Hash();
        scene.Frames(1);

        Assert.NotEqual(rest, sliding);
        Assert.NotEqual(rest, settled);
        Assert.NotEqual(sliding, settled);
        Assert.Equal(settled, scene.Hash());   // at rest it is a still picture
    }

    [Fact]
    public void A_row_longer_than_the_screen_scrolls_to_keep_the_focus_in_view()
    {
        // Twelve apps with their own background make the Games row 20 tiles long.
        var apps = Enumerable.Range(0, 12).Select(i => ($"PPSA{30000 + i}", $"previews/{i}.jpg"));
        ThemePackManifest manifest = Manifest(previews: Previews(apps: apps));
        using var scene = new Scene(manifest, Archive(manifest));
        scene.Open();
        scene.Frames(80);
        long start = scene.Hash();
        for (int i = 0; i < 19; i++) scene.Press(Right);
        scene.Frames(120);

        Assert.Equal(19, scene.Browser.PreviewFocus);
        Assert.NotEqual(start, scene.Hash());
        // The focused (last) tile is the first of the text under the row: its name is on screen, between the margins.
        bool anyLight = false;
        for (int x = Theme.SafeX; x < Theme.ContentRight; x++) anyLight |= (scene.Pixel(x, 312) & 0xFFFFFF) > 0xC0C0C0;
        Assert.True(anyLight);
    }

    // ---- The control centre -----------------------------------------------------------------------------------------

    private static int DiscCentreX(int button) => Theme.SafeX + button * ThemeRenderer.CcPitch + ThemeRenderer.CcPitch / 2;
    private static int DiscCentreY() => ThemeRenderer.CcDiscY + ThemeRenderer.CcDisc / 2;

    [Fact]
    public void The_control_centre_draws_the_packs_renders_and_a_stand_in_for_each_button_without_one()
    {
        ThemePackManifest manifest = Manifest(previews: Previews(icons: [("iconid_home", "previews/home.png")]));
        using var scene = new Scene(manifest, Archive(manifest, ("previews/home.png", Png(128, 128, 255, 255, 255))));
        scene.Open();
        scene.Press(UiInput.None, triangle: true);
        scene.Frames(30);

        Assert.Equal(PreviewScene.ControlCentre, scene.Browser.PreviewScene);
        // The pack's white render, 64 px in the middle of the first disc.
        Assert.Equal(Rgb(255, 255, 255), scene.Pixel(DiscCentreX(0), DiscCentreY()));
        Assert.Equal(Rgb(255, 255, 255), scene.Pixel(DiscCentreX(0) - 30, DiscCentreY() - 30));
        // The other eleven have strokes of the theme's white of their own in their discs (the stand-ins) and the disc around them.
        for (int i = 1; i < 12; i++)
        {
            bool white = false;
            for (int dy = -26; dy <= 26 && !white; dy++)
                for (int dx = -26; dx <= 26; dx++)
                    if (scene.Pixel(DiscCentreX(i) + dx, DiscCentreY() + dy) == Theme.White.Value) { white = true; break; }
            Assert.True(white, $"button {i} has no stand-in drawn");
            Assert.NotEqual(CoverColour, scene.Pixel(DiscCentreX(i), DiscCentreY() + 40));   // the glass disc is over the picture
        }
    }

    [Fact]
    public void The_focus_ring_follows_the_focused_button_in_the_control_centre()
    {
        ThemePackManifest manifest = Manifest();
        using var scene = new Scene(manifest, Archive(manifest));
        scene.Open();
        scene.Press(UiInput.None, triangle: true);
        scene.Frames(5);
        // The ring is white, 3 px wide, its outer edge 6 px outside the disc.
        int ringY = DiscCentreY(), ringOffset = ThemeRenderer.CcDisc / 2 + 5;
        Assert.Equal(Theme.White.Value, scene.Pixel(DiscCentreX(0) - ringOffset, ringY));
        Assert.NotEqual(Theme.White.Value, scene.Pixel(DiscCentreX(3) - ringOffset, ringY));

        for (int i = 0; i < 3; i++) scene.Press(Right);
        scene.Frames(5);

        Assert.NotEqual(Theme.White.Value, scene.Pixel(DiscCentreX(0) - ringOffset, ringY));
        Assert.Equal(Theme.White.Value, scene.Pixel(DiscCentreX(3) - ringOffset, ringY));
    }

    // ---- The footer and the words -----------------------------------------------------------------------------------

    [Fact]
    public void The_footer_has_at_most_four_hints_and_the_music_only_when_the_pack_has_some()
    {
        IReadOnlyList<HintItem[]> sets = ThemeRenderer.AllPreviewHints;

        Assert.Equal(6, sets.Count);
        Assert.All(sets, hints => Assert.InRange(hints.Length, 3, 4));
        Assert.All(sets, hints => Assert.Equal(PadButton.Cross, hints[0].Button));
        Assert.All(sets, hints => Assert.Equal(PadButton.Circle, hints[^1].Button));
        Assert.Equal(3, sets[0].Length);                       // home, no music in the pack
        Assert.DoesNotContain(sets[0], h => h.Button == PadButton.Square);
        Assert.Equal("Music", sets[1].Single(h => h.Button == PadButton.Square).Label);
        Assert.Equal("Stop music", sets[2].Single(h => h.Button == PadButton.Square).Label);
        Assert.Equal("Control centre", sets[1].Single(h => h.Button == PadButton.Triangle).Label);
        Assert.Equal("Home screen", sets[4].Single(h => h.Button == PadButton.Triangle).Label);
    }

    [Fact]
    public void The_footer_shows_the_music_hint_only_for_a_pack_with_music()
    {
        ThemePackManifest silent = Manifest(), loud = Manifest(previews: Previews(music: "previews/music.mp3"));
        using var quiet = new Scene(silent, Archive(silent));
        using var music = new Scene(loud, Archive(loud, ("previews/music.mp3", [1, 2, 3])));
        quiet.Open();
        music.Open();
        quiet.Frames(70);
        music.Frames(70);

        // The hint bar is right-aligned: with one hint more it starts further left.
        int FirstInk(Scene s)
        {
            for (int x = 900; x < Theme.ContentRight; x++)
                for (int y = 990; y < 1020; y++)
                    if (s.Pixel(x, y) != s.Pixel(900, y)) return x;
            return -1;
        }
        Assert.True(FirstInk(music) < FirstInk(quiet) - 100);
    }

    // ---- The controller ---------------------------------------------------------------------------------------------

    private static (ThemePackManifest Manifest, byte[] Archive) WithSounds()
    {
        ThemePackManifest manifest = Manifest(
            [Target("rnps/NPXS40047/appdb/default/icon0.png", "assets/store.png"),
             Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/move.vag", "vag", "psfx_focus_move"),
             Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/enter.vag", "vag", "psfx_enter"),
             Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/cancel.vag", "vag", "psfx_cancel")],
            Previews(music: "previews/music.mp3"));
        return (manifest, Archive(manifest, ("assets/store.png", Png(128, 128, 1, 2, 3)), ("sounds/move.vag", [1]), ("sounds/enter.vag", [2]),
            ("sounds/cancel.vag", [3]), ("previews/music.mp3", [9, 9, 9])));
    }

    [Fact]
    public void Opening_reads_the_pack_on_a_worker_and_hands_the_result_over_on_the_frame_thread()
    {
        (ThemePackManifest manifest, byte[] archive) = WithSounds();
        using var scene = new Scene(manifest, archive);

        scene.Press(UiInput.None, square: true);

        Assert.True(scene.Controller.IsOpen);
        Assert.Equal(2, scene.Worker.Pending);                    // nothing was read on this thread (the pack, and the smooth cover)
        Assert.True(scene.Renderer.Preview.IsEmpty);
        Assert.Equal(["clips 0"], scene.Audio.Calls);             // the earlier theme's sounds are taken away at once
        scene.Worker.RunAll();
        Assert.NotNull(scene.Renderer.Preview.Tile("NPXS40047"));
        Assert.Equal(["clips 0", "clips 3"], scene.Audio.Calls);
        Assert.Equal(new byte[] { 1 }, scene.Audio.Clips[PreviewSound.Move]);
        Assert.Equal(new byte[] { 2 }, scene.Audio.Clips[PreviewSound.Confirm]);
        Assert.Equal(new byte[] { 3 }, scene.Audio.Clips[PreviewSound.Back]);
        Assert.False(scene.Audio.Clips.ContainsKey(PreviewSound.End));   // the pack has no end-of-list sound: that one stays silent
    }

    [Fact]
    public void A_result_that_comes_after_the_preview_was_left_is_dropped_and_freed()
    {
        (ThemePackManifest manifest, byte[] archive) = WithSounds();
        using var scene = new Scene(manifest, archive);
        scene.Press(UiInput.None, square: true);
        Action[] late = scene.Worker.WorkOnly();   // the worker is done reading, its answer has not arrived

        scene.Press(O);
        Assert.False(scene.Controller.IsOpen);
        foreach (Action done in late) done();

        Assert.True(scene.Renderer.Preview.IsEmpty);                       // the art was not taken over (it was freed)
        Assert.DoesNotContain("clips 3", scene.Audio.Calls);               // and the sounds were not either
        // Opening again starts over, with its own answer.
        scene.Press(UiInput.None, square: true);
        scene.Worker.RunAll();
        Assert.NotNull(scene.Renderer.Preview.Tile("NPXS40047"));
    }

    [Fact]
    public void A_result_for_an_earlier_pack_does_not_land_in_the_next_one()
    {
        (ThemePackManifest manifest, byte[] archive) = WithSounds();
        using var scene = new Scene(manifest, archive);
        scene.Press(UiInput.None, square: true);
        Action[] first = scene.Worker.WorkOnly();
        scene.Press(O);
        // A rescan made a new entry for the same pack while the first preview's answer was on its way.
        scene.Entries = [scene.Entries[0] with { Manifest = manifest with { Name = "Again" } }];
        scene.Press(UiInput.None, square: true);
        Action[] second = scene.Worker.WorkOnly();

        foreach (Action done in first) done();
        Assert.True(scene.Renderer.Preview.IsEmpty);
        foreach (Action done in second) done();
        Assert.NotNull(scene.Renderer.Preview.Tile("NPXS40047"));
    }

    [Fact]
    public void The_cover_is_stretched_at_once_and_then_replaced_by_a_smooth_one_made_on_the_worker()
    {
        (ThemePackManifest manifest, byte[] archive) = Pictures();
        byte[]? cover = null;
        // The cover decodes to columns of black and white: a nearest-neighbour stretch keeps only those two colours, a
        // bilinear one mixes them at every edge.
        PixelBuffer Stripes(byte[] bytes)
        {
            if (!ReferenceEquals(bytes, cover)) return Decode(bytes);
            var stripes = new PixelBuffer(64, 36);
            for (int x = 0; x < 64; x++)
                Paint.FillRect(stripes.AsSurface(), x, 0, 1, 36, x % 2 == 0 ? Color.FromRgb(0, 0, 0) : Color.FromRgb(255, 255, 255));
            return stripes;
        }
        using var scene = new Scene(manifest, archive, Stripes);
        cover = scene.Entries[0].Preview;
        static int Colours(PreviewPicture picture)
        {
            var seen = new HashSet<uint>();
            for (int x = 0; x < 1920; x++) seen.Add(PixelAt(picture.Full, x, 540));
            return seen.Count;
        }

        scene.Press(UiInput.None, square: true);
        scene.Renderer.Draw(scene.Canvas.AsSurface(), scene.Browser, scene.Entries, scene.Time, 1.0 / 60);   // the worker has not answered
        PreviewPicture quick = scene.Renderer.Preview.Picture(PreviewModel.CoverKey)!;
        Assert.Equal(2, Colours(quick));

        scene.Worker.RunAll();
        PreviewPicture smooth = scene.Renderer.Preview.Picture(PreviewModel.CoverKey)!;
        Assert.NotSame(quick, smooth);
        Assert.True(Colours(smooth) > 2);
        scene.Frames(2);   // and it is the one drawn from now on
        Assert.Same(smooth, scene.Renderer.Preview.Picture(PreviewModel.CoverKey));
    }

    [Fact]
    public void A_rescan_that_replaces_the_entry_closes_the_preview_and_opens_it_again()
    {
        (ThemePackManifest manifest, byte[] archive) = WithSounds();
        using var scene = new Scene(manifest, archive);
        scene.Open();
        Assert.Equal(0, scene.Audio.Closed);

        scene.Entries = [scene.Entries[0] with { }];   // the library was scanned again: a new object for the same pack
        scene.Controller.Update(scene.Entries);

        Assert.Equal(1, scene.Audio.Closed);
        Assert.Equal(2, scene.Worker.Pending);   // the pack again, and its smooth cover
        scene.Worker.RunAll();
        Assert.True(scene.Controller.IsOpen);
    }

    [Fact]
    public void The_sounds_follow_the_buttons_and_the_back_clip_of_leaving_is_played_before_the_sound_closes()
    {
        (ThemePackManifest manifest, byte[] archive) = WithSounds();
        using var scene = new Scene(manifest, archive);
        scene.Open();
        scene.Audio.Calls.Clear();

        scene.Press(Right);
        scene.Press(UiInput.None with { Confirm = true });
        scene.Press(UiInput.None with { Confirm = true });
        scene.Press(O);

        Assert.Equal(["play Move", "play Confirm", "play Confirm", "play Back", "close"], scene.Audio.Calls);
        Assert.False(scene.Controller.IsOpen);
        // Outside the preview nothing is asked for any more.
        scene.Audio.Calls.Clear();
        scene.Press(Right);
        scene.Press(O);
        Assert.Empty(scene.Audio.Calls);
    }

    [Fact]
    public void Square_starts_and_stops_the_music_of_a_pack_that_has_some()
    {
        (ThemePackManifest manifest, byte[] archive) = WithSounds();
        using var scene = new Scene(manifest, archive);
        scene.Open();

        scene.Press(UiInput.None, square: true);
        Assert.Equal(new byte[] { 9, 9, 9 }, scene.Audio.MusicStarted);
        Assert.True(scene.Renderer.PreviewMusicPlaying);
        Assert.True(scene.Browser.View == ThemeView.Preview);

        scene.Press(UiInput.None, square: true);
        Assert.False(scene.Renderer.PreviewMusicPlaying);
        Assert.Equal(["music True", "music False"], scene.Audio.Calls.Where(c => c.StartsWith("music")).ToArray());

        // Leaving stops it and the hint goes back to "Music".
        scene.Press(UiInput.None, square: true);
        scene.Press(O);
        Assert.False(scene.Renderer.PreviewMusicPlaying);
        Assert.False(scene.Audio.MusicPlaying);
    }

    [Fact]
    public void A_pack_without_music_or_still_being_read_has_no_music_to_start()
    {
        ThemePackManifest manifest = Manifest();
        using var quiet = new Scene(manifest, Archive(manifest));
        quiet.Open();
        quiet.Press(UiInput.None, square: true);
        Assert.DoesNotContain(quiet.Audio.Calls, c => c.StartsWith("music"));

        (ThemePackManifest withMusic, byte[] archive) = WithSounds();
        using var early = new Scene(withMusic, archive);
        early.Press(UiInput.None, square: true);   // opened, but the worker has not answered
        early.Press(UiInput.None, square: true);
        Assert.DoesNotContain(early.Audio.Calls, c => c.StartsWith("music"));
        Assert.False(early.Renderer.PreviewMusicPlaying);
    }

    private const string HomeMusic = "vsh_asset/bgm_home.at9";

    // A pack whose only music is the home music (an ATRAC9 file as the payload of the bgm_home.at9 target), no MP3 excerpt.
    private static (ThemePackManifest Manifest, byte[] Archive) WithHomeMusicOnly()
    {
        ThemePackManifest manifest = Manifest([Target(HomeMusic, "music/bgm_home.at9", "sound")]);
        return (manifest, Archive(manifest, ("music/bgm_home.at9", [7, 7, 7, 7])));
    }

    [Fact]
    public void Square_plays_the_home_music_of_a_pack_that_has_no_mp3_excerpt()
    {
        (ThemePackManifest manifest, byte[] archive) = WithHomeMusicOnly();
        using var scene = new Scene(manifest, archive);
        scene.Open();

        scene.Press(UiInput.None, square: true);
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, scene.Audio.MusicStarted);
        Assert.True(scene.Renderer.PreviewMusicPlaying);

        scene.Press(UiInput.None, square: true);
        Assert.False(scene.Renderer.PreviewMusicPlaying);
        Assert.Equal(["music at9 True", "music at9 False"], scene.Audio.Calls.Where(c => c.StartsWith("music")).ToArray());
    }

    [Fact]
    public void The_mp3_excerpt_is_played_instead_of_the_home_music_when_a_pack_has_both()
    {
        ThemePackManifest manifest = Manifest([Target(HomeMusic, "music/bgm_home.at9", "sound")], Previews(music: "previews/music.mp3"));
        using var scene = new Scene(manifest, Archive(manifest, ("music/bgm_home.at9", [7, 7, 7, 7]), ("previews/music.mp3", [9, 9, 9])));
        scene.Open();

        scene.Press(UiInput.None, square: true);

        Assert.Equal(new byte[] { 9, 9, 9 }, scene.Audio.MusicStarted);
        Assert.Equal(["music True"], scene.Audio.Calls.Where(c => c.StartsWith("music")).ToArray());
    }

    [Fact]
    public void The_footer_shows_the_music_hint_for_a_pack_with_only_the_home_music()
    {
        ThemePackManifest silent = Manifest();
        (ThemePackManifest home, byte[] homeArchive) = WithHomeMusicOnly();
        using var quiet = new Scene(silent, Archive(silent));
        using var music = new Scene(home, homeArchive);
        quiet.Open();
        music.Open();
        quiet.Frames(70);
        music.Frames(70);

        int FirstInk(Scene s)
        {
            for (int x = 900; x < Theme.ContentRight; x++)
                for (int y = 990; y < 1020; y++)
                    if (s.Pixel(x, y) != s.Pixel(900, y)) return x;
            return -1;
        }
        Assert.True(FirstInk(music) < FirstInk(quiet) - 100);
    }

    [Fact]
    public void A_pack_that_cannot_be_read_leaves_the_preview_with_its_stand_ins_and_nothing_throws()
    {
        (ThemePackManifest manifest, byte[] _) = Pictures(icon: true);
        using var scene = new Scene(manifest, [], readPack: _ => throw new IOException("gone"));

        scene.Open();
        for (int i = 0; i < 6; i++) scene.Press(Right);
        scene.Frames(80);

        Assert.True(scene.Controller.IsOpen);
        Assert.Equal(CoverColour, scene.Pixel(960, 500));   // pictures that cannot be had: the cover
        Assert.True(scene.Renderer.Preview.HasFailed("app:PPSA11111") || scene.Renderer.Preview.HasFailed("allapps"));
        Assert.Equal(PreviewScene.Home, scene.Browser.PreviewScene);
    }

    [Fact]
    public void Pictures_are_asked_for_one_at_a_time()
    {
        (ThemePackManifest manifest, byte[] archive) = Pictures();
        using var scene = new Scene(manifest, archive);
        scene.Open();
        scene.Frames(3);
        for (int i = 0; i < 5; i++) scene.Press(Right);
        scene.Renderer.Draw(scene.Canvas.AsSurface(), scene.Browser, scene.Entries, scene.Time, 1.0 / 60);   // asks for the app's picture

        scene.Controller.Update(scene.Entries);
        Assert.Equal(1, scene.Worker.Pending);
        scene.Controller.Update(scene.Entries);   // another frame while that one is being made: no second job
        scene.Press(Right);
        Assert.Equal(1, scene.Worker.Pending);

        scene.Worker.RunAll();
        scene.Frames(2);
        Assert.True(scene.Renderer.Preview.HasPicture("app:PPSA11111"));
    }

    [Fact]
    public void The_layout_is_known_to_the_browser_before_the_first_press_in_the_preview()
    {
        ThemePackManifest manifest = Manifest();
        using var scene = new Scene(manifest, Archive(manifest));
        scene.Controller.BeforeUpdate(scene.Entries);
        scene.Browser.Update(UiInput.None, preview: true);

        // The very next press moves: the rows' lengths came from the model, not from a default of one tile.
        scene.Controller.BeforeUpdate(scene.Entries);
        scene.Browser.Update(Right);
        Assert.Equal(1, scene.Browser.PreviewFocus);
        PreviewModel model = scene.Renderer.PreviewModelFor(scene.Entries[0]);
        for (int i = 0; i < 20; i++)
        {
            scene.Controller.BeforeUpdate(scene.Entries);
            scene.Browser.Update(Right);
        }
        Assert.Equal(model.Games.Count - 1, scene.Browser.PreviewFocus);
    }

    [Fact]
    public void The_model_of_a_pack_is_made_once_for_its_manifest()
    {
        using var renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), Decode);
        var entry = new ThemeLibraryEntry("p", Manifest(), new PackReport(), null);

        PreviewModel first = renderer.PreviewModelFor(entry);

        Assert.Same(first, renderer.PreviewModelFor(entry));
        Assert.Same(first, renderer.PreviewModelFor(entry with { Path = "elsewhere" }));       // the same manifest object
        Assert.NotSame(first, renderer.PreviewModelFor(entry with { Manifest = Manifest() }));  // a rescan's new manifest
    }
}
