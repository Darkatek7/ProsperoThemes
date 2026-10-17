using System.IO;
using System.Linq;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The PNG (glyph/icon/ui) layer, validated through the same engine as the backgrounds.
/// </summary>
/// <remarks>
/// This layer existing at all is the point: it rode the engine with no change to <c>Shared/</c>, because the
/// format addresses <em>targets</em> rather than describing features. A test that only proved backgrounds
/// would not show that, so the fixture here covers three different layers at once.
/// </remarks>
public class IconLayerPackTests
{
    private static string FixturePath(string name) =>
        Path.Combine(System.AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void A_png_layer_pack_validates()
    {
        string path = FixturePath("icon-layer.p5theme");
        Assert.True(File.Exists(path), $"fixture is missing from the test output: {path}");

        ThemePack pack = ThemePack.OpenFile(path);
        PackReport report = pack.Validate();

        Assert.False(report.HasErrors, string.Join("; ", report.Errors.Select(e => e.Message)));
        Assert.Equal("icon-test", pack.Manifest.Id);
        Assert.Equal(4, pack.ApplicableTargets().Count());
    }

    [Fact]
    public void The_pack_carries_several_layers_through_one_engine()
    {
        ThemePack pack = ThemePack.OpenFile(FixturePath("icon-layer.p5theme"));
        string[] layers = pack.Manifest.Targets.Select(t => t.Layer).Distinct().OrderBy(l => l).ToArray();

        Assert.Equal(new[] { "glyph", "icon", "ui" }, layers);

        // Every layer is a known layer, so none of them would be skipped by the applier.
        Assert.All(layers, l => Assert.Contains(l, ThemePackManifest.KnownLayers));
    }

    [Fact]
    public void Png_payloads_are_shipped_as_real_pngs()
    {
        // No encoding happens on the console, so a PNG target's payload is the file itself. Checking the
        // signature here is what would catch a creator that quietly shipped something else.
        ThemePack pack = ThemePack.OpenFile(FixturePath("icon-layer.p5theme"));
        byte[] pngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        foreach (ThemePackTarget target in pack.Manifest.Targets)
        {
            byte[] payload = pack.Payload(target);
            Assert.True(payload.Length > 8);
            Assert.Equal(pngMagic, payload.Take(8).ToArray());

            // and the manifest must describe the payload exactly, which is what the applier trusts
            Assert.Equal(target.Bytes, payload.LongLength);
            Assert.Equal(target.Sha256, ThemePack.Hash(payload));
            Assert.False(string.IsNullOrEmpty(target.StockSha256));
        }
    }
}
