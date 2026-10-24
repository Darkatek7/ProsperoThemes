using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class StockLookTests
{
    /// <summary>An icon container of 120 titled filler icons plus <paramref name="titles"/>.</summary>
    internal static byte[] Container(IEnumerable<string> titles)
    {
        var text = new StringBuilder("RCOF");
        for (int i = 0; i < 120; i++) text.Append($"<svg><title>utility/iconid_filler_{i}</title><path/></svg>");
        foreach (string title in titles) text.Append($"<svg><title>{title}</title><path/></svg>");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    internal static byte[] StockContainer() => Container(StockLook.ThemeIcons.Select(name => "utility/" + name));

    [Fact]
    public void A_stock_container_titles_every_theme_icon()
    {
        Assert.Empty(StockLook.UntitledThemeIcons(StockContainer()));
    }

    [Fact]
    public void A_container_missing_a_theme_icons_title_is_a_themes()
    {
        byte[] themed = Container(StockLook.ThemeIcons.Where(n => n != "iconid_home").Select(name => "brand/" + name));
        Assert.Equal(["iconid_home"], StockLook.UntitledThemeIcons(themed));
        Assert.Equal(StockLook.ThemeIcons.Count, StockLook.UntitledThemeIcons(Container([])).Count);
    }

    [Fact]
    public void A_small_container_is_not_judged()
    {
        Assert.Empty(StockLook.UntitledThemeIcons(Encoding.UTF8.GetBytes("RCOF<svg><title>utility/iconid_settings</title></svg>")));
    }

    [Fact]
    public void Tile_backgrounds_that_share_a_picture_are_found_in_both_names()
    {
        var hashes = new Dictionary<string, string>
        {
            ["vsh_asset/bg_NPXS40037.dds.zst"] = "a",
            ["vsh_asset/bg_NPXS40047.dds"] = "a",
            ["vsh_asset/bg_NPXS40053.dds"] = "b",
            // The main background is no tile's, so it may look like one.
            ["vsh_asset/bg_hub_default.dds"] = "b",
        };
        Assert.Equal(["a"], StockLook.SharedTileHashes(hashes));
        Assert.True(StockLook.IsTileBackground("vsh_asset/bg_NPXS40047.dds"));
        Assert.False(StockLook.IsTileBackground("vsh_asset/bg_hub_default.dds.zst"));
    }
}
