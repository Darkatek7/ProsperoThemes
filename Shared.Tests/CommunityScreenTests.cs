using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using SharpProspero.Ui;
using Xunit;
using HttpMethod = SharpProspero.Platform.HttpMethod;

namespace ProsperoThemes.Shared.Tests;

/// <summary>The Community (store) screen: layout, scrolling, the heart of a liked theme, hints, and the likes record.</summary>
public unsafe class CommunityScreenTests
{
    private static ThemeRenderer NewRenderer(Func<byte[], PixelBuffer>? decode = null) =>
        new(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), decode ?? (_ => new PixelBuffer(16, 9)));

    private static List<CommunityTheme> Themes(int count) =>
        Enumerable.Range(0, count).Select(i =>
            new CommunityTheme("id" + i, "Theme " + i, "Author " + i, "1." + i, "", 1, 10 + i, 100 + i, null, "http://x/" + i,
                Fallback: true, AllApps: i % 2 == 0, PerApp: i % 3, Icons: 4, Tiles: 2)).ToList();

    // A renderer showing the Community tab with the given list, ready to draw.
    private sealed class Screen : IDisposable
    {
        public readonly ThemeRenderer Renderer;
        public readonly ThemeBrowser Browser = new();
        public readonly PixelBuffer Canvas = new(1920, 1080);
        public readonly List<CommunityTheme> Items;
        private double _now = 5;

        public Screen(int count, Func<byte[], PixelBuffer>? decode = null)
        {
            Renderer = NewRenderer(decode);
            Items = Themes(count);
            Renderer.Community = Items;
            Renderer.SelectedCommunityId = Items.Count > 0 ? Items[0].Id : null;
            Browser.StoreUrl = "http://store.example";
            Browser.SetCommunityCount(count);
            Browser.Update(UiInput.None with { PageDown = true });
            Assert.Equal(ThemeView.Community, Browser.View);
        }

        public Surface Frame(int frames = 1)
        {
            for (int i = 0; i < frames; i++)
            {
                Renderer.Draw(Canvas.AsSurface(), Browser, Array.Empty<ThemeLibraryEntry>(), _now, 1.0 / 60);
                _now += 1.0 / 60;
            }
            return Canvas.AsSurface();
        }

        // Frames until the view's slide-in and any scroll have settled.
        public Surface Settle()
        {
            _now += 2;
            return Frame(90);
        }

        public void Press(UiInput input, bool square = false)
        {
            Browser.Update(input, preview: square);
            Renderer.SelectedCommunityId = Browser.CommunitySelected >= 0 ? Items[Browser.CommunitySelected].Id : null;
        }

        public void Dispose() { Renderer.Dispose(); Canvas.Dispose(); }
    }

    private static uint Px(Surface s, int x, int y) => s.Pixels[y * s.Stride + x];
    private static bool Pink(uint p) => ((p >> 16) & 0xFF) > 235 && ((p >> 8) & 0xFF) is > 90 and < 165 && (p & 0xFF) is > 120 and < 195;
    private static bool Bright(uint p) => ((p >> 16) & 0xFF) > 225 && ((p >> 8) & 0xFF) > 225 && (p & 0xFF) > 225;

    private static int Count(Surface s, int x, int y, int w, int h, Func<uint, bool> test)
    {
        int n = 0;
        for (int py = y; py < y + h; py++)
            for (int px = x; px < x + w; px++)
                if (test(Px(s, px, py))) n++;
        return n;
    }

    private static int CardX(int column) => Theme.SafeX + column * (ThemeRenderer.CommunityCardW + 24);

    // ---- Layout -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Four_columns_fill_the_width_and_two_rows_fill_the_content_area()
    {
        Assert.Equal(Theme.ContentRight, CardX(3) + ThemeRenderer.CommunityCardW);
        Assert.Equal(Theme.ContentBottom, ThemeRenderer.CommunityGridTop + 2 * ThemeRenderer.CommunityCardH + ThemeRenderer.CommunityRowGap);
        Assert.Equal(ThemeRenderer.CommunityCardH + ThemeRenderer.CommunityRowGap, ThemeRenderer.CommunityRowStride);
        // The focus glow reaches 12 px: it must stay clear of the sort row above and the footer below.
        Assert.True(ThemeRenderer.CommunityGridTop - 12 > 212 + 44, "glow touches the sort row");
        Assert.True(Theme.ContentBottom + 12 < Theme.FooterY, "glow touches the footer");
    }

    [Fact]
    public void Cards_leave_the_gaps_between_them_and_the_footer_band_clear()
    {
        // The screen without cards is the reference for what the backdrop looks like where nothing is drawn (its message
        // is centred, away from the points looked at).
        using var empty = new Screen(0);
        empty.Browser.StoreUrl = "";
        uint[] reference = Sample(empty.Settle());
        using var screen = new Screen(24);
        uint[] shown = Sample(screen.Settle());
        // The columns' gap (between the 3rd and 4th card), the rows' gap under the last column, and the margin above the footer.
        Assert.Equal(reference, shown);

        static uint[] Sample(Surface s)
        {
            int w = ThemeRenderer.CommunityCardW;
            int gapX = CardX(2) + w + 12, rowGapY = ThemeRenderer.CommunityGridTop + ThemeRenderer.CommunityCardH + 12;
            var points = new List<uint>();
            for (int y = ThemeRenderer.CommunityGridTop; y < Theme.ContentBottom; y += 37) points.Add(Px(s, gapX, y));
            for (int x = CardX(3); x < Theme.ContentRight; x += 41) points.Add(Px(s, x, rowGapY));
            for (int x = Theme.SafeX; x < Theme.ContentRight; x += 43)
                for (int y = Theme.ContentBottom + 16; y < Theme.FooterY - 4; y += 9) points.Add(Px(s, x, y));
            return points.ToArray();
        }
    }

    // ---- Scrolling ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_grid_scrolls_the_focused_row_into_view_and_a_jump_back_snaps()
    {
        using var screen = new Screen(24);
        var down = UiInput.None with { Down = true };
        int w = ThemeRenderer.CommunityCardW, h = ThemeRenderer.CommunityCardH;
        // The focus ring's white band, 5 px above the card's top edge.
        bool RingAt(Surface s, int column, int y) => Bright(Px(s, CardX(column) + w / 2, y - 5));

        Surface s = screen.Settle();
        Assert.True(RingAt(s, 0, ThemeRenderer.CommunityGridTop));          // the first card, in the first row
        screen.Press(down); s = screen.Settle();
        Assert.True(RingAt(s, 0, ThemeRenderer.CommunityGridTop + ThemeRenderer.CommunityRowStride));   // second row: the window has not moved
        screen.Press(down); screen.Press(down); screen.Press(down); s = screen.Settle();
        // Row 4 is the second of the two visible rows (rows 3 and 4).
        Assert.True(RingAt(s, 0, ThemeRenderer.CommunityGridTop + ThemeRenderer.CommunityRowStride));
        Assert.False(RingAt(s, 0, ThemeRenderer.CommunityGridTop));

        // Square resets the selection to the first card: the very next frame is already at the top, not gliding there.
        screen.Press(UiInput.None, square: true);
        s = screen.Frame();
        Assert.Equal(0, screen.Browser.CommunitySelected);
        Assert.True(RingAt(s, 0, ThemeRenderer.CommunityGridTop));
        // And nothing of the rows that were scrolled past is left drawn outside the content band.
        Assert.Equal(0, Count(s, Theme.SafeX, Theme.ContentBottom + 16, Theme.ContentWidth, Theme.FooterY - 4 - Theme.ContentBottom - 16, Bright));
        _ = h;
    }

    [Fact]
    public void A_row_in_the_middle_of_a_glide_stays_inside_the_band()
    {
        using var screen = new Screen(24);
        screen.Settle();
        screen.Press(UiInput.None with { Down = true });
        screen.Press(UiInput.None with { Down = true });   // the focus leaves the window: one row of scroll
        using var reference = new Screen(0);
        reference.Browser.StoreUrl = "";
        uint[] clear = Strip(reference.Settle());
        for (int frame = 0; frame < 30; frame++)
        {
            // Under the content area and above the footer is never drawn on, whatever the scroll.
            Assert.Equal(clear, Strip(screen.Frame()));
        }

        static uint[] Strip(Surface s)
        {
            var list = new List<uint>();
            for (int y = Theme.ContentBottom + 15; y < Theme.FooterY - 4; y += 5)
                for (int x = Theme.SafeX - 20; x < Theme.ContentRight + 20; x += 31) list.Add(Px(s, x, y));
            return list.ToArray();
        }
    }

    // ---- Hearts and hints ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_liked_theme_shows_a_heart_on_its_card_and_other_cards_do_not()
    {
        using var screen = new Screen(8);
        int w = ThemeRenderer.CommunityCardW, h = ThemeRenderer.CommunityCardH;
        Surface s = screen.Settle();
        int none = Count(s, CardX(1), ThemeRenderer.CommunityGridTop, w, h, Pink);
        Assert.Equal(0, none);   // an outline heart is muted, not pink

        screen.Renderer.CommunityLiked = new HashSet<string> { "id1" };
        s = screen.Settle();
        Assert.True(Count(s, CardX(1), ThemeRenderer.CommunityGridTop, w, h, Pink) > 100);   // the chip on the picture and the heart by the count
        Assert.Equal(0, Count(s, CardX(0), ThemeRenderer.CommunityGridTop, w, h, Pink));
        Assert.Equal(0, Count(s, CardX(2), ThemeRenderer.CommunityGridTop, w, h, Pink));

        // Unliking takes it away again (the card is made again for the changed flag).
        screen.Renderer.CommunityLiked = new HashSet<string>();
        s = screen.Settle();
        Assert.Equal(0, Count(s, CardX(1), ThemeRenderer.CommunityGridTop, w, h, Pink));
    }

    [Fact]
    public void Liking_in_the_app_replaces_the_theme_and_the_card_shows_the_new_count()
    {
        using var screen = new Screen(4);
        Surface s = screen.Settle();
        long before = Fingerprint(s, CardX(0), ThemeRenderer.CommunityGridTop);
        var liked = new List<CommunityTheme>(screen.Items);
        liked[0] = liked[0] with { Likes = liked[0].Likes + 1 };   // what the app does with the store's answer
        screen.Renderer.Community = liked;
        s = screen.Settle();
        Assert.NotEqual(before, Fingerprint(s, CardX(0), ThemeRenderer.CommunityGridTop));
    }

    private static long Fingerprint(Surface s, int x0, int y0)
    {
        long hash = 17;
        for (int y = y0 + ThemeRenderer.CommunityCardH - 112; y < y0 + ThemeRenderer.CommunityCardH; y++)
            for (int x = x0; x < x0 + ThemeRenderer.CommunityCardW; x++) hash = unchecked(hash * 31 + Px(s, x, y));
        return hash;
    }

    [Fact]
    public void Footer_hints_offer_download_or_update_and_like_or_unlike()
    {
        using var screen = new Screen(3);
        ThemeRenderer r = screen.Renderer;
        string[] Labels() => r.CommunityHints(screen.Browser).Select(h => h.Label).ToArray();

        Assert.Equal(["Download", "Like", "Refresh", "Back"], Labels());
        r.CommunityLiked = new HashSet<string> { "id0" };
        Assert.Equal(["Download", "Unlike", "Refresh", "Back"], Labels());
        r.CommunityUpdates = new HashSet<string> { "id0" };
        Assert.Equal(["Update", "Unlike", "Refresh", "Back"], Labels());
        r.CommunityLiked = new HashSet<string>();
        Assert.Equal(["Update", "Like", "Refresh", "Back"], Labels());
        // The same arrays every time: nothing is built per frame.
        Assert.Same(r.CommunityHints(screen.Browser), r.CommunityHints(screen.Browser));

        r.Community = Array.Empty<CommunityTheme>();
        Assert.Equal(["Refresh", "Back"], Labels());
    }

    // ---- Header ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_header_names_the_sort_the_list_was_loaded_with_not_the_one_the_pills_moved_to()
    {
        using var screen = new Screen(8);
        screen.Renderer.CommunityListSort = (int)CommunitySort.Top;
        long Label() => LabelHash(screen.Settle());
        long top = Label();
        // Square moved the pills on to "Most downloaded", but the list still on screen is the "Top rated" one.
        screen.Press(UiInput.None, square: true);
        Assert.Equal(1, screen.Browser.CommunitySort);
        Assert.Equal(top, Label());
        // Once the list in the new sort has arrived, the label follows it.
        screen.Renderer.CommunityListSort = (int)CommunitySort.Downloads;
        Assert.NotEqual(top, Label());

        static long LabelHash(Surface s)
        {
            long hash = 17;
            for (int y = Theme.ContentTop; y < Theme.ContentTop + 32; y++)
                for (int x = Theme.ContentRight - 400; x < Theme.ContentRight; x++) hash = unchecked(hash * 31 + Px(s, x, y));
            return hash;
        }
    }

    [Fact]
    public void The_empty_states_each_draw_something_different()
    {
        using var screen = new Screen(0);
        var seen = new HashSet<long>();
        long Shot()
        {
            Surface s = screen.Settle();
            long hash = 17;
            for (int y = 400; y < 760; y++)
                for (int x = 400; x < 1520; x++) hash = unchecked(hash * 31 + Px(s, x, y));
            return hash;
        }
        screen.Browser.StoreUrl = "";
        long noStore = Shot();
        seen.Add(noStore);                                        // no store set
        screen.Renderer.Busy = true;                              // a library scan running does not make it a load
        Assert.Equal(noStore, Shot());
        screen.Renderer.Busy = false;
        screen.Browser.StoreUrl = "http://store.example";
        seen.Add(Shot());                                         // nothing loaded yet
        screen.Renderer.CommunityStatus = "No themes in the store yet.";
        seen.Add(Shot());                                         // loaded, and empty
        screen.Renderer.CommunityStatus = "Can’t reach the store: failed";
        screen.Renderer.CommunityError = true;
        seen.Add(Shot());                                         // failed
        screen.Renderer.Busy = true;
        seen.Add(Shot());                                         // loading
        Assert.Equal(5, seen.Count);
    }

    // ---- Cards: cost -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_steady_frame_allocates_no_more_than_the_screen_with_one_card()
    {
        // The focus ring (a shared helper, Paint.FocusRing) allocates a little in a Debug build and not in a Release one;
        // the cards, badges, hearts, sort control and count must add nothing to it.
        long Steady(Screen screen)
        {
            screen.Settle();
            screen.Frame(5);
            long before = GC.GetAllocatedBytesForCurrentThread();
            screen.Frame(30);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        using var one = new Screen(1);
        long baseline = Steady(one);

        using var screen = new Screen(24);
        screen.Renderer.CommunityLiked = new HashSet<string> { "id1" };
        screen.Renderer.CommunityUpdates = new HashSet<string> { "id2" };
        screen.Renderer.CommunityInLibrary = new HashSet<string> { "id3" };
        screen.Renderer.CommunityListSort = 0;
        Assert.True(Steady(screen) <= baseline, "the community grid allocates in a steady frame");
    }

    [Fact]
    public void Previews_are_decoded_a_couple_a_frame_and_each_only_once()
    {
        int decodes = 0;
        using var screen = new Screen(8, _ => { decodes++; var b = new PixelBuffer(64, 36); b.Clear(Color.FromRgb(40, 90, 200)); return b; });
        screen.Settle();
        Assert.Equal(0, decodes);
        Assert.Equal(8, screen.Renderer.CommunityBakes);   // the eight visible cards, made once, with the placeholder
        foreach (var t in screen.Items) screen.Renderer.CommunityPreviews[t.Id] = [1];
        screen.Frame();
        Assert.Equal(2, decodes);                 // not all eight in one frame
        // And the six that wait are not made over and over in the meantime: only the two with a decode slot are made again.
        Assert.Equal(10, screen.Renderer.CommunityBakes);
        screen.Frame(10);
        Assert.Equal(8, decodes);
        Assert.Equal(16, screen.Renderer.CommunityBakes);
        long settled = Fingerprint(screen.Frame(), CardX(3), ThemeRenderer.CommunityGridTop);
        screen.Frame(20);
        Assert.Equal(8, decodes);                 // made once, copied since
        Assert.Equal(16, screen.Renderer.CommunityBakes);
        Assert.Equal(settled, Fingerprint(screen.Frame(), CardX(3), ThemeRenderer.CommunityGridTop));
    }

    [Fact]
    public void A_preview_that_cannot_be_decoded_leaves_the_placeholder_and_is_not_tried_again()
    {
        int decodes = 0;
        using var screen = new Screen(2, _ => { decodes++; throw new InvalidDataException(); });
        screen.Renderer.CommunityPreviews["id0"] = [1];
        screen.Settle();
        screen.Frame(30);
        Assert.Equal(1, decodes);
    }

    // ---- Text ---------------------------------------------------------------------------------------------------------

    private static AtlasFont Font(string file) => new(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", file)));

    [Fact]
    public void Contents_drop_whole_groups_instead_of_cutting_a_word()
    {
        using var renderer = NewRenderer();
        var small = Font("inter-22.p5font");
        const string all = "Background · App background · 16 app images · 9 icons · 9 tile icons";
        foreach (int width in new[] { 600, 378, 300, 230, 200 })
        {
            string fitted = renderer.FitCommunityContents(all, width);
            Assert.True(small.MeasureText(fitted) <= width || fitted.Split(" · ").Length == 1, fitted);
            Assert.DoesNotContain("…", fitted);                  // whole groups only, nothing cut inside a word
            if (fitted != all) Assert.Matches(@" · \+\d( more)?$", fitted);   // and what was dropped is counted
        }
        Assert.Equal(all, renderer.FitCommunityContents(all, 1000));
        Assert.Matches(@"^Background · App background · \+3( more)?$", renderer.FitCommunityContents(all, 378));
        Assert.Equal("Theme", renderer.FitCommunityContents("Theme", 378));
    }

    [Fact]
    public void A_cut_name_ends_at_a_word_and_never_in_a_dash()
    {
        var label = Font("inter-28-semibold.p5font");
        string name = "The Simpsons - Springfield After Dark";
        for (int width = 120; width < 400; width += 7)
        {
            string fitted = ThemeRenderer.FitCommunityLine(label, name, width);
            Assert.True(label.MeasureText(fitted) <= width, fitted);
            Assert.DoesNotContain("-…", fitted);
            Assert.DoesNotContain(" …", fitted);
        }
        Assert.Equal(name, ThemeRenderer.FitCommunityLine(label, name, 1000));
        Assert.Equal("", ThemeRenderer.FitCommunityLine(label, "", 100));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(1234, "1.2k")]
    [InlineData(1999, "1.9k")]
    [InlineData(9999, "9.9k")]
    [InlineData(10000, "10k")]
    [InlineData(123456, "123k")]
    [InlineData(1000000, "1M")]
    [InlineData(2500000, "2.5M")]
    [InlineData(-3, "0")]
    public void Counts_are_exact_below_a_thousand_and_short_above(int count, string expected) =>
        Assert.Equal(expected, ThemeRenderer.CommunityCompactCount(count));

    // ---- The likes record and the like request ---------------------------------------------------------------------------

    [Fact]
    public void Liked_ids_round_trip_sorted_and_without_duplicates()
    {
        string text = CommunityList.FormatLikedIds(["b2", "a1", "c3", "a1"]);
        Assert.Equal(["a1", "b2", "c3"], System.Text.Json.JsonSerializer.Deserialize<string[]>(text)!.ToArray());
        Assert.Equal(new HashSet<string> { "a1", "b2", "c3" }, CommunityList.ParseLikedIds(text));
        Assert.Equal(text, CommunityList.FormatLikedIds(CommunityList.ParseLikedIds(text)));
        Assert.Empty(CommunityList.ParseLikedIds(CommunityList.FormatLikedIds([])));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("\"a\"")]
    [InlineData("[")]
    public void A_missing_or_broken_likes_file_records_nothing(string? json) =>
        Assert.Empty(CommunityList.ParseLikedIds(json));

    [Fact]
    public void Entries_that_are_not_ids_are_skipped()
    {
        string json = "[\"ok\", 5, null, \"\", {\"x\":1}, [\"nested\"], true, \"" + new string('z', 200) + "\", \"fine\"]";
        Assert.Equal(new HashSet<string> { "ok", "fine" }, CommunityList.ParseLikedIds(json));
    }

    [Fact]
    public void A_liked_theme_is_unliked_with_delete_and_any_other_is_liked_with_post()
    {
        Assert.Equal(HttpMethod.Delete, CommunityList.LikeMethod(alreadyLiked: true));
        Assert.Equal(HttpMethod.Post, CommunityList.LikeMethod(alreadyLiked: false));
        Assert.Equal("/data/prospero-themes/likes.json", CommunityList.LikesPath);
    }

    [Theory]
    [InlineData("""{"likes":13,"liked":true}""", true)]
    [InlineData("""{"likes":12,"liked":false}""", false)]
    [InlineData("""{"likes":12}""", null)]
    [InlineData("""{"likes":12,"liked":"yes"}""", null)]
    [InlineData("nope", null)]
    [InlineData("", null)]
    public void The_answer_says_whether_the_theme_is_liked_now(string json, bool? expected) =>
        Assert.Equal(expected, CommunityList.ParseLiked(json));
}
