using System.IO;
using System.Linq;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The video layer, validated through the same engine as backgrounds and PNGs.
/// </summary>
/// <remarks>
/// Video is the third layer to ride the engine unchanged. It is also the first where the payload has to
/// match the target's own encoding parameters rather than just its size: the console's clips are HEVC
/// (tagged <c>hev1</c> or <c>hvc1</c>) at a specific resolution and frame rate, and a mismatch is a clip
/// that will not play. That is the creator's job, but the engine has to carry and verify the bytes.
/// </remarks>
public class VideoLayerPackTests
{
    private static string FixturePath(string name) =>
        Path.Combine(System.AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void A_video_layer_pack_validates()
    {
        string path = FixturePath("video-layer.p5theme");
        Assert.True(File.Exists(path), $"fixture is missing from the test output: {path}");

        ThemePack pack = ThemePack.OpenFile(path);
        PackReport report = pack.Validate();

        Assert.False(report.HasErrors, string.Join("; ", report.Errors.Select(e => e.Message)));
        Assert.Equal("video-test", pack.Manifest.Id);

        ThemePackTarget target = Assert.Single(pack.Manifest.Targets);
        Assert.Equal("video", target.Layer);
        Assert.Equal("vsh_asset/psbutton_press.mp4", target.Path);
    }

    [Fact]
    public void The_payload_is_an_iso_base_media_file()
    {
        ThemePack pack = ThemePack.OpenFile(FixturePath("video-layer.p5theme"));
        ThemePackTarget target = pack.Manifest.Targets.Single();
        byte[] payload = pack.Payload(target);

        Assert.Equal(target.Bytes, payload.LongLength);
        Assert.Equal(target.Sha256, ThemePack.Hash(payload));
        Assert.False(string.IsNullOrEmpty(target.StockSha256));

        // ISO base media: a 4-byte box size followed by 'ftyp'. The shell's player expects a real MP4, and
        // the creator re-encodes to the stock clip's codec, tag, dimensions and frame rate rather than
        // shipping whatever the user supplied.
        Assert.Equal((byte)'f', payload[4]);
        Assert.Equal((byte)'t', payload[5]);
        Assert.Equal((byte)'y', payload[6]);
        Assert.Equal((byte)'p', payload[7]);
    }

    [Fact]
    public void Every_layer_the_creator_emits_is_a_known_layer()
    {
        // The applier *skips* a target whose layer it does not know, rather than refusing the pack. That is
        // what makes newer packs degrade gracefully on an older app -- but it also means a layer added here
        // without being added to KnownLayers would silently do nothing at all.
        string[] emitted = { "background", "glyph", "icon", "ui", "video" };
        Assert.All(emitted, layer => Assert.Contains(layer, ThemePackManifest.KnownLayers));
    }
}
