using System.IO;
using System.Linq;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// Cross-implementation tests: the fixture packs in <c>fixtures/</c> were produced by
/// <c>ThemePackCreator/theme_pack.py</c>, so these exercise the C# reader against the Python writer. Two
/// independent implementations of one format drift unless something checks them against each other, and
/// this is that check.
/// </summary>
public class CreatorPackTests
{
    private static string FixturePath(string name) =>
        Path.Combine(System.AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void A_pack_built_by_the_creator_validates()
    {
        string path = FixturePath("two-screen.p5theme");
        Assert.True(File.Exists(path), $"fixture is missing from the test output: {path}");

        ThemePack pack = ThemePack.OpenFile(path);
        PackReport report = pack.Validate();

        Assert.False(report.HasErrors, string.Join("; ", report.Errors.Select(e => e.Message)));
        Assert.Equal(ThemePackManifest.CurrentFormat, pack.Manifest.Format);
        Assert.Equal("test-pack", pack.Manifest.Id);
        Assert.Equal(2, pack.ApplicableTargets().Count());
    }

    [Fact]
    public void The_fixture_proves_both_dds_header_flavours_survive_the_round_trip()
    {
        // The console ships two header flavours: 15 assets with a 148-byte DX10 header and
        // bg_image_load_error with a 128-byte legacy one. They differ by 20 bytes over an identical pixel
        // payload, so a replacement has to keep its target's own header -- and the creator's output must
        // therefore differ in length between the two by exactly that.
        ThemePack pack = ThemePack.OpenFile(FixturePath("two-screen.p5theme"));

        ThemePackTarget dx10 = pack.Manifest.Targets.Single(t => t.Path.Contains("bg_hub_default"));
        ThemePackTarget legacy = pack.Manifest.Targets.Single(t => t.Path.Contains("bg_image_load_error"));

        foreach (ThemePackTarget t in new[] { dx10, legacy })
        {
            byte[] payload = pack.Payload(t);
            Assert.Equal(t.Bytes, payload.LongLength);
            Assert.Equal(t.Sha256, ThemePack.Hash(payload));

            // The payload is a zstd frame (not a bare DDS): the console has no zstd compressor, so packs
            // ship final on-disk bytes.
            Assert.Equal(0x28, payload[0]);
            Assert.Equal(0xB5, payload[1]);
            Assert.Equal(0x2F, payload[2]);
            Assert.Equal(0xFD, payload[3]);
        }

        // Both targets carry a stock hash, which is what lets an applier tell stock from already-changed.
        Assert.All(pack.Manifest.Targets, t => Assert.False(string.IsNullOrEmpty(t.StockSha256)));
        Assert.Equal("background", dx10.Layer);
        Assert.Equal("background", legacy.Layer);
    }
}
