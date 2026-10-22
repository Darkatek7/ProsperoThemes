using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The pointers to the store's web creator: the QR card beside USB Import, the Community header line and the empty
/// Library's sentence. They follow the store address and are gone without one.
/// </summary>
public class CreatorPromoTests
{
    private const string DefaultStore = "https://themes.darkatek7.com";
    private static readonly ThemeLibraryEntry[] None = [];

    private static ThemeRenderer NewRenderer() =>
        new(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), _ => new PixelBuffer(4, 4));

    private static void Show(ThemeRenderer renderer, PixelBuffer canvas, ThemeBrowser browser, ThemeView view, double at = 5)
    {
        int guard = 0;
        while (browser.View != view && guard++ < 8) browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(view, browser.View);
        renderer.Draw(canvas.AsSurface(), browser, None, at);
        renderer.Draw(canvas.AsSurface(), browser, None, at + 1);
    }

    private static unsafe long Hash(PixelBuffer canvas, int x0 = 0, int y0 = 0, int x1 = 1920, int y1 = 1080)
    {
        Surface s = canvas.AsSurface();
        long hash = 17;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) hash = unchecked(hash * 31 + s.Pixels[y * s.Stride + x]);
        return hash;
    }

    private static unsafe bool Dark(PixelBuffer canvas, int x, int y)
    {
        uint p = canvas.AsSurface().Pixels[y * canvas.AsSurface().Stride + x];
        return ((p >> 16 & 0xFF) + (p >> 8 & 0xFF) + (p & 0xFF)) / 3 < 128;
    }

    [Theory]
    [InlineData("https://themes.darkatek7.com", "https://themes.darkatek7.com/#/creator")]
    [InlineData("https://themes.darkatek7.com/", "https://themes.darkatek7.com/#/creator")]
    [InlineData("http://192.168.0.5:8080", "http://192.168.0.5:8080/#/creator")]
    [InlineData("  http://store.example/  ", "http://store.example/#/creator")]
    public void The_creator_address_is_the_store_address_with_the_creator_path(string store, string expected) =>
        Assert.Equal(expected, ThemeRenderer.CreatorUrlOf(store));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("http://")]
    public void Without_a_store_there_is_no_creator_address(string store) => Assert.Null(ThemeRenderer.CreatorUrlOf(store));

    [Theory]
    [InlineData("https://themes.darkatek7.com/#/creator", "themes.darkatek7.com/#/creator")]
    [InlineData("http://192.168.0.5:8080/#/creator", "192.168.0.5:8080/#/creator")]
    [InlineData("HTTPS://Example.org/#/creator", "Example.org/#/creator")]
    [InlineData("ftp://other/#/creator", "ftp://other/#/creator")]
    public void The_address_is_shown_without_its_scheme(string url, string shown) => Assert.Equal(shown, ThemeRenderer.CreatorAddressOf(url));

    [Fact]
    public void The_texts_stay_within_the_font_atlas()
    {
        // The atlases hold ASCII, a few accents and the middle dot (tools/bake-assets.py); no arrows, no other symbols.
        string[] texts = [ThemeRenderer.CreatorHeading, ThemeRenderer.CreatorScan, ThemeRenderer.CreatorIntro,
            ThemeRenderer.CreatorCaptionPrefix + "themes.darkatek7.com/#/creator", ThemeRenderer.CreatorEmptyLibrary];
        foreach (var file in new[] { Theme.TypeScale.Caption.File, Theme.TypeScale.Body.File, Theme.TypeScale.Heading.File })
        {
            var font = new AtlasFont(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", file)));
            foreach (string text in texts)
                foreach (char c in text) Assert.True(font.HasGlyph(c), $"'{c}' (U+{(int)c:X4}) is not in {file}");
        }
    }

    [Fact]
    public void The_import_screen_shows_the_creator_card_and_a_qr_plate_that_is_the_symbol()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser { StoreUrl = DefaultStore };
        Show(renderer, canvas, browser, ThemeView.Import);

        Assert.True(renderer.CreatorCardShown);
        Assert.Equal("https://themes.darkatek7.com/#/creator", renderer.CreatorUrl);

        // Two cards fill the width between the safe margins, side by side and as tall as each other.
        var (ix, iy, iw, ih) = renderer.ImportCard();
        var (cx, cy, cw, ch) = renderer.CreatorCard();
        Assert.Equal(Theme.SafeX, ix);
        Assert.Equal(Theme.ContentRight, cx + cw);
        Assert.Equal(32, cx - (ix + iw));
        Assert.Equal((iy, ih), (cy, ch));
        Assert.True(iy >= Theme.ContentTop && iy + ih <= Theme.ContentBottom);

        // The plate holds exactly the symbol of the address: a quiet zone of four light modules around it.
        var (px, py, size, module) = renderer.CreatorPlate()!.Value;
        QrCode code = QrCode.Encode("https://themes.darkatek7.com/#/creator")!;
        Assert.Equal((code.Size + 8) * module, size);
        Assert.InRange(module, 7, 8);
        Assert.True(px >= cx && px + size <= cx + cw && py >= cy && py + size <= cy + ch);
        for (int my = -4; my < code.Size + 4; my++)
            for (int mx = -4; mx < code.Size + 4; mx++)
            {
                // The plate's rounded corners cut into the four corner modules of the quiet zone.
                if ((mx is -4 || mx == code.Size + 3) && (my is -4 || my == code.Size + 3)) continue;
                bool expected = code.Get(mx, my);
                int x = px + (mx + 4) * module + module / 2, y = py + (my + 4) * module + module / 2;
                Assert.True(expected == Dark(canvas, x, y), $"module {mx},{my}");
            }
    }

    [Fact]
    public void Without_a_store_address_the_qr_and_the_pointers_are_hidden_and_the_import_card_is_centred()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser { StoreUrl = "" };
        Show(renderer, canvas, browser, ThemeView.Import);
        Assert.False(renderer.CreatorCardShown);
        Assert.Null(renderer.CreatorUrl);
        var (x, _, w, _) = renderer.ImportCard();
        Assert.Equal(Theme.ScreenWidth, 2 * x + w);
        long without = Hash(canvas);

        browser.StoreUrl = DefaultStore;
        Show(renderer, canvas, browser, ThemeView.Import);
        Assert.True(renderer.CreatorCardShown);
        Assert.NotEqual(without, Hash(canvas));

        browser.StoreUrl = "";   // the store is removed on purpose: back to the card alone, exactly as before
        Show(renderer, canvas, browser, ThemeView.Import);
        Assert.False(renderer.CreatorCardShown);
        Assert.Equal(without, Hash(canvas));
    }

    [Fact]
    public void Changing_the_store_address_changes_the_qr()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser { StoreUrl = DefaultStore };
        Show(renderer, canvas, browser, ThemeView.Import);
        var (cx, cy, cw, ch) = renderer.CreatorCard();
        long first = Hash(canvas, cx, cy, cx + cw, cy + ch);

        browser.StoreUrl = "http://192.168.0.5:8080";
        Show(renderer, canvas, browser, ThemeView.Import);
        Assert.Equal("http://192.168.0.5:8080/#/creator", renderer.CreatorUrl);
        Assert.NotEqual(first, Hash(canvas, cx, cy, cx + cw, cy + ch));

        // The new plate is the new address's symbol.
        var (px, py, _, module) = renderer.CreatorPlate()!.Value;
        QrCode code = QrCode.Encode("http://192.168.0.5:8080/#/creator")!;
        for (int my = 0; my < code.Size; my++)
            for (int mx = 0; mx < code.Size; mx++)
                Assert.True(code.Get(mx, my) == Dark(canvas, px + (mx + 4) * module + module / 2, py + (my + 4) * module + module / 2), $"module {mx},{my}");
    }

    [Fact]
    public void The_longest_store_address_still_gets_a_code_and_one_too_long_for_a_code_gets_none()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser { StoreUrl = "http://" + new string('a', AppSettings.MaxStoreUrlLength - 7) };
        Show(renderer, canvas, browser, ThemeView.Import);
        Assert.True(renderer.CreatorCardShown);   // 200 + "/#/creator" is 210 bytes: version 10
        var (_, _, size, module) = renderer.CreatorPlate()!.Value;
        Assert.InRange(module, 3, 5);
        Assert.InRange(size, 200, 300);

        // Characters outside ASCII take two bytes each in the code; the card is left out, the text pointers stay.
        browser.StoreUrl = "http://" + new string('é', 105);
        Show(renderer, canvas, browser, ThemeView.Import);
        Assert.False(renderer.CreatorCardShown);
        Assert.NotNull(renderer.CreatorUrl);
        var (x, _, w, _) = renderer.ImportCard();
        Assert.Equal(Theme.ScreenWidth, 2 * x + w);
    }

    [Fact]
    public void The_community_header_and_the_empty_library_follow_the_store()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser { StoreUrl = "" };

        Show(renderer, canvas, browser, ThemeView.Community);
        long communityWithout = Hash(canvas, 0, Theme.ContentTop - 8, 1920, Theme.ContentTop + 40);
        browser.StoreUrl = DefaultStore;
        Show(renderer, canvas, browser, ThemeView.Community);
        long communityWith = Hash(canvas, 0, Theme.ContentTop - 8, 1920, Theme.ContentTop + 40);
        Assert.NotEqual(communityWithout, communityWith);

        // The empty Library says where to make a theme of one's own when there is a store, and keeps its old sentence when there is none.
        browser.StoreUrl = "";
        Show(renderer, canvas, browser, ThemeView.Library);
        long libraryWithout = Hash(canvas, 0, 400, 1920, 560);
        browser.StoreUrl = DefaultStore;
        Show(renderer, canvas, browser, ThemeView.Library);
        Assert.NotEqual(libraryWithout, Hash(canvas, 0, 400, 1920, 560));
        browser.StoreUrl = "";
        Show(renderer, canvas, browser, ThemeView.Library);
        Assert.Equal(libraryWithout, Hash(canvas, 0, 400, 1920, 560));
    }

    // The least a steady frame allocates over twenty of them: garbage made by the screen is in every frame, while the
    // runtime's own work (JIT stubs on a first tier) comes and goes.
    private static long SteadyAllocation(ThemeRenderer renderer, PixelBuffer canvas, ThemeBrowser browser)
    {
        for (int i = 0; i < 40; i++) renderer.Draw(canvas.AsSurface(), browser, None, 5 + i * 0.1);
        long least = long.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            renderer.Draw(canvas.AsSurface(), browser, None, 10 + i * 0.4);
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return least;
    }

    [Fact]
    public void The_pointers_add_no_allocation_to_a_steady_frame()
    {
        using var renderer = NewRenderer();
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser { StoreUrl = "" };
        foreach (ThemeView view in new[] { ThemeView.Library, ThemeView.Community, ThemeView.Import })
        {
            Show(renderer, canvas, browser, view);
            browser.StoreUrl = "";
            long without = SteadyAllocation(renderer, canvas, browser);
            browser.StoreUrl = DefaultStore;
            long with = SteadyAllocation(renderer, canvas, browser);
            Assert.True(with <= without, $"{view}: {with} bytes a frame with the pointers, {without} without");
        }
    }
}
