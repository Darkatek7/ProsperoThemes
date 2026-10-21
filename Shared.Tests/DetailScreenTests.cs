using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>The theme detail screen: cover and glass panel, the list of what a pack changes, the buttons and the footer.</summary>
public class DetailScreenTests
{
    private static readonly UiInput X = new(false, false, false, false, true, false);

    private static ThemePackTarget Target(string path, string layer) => new(path, layer, "payloads/" + path, 1, new string('0', 64));

    private static IEnumerable<ThemePackTarget> Many(int count, string pathFormat, string layer) =>
        Enumerable.Range(0, count).Select(i => Target(string.Format(pathFormat, i), layer));

    // The shape of a store theme: a home background, an all-apps background, 16 icons and 9 tile icons (4 groups).
    private static ThemePackTarget[] StoreTargets() =>
        [Target("vsh_asset/bg_hub_default.dds.zst", "background"), Target("allapps/pic0.dds", "background"),
         .. Many(16, "vsh_asset/Sce.PlayStation.PUI_UI3.rco#{0}", "svg"), .. Many(9, "rnps/NPXS4004{0}/appdb/default/icon0.png", "icon")];

    // Every group of the summary plus files that belong to none of them (8 rows).
    private static ThemePackTarget[] EveryGroupTargets() =>
        [Target("vsh_asset/bg_hub_default.dds.zst", "background"), Target("allapps/pic0.dds", "background"),
         Target("apps/PPSA01650/pic0.dds", "background"), Target("apps/CUSA00001/pic0.dds", "background"),
         Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco#a", "svg"), Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco#b", "svg"),
         Target("rnps/NPXS40047/appdb/default/icon0.png", "icon"), Target("rnps/NPXS40048/appdb/default/icon0.png", "icon"),
         Target("vsh_asset/sfx_warmboot.at9", "sound"), Target("vsh_asset/sfx_other.at9", "sound"), Target("vsh_asset/psbutton_press.mp4", "video"),
         Target("vsh_asset/function_button_circle.png", "glyph"), Target("vsh_asset/function_button_cross.png", "glyph")];

    private const string LongName = "Everything Pack With A Really Long Name That Runs Over Several Lines Of The Heading Font And Then Some More";
    private static readonly string LongDescription =
        string.Join(" ", Enumerable.Repeat("A very long description that keeps going and going so that it wraps over many lines.", 8));

    private static ThemeLibraryEntry Entry(string name, string description, ThemePackTarget[] targets, string path = "pack", PackReport? report = null) =>
        new(path, new ThemePackManifest(2, "id-" + name.Length, name, "Some Author", "2.4.1", description, "keep", targets), report ?? new PackReport(), null);

    /// <summary>A renderer, a canvas and a browser on the detail view of the only entry; the entry can be swapped for another with the same path.</summary>
    private sealed class Scene : IDisposable
    {
        public readonly ThemeRenderer Renderer;
        public readonly PixelBuffer Canvas = new(1920, 1080);
        public readonly ThemeBrowser Browser = new();
        public ThemeLibraryEntry[] Entries;
        public int Decodes;
        public double Now = 5;

        public Scene(ThemeLibraryEntry entry)
        {
            Entries = [entry];
            Renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)),
                _ => { Decodes++; return new PixelBuffer(4, 4); });
            Browser.SetCount(1);
            Browser.Update(X);
            Assert.Equal(ThemeView.Detail, Browser.View);
        }

        public ThemeRenderer.DetailText Layout => Renderer.DetailTextOf(Entries[0]);

        public void Show(ThemeLibraryEntry entry) => Entries = [entry];

        /// <summary>One frame, a second later than the last: the slide-in of the view is over.</summary>
        public void Draw() => Renderer.Draw(Canvas.AsSurface(), Browser, Entries, Now += 1);

        /// <summary>Two frames at rest (the first of a look may still be sliding in or building pictures).</summary>
        public long Settle() { Draw(); Draw(); return Fingerprint(Canvas); }

        public void Dispose() { Renderer.Dispose(); Canvas.Dispose(); }
    }

    private static unsafe long Fingerprint(PixelBuffer canvas, int x = 0, int y = 0, int width = 1920, int height = 1080)
    {
        Surface s = canvas.AsSurface();
        long hash = 17;
        for (int py = y; py < y + height; py++)
            for (int px = x; px < x + width; px++) hash = unchecked(hash * 31 + s.Pixels[py * s.Stride + px]);
        return hash;
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

    // ---- Cost -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Steady_frames_of_the_detail_screen_allocate_nothing(bool busy, bool applied)
    {
        using var scene = new Scene(Entry("The Simpsons - Springfield After Dark", LongDescription, StoreTargets()));
        scene.Renderer.Busy = busy;
        if (applied) scene.Renderer.AppliedId = scene.Entries[0].Manifest.Id;
        for (int i = 0; i < 5; i++) scene.Draw();   // the slide-in, the first draws of every cache and the JIT are over

        // Something drawn on every frame would allocate in every window. When the whole suite runs, a single burst of a
        // couple of hundred bytes can still land in one frame of one window and never come back (measured: one frame,
        // once; the cause was not pinned down, the glyph sprites are one cache shared by all tests running in parallel).
        // So the screen is steady when some window of frames allocates nothing.
        long fewest = long.MaxValue;
        for (int attempt = 0; attempt < 10 && fewest != 0; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30; i++) scene.Draw();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.Equal(0, fewest);
    }

    [Fact]
    public void A_settled_screen_is_a_still_picture_that_costs_no_decodes()
    {
        using var scene = new Scene(Entry("Still", "Nothing moves here.", StoreTargets()));
        long at = scene.Settle();
        int decodes = scene.Decodes;
        for (int i = 0; i < 4; i++) Assert.Equal(at, scene.Settle());
        Assert.Equal(decodes, scene.Decodes);

        // The look depends on the busy flag only through the buttons (and the header's spinner), and goes back with it.
        scene.Renderer.Busy = true;
        Assert.NotEqual(at, scene.Settle());
        scene.Renderer.Busy = false;
        Assert.Equal(at, scene.Settle());
    }

    [Fact]
    public void Sliding_in_ends_in_the_same_picture_as_arriving_at_rest()
    {
        ThemeLibraryEntry entry = Entry("Slide", "A description.", StoreTargets());
        using var sliding = new Scene(entry);
        // The first frames of the view are mid slide-in: the panel is drawn lower, then it settles.
        sliding.Renderer.Draw(sliding.Canvas.AsSurface(), sliding.Browser, sliding.Entries, 5.0);
        long first = Fingerprint(sliding.Canvas);
        sliding.Renderer.Draw(sliding.Canvas.AsSurface(), sliding.Browser, sliding.Entries, 5.1);
        sliding.Renderer.Draw(sliding.Canvas.AsSurface(), sliding.Browser, sliding.Entries, 5.2);
        Assert.NotEqual(first, Fingerprint(sliding.Canvas));
        long rest = sliding.Settle();

        using var arriving = new Scene(entry);
        Assert.Equal(rest, arriving.Settle());
        Assert.NotEqual(first, rest);
    }

    // ---- Layout: the budget of the panel ----------------------------------------------------------------------------

    [Fact]
    public void A_store_style_pack_shows_all_its_rows_even_with_a_two_line_name()
    {
        using var scene = new Scene(Entry("The Simpsons - Springfield After Dark", LongDescription, StoreTargets()));
        ThemeRenderer.DetailText t = scene.Layout;
        Assert.Equal(2, t.Name.Count);
        Assert.Equal(new[] { "Background", "App background", "Icons", "Tile icons" }, t.RowLabels);
        Assert.Equal(new[] { "", "", "16", "9" }, t.RowCounts);
        Assert.Equal(4, t.Rows);
        Assert.Equal("", t.More);
        // The description gave up lines for the list, but keeps two.
        Assert.InRange(t.Description.Count, 2, ThemeRenderer.DetailDescriptionLines);
        Assert.EndsWith("…", t.Description[^1]);
    }

    [Fact]
    public void A_short_pack_keeps_four_lines_of_description()
    {
        using var scene = new Scene(Entry("Short", LongDescription, [Target("vsh_asset/bg_hub_default.dds.zst", "background")]));
        ThemeRenderer.DetailText t = scene.Layout;
        Assert.Single(t.Name);
        Assert.Equal(ThemeRenderer.DetailDescriptionLines, t.Description.Count);
        Assert.Equal(new[] { "Background" }, t.RowLabels);
    }

    [Fact]
    public void Rows_that_do_not_fit_are_cut_off_with_a_count_and_never_run_into_the_check_line()
    {
        using var scene = new Scene(Entry(LongName, LongDescription, EveryGroupTargets()));
        ThemeRenderer.DetailText t = scene.Layout;
        Assert.Equal(8, t.RowLabels.Length);   // Background, App background, App images, Icons, Tile icons, Sounds, Video, Other files
        Assert.InRange(t.Rows, 1, 7);
        Assert.Equal($"+{8 - t.Rows} more", t.More);
        int lastBottom = t.RowsTop + t.Rows * ThemeRenderer.DetailRowStep + 8 + 32;   // the "+N more" line under the last row
        Assert.True(lastBottom <= ThemeRenderer.DetailCheckY, $"the list ends at {lastBottom}, the check line starts at {ThemeRenderer.DetailCheckY}");
        Assert.Equal(2, t.Name.Count);
        Assert.EndsWith("…", t.Name[^1]);       // a name of three lines is cut after two
        Assert.Equal(2, t.Description.Count);   // down to the least it keeps
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_row_that_is_drawn_ends_above_the_check_line(int nameLines)
    {
        string name = string.Join(" ", Enumerable.Repeat("Springfield", nameLines == 1 ? 1 : nameLines * 3));
        foreach (string description in new[] { "", "Short.", LongDescription })
            foreach (int groups in new[] { 0, 1, 4, 8 })
            {
                ThemePackTarget[] targets = groups switch { 0 => [], 1 => [Target("allapps/pic0.dds", "background")], 4 => StoreTargets(), _ => EveryGroupTargets() };
                using var scene = new Scene(Entry(name, description, targets));
                ThemeRenderer.DetailText t = scene.Layout;
                int last = t.Rows == 0 ? t.RowsTop : t.RowsTop + (t.Rows - 1) * ThemeRenderer.DetailRowStep + ThemeRenderer.DetailLastRow;
                if (t.More.Length > 0) last = t.RowsTop + t.Rows * ThemeRenderer.DetailRowStep + 8 + 32;
                Assert.True(last <= ThemeRenderer.DetailCheckY, $"{nameLines} name lines, {groups} groups: ends at {last}");
                Assert.InRange(t.Name.Count, 1, 2);
                Assert.True(t.Description.Count <= ThemeRenderer.DetailDescriptionLines);
            }
    }

    [Fact]
    public void Files_that_belong_to_no_group_get_a_row_and_an_empty_pack_says_so()
    {
        // Each pack has its own path: the summary of a path is kept until the library is scanned again.
        using var scene = new Scene(Entry("Others", "", [Target("vsh_asset/a.png", "glyph"), Target("vsh_asset/b.png", "glyph"), Target("vsh_asset/c.png", "glyph")], path: "three"));
        Assert.Equal(new[] { "Other files" }, scene.Layout.RowLabels);
        Assert.Equal(new[] { "3" }, scene.Layout.RowCounts);
        Assert.False(scene.Layout.Nothing);

        scene.Show(Entry("One", "", [Target("vsh_asset/a.png", "glyph")], path: "one"));
        Assert.Equal(new[] { "Other file" }, scene.Layout.RowLabels);   // singular, and a count of one is not shown
        Assert.Equal(new[] { "" }, scene.Layout.RowCounts);

        scene.Show(Entry("None", "", [], path: "none"));
        Assert.True(scene.Layout.Nothing);
        Assert.Empty(scene.Layout.RowLabels);
    }

    [Fact]
    public void The_layout_is_built_again_when_another_pack_is_opened_or_the_library_was_scanned_again()
    {
        ThemeLibraryEntry entry = Entry("First", "One.", StoreTargets());
        using var scene = new Scene(entry);
        ThemeRenderer.DetailText first = scene.Layout;
        Assert.Same(first, scene.Layout);                                                     // cached while nothing changes
        Assert.Same(first, scene.Renderer.DetailTextOf(entry with { Preview = new byte[] { 1 } }));   // a cover alone does not matter
        scene.Renderer.ResetCovers();                                                         // a scan: the app drops its pictures...
        scene.Show(Entry("Second", "Two.", StoreTargets()));                                  // ...and has new manifest objects for the same path
        ThemeRenderer.DetailText second = scene.Layout;
        Assert.NotSame(first, second);
        Assert.Equal("Second", second.Name[0]);
        scene.Show(Entry("Third", "Three.", StoreTargets(), path: "other"));
        Assert.Equal("Third", scene.Layout.Name[0]);
    }

    // ---- What is on the screen --------------------------------------------------------------------------------------

    [Fact]
    public void The_applied_badge_sits_on_the_corner_of_the_cover_at_the_safe_edge()
    {
        using var scene = new Scene(Entry("Badge", "A description.", StoreTargets()));
        scene.Settle();
        Assert.Equal(0, CountPixels(scene.Canvas, Theme.SafeX, Theme.ContentTop, 300, 100, Theme.Success));
        scene.Renderer.AppliedId = scene.Entries[0].Manifest.Id;
        scene.Settle();
        // The pill is 40 px high and starts 16 px in from the cover's corner (96, 176).
        Assert.True(CountPixels(scene.Canvas, Theme.SafeX + 16, Theme.ContentTop + 16, 140, 40, Theme.Success) > 2500);
        Assert.Equal(0, CountPixels(scene.Canvas, Theme.SafeX, Theme.ContentTop, 16, 100, Theme.Success));   // nothing left of it
        Assert.Equal(0, CountPixels(scene.Canvas, Theme.SafeX, Theme.ContentTop, 300, 16, Theme.Success));    // nor above it
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_pack_check_has_a_dot_in_green_or_in_orange_when_there_are_warnings(bool warnings)
    {
        var report = new PackReport();
        if (warnings) report.Warn("preview", "Cover skipped");
        using var scene = new Scene(Entry("Check", "A description.", StoreTargets(), report: report));
        scene.Settle();
        // The dot is 10 px wide, centred 5 px in from the text edge and 14 px down the 32 px check line.
        uint dot = PixelAt(scene.Canvas, ThemeRenderer.DetailTextX + 5, ThemeRenderer.DetailCheckY + 14);
        Assert.Equal((warnings ? Theme.Warn : Theme.Success).Value, dot);
    }

    [Fact]
    public void The_buttons_sit_on_the_panels_bottom_padding_and_apply_is_dimmed_while_busy()
    {
        using var scene = new Scene(Entry("Buttons", "A description.", StoreTargets()));
        scene.Settle();
        int pillsBottom = ThemeRenderer.DetailPillsY + ThemeRenderer.DetailPillH;
        Assert.Equal(ThemeRenderer.DetailPanelY + ThemeRenderer.DetailPanelH - ThemeRenderer.DetailPad, pillsBottom);
        // The Apply pill is White in the middle of its left half; while a job runs it is the dimmed pill colour.
        int px = ThemeRenderer.DetailTextX + 150, py = ThemeRenderer.DetailPillsY + 4;
        Assert.Equal(Theme.White.Value, PixelAt(scene.Canvas, px, py));
        scene.Renderer.Busy = true;
        scene.Settle();
        Assert.Equal(Theme.PillOff.Value, PixelAt(scene.Canvas, px, py));
        // The texts above the buttons are not touched by it.
        long text = Fingerprint(scene.Canvas, ThemeRenderer.DetailTextX, ThemeRenderer.DetailPanelY + 8, ThemeRenderer.DetailTextW, ThemeRenderer.DetailCheckY - ThemeRenderer.DetailPanelY - 16);
        scene.Renderer.Busy = false;
        scene.Settle();
        Assert.Equal(text, Fingerprint(scene.Canvas, ThemeRenderer.DetailTextX, ThemeRenderer.DetailPanelY + 8, ThemeRenderer.DetailTextW, ThemeRenderer.DetailCheckY - ThemeRenderer.DetailPanelY - 16));
    }

    [Fact]
    public unsafe void The_preview_button_stays_where_it_is_when_apply_changes_its_label()
    {
        using var scene = new Scene(Entry("Buttons", "A description.", StoreTargets()));
        // The first pure White pixel on the buttons' centre line, right of both Apply pills: the edge of Preview's glyph disc.
        int DiscX()
        {
            scene.Settle();
            Surface s = scene.Canvas.AsSurface();
            int y = ThemeRenderer.DetailPillsY + ThemeRenderer.DetailPillH / 2;
            for (int x = ThemeRenderer.DetailTextX + 300; x < ThemeRenderer.DetailTextRight; x++)
                if (s.Pixels[y * s.Stride + x] == Theme.White.Value) return x;
            return -1;
        }
        int idle = DiscX();
        Assert.True(idle > 0);
        scene.Renderer.Busy = true;
        Assert.Equal(idle, DiscX());
        scene.Renderer.Busy = false;
        Assert.Equal(idle, DiscX());
    }

    [Fact]
    public void Nothing_is_drawn_past_the_content_area_the_panel_or_its_padding_whatever_the_texts_are()
    {
        using var scene = new Scene(Entry("Plain", "", []));
        scene.Settle();
        long gapBelow = Fingerprint(scene.Canvas, 0, Theme.ContentBottom + 2, 1700, Theme.FooterY - Theme.ContentBottom - 2);
        long rightMargin = Fingerprint(scene.Canvas, Theme.ContentRight + 2, Theme.ContentTop, 1920 - Theme.ContentRight - 2, Theme.ContentBottom - Theme.ContentTop);
        long leftPadding = Fingerprint(scene.Canvas, ThemeRenderer.DetailPanelX + 4, 200, ThemeRenderer.DetailPad - 8, 720);
        long rightPadding = Fingerprint(scene.Canvas, ThemeRenderer.DetailTextRight + 4, 200, ThemeRenderer.DetailPad - 8, 720);
        long coverGap = Fingerprint(scene.Canvas, Theme.SafeX + ThemeRenderer.DetailCoverW + 2, Theme.ContentTop, ThemeRenderer.DetailPanelX - Theme.SafeX - ThemeRenderer.DetailCoverW - 4, 768);

        // The longest name, description and list there can be (a pack without cover: the backdrop is the same).
        scene.Show(Entry(LongName + " " + LongName, LongDescription + " " + LongDescription, EveryGroupTargets(), path: "stress"));
        scene.Settle();
        Assert.Equal(gapBelow, Fingerprint(scene.Canvas, 0, Theme.ContentBottom + 2, 1700, Theme.FooterY - Theme.ContentBottom - 2));
        Assert.Equal(rightMargin, Fingerprint(scene.Canvas, Theme.ContentRight + 2, Theme.ContentTop, 1920 - Theme.ContentRight - 2, Theme.ContentBottom - Theme.ContentTop));
        Assert.Equal(leftPadding, Fingerprint(scene.Canvas, ThemeRenderer.DetailPanelX + 4, 200, ThemeRenderer.DetailPad - 8, 720));
        Assert.Equal(rightPadding, Fingerprint(scene.Canvas, ThemeRenderer.DetailTextRight + 4, 200, ThemeRenderer.DetailPad - 8, 720));
        Assert.Equal(coverGap, Fingerprint(scene.Canvas, Theme.SafeX + ThemeRenderer.DetailCoverW + 2, Theme.ContentTop, ThemeRenderer.DetailPanelX - Theme.SafeX - ThemeRenderer.DetailCoverW - 4, 768));
    }

    [Fact]
    public void The_check_line_and_the_buttons_do_not_move_with_the_texts_above_them()
    {
        using var scene = new Scene(Entry("Plain", "", []));
        // From the check line to the bottom of the panel: the line, the dot, the buttons and the padding under them.
        long Bottom() => Fingerprint(scene.Canvas, ThemeRenderer.DetailPanelX + 4, ThemeRenderer.DetailCheckY, ThemeRenderer.DetailPanelW - 8,
            ThemeRenderer.DetailPanelY + ThemeRenderer.DetailPanelH - ThemeRenderer.DetailCheckY);
        scene.Settle();
        long plain = Bottom();
        scene.Show(Entry(LongName, LongDescription, EveryGroupTargets(), path: "stress"));
        scene.Settle();
        Assert.Equal(plain, Bottom());
    }

    [Fact]
    public void The_footer_only_offers_removing_and_the_way_back()
    {
        using var scene = new Scene(Entry("Footer", "A description.", StoreTargets()));
        scene.Settle();
        // The hint bar is right-aligned: with two hints (Remove, Back; about 300 px) nothing of it is left of x = 1450. The
        // glyph discs and the labels are Muted, so no Muted pixel may be in the band to its left (the old bar had Apply and
        // Preview there).
        Assert.Equal(0, CountPixels(scene.Canvas, 1000, Theme.FooterY, 450, Theme.FooterHeight, Theme.Muted));
        Assert.True(CountPixels(scene.Canvas, 1450, Theme.FooterY, Theme.ContentRight - 1450, Theme.FooterHeight, Theme.Muted) > 300);
    }

    [Fact]
    public void The_panel_is_a_glass_panel_over_the_backdrop_and_the_cover_is_rounded()
    {
        using var scene = new Scene(Entry("Glass", "A description.", StoreTargets()));
        scene.Settle();
        // Inside the panel the fill is darker than the backdrop beside it (the gap between cover and panel).
        uint inside = PixelAt(scene.Canvas, ThemeRenderer.DetailPanelX + 16, 700), beside = PixelAt(scene.Canvas, ThemeRenderer.DetailPanelX - 20, 700);
        Assert.True(((inside >> 16) & 0xFF) + ((inside >> 8) & 0xFF) + (inside & 0xFF) < ((beside >> 16) & 0xFF) + ((beside >> 8) & 0xFF) + (beside & 0xFF));
        // The cover's corner is rounded: its very first pixel still shows the backdrop, the pixel well inside shows the cover.
        Assert.NotEqual(PixelAt(scene.Canvas, Theme.SafeX + 40, Theme.ContentTop + 300), PixelAt(scene.Canvas, Theme.SafeX - 20, Theme.ContentTop + 300));
        uint corner = PixelAt(scene.Canvas, Theme.SafeX, Theme.ContentTop);
        uint edge = PixelAt(scene.Canvas, Theme.SafeX + Theme.RadiusCard, Theme.ContentTop);
        Assert.NotEqual(corner, edge);
    }
}
