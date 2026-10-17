using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class ThemeRendererTests
{
    [Fact]
    public void Sliding_the_visible_window_does_not_decode_covers_on_stationary_frames()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "two-screen.p5theme"));
        var entry = ThemeLibraryEntry.Inspect("original", bytes);
        var entries = Enumerable.Range(0, 7).Select(i => entry with { Path = i.ToString(), Preview = new byte[] { 1 } }).ToArray();
        int decodes = 0;
        using var renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), _ =>
        { decodes++; return new PixelBuffer(4, 4); });
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser(); browser.SetCount(entries.Length);
        renderer.Draw(canvas.AsSurface(), browser, entries, 1);
        for (int i = 0; i < 4; i++) browser.Update(new UiInput(false, false, false, true, false, false));
        renderer.Draw(canvas.AsSurface(), browser, entries, 2);
        browser.Update(new UiInput(false, false, false, true, false, false));
        renderer.Draw(canvas.AsSurface(), browser, entries, 3);
        int afterMove = decodes;
        for (int frame = 0; frame < 3; frame++) renderer.Draw(canvas.AsSurface(), browser, entries, 4 + frame);
        Assert.Equal(afterMove, decodes);
    }

    [Theory]
    [InlineData(0, "0 themes")]
    [InlineData(1, "1 theme")]
    [InlineData(2, "2 themes")]
    public void Plural_counts_with_a_regular_english_plural(int count, string expected) =>
        Assert.Equal(expected, ThemeRenderer.Plural(count, "theme"));

    private static ThemeRenderer NewRenderer() =>
        new(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), _ => new PixelBuffer(4, 4));

    private static unsafe long Fingerprint(PixelBuffer canvas)
    {
        Surface s = canvas.AsSurface();
        long hash = 17;
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++) hash = unchecked(hash * 31 + s.Pixels[y * s.Stride + x]);
        return hash;
    }

    private static long Frame(ThemeRenderer renderer, PixelBuffer canvas, ThemeBrowser browser, double seconds = 5)
    {
        renderer.Draw(canvas.AsSurface(), browser, Array.Empty<ThemeLibraryEntry>(), seconds);
        return Fingerprint(canvas);
    }

    [Fact]
    public void Store_address_keyboard_draws_empty_typed_long_and_with_a_notice()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        var seen = new HashSet<long>();
        foreach (string text in new[] { "", "http://192.168.0.5:8080", "https://" + new string('x', 180) + ".example.de" })
            foreach (string notice in new[] { "", "The address must start with http:// or https://." })
            {
                browser.OpenStoreAddress(text, notice);
                seen.Add(Frame(renderer, canvas, browser));
            }
        Assert.Equal(6, seen.Count);   // text and notice both show
    }

    [Fact]
    public void Store_address_keyboard_shows_the_focused_key_and_blinks_the_caret()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        browser.OpenStoreAddress("http://a");
        long first = Frame(renderer, canvas, browser);
        Assert.Equal(first, Frame(renderer, canvas, browser));
        Assert.NotEqual(first, Frame(renderer, canvas, browser, seconds: 5.8));   // caret off
        browser.Update(new UiInput(false, false, false, true, false, false));
        Assert.NotEqual(first, Frame(renderer, canvas, browser));                  // focus moved
        browser.Update(new UiInput(false, true, false, false, false, false));
        browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(ThemeView.StoreAddress, browser.View);
    }

    [Fact]
    public void System_screen_shows_the_store_address_or_that_none_is_set()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.System, browser.View);
        long none = Frame(renderer, canvas, browser);
        browser.StoreUrl = "http://10.0.0.22:8080";
        long set = Frame(renderer, canvas, browser);
        browser.StoreUrl = new string('a', 200);   // far wider than the row: clipped, no overflow
        long long_ = Frame(renderer, canvas, browser);
        Assert.Equal(3, new[] { none, set, long_ }.Distinct().Count());
    }

    // ---- Notices, dialogs, the System list and the applied badge ---------------------------------------------------

    private static readonly UiInput X = new(false, false, false, false, true, false);
    private static readonly UiInput O = new(false, false, false, false, false, true);
    private static readonly UiInput Down = new(false, true, false, false, false, false);
    private static readonly UiInput Right = new(false, false, false, true, false, false);

    private static ThemeLibraryEntry FixturePack() =>
        ThemeLibraryEntry.Inspect("two-screen", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "two-screen.p5theme")));

    // Two frames per look, a second apart: the first frame of a view is mid slide-in.
    private static long Settled(ThemeRenderer renderer, PixelBuffer canvas, ThemeBrowser browser, IReadOnlyList<ThemeLibraryEntry> entries, double at)
    {
        renderer.Draw(canvas.AsSurface(), browser, entries, at);
        renderer.Draw(canvas.AsSurface(), browser, entries, at + 1);
        return Fingerprint(canvas);
    }

    private static unsafe uint PixelAt(PixelBuffer canvas, int x, int y)
    {
        Surface s = canvas.AsSurface();
        return s.Pixels[y * s.Stride + x];
    }

    private static unsafe int CountPixels(PixelBuffer canvas, int x, int y, int width, int height, Color color)
    {
        Surface s = canvas.AsSurface();
        int count = 0;
        for (int py = y; py < y + height; py++)
            for (int px = x; px < x + width; px++)
                if (s.Pixels[py * s.Stride + px] == color.Value) count++;
        return count;
    }

    [Fact]
    public void A_notice_shows_stays_while_busy_and_fades_about_six_seconds_after_it_was_posted()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        long At(double t) => Frame(renderer, canvas, browser, t);
        At(1); At(2);
        long clean = At(3);

        renderer.Notify("Applied and verified 27 target(s).", NoticeLevel.Success);
        long shown = At(10);              // the first draw after Notify is the moment it was posted
        Assert.NotEqual(clean, shown);
        Assert.Equal(shown, At(14));      // still fully there
        long fading = At(15.7);           // 5.7 s after: in the fade-out
        Assert.NotEqual(clean, fading);
        Assert.NotEqual(shown, fading);
        Assert.Equal(clean, At(17));      // gone

        renderer.Busy = true;                // an expired notice does not come back when the next job starts
        long busyClean = At(30);
        Assert.NotEqual(clean, busyClean);   // (the busy dot differs from the idle screen)
        renderer.Notify("Applying theme…", NoticeLevel.Info);
        long progress = At(31);
        Assert.NotEqual(busyClean, progress);
        Assert.Equal(progress, At(90));      // while busy it stays, however long that takes
        renderer.Busy = false;               // and once the work is over it counts from the post
        Assert.Equal(clean, At(91));
    }

    [Fact]
    public void A_notice_has_a_colour_per_level_a_new_one_replaces_it_and_it_can_be_cleared()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        long At(double t) => Frame(renderer, canvas, browser, t);
        At(1); At(2);
        long clean = At(3);

        var seen = new HashSet<long>();
        double at = 100;
        foreach (NoticeLevel level in Enum.GetValues<NoticeLevel>())
        {
            renderer.Notify("Same words", level);
            seen.Add(At(at += 50));
        }
        Assert.Equal(3, seen.Count);
        renderer.Notify("Other words", NoticeLevel.Info);
        Assert.DoesNotContain(At(at += 50), seen);

        renderer.ClearNotice();
        Assert.Equal(clean, At(at += 1));

        // Text that does not fit is cut, and never reaches outside the canvas.
        renderer.Notify(string.Join(" ", Enumerable.Repeat("a very long message", 200)), NoticeLevel.Error);
        Assert.NotEqual(clean, At(at += 50));
        renderer.Notify("", NoticeLevel.Info);
        At(at += 50);

        // The clock may start over (the PC preview, tests): the notice then counts from the new time.
        renderer.Notify("Again", NoticeLevel.Success);
        long first = At(500);
        Assert.NotEqual(clean, first);
        Assert.Equal(first, At(3));
        Assert.Equal(first, At(8));
    }

    [Fact]
    public void Dialogs_dim_the_screen_show_the_focus_and_leave_the_screen_as_it_was()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        ThemeLibraryEntry[] entries = [FixturePack()];
        var browser = new ThemeBrowser();
        browser.SetCount(1);
        browser.Update(X);
        Assert.Equal(ThemeView.Detail, browser.View);
        long detail = Settled(renderer, canvas, browser, entries, 5);

        browser.Update(X);
        Assert.Equal(DialogKind.Apply, browser.Dialog);
        renderer.Draw(canvas.AsSurface(), browser, entries, 10);   // drawn and dimmed...
        long drawn = Fingerprint(canvas);
        renderer.Draw(canvas.AsSurface(), browser, entries, 11);   // ...then copied: the same picture
        long apply = Fingerprint(canvas);
        Assert.Equal(drawn, apply);
        Assert.NotEqual(detail, apply);
        renderer.Busy = true;                                       // what the dimmed screen shows changed: it is drawn again
        long busy = Settled(renderer, canvas, browser, entries, 12);
        renderer.Busy = false;
        Assert.NotEqual(apply, busy);
        renderer.AppliedId = entries[0].Manifest.Id;
        long badge = Settled(renderer, canvas, browser, entries, 14);
        Assert.NotEqual(apply, badge);
        renderer.AppliedId = null;
        Assert.Equal(apply, Settled(renderer, canvas, browser, entries, 16));
        browser.Update(Right);                                          // focus on Cancel
        long cancelFocused = Settled(renderer, canvas, browser, entries, 15);
        Assert.NotEqual(apply, cancelFocused);
        browser.Update(O);
        Assert.Equal(detail, Settled(renderer, canvas, browser, entries, 20));

        browser.Update(X, asksScope: true);
        Assert.Equal(DialogKind.ApplyScope, browser.Dialog);
        long scope = Settled(renderer, canvas, browser, entries, 25);
        Assert.DoesNotContain(scope, new[] { detail, apply, cancelFocused });
        browser.Update(Right, asksScope: true);
        Assert.NotEqual(scope, Settled(renderer, canvas, browser, entries, 30));
        browser.Update(O);

        // A dialog also dims the header: its corner is darker than the plain screen's.
        browser.Update(X);
        Settled(renderer, canvas, browser, entries, 35);
        uint withDialogCorner = PixelAt(canvas, 20, 300);
        browser.Update(O);
        Settled(renderer, canvas, browser, entries, 40);
        uint plainCorner = PixelAt(canvas, 20, 300);
        Assert.True(((withDialogCorner >> 16) & 0xFF) <= ((plainCorner >> 16) & 0xFF));
    }

    [Fact]
    public void The_system_list_shows_which_row_has_the_focus_and_the_restore_dialog()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.System, browser.View);
        var none = Array.Empty<ThemeLibraryEntry>();
        var rows = new HashSet<long>();
        for (int row = 0; row < ThemeBrowser.SystemRowCount; row++)
        {
            Assert.Equal(row, browser.SystemSelected);
            rows.Add(Settled(renderer, canvas, browser, none, 5 + row * 3));
            browser.Update(Down);
        }
        Assert.Equal(ThemeBrowser.SystemRowCount, rows.Count);          // every focus position looks different
        Assert.Equal(ThemeBrowser.SystemRowCount - 1, browser.SystemSelected);

        browser.Update(X);
        Assert.Equal(DialogKind.Restore, browser.Dialog);
        long restore = Settled(renderer, canvas, browser, none, 30);
        Assert.DoesNotContain(restore, rows);
        browser.Update(new UiInput(true, false, false, false, false, false));   // Up: "Restore defaults"
        Assert.NotEqual(restore, Settled(renderer, canvas, browser, none, 35));

        renderer.AllAppsBackgrounds = true;                              // the value of the first row follows the setting
        browser.Update(O);
        long all = Settled(renderer, canvas, browser, none, 40);
        renderer.AllAppsBackgrounds = false;
        Assert.NotEqual(all, Settled(renderer, canvas, browser, none, 45));
    }

    [Fact]
    public void The_applied_pack_carries_a_badge_on_its_library_card_and_on_its_detail_cover()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        ThemeLibraryEntry first = FixturePack() with { Path = "a" };
        ThemeLibraryEntry second = first with { Path = "b", Manifest = first.Manifest with { Id = "other-pack" } };
        ThemeLibraryEntry[] entries = [first, second];
        var browser = new ThemeBrowser();
        browser.SetCount(2);

        // Library: the cards are 320 x 180 at y = 696, the first at x = 96, 352 px apart. The focused card is drawn 344 x 194,
        // centred on its slot. The badge is a Success-coloured pill in the card's top-left corner.
        int Badge(double at, int slot, bool focused)
        {
            Settled(renderer, canvas, browser, entries, at);
            return focused ? CountPixels(canvas, 96 + slot * 352 - 12, 696 - 7, 344, 70, Theme.Success)
                           : CountPixels(canvas, 96 + slot * 352, 696, 320, 60, Theme.Success);
        }
        Assert.Equal(0, Badge(5, 0, focused: true));
        renderer.AppliedId = first.Manifest.Id;
        Assert.True(Badge(10, 0, focused: true) > 500);
        Assert.Equal(0, Badge(10, 1, focused: false));
        renderer.AppliedId = "unknown-pack";
        Assert.Equal(0, Badge(15, 0, focused: true));
        renderer.AppliedId = "";
        Assert.Equal(0, Badge(20, 0, focused: true));
        renderer.AppliedId = second.Manifest.Id;                         // the second card (not focused) has it, not the first
        Assert.Equal(0, Badge(25, 0, focused: true));
        Assert.True(Badge(25, 1, focused: false) > 500);
        browser.Update(Right);                                           // the same badge on the card once it is focused
        Assert.True(Badge(30, 1, focused: true) > 500);
        Assert.Equal(0, Badge(30, 0, focused: false));

        // Detail: the cover starts at (96, 176), the safe edge and the top of the content area.
        renderer.AppliedId = first.Manifest.Id;
        browser.Update(Left);
        browser.Update(X);
        Assert.Equal(ThemeView.Detail, browser.View);
        Settled(renderer, canvas, browser, entries, 30);
        Assert.True(CountPixels(canvas, 96, 176, 400, 80, Theme.Success) > 500);
        renderer.AppliedId = null;
        Settled(renderer, canvas, browser, entries, 35);
        Assert.Equal(0, CountPixels(canvas, 96, 176, 400, 80, Theme.Success));
    }

    [Fact]
    public void The_busy_spinner_turns_once_a_second_and_keeps_turning_under_a_dialog()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        ThemeLibraryEntry[] entries = [FixturePack()];
        var browser = new ThemeBrowser();
        browser.SetCount(1);
        long At(double t) { renderer.Draw(canvas.AsSurface(), browser, entries, t); return Fingerprint(canvas); }
        At(1); At(2);                                  // the view slides in
        long idle = At(3);
        renderer.Busy = true;
        long head0 = At(10.0), head1 = At(10.25), again = At(11.0);
        Assert.NotEqual(idle, head0);
        Assert.NotEqual(head0, head1);                 // it turns...
        Assert.Equal(head0, again);                    // ...once a second, so the same time is the same picture

        browser.Update(X);                             // Detail, then its question; the dimmed screen is kept after it rests
        At(20);
        browser.Update(X);
        Assert.Equal(DialogKind.Apply, browser.Dialog);
        At(21);
        long under0 = At(30.0), under1 = At(30.25), underAgain = At(31.0);
        Assert.NotEqual(under0, under1);               // not frozen in the dimmed snapshot
        Assert.Equal(under0, underAgain);
    }

    [Fact]
    public void The_library_row_keeps_the_focused_card_in_view_and_comes_to_rest()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        ThemeLibraryEntry pack = FixturePack();
        ThemeLibraryEntry[] entries = Enumerable.Range(0, 9).Select(i => pack with { Path = "p" + i }).ToArray();
        var browser = new ThemeBrowser();
        browser.SetCount(entries.Length);
        double t = 5;
        long Rest()
        {
            for (int i = 0; i < 150; i++) renderer.Draw(canvas.AsSurface(), browser, entries, t += 1.0 / 60);
            return Fingerprint(canvas);
        }
        // The focus ring's top edge is a solid White line from y = 683 (6 px above the enlarged card, which starts at 689);
        // the slots are 352 px apart from x = 96, and a card is 320 wide.
        bool Ring(int slot) => PixelAt(canvas, 96 + slot * 352 + 160, 684) == Theme.White.Value;

        Rest();
        Assert.True(Ring(0));
        for (int i = 0; i < 6; i++) browser.Update(Right);
        long scrolled = Rest();
        Assert.True(Ring(2));                           // the focused card stays the third one
        Assert.False(Ring(0));
        Assert.Equal(scrolled, Rest());                 // a row at rest is a still picture

        browser.Update(UiInput.None with { End = true });
        Rest();
        Assert.True(Ring(3));                           // at the end the row stops, with the add card last, and focus moves right
        browser.Update(UiInput.None with { Home = true });
        Rest();
        Assert.True(Ring(0));
    }

    [Fact]
    public void The_header_keeps_the_tab_glyphs_in_place_and_leaves_them_out_on_the_keyboard()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        var none = Array.Empty<ThemeLibraryEntry>();
        // L1 sits left of the centred tab group and R1 right of it; their pills are solid Muted.
        int Glyphs()
        {
            Settled(renderer, canvas, browser, none, 5);
            return CountPixels(canvas, 540, 70, 70, 60, Theme.Muted) + CountPixels(canvas, 1316, 70, 60, 60, Theme.Muted);
        }
        int library = Glyphs();
        Assert.True(library > 800);
        for (int tab = 0; tab < 3; tab++)                  // Community, USB Import, System: the active tab moves, the glyphs do not
        {
            browser.Update(UiInput.None with { PageDown = true });
            Assert.Equal(library, Glyphs());
        }
        browser.OpenStoreAddress("");                      // the tabs do not switch while typing
        Assert.Equal(0, Glyphs());
    }

    private static readonly UiInput Left = new(false, false, true, false, false, false);

    [Theory]
    [InlineData("13.000.000", "13.00")]
    [InlineData("11.020.000", "11.02")]
    [InlineData("9.600.000", "9.60")]
    [InlineData("", "unknown")]
    [InlineData("13.00", "13.00")]
    public void Firmware_shows_as_the_console_settings_show_it(string version, string label) =>
        Assert.Equal(label, ThemeRenderer.FirmwareLabel(version));
}
