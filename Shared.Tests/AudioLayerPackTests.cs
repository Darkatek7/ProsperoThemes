using System;
using System.IO;
using System.Linq;
using System.Text;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The audio layer, validated through the same engine as every other layer.
/// </summary>
/// <remarks>
/// This is the layer that was blocked longest, and not on anything in this repository: the console's sounds
/// are ATRAC9, for which no open-source encoder exists. Sony's own <c>at9tool</c> — which ships inside
/// ATRACTool's portable release, since that GUI is only a wrapper around it — produces output that matches
/// the stock assets structurally apart from their length.
/// </remarks>
public class AudioLayerPackTests
{
    private static string FixturePath(string name) =>
        Path.Combine(System.AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void An_audio_layer_pack_validates()
    {
        string path = FixturePath("audio-layer.p5theme");
        Assert.True(File.Exists(path), $"fixture is missing from the test output: {path}");

        ThemePack pack = ThemePack.OpenFile(path);
        PackReport report = pack.Validate();

        Assert.False(report.HasErrors, string.Join("; ", report.Errors.Select(e => e.Message)));
        Assert.Equal("audio-test", pack.Manifest.Id);

        ThemePackTarget target = Assert.Single(pack.Manifest.Targets);
        Assert.Equal("sound", target.Layer);
        Assert.Equal("vsh_asset/sfx_warmboot.at9", target.Path);
        Assert.Equal(target.Bytes, pack.Payload(target).LongLength);
    }

    [Fact]
    public void The_payload_is_a_riff_wave_container_carrying_atrac9()
    {
        // The stock .at9 assets are plain RIFF/WAVE with an ATRAC9 codec block, not an opaque Sony container,
        // so a replacement has to be the same thing. These offsets are the WAVE header's own.
        ThemePack pack = ThemePack.OpenFile(FixturePath("audio-layer.p5theme"));
        byte[] payload = pack.Payload(pack.Manifest.Targets.Single());

        Assert.Equal("RIFF", Encoding.ASCII.GetString(payload, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(payload, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(payload, 12, 4));

        // WAVE_FORMAT_EXTENSIBLE, which is what the console's own assets declare.
        // Cast to int rather than relying on overload inference: int against ushort/uint picks a
        // surprising Assert.Equal overload.
        Assert.Equal(0xFFFE, (int)BitConverter.ToUInt16(payload, 20));

        // Six channels at 48 kHz: every stock asset is 5.1, and at9tool refuses a channel count its bitrate
        // does not allow, so the creator converts the source rather than assuming it is compatible.
        Assert.Equal(6, (int)BitConverter.ToUInt16(payload, 22));
        Assert.Equal(48000, (int)BitConverter.ToUInt32(payload, 24));
    }

    [Fact]
    public void Every_layer_the_creator_emits_is_a_known_layer()
    {
        // The applier skips a target whose layer it does not recognise rather than refusing the pack, so a
        // layer emitted without being in KnownLayers would silently do nothing at all.
        string[] emitted = { "background", "glyph", "icon", "ui", "video", "sound" };
        Assert.All(emitted, layer => Assert.Contains(layer, ThemePackManifest.KnownLayers));
    }
}
