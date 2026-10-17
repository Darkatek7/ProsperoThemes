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

    [Theory]
    [InlineData("13.000.000", "13.00")]
    [InlineData("11.020.000", "11.02")]
    [InlineData("9.600.000", "9.60")]
    [InlineData("", "unknown")]
    [InlineData("13.00", "13.00")]
    public void Firmware_shows_as_the_console_settings_show_it(string version, string label) =>
        Assert.Equal(label, ThemeRenderer.FirmwareLabel(version));
}
