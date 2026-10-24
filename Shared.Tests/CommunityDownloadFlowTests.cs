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
/// The two things a store card can do besides download: X asks "Download", "Download and apply" or "Cancel" (and the second goes
/// on to the Detail's own apply question once the pack is in the library), and Square previews the theme without keeping it.
/// </summary>
public class CommunityDownloadFlowTests
{
    private static readonly UiInput X = UiInput.None with { Confirm = true }, O = UiInput.None with { Cancel = true },
        Up = UiInput.None with { Up = true }, Down = UiInput.None with { Down = true }, Right = UiInput.None with { Right = true },
        L1 = UiInput.None with { PageUp = true }, R1 = UiInput.None with { PageDown = true };

    private static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)(r << 16 | g << 8 | b);

    private static ThemeBrowser OnCommunity(int cards = 6, int library = 0)
    {
        var browser = new ThemeBrowser();
        browser.SetCount(library);
        browser.Update(R1);
        Assert.Equal(ThemeView.Community, browser.View);
        browser.TakeAction();   // the first visit asks for the list
        browser.SetCommunityCount(cards);
        return browser;
    }

    // ---- The download question ---------------------------------------------------------------------------------------

    [Fact]
    public void X_on_a_card_opens_the_question_and_that_same_x_does_not_answer_it()
    {
        ThemeBrowser browser = OnCommunity();
        browser.Update(X);
        Assert.Equal(DialogKind.CommunityDownload, browser.Dialog);
        Assert.Equal(["Download", "Download and apply", "Cancel"], browser.DialogOptions);
        Assert.Equal(0, browser.DialogFocus);                       // plain download is one more X
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        // X together with other buttons on the same frame opens it and nothing else.
        browser.Update(O);
        Assert.Equal(DialogKind.None, browser.Dialog);
        browser.Update(X, preview: true, triangle: true, options: true);
        Assert.Equal(DialogKind.CommunityDownload, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Each_option_asks_for_its_own_action_and_cancel_or_circle_ask_for_none()
    {
        ThemeBrowser browser = OnCommunity();
        browser.Update(X);
        browser.Update(X);
        Assert.Equal(ThemeAction.CommunityDownload, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(X);
        browser.Update(Down);
        browser.Update(X);
        Assert.Equal(ThemeAction.CommunityDownloadApply, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.Community, browser.View);            // the pack must be in the library first: the app takes it from here

        browser.Update(X);
        browser.Update(Down);
        browser.Update(Down);
        browser.Update(Down);                                       // stops at Cancel
        Assert.Equal(2, browser.DialogFocus);
        browser.Update(X);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(X);
        browser.Update(O);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void While_the_question_is_open_every_other_button_is_ignored()
    {
        ThemeBrowser browser = OnCommunity();
        browser.Update(X);
        int selected = browser.CommunitySelected;
        browser.Update(R1, preview: true, triangle: true, options: true);
        Assert.Equal(DialogKind.CommunityDownload, browser.Dialog);
        Assert.Equal(ThemeView.Community, browser.View);
        Assert.Equal(selected, browser.CommunitySelected);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void An_update_says_update_and_a_download_waits_for_the_store_even_while_the_app_is_busy()
    {
        ThemeBrowser browser = OnCommunity();
        browser.Update(X, communityUpdate: true);
        Assert.Equal(["Update", "Update and apply", "Cancel"], browser.DialogOptions);
        browser.Update(X, busy: true);
        // A download is not a console change: the store queue holds it behind the card pictures, so it is not turned into "Please wait".
        Assert.Equal(ThemeAction.CommunityDownload, browser.TakeAction());

        browser.Update(X, communityUpdate: false);
        Assert.Equal(["Download", "Download and apply", "Cancel"], browser.DialogOptions);
    }

    [Fact]
    public void A_new_list_closes_the_question_because_the_card_under_it_may_be_another_theme()
    {
        ThemeBrowser browser = OnCommunity();
        browser.Update(X);
        browser.SetCommunityCount(6);
        Assert.Equal(DialogKind.None, browser.Dialog);

        // A library scan is about packs, not store cards: the question stays.
        browser.Update(X);
        browser.SetCount(3);
        Assert.Equal(DialogKind.CommunityDownload, browser.Dialog);
    }

    // ---- Square ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Square_previews_the_card_and_only_sorts_on_the_chips_or_with_no_cards()
    {
        ThemeBrowser browser = OnCommunity();
        browser.Update(Right);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeAction.CommunityPreview, browser.TakeAction());
        Assert.Equal(0, browser.CommunitySort);
        Assert.Equal(1, browser.CommunitySelected);                 // and the cursor stays on the card

        browser.Update(Up);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());
        Assert.Equal(1, browser.CommunitySort);

        ThemeBrowser empty = OnCommunity(0);
        empty.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeAction.CommunityRefresh, empty.TakeAction());
        Assert.Equal(1, empty.CommunitySort);
    }

    [Fact]
    public void The_preview_of_a_card_opens_only_while_the_user_is_still_on_the_store_with_no_question_open()
    {
        ThemeBrowser browser = OnCommunity();
        Assert.True(browser.OpenCommunityPreview());
        Assert.Equal(ThemeView.Preview, browser.View);
        Assert.True(browser.PreviewOfStore);
        browser.Update(O);
        Assert.Equal(ThemeView.Community, browser.View);            // back to the card, not to the library
        Assert.False(browser.PreviewOfStore);
        Assert.Equal(PreviewSound.Back, browser.TakePreviewSound());

        browser.Update(X);
        Assert.False(browser.OpenCommunityPreview());               // a question is open now
        Assert.Equal(ThemeView.Community, browser.View);
        browser.Update(O);

        browser.Update(R1);                                         // left for USB Import while the pack was on its way
        Assert.False(browser.OpenCommunityPreview());
        Assert.Equal(ThemeView.Import, browser.View);
    }

    [Fact]
    public void In_the_store_preview_square_is_the_music_x_hides_the_overlay_and_an_empty_library_does_not_end_it()
    {
        ThemeBrowser browser = OnCommunity(6, library: 0);
        browser.OpenCommunityPreview();
        browser.SetPreviewLayout(5, 3);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeAction.PreviewMusic, browser.TakeAction());
        browser.Update(X);
        Assert.False(browser.ShowOverlay);
        browser.Update(X);
        browser.Update(Right);
        Assert.Equal(1, browser.PreviewFocus);
        // The library is empty (SetCount(0) sends a library preview back to the Library); a store preview is not of the library.
        browser.SetCount(0);
        Assert.Equal(ThemeView.Preview, browser.View);
        browser.Update(O);
        Assert.Equal(ThemeView.Community, browser.View);
    }

    [Fact]
    public void A_library_preview_after_a_store_preview_goes_back_to_the_library()
    {
        ThemeBrowser browser = OnCommunity(6, library: 2);
        browser.OpenCommunityPreview();
        browser.Update(O);
        browser.Update(L1);
        Assert.Equal(ThemeView.Library, browser.View);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Preview, browser.View);
        Assert.False(browser.PreviewOfStore);
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
    }

    // ---- Download and apply: the hand-over -----------------------------------------------------------------------------

    // What the Detail screen's X opens for a pack with these parts, to compare the hand-over with.
    private static (DialogKind Kind, List<string> Options, int Focus, ThemePart Ticked) FromDetail(ThemePart parts, bool asksScope, bool sounds = true)
    {
        var browser = new ThemeBrowser();
        browser.SetCount(2);
        browser.Select(1);
        browser.Update(X);                                          // Library -> Detail
        browser.Update(X, parts: parts, asksScope: asksScope, sounds: sounds);
        return (browser.Dialog, browser.DialogOptions.ToList(), browser.DialogFocus, browser.ApplyParts);
    }

    private static ThemeBrowser AfterDownload(int selected = 1)
    {
        ThemeBrowser browser = OnCommunity(6, library: 3);
        browser.Select(selected);                                   // what the app does with the scan's result
        return browser;
    }

    [Theory]
    [InlineData(ThemePart.Background, false)]                       // one part: the plain question
    [InlineData(ThemePart.AppBackgrounds, true)]                    // one part with an all-apps background: where does it go
    [InlineData(ThemePart.Background | ThemePart.Icons | ThemePart.Sounds, false)]   // several parts: tick them
    [InlineData(ThemePart.All, true)]
    public void Download_and_apply_asks_exactly_what_X_on_the_Detail_asks(ThemePart parts, bool asksScope)
    {
        var expected = FromDetail(parts, asksScope);
        ThemeBrowser browser = AfterDownload();
        Assert.True(browser.ApplyAfterDownload());
        Assert.Equal(ThemeView.Detail, browser.View);
        Assert.Equal(1, browser.Selected);
        Assert.Equal(DialogKind.None, browser.Dialog);              // the question opens on the next frame, with the pack's parts
        browser.Update(UiInput.None, parts: parts, asksScope: asksScope);
        Assert.Equal(expected.Kind, browser.Dialog);
        Assert.Equal(expected.Options, browser.DialogOptions.ToList());
        Assert.Equal(expected.Focus, browser.DialogFocus);
        Assert.Equal(expected.Ticked, browser.ApplyParts);
        Assert.Equal(ThemeAction.None, browser.TakeAction());       // nothing applies before it is confirmed
    }

    [Fact]
    public void Sounds_that_are_switched_off_start_unticked_here_too()
    {
        var expected = FromDetail(ThemePart.All, false, sounds: false);
        ThemeBrowser browser = AfterDownload();
        browser.ApplyAfterDownload();
        browser.Update(UiInput.None, parts: ThemePart.All, sounds: false);
        Assert.Equal(expected.Ticked, browser.ApplyParts);
        Assert.Equal(ThemePart.All & ~ThemePart.Sounds, browser.ApplyParts);
    }

    [Fact]
    public void The_apply_question_can_still_be_cancelled_and_confirming_it_is_a_separate_x()
    {
        ThemeBrowser browser = AfterDownload();
        browser.ApplyAfterDownload();
        browser.Update(UiInput.None, parts: ThemePart.Background);
        Assert.Equal(DialogKind.Apply, browser.Dialog);
        browser.Update(O);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.Detail, browser.View);               // the pack's Detail, one Circle from the Library
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);

        // And confirmed: the same apply the Detail starts.
        browser = AfterDownload();
        browser.ApplyAfterDownload();
        browser.Update(UiInput.None, parts: ThemePart.Background);
        browser.Update(X);
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
    }

    [Fact]
    public void While_the_app_is_busy_no_apply_question_opens()
    {
        ThemeBrowser browser = AfterDownload();
        browser.ApplyAfterDownload();
        browser.Update(UiInput.None, parts: ThemePart.Background, busy: true);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());       // the app says "Please wait"; X on the Detail asks again
        browser.Update(X, parts: ThemePart.Background);
        Assert.Equal(DialogKind.Apply, browser.Dialog);
    }

    [Fact]
    public void The_hand_over_needs_the_user_to_still_be_waiting_on_the_store()
    {
        ThemeBrowser away = AfterDownload();
        away.Update(R1);                                            // Import
        Assert.False(away.ApplyAfterDownload());
        Assert.Equal(ThemeView.Import, away.View);

        ThemeBrowser asking = AfterDownload();
        asking.Update(X);                                           // a download question for another card is open
        Assert.False(asking.ApplyAfterDownload());
        Assert.Equal(DialogKind.CommunityDownload, asking.Dialog);

        ThemeBrowser none = OnCommunity(6, library: 0);             // no pack selected
        Assert.False(none.ApplyAfterDownload());
    }

    [Fact]
    public void A_dialog_that_opens_in_between_keeps_the_apply_question_waiting_instead_of_replacing_it()
    {
        ThemeBrowser browser = AfterDownload();
        browser.ApplyAfterDownload();
        browser.AskRestartShell();                                  // a job finished and offers the restart first
        browser.Update(UiInput.None, parts: ThemePart.Background);
        Assert.Equal(DialogKind.RestartShell, browser.Dialog);
        browser.Update(O);
        browser.Update(UiInput.None, parts: ThemePart.Background);
        Assert.Equal(DialogKind.Apply, browser.Dialog);
    }

    // ---- The preview as the app drives it ----------------------------------------------------------------------------

    private static (ThemePackManifest Manifest, byte[] Archive) WithMusicAndSounds()
    {
        ThemePackManifest manifest = Manifest(
            [Target("rnps/NPXS40047/appdb/default/icon0.png", "assets/store.png"),
             Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "sounds/move.vag", "vag", "psfx_focus_move")],
            Previews(apps: [("PPSA11111", "previews/a.jpg"), ("PPSA22222", "previews/b.jpg")], music: "previews/music.mp3"));
        return (manifest, Archive(manifest, ("assets/store.png", Png(128, 128, 1, 2, 3)), ("sounds/move.vag", [1]),
            ("previews/a.jpg", FakeJpeg(64, 36, 0, 0, 200)), ("previews/b.jpg", FakeJpeg(64, 36, 0, 160, 0)), ("previews/music.mp3", [9, 9, 9])));
    }

    private sealed class StoreScene : IDisposable
    {
        public readonly ThemeRenderer Renderer;
        public readonly PixelBuffer Canvas = new(1920, 1080);
        public readonly ThemeBrowser Browser = OnCommunity(6, library: 0);
        public readonly PreviewController Controller;
        public readonly RecordingAudio Audio = new();
        public readonly QueueWorker Worker = new();
        public IReadOnlyList<ThemeLibraryEntry> Library = [];
        public int PackReads;
        public double Time = 5;

        public StoreScene()
        {
            Renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), Decode);
            // A store theme is never read from the library's folder: any read through here is a bug.
            Controller = new PreviewController(Renderer, Browser, Audio, Decode, path => { PackReads++; throw new InvalidOperationException(path); }, Worker);
        }

        public ThemeAction Press(UiInput input, bool square = false)
        {
            Controller.BeforeUpdate(Library);
            Browser.Update(input, preview: square);
            ThemeAction action = Browser.TakeAction();
            if (action == ThemeAction.PreviewMusic) Controller.ToggleMusic();
            Controller.Update(Library);
            return action;
        }

        // What the app does when the store's answer is here.
        public bool Show(ThemeLibraryEntry entry, byte[] pack)
        {
            Controller.ShowStorePack(entry, pack);
            bool opened = Browser.OpenCommunityPreview();
            if (!opened) Controller.DropStorePack();
            Controller.Update(Library);
            return opened;
        }

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Renderer.Draw(Canvas.AsSurface(), Browser, Library, Time, 1.0 / 60);
                Time += 1.0 / 60;
                Controller.Update(Library);
                Worker.RunAll();
            }
        }

        public uint Pixel(int x, int y) => PixelAt(Canvas, x, y);

        public void Dispose()
        {
            Controller.Dispose();
            Renderer.Dispose();
            Canvas.Dispose();
        }
    }

    private static ThemeLibraryEntry StoreEntry(ThemePackManifest manifest, string id = "abc") =>
        new("store:" + id, manifest, new PackReport(), Png(64, 36, 120, 60, 30));

    [Fact]
    public void A_store_pack_is_previewed_from_memory_with_its_cover_sounds_and_music_and_let_go_on_leaving()
    {
        (ThemePackManifest manifest, byte[] archive) = WithMusicAndSounds();
        using var scene = new StoreScene();
        Assert.Equal(ThemeAction.CommunityPreview, scene.Press(UiInput.None, square: true));
        Assert.False(scene.Controller.IsOpen);                      // the pack is still on its way

        ThemeLibraryEntry entry = StoreEntry(manifest);
        Assert.True(scene.Show(entry, archive));
        Assert.True(scene.Controller.IsOpen);
        Assert.Equal(1, scene.Worker.Pending);                      // read off the frame thread
        scene.Worker.RunAll();
        scene.Frames(3);

        Assert.Equal(0, scene.PackReads);                           // never through the library's reader
        Assert.NotNull(scene.Renderer.Preview.Tile("NPXS40047"));   // the pack's own tile icon
        Assert.Equal(new byte[] { 1 }, scene.Audio.Clips[PreviewSound.Move]);
        Assert.Equal(Rgb(120, 60, 30), scene.Pixel(960, 500));      // the cover, behind the home row
        Assert.Same(entry, scene.Renderer.StorePreview);

        // Moving along the row uses the store pack's tiles: two apps with pictures of their own after the system tiles.
        int tiles = scene.Renderer.PreviewModelFor(entry).Games.Count;
        for (int i = 0; i < tiles + 3; i++) scene.Press(Right);
        Assert.Equal(tiles - 1, scene.Browser.PreviewFocus);

        scene.Press(UiInput.None, square: true);
        Assert.Equal(new byte[] { 9, 9, 9 }, scene.Audio.MusicStarted);
        Assert.True(scene.Renderer.PreviewMusicPlaying);

        scene.Press(O);                                             // Circle: back to the store, and everything is released
        Assert.Equal(ThemeView.Community, scene.Browser.View);
        Assert.False(scene.Controller.IsOpen);
        Assert.Null(scene.Renderer.StorePreview);
        Assert.False(scene.Audio.MusicPlaying);
        Assert.Equal(1, scene.Audio.Closed);
        scene.Frames(2);                                            // the store draws again
        Assert.True(scene.Renderer.Preview.IsEmpty);
        Assert.Equal(ThemeView.Community, scene.Browser.View);
    }

    [Fact]
    public void A_pack_in_the_library_does_not_decide_a_store_previews_rows_or_cover()
    {
        (ThemePackManifest manifest, byte[] archive) = WithMusicAndSounds();
        using var scene = new StoreScene();
        scene.Library = [new ThemeLibraryEntry("lib", Manifest(), new PackReport(), Png(64, 36, 10, 200, 10))];
        scene.Browser.SetCount(1);
        scene.Press(UiInput.None, square: true);
        ThemeLibraryEntry entry = StoreEntry(manifest);
        Assert.True(scene.Show(entry, archive));
        scene.Worker.RunAll();
        scene.Frames(3);
        Assert.Equal(Rgb(120, 60, 30), scene.Pixel(960, 500));      // the store pack's cover, not the library pack's green
        int storeTiles = scene.Renderer.PreviewModelFor(entry).Games.Count;
        int libraryTiles = scene.Renderer.PreviewModelFor(scene.Library[0]).Games.Count;
        Assert.True(storeTiles > libraryTiles);
        for (int i = 0; i < storeTiles + 2; i++) scene.Press(Right);
        Assert.Equal(storeTiles - 1, scene.Browser.PreviewFocus);
    }

    [Fact]
    public void A_pack_that_arrives_after_the_user_left_the_store_is_dropped_without_a_preview()
    {
        (ThemePackManifest manifest, byte[] archive) = WithMusicAndSounds();
        using var scene = new StoreScene();
        scene.Press(UiInput.None, square: true);
        scene.Press(R1);                                            // to USB Import while it loads
        Assert.False(scene.Show(StoreEntry(manifest), archive));
        Assert.False(scene.Controller.IsOpen);
        Assert.Null(scene.Renderer.StorePreview);
        Assert.Equal(0, scene.Worker.Pending);
        Assert.Equal(ThemeView.Import, scene.Browser.View);

        // Or while a question about another card is open.
        scene.Press(L1);
        scene.Press(X);
        Assert.Equal(DialogKind.CommunityDownload, scene.Browser.Dialog);
        Assert.False(scene.Show(StoreEntry(manifest, "def"), archive));
        Assert.Null(scene.Renderer.StorePreview);
        Assert.Equal(DialogKind.CommunityDownload, scene.Browser.Dialog);
    }

    [Fact]
    public void A_result_that_comes_after_the_store_preview_was_left_is_dropped_and_the_next_preview_starts_over()
    {
        (ThemePackManifest manifest, byte[] archive) = WithMusicAndSounds();
        using var scene = new StoreScene();
        scene.Show(StoreEntry(manifest), archive);
        Action[] late = scene.Worker.WorkOnly();                    // read, but not yet handed over
        scene.Press(O);
        Assert.Null(scene.Renderer.StorePreview);
        foreach (Action done in late) done();
        Assert.True(scene.Renderer.Preview.IsEmpty);                // not taken over
        Assert.DoesNotContain("clips 2", scene.Audio.Calls);

        Assert.True(scene.Show(StoreEntry(manifest, "def"), archive));
        scene.Worker.RunAll();
        Assert.NotNull(scene.Renderer.Preview.Tile("NPXS40047"));
    }
}
