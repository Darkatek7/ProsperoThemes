using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The System, USB Import, store-address keyboard and Home-preview screens: layout on the safe margins, what each screen
/// shows, and that a steady frame costs no allocations (the console's frame budget has no room for garbage).
/// </summary>
public class SystemScreenTests
{
    private static readonly UiInput X = new(false, false, false, false, true, false);
    private static readonly UiInput O = new(false, false, false, false, false, true);
    private static readonly UiInput Down = new(false, true, false, false, false, false);
    private static readonly ThemeLibraryEntry[] None = [];

    private static ThemeRenderer NewRenderer(Func<byte[], PixelBuffer>? decode = null) =>
        new(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), decode ?? (_ => new PixelBuffer(4, 4)));

    // A cover of one flat colour: whatever is drawn over it shows up as a change of colour.
    private static PixelBuffer FlatCover(byte[] _)
    {
        var cover = new PixelBuffer(16, 9);
        cover.Clear(Color.FromRgb(200, 150, 90));
        return cover;
    }

    private static ThemeLibraryEntry Pack(string path = "pack-a") =>
        ThemeLibraryEntry.Inspect(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "two-screen.p5theme")))
        with { Preview = new byte[] { 1 } };

    private static unsafe uint PixelAt(PixelBuffer canvas, int x, int y)
    {
        Surface s = canvas.AsSurface();
        return s.Pixels[y * s.Stride + x];
    }

    private static unsafe long Hash(PixelBuffer canvas)
    {
        Surface s = canvas.AsSurface();
        long hash = 17;
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++) hash = unchecked(hash * 31 + s.Pixels[y * s.Stride + x]);
        return hash;
    }

    // Two frames a second apart: the first frame of a view is still sliding in.
    private static long Settled(ThemeRenderer renderer, PixelBuffer canvas, ThemeBrowser browser, IReadOnlyList<ThemeLibraryEntry> entries, double at)
    {
        renderer.Draw(canvas.AsSurface(), browser, entries, at);
        renderer.Draw(canvas.AsSurface(), browser, entries, at + 1);
        return Hash(canvas);
    }

    private static void To(ThemeBrowser browser, ThemeView view)
    {
        int guard = 0;
        while (browser.View != view && guard++ < 8) browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(view, browser.View);
    }

    // The bounding box of every pixel that differs between two frames of the same size, or null when they are the same.
    private static unsafe (int X0, int Y0, int X1, int Y1)? DiffBox(uint[] before, PixelBuffer after)
    {
        Surface s = after.AsSurface();
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (s.Pixels[y * s.Stride + x] != before[y * s.Width + x])
                {
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                }
        return x1 < 0 ? null : (x0, y0, x1, y1);
    }

    private static unsafe uint[] Snapshot(PixelBuffer canvas)
    {
        Surface s = canvas.AsSurface();
        var copy = new uint[s.Width * s.Height];
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++) copy[y * s.Width + x] = s.Pixels[y * s.Stride + x];
        return copy;
    }

    // ---- Allocations ------------------------------------------------------------------------------------------------

    [Fact]
    public void Steady_frames_of_the_four_screens_allocate_nothing()
    {
        using var renderer = NewRenderer(FlatCover);
        using var canvas = new PixelBuffer(1920, 1080);
        ThemeLibraryEntry[] entries = [Pack()];
        var browser = new ThemeBrowser();
        browser.SetCount(entries.Length);
        renderer.LibraryStatus = "2 imported · 1 already in library · 3 skipped · a-pack.p5theme: Pack too large (48 MiB at most).";
        renderer.Firmware = "13.000.000";
        renderer.AppVersion = "01.000.005";
        browser.StoreUrl = "http://192.168.0.5:8080";

        // Paint.FocusRing builds a small table with a collection expression; on the JIT's first tier (a Debug test run)
        // that costs a few bytes per call, on the optimised JIT and on the console it costs nothing. Measure that here,
        // under the same conditions, so only what the screens themselves allocate is held against them.
        Surface scratch = canvas.AsSurface();
        for (int i = 0; i < 3; i++) Paint.FocusRing(scratch, 100, 100, 200, 80, 14);
        long ringBefore = GC.GetAllocatedBytesForCurrentThread();
        Paint.FocusRing(scratch, 100, 100, 200, 80, 14);
        long ring = GC.GetAllocatedBytesForCurrentThread() - ringBefore;

        void Steady(string name, ThemeBrowser b, int rings = 0)
        {
            // Warm up: the view slides in for about half a second, caches and sprites are made on first use, and in a full
            // test run the JIT promotes the drawing methods during the first few dozen frames.
            for (int i = 0; i < 40; i++) renderer.Draw(canvas.AsSurface(), b, entries, 5 + i * 0.1);
            var perFrame = new long[40];
            for (int i = 0; i < perFrame.Length; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                renderer.Draw(canvas.AsSurface(), b, entries, 10 + i * 0.4);   // the caret blinks on the way
                perFrame[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            // Some frames may carry the runtime's own work (tiered JIT stubs allocate on the calling thread, at moments that
            // depend on what the rest of the test run warmed up); garbage made by the screen would show in every frame, so
            // ten clean frames in a row prove the screen itself allocates nothing.
            int run = 0, best = 0;
            foreach (long bytes in perFrame) { run = bytes <= rings * ring ? run + 1 : 0; best = Math.Max(best, run); }
            Assert.True(best >= 10,
                $"{name}: bytes allocated per steady frame: {string.Join(", ", perFrame)} (a focus ring is {ring})");
        }

        To(browser, ThemeView.Import);
        Steady("import", browser);
        renderer.Busy = true;
        Steady("import busy", browser);
        renderer.Busy = false;
        To(browser, ThemeView.System);
        Steady("system", browser, rings: 1);
        browser.Update(Down);
        Steady("system, store address row", browser, rings: 1);
        browser.OpenStoreAddress("https://" + new string('x', 150) + ".example.de", "The address must start with http:// or https://.");
        Steady("keyboard, long text", browser, rings: 1);
        browser.OpenStoreAddress("");
        Steady("keyboard, empty", browser, rings: 1);

        // The download question over a store card: its name and line are made once, not per frame.
        renderer.Community = [new CommunityTheme("id0", "Theme 0", "Author 0", "1.0", "", 1, 10, 100, null, "http://x/0",
            Fallback: true, AllApps: true, PerApp: 1, Icons: 4, Tiles: 2)];
        var store = new ThemeBrowser();
        store.SetCommunityCount(1);
        To(store, ThemeView.Community);
        store.Update(UiInput.None with { Confirm = true });
        Assert.Equal(DialogKind.CommunityDownload, store.Dialog);
        Steady("community download question", store, rings: 1);

        var preview = new ThemeBrowser();
        preview.SetCount(1);
        preview.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Preview, preview.View);
        Steady("preview", preview, rings: 1);
        preview.Update(UiInput.None, triangle: true);
        Assert.Equal(PreviewScene.ControlCentre, preview.PreviewScene);
        Steady("preview, control centre", preview, rings: 1);
    }

    // ---- System -----------------------------------------------------------------------------------------------------

    [Fact]
    public void System_rows_are_equal_glass_rows_between_the_safe_margins_and_the_focused_one_is_lighter()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        To(browser, ThemeView.System);
        for (int row = 0; row < ThemeBrowser.SystemRowCount; row++)
        {
            Settled(renderer, canvas, browser, None, 5 + row * 3);
            for (int other = 0; other < ThemeBrowser.SystemRowCount; other++)
            {
                int mid = ThemeRenderer.SysRowsTop + other * (ThemeRenderer.SysRowH + ThemeRenderer.SysRowGap) + ThemeRenderer.SysRowH / 2;
                bool lit = PixelAt(canvas, Theme.SafeX + 6, mid) == ThemeRenderer.SysRowFocusFill.Value;
                Assert.Equal(other == row, lit);
                // The same fill ends exactly on the margins: the pixel just outside is not it, the one just inside is (focused row only).
                Assert.NotEqual(ThemeRenderer.SysRowFocusFill.Value, PixelAt(canvas, Theme.SafeX - 1, mid));
                Assert.NotEqual(ThemeRenderer.SysRowFocusFill.Value, PixelAt(canvas, Theme.ContentRight + 1, mid));
                if (other == row) Assert.Equal(ThemeRenderer.SysRowFocusFill.Value, PixelAt(canvas, Theme.ContentRight - 6, mid));
            }
            browser.Update(Down);
        }
    }

    [Fact]
    public void System_info_follows_the_library_count_the_firmware_and_the_app_version()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        To(browser, ThemeView.System);
        long plain = Settled(renderer, canvas, browser, None, 5);
        Assert.Equal(plain, Settled(renderer, canvas, browser, None, 20));   // nothing on this screen moves once it has slid in

        long withPacks = Settled(renderer, canvas, browser, [Pack("a"), Pack("b")], 10);
        Assert.NotEqual(plain, withPacks);
        renderer.Firmware = "13.000.000";
        long withFirmware = Settled(renderer, canvas, browser, None, 30);
        Assert.NotEqual(plain, withFirmware);
        renderer.AppVersion = "01.000.005";
        long withVersion = Settled(renderer, canvas, browser, None, 40);
        Assert.NotEqual(withFirmware, withVersion);
        renderer.AppVersion = "";
        Assert.Equal(withFirmware, Settled(renderer, canvas, browser, None, 50));   // an empty version leaves the row out
    }

    [Fact]
    public void System_never_leaves_the_content_area_even_with_every_text_at_its_longest()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        To(browser, ThemeView.System);
        Settled(renderer, canvas, browser, None, 5);
        uint[] plain = Snapshot(canvas);
        browser.StoreUrl = new string('w', 300);
        renderer.AppVersion = new string('9', 120);
        renderer.AllAppsBackgrounds = true;
        Settled(renderer, canvas, browser, None, 10);
        var box = DiffBox(plain, canvas);
        Assert.NotNull(box);
        // The focus ring (12 px beyond the first row) is the only thing that may reach outside the margins, and only on the first row.
        Assert.InRange(box!.Value.X0, Theme.SafeX, Theme.ContentRight);
        Assert.InRange(box.Value.X1, Theme.SafeX, Theme.ContentRight);
        Assert.InRange(box.Value.Y1, 0, Theme.ContentBottom);
    }

    // ---- USB Import -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_import_card_is_centred_and_holds_the_last_result_inside_it()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        To(browser, ThemeView.Import);
        var (cardX, cardY, cardW, cardH) = renderer.ImportCard();
        Assert.Equal(Theme.ScreenWidth, 2 * cardX + cardW);                  // centred
        Assert.InRange(cardW, 960, 1040);                                    // about 1000 px
        Assert.True(cardY >= Theme.ContentTop && cardY + cardH <= Theme.ContentBottom);

        renderer.LibraryStatus = "";
        long empty = Settled(renderer, canvas, browser, None, 5);
        uint[] plain = Snapshot(canvas);
        renderer.LibraryStatus = "2 imported · 1 already in library · 3 skipped · " + new string('m', 400);
        long result = Settled(renderer, canvas, browser, None, 10);
        Assert.NotEqual(empty, result);
        var box = DiffBox(plain, canvas);
        Assert.NotNull(box);
        // Only the result line changes, and it stays inside the card's padding.
        Assert.True(box!.Value.X0 >= cardX + ThemeRenderer.ImportCardPad - 4 && box.Value.X1 <= cardX + cardW - ThemeRenderer.ImportCardPad + 4);
        Assert.True(box.Value.Y0 >= cardY && box.Value.Y1 <= cardY + cardH);

        renderer.Busy = true;                                                // the button reads "Importing…" and goes quiet
        Assert.NotEqual(result, Settled(renderer, canvas, browser, None, 15));
    }

    // ---- Store-address keyboard -------------------------------------------------------------------------------------

    [Fact]
    public void The_keyboard_grid_spans_the_safe_margins_exactly_and_ends_above_the_footer()
    {
        Assert.Equal(Theme.ContentWidth, 10 * ThemeRenderer.KeyboardUnit + 9 * ThemeRenderer.KeyboardGap);
        int bottom = ThemeRenderer.KeyboardKeysTop + 5 * ThemeRenderer.KeyboardKeyH + 4 * ThemeRenderer.KeyboardGap;
        Assert.True(bottom + 12 <= Theme.ContentBottom, $"the last row ends at {bottom}, its ring 12 px later");   // the focus ring reaches 12 px out

        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        browser.OpenStoreAddress("http://a");                                // focus starts on the first key, row 0
        Settled(renderer, canvas, browser, None, 5);
        foreach (int row in new[] { 1, 3, 4 })                                // unfocused rows: idle keys have their exact fill
        {
            int mid = ThemeRenderer.KeyboardKeysTop + row * (ThemeRenderer.KeyboardKeyH + ThemeRenderer.KeyboardGap) + ThemeRenderer.KeyboardKeyH / 2;
            uint key = Theme.KeyFill.Value;
            Assert.NotEqual(key, PixelAt(canvas, Theme.SafeX - 2, mid));
            Assert.Equal(key, PixelAt(canvas, Theme.SafeX + 8, mid));
            Assert.Equal(key, PixelAt(canvas, Theme.ContentRight - 9, mid));
            Assert.NotEqual(key, PixelAt(canvas, Theme.ContentRight + 2, mid));
        }
    }

    [Fact]
    public void The_keyboard_counter_and_the_end_of_a_long_address_follow_the_text()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        var seen = new HashSet<long>();
        foreach (string text in new[] { "", "http://a", "http://ab", "http://" + new string('x', 190) + "y", "http://" + new string('x', 190) + "z" })
        {
            browser.OpenStoreAddress(text);
            seen.Add(Settled(renderer, canvas, browser, None, 5));
        }
        Assert.Equal(5, seen.Count);   // the counter changes with every length, the last two differ only in the last letter
    }

    [Fact]
    public void The_caret_of_an_empty_address_waits_in_front_of_the_placeholder_and_blinks()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        browser.OpenStoreAddress("");
        // Accent pixels inside the box's text rows (the box's own 2 px edge is outside them), left of the placeholder and over it.
        int Accent(int x0, int x1)
        {
            int count = 0;
            for (int y = 316; y < 364; y++)
                for (int x = x0; x < x1; x++)
                    if (PixelAt(canvas, x, y) == Theme.Accent.Value) count++;
            return count;
        }
        const int textX = Theme.SafeX + 24;
        renderer.Draw(canvas.AsSurface(), browser, None, 5);
        renderer.Draw(canvas.AsSurface(), browser, None, 6);
        Assert.True(Accent(textX - 10, textX - 1) > 60);    // caret on: a bar just left of where the text starts
        Assert.Equal(0, Accent(textX, textX + 40));         // and nothing of it on the placeholder's first letters
        renderer.Draw(canvas.AsSurface(), browser, None, 6.8);
        Assert.Equal(0, Accent(textX - 10, textX - 1));     // caret off
    }

    // ---- Home preview -----------------------------------------------------------------------------------------------

    private static (ThemeRenderer Renderer, PixelBuffer Canvas, ThemeBrowser Browser, ThemeLibraryEntry[] Entries) PreviewSetup(string name = "Flat Cover")
    {
        ThemeLibraryEntry pack = Pack();
        ThemeLibraryEntry[] entries = [pack with { Manifest = pack.Manifest with { Name = name } }];
        var browser = new ThemeBrowser();
        browser.SetCount(1);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Preview, browser.View);
        return (NewRenderer(FlatCover), new PixelBuffer(1920, 1080), browser, entries);
    }

    private static bool RowIsFlat(PixelBuffer canvas, int y, int x0, int x1)
    {
        uint first = PixelAt(canvas, x0, y);
        for (int x = x0 + 1; x < x1; x++)
            if (PixelAt(canvas, x, y) != first) return false;
        return true;
    }

    [Fact]
    public void The_preview_draws_a_home_row_at_the_top_left_and_the_name_at_the_bottom_left_and_nothing_else_over_the_art()
    {
        var (renderer, canvas, browser, entries) = PreviewSetup();
        using (renderer) using (canvas)
        {
            long overlay = Settled(renderer, canvas, browser, entries, 5);
            Assert.Equal(overlay, Settled(renderer, canvas, browser, entries, 20));   // static: the bands are copied, not drawn again

            // Over a flat cover every pixel that is not flat is something drawn. Right of the home row (a pack without tile
            // icons has eight tiles in Games), and in the middle of the picture, there is nothing (the old "Home preview"
            // label sat at the top right).
            for (int y = 0; y < 352; y++) Assert.True(RowIsFlat(canvas, y, 1400, 1920), $"row {y} of the top band is not flat right of the tiles");
            for (int y = 700; y < 940; y++) Assert.True(RowIsFlat(canvas, y, 1300, 1920), $"row {y} is not flat right of the name");
            for (int y = 352; y < 680; y += 7) Assert.True(RowIsFlat(canvas, y, 0, 1920), $"row {y} of the open picture is not flat");
            // Below the name and left of the hints (four of them, from about x = 1200) is just the scrim.
            for (int y = 950; y < 1080; y++) Assert.True(RowIsFlat(canvas, y, 0, 1100), $"row {y} below the name is not flat");
            // What is drawn: the tiles, the name, the hints.
            Assert.False(RowIsFlat(canvas, 200, 90, 800));
            Assert.False(RowIsFlat(canvas, 910, 90, 800));
            Assert.False(RowIsFlat(canvas, 1002, 1500, 1830));
        }
    }

    [Fact]
    public void The_preview_bands_fade_in_without_an_edge_and_leave_the_open_picture_as_it_is()
    {
        var (renderer, canvas, browser, entries) = PreviewSetup();
        using (renderer) using (canvas)
        {
            Settled(renderer, canvas, browser, entries, 5);
            // The cover as the open picture shows it (the hidden overlay draws the bare cover).
            browser.Update(X);
            Assert.False(browser.ShowOverlay);
            Settled(renderer, canvas, browser, entries, 10);
            uint cover = PixelAt(canvas, 1500, 500);
            Assert.Equal(cover, PixelAt(canvas, 100, 20));   // a flat cover really is flat
            browser.Update(X);
            Settled(renderer, canvas, browser, entries, 15);

            int Red(uint p) => (int)((p >> 16) & 0xFF);
            // Strong at the very top and bottom, nothing at the picture's side of each band.
            Assert.True(Red(PixelAt(canvas, 1500, 2)) < Red(cover) - 40);
            Assert.True(Red(PixelAt(canvas, 1500, 1075)) < Red(cover) - 100);
            Assert.Equal(Red(cover), Red(PixelAt(canvas, 1500, 352)));
            Assert.Equal(Red(cover), Red(PixelAt(canvas, 1500, 679)));
            // No step where a band ends: neighbouring rows differ by a few levels at most.
            for (int y = 340; y < 352; y++) Assert.InRange(Red(PixelAt(canvas, 1500, y)) - Red(PixelAt(canvas, 1500, y + 1)), -1, 1);
            for (int y = 679; y < 700; y++) Assert.InRange(Red(PixelAt(canvas, 1500, y + 1)) - Red(PixelAt(canvas, 1500, y)), -3, 0);
        }
    }

    [Fact]
    public void The_preview_hides_its_overlay_and_comes_back_as_it_was_after_another_screen_took_the_bands_away()
    {
        var (renderer, canvas, browser, entries) = PreviewSetup();
        using (renderer) using (canvas)
        {
            long overlay = Settled(renderer, canvas, browser, entries, 5);
            browser.Update(X);
            long open = Settled(renderer, canvas, browser, entries, 10);
            Assert.NotEqual(overlay, open);
            browser.Update(X);
            Assert.Equal(overlay, Settled(renderer, canvas, browser, entries, 15));

            // Another screen lets go of the bands; a later preview makes them again, identically.
            browser.Update(O);
            Assert.Equal(ThemeView.Library, browser.View);
            To(browser, ThemeView.System);
            Settled(renderer, canvas, browser, entries, 20);
            To(browser, ThemeView.Library);
            browser.Update(UiInput.None, preview: true);
            Assert.Equal(overlay, Settled(renderer, canvas, browser, entries, 30));
        }
    }

    [Fact]
    public void The_preview_follows_the_pack_and_its_name_even_when_the_library_is_scanned_again_under_the_same_path()
    {
        var (renderer, canvas, browser, entries) = PreviewSetup("Short");
        using (renderer) using (canvas)
        {
            long first = Settled(renderer, canvas, browser, entries, 5);
            ThemeLibraryEntry same = entries[0];
            // A rescan: the same file, a new manifest object (here also another name).
            ThemeLibraryEntry renamed = same with { Manifest = same.Manifest with { Name = "Renamed" } };
            long second = Settled(renderer, canvas, browser, [renamed], 10);
            Assert.NotEqual(first, second);
            // A name too long for one line of the big title wraps to two lines of the smaller one and stays above the footer.
            string longName = "An extremely long theme name that cannot possibly fit on a single line of the big title font";
            ThemeLibraryEntry wrapped = same with { Manifest = same.Manifest with { Name = longName } };
            Settled(renderer, canvas, browser, [wrapped], 15);
            for (int y = 950; y < 1080; y++) Assert.True(RowIsFlat(canvas, y, 0, 1100), $"row {y} below a two-line name is not flat");
            Assert.False(RowIsFlat(canvas, 915, 90, 1300));   // the second line is there
        }
    }
}
