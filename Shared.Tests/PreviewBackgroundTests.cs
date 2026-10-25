using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using Xunit;
using static ProsperoThemes.Shared.Tests.PreviewKit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>Home backgrounds made in code: a BC7 DDS of one colour, plain or in a zstd frame.</summary>
internal static class BackgroundKit
{
    /// <summary>A mode 6 block whose 16 pixels are all (r, g, b, a): both endpoints that colour, every index 0.</summary>
    public static byte[] SolidBlock(byte r, byte g, byte b, byte a)
    {
        var block = new byte[16];
        int pos = 0;
        void Put(int value, int count)
        {
            for (int i = 0; i < count; i++, pos++)
                if (((value >> i) & 1) != 0) block[pos >> 3] |= (byte)(1 << (pos & 7));
        }
        Put(1 << 6, 7);                                              // mode 6
        foreach (byte v in new[] { r, r, g, g, b, b, a, a }) Put(v >> 1, 7);   // endpoints: seven bits each, red, green, blue, alpha
        Put(r & 1, 1);                                               // the p-bit of an endpoint is the low bit of all its channels...
        Put(r & 1, 1);                                               // ...so the colours must share it: the caller passes such values
        return block;
    }

    /// <summary>A DX10 BC7 DDS (DXGI 98) of <paramref name="width"/> x <paramref name="height"/> in one colour. The colour needs equal low bits in all four channels (mode 6 has one p-bit).</summary>
    public static byte[] Dds(int width, int height, byte r, byte g, byte b, byte a = 254)
    {
        if (((r ^ g) | (r ^ b) | (r ^ a)) % 2 != 0) throw new ArgumentException("the four channels need the same low bit (mode 6 has one p-bit)");
        int blocks = (width + 3) / 4 * ((height + 3) / 4);
        var dds = new byte[148 + blocks * 16];
        "DDS "u8.CopyTo(dds);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80), 4);
        "DX10"u8.CopyTo(dds.AsSpan(84));
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(128), 98);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(132), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(140), 1);
        byte[] block = SolidBlock(r, g, b, a);
        for (int i = 0; i < blocks; i++) block.CopyTo(dds, 148 + i * 16);
        return dds;
    }

    /// <summary>A zstd frame of <paramref name="data"/> made of raw blocks (nothing here can compress).</summary>
    public static byte[] Zstd(byte[] data)
    {
        var frame = new MemoryStream();
        frame.Write([0x28, 0xB5, 0x2F, 0xFD, 0xA0]);   // magic; single segment with a 4-byte content size
        frame.Write(BitConverter.GetBytes(data.Length));
        for (int at = 0; at < data.Length || at == 0; at += 128 * 1024)
        {
            int size = Math.Min(128 * 1024, data.Length - at);
            uint header = (at + size >= data.Length ? 1u : 0u) | ((uint)size << 3);   // last-block bit, raw type
            frame.Write([(byte)header, (byte)(header >> 8), (byte)(header >> 16)]);
            frame.Write(data, at, size);
            if (data.Length == 0) break;
        }
        return frame.ToArray();
    }

    public static byte[] Background(int width, int height, byte r, byte g, byte b, byte a = 254) => Zstd(Dds(width, height, r, g, b, a));
}

/// <summary>
/// A theme's real home background in the preview: the payload of a <c>bg_*.dds.zst</c> target (zstd'd BC7) decoded to the
/// screen's size on a worker, and the files and keys the model names for it.
/// </summary>
public class PreviewBackgroundTests
{
    private static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)(r << 16 | g << 8 | b);

    [Fact]
    public void The_test_kit_makes_the_colour_it_says()
    {
        Span<uint> pixels = stackalloc uint[16];
        Bc7.DecodeBlock(BackgroundKit.SolidBlock(200, 100, 50, 24), pixels);
        Assert.All(pixels.ToArray(), p => Assert.Equal(0x18C86432u, p));
    }

    [Fact]
    public void A_4K_background_is_decoded_straight_to_the_screens_size_and_is_opaque()
    {
        // Alpha 0 in the texture: a home background has none, so the picture is opaque all the same.
        using PixelBuffer picture = PreviewVisuals.DecodeBackground(BackgroundKit.Background(3840, 2160, 40, 120, 200, 0));

        Assert.Equal((1920, 1080), (picture.Width, picture.Height));
        foreach (var (x, y) in new[] { (0, 0), (1919, 0), (0, 1079), (1919, 1079), (960, 540), (1234, 777) })
            Assert.Equal(Rgb(40, 120, 200), PixelAt(picture, x, y));
    }

    [Fact]
    public void A_plain_dds_is_taken_as_it_is_and_another_size_is_decoded_whole_for_scaling()
    {
        using PixelBuffer picture = PreviewVisuals.DecodeBackground(BackgroundKit.Dds(64, 36, 200, 100, 50));

        Assert.Equal((64, 36), (picture.Width, picture.Height));
        Assert.Equal(Rgb(200, 100, 50), PixelAt(picture, 63, 35));
        using PixelBuffer screen = PreviewVisuals.DecodeBackground(BackgroundKit.Dds(1920, 1080, 10, 20, 30));
        Assert.Equal((1920, 1080), (screen.Width, screen.Height));
    }

    [Fact]
    public void Anything_that_is_not_a_bc7_dds_is_refused_with_an_exception_the_worker_catches()
    {
        byte[] good = BackgroundKit.Dds(64, 36, 2, 2, 2);
        byte[] dxt5 = (byte[])good.Clone();
        "DXT5"u8.CopyTo(dxt5.AsSpan(84));
        byte[] bc1 = (byte[])good.Clone();
        bc1[128] = 71;

        Assert.Throws<InvalidDataException>(() => PreviewVisuals.DecodeBackground(dxt5));
        Assert.Throws<InvalidDataException>(() => PreviewVisuals.DecodeBackground(bc1));
        Assert.Throws<InvalidDataException>(() => PreviewVisuals.DecodeBackground([1, 2, 3, 4, 5, 6, 7, 8]));            // not zstd, not a DDS
        Assert.Throws<InvalidDataException>(() => PreviewVisuals.DecodeBackground(good.AsSpan(0, 148 + 16 * 100).ToArray()));   // the blocks are cut short
        Assert.Throws<InvalidDataException>(() => PreviewVisuals.DecodeBackground(BackgroundKit.Zstd(good.AsSpan(0, 100).ToArray())));   // a cut-off header
        Assert.Throws<InvalidDataException>(() => PreviewVisuals.DecodeBackground(BackgroundKit.Zstd([0x44, 0x44, 0x53, 0x20, 1, 2, 3])));
    }

    [Fact]
    public void A_background_bigger_than_the_console_has_is_refused_before_anything_big_is_allocated()
    {
        // The header of an 8K texture over a few blocks: refused on its size, not on the missing data.
        byte[] huge = BackgroundKit.Dds(8, 8, 2, 2, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(12), 4320);
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(16), 7680);
        Assert.Throws<InvalidDataException>(() => PreviewVisuals.DecodeBackground(huge));

        // A zstd frame that claims to inflate to more than a 4K texture needs is refused by the decoder's own limit.
        byte[] claim = BackgroundKit.Zstd(new byte[16]);
        BinaryPrimitives.WriteUInt32LittleEndian(claim.AsSpan(5), (uint)PreviewVisuals.MaxBackgroundBytes + 1);
        Assert.Throws<InvalidDataException>(() => PreviewVisuals.DecodeBackground(claim));
    }

    [Fact]
    public void A_consumed_source_is_reused_when_it_has_the_screens_size_and_freed_when_it_does_not()
    {
        var screen = new PixelBuffer(1920, 1080);
        screen.Clear(Color.FromRgb(9, 8, 7));
        PreviewPicture reused = ThemeRenderer.BuildPreviewPicture(screen, smooth: true, consume: true);
        Assert.Same(screen, reused.Full);   // no copy of the 8 MB
        Assert.Equal(Rgb(9, 8, 7), PixelAt(reused.Full, 1000, 500));

        var small = new PixelBuffer(64, 36);
        small.Clear(Color.FromRgb(9, 8, 7));
        PreviewPicture scaled = ThemeRenderer.BuildPreviewPicture(small, smooth: true, consume: true);
        Assert.Throws<ObjectDisposedException>(() => small.AsSurface());
        Assert.Equal((1920, 1080), (scaled.Full.Width, scaled.Full.Height));

        var kept = new PixelBuffer(1920, 1080);
        PreviewPicture copy = ThemeRenderer.BuildPreviewPicture(kept, smooth: true);
        Assert.NotSame(kept, copy.Full);   // the default leaves the source to the caller
        kept.AsSurface();
        reused.Dispose();
        scaled.Dispose();
        copy.Dispose();
        kept.Dispose();
    }

    // ---- The files the preview reads --------------------------------------------------------------------------------

    private static ThemePackTarget Bg(string path, string file) => Target(path, file, layer: "texture");

    private static string TileBackground(string id) => $"vsh_asset/bg_{id}.dds.zst";

    [Fact]
    public void The_backgrounds_are_read_once_each_and_the_main_one_first_within_a_total_budget()
    {
        byte[] Payload(int size, byte fill) => Enumerable.Repeat(fill, size).ToArray();
        PreviewModel model = PreviewModel.Of(Manifest([
            Bg(AssetRoots.HubBackground, "assets/hub.zst"),
            Bg(TileBackground("NPXS40047"), "assets/store.zst"),
            Bg(TileBackground("NPXS40037"), "assets/plus.zst"),
            Bg(TileBackground("NPXS40071"), "assets/library.zst"),
        ]));
        Assert.Equal(["assets/hub.zst", "assets/store.zst", "assets/plus.zst", "assets/library.zst"], model.BackgroundFiles);

        // 10 MB each: the first two fit in the 24 MB budget, the third would not; a smaller fourth still does.
        PreviewAssets assets = PreviewAssets.Load(model, Archive(Manifest(),
            ("assets/hub.zst", Payload(10 << 20, 1)), ("assets/store.zst", Payload(10 << 20, 2)), ("assets/plus.zst", Payload(10 << 20, 3)), ("assets/library.zst", Payload(1 << 20, 4))));

        Assert.NotNull(assets.Get("assets/hub.zst"));
        Assert.NotNull(assets.Get("assets/store.zst"));
        Assert.Null(assets.Get("assets/plus.zst"));
        Assert.NotNull(assets.Get("assets/library.zst"));
    }

    [Fact]
    public void A_background_over_the_size_limit_or_missing_from_the_pack_is_left_out_without_trouble()
    {
        PreviewModel model = PreviewModel.Of(Manifest([Bg(AssetRoots.HubBackground, "assets/hub.zst"), Bg(TileBackground("NPXS40047"), "assets/gone.zst")]));

        PreviewAssets assets = PreviewAssets.Load(model, Archive(Manifest(), ("assets/hub.zst", new byte[PreviewAssets.MaxBackground + 1])));

        Assert.Null(assets.Get("assets/hub.zst"));
        Assert.Null(assets.Get("assets/gone.zst"));
        Assert.Equal(0, assets.Count);
    }

    [Fact]
    public void Only_the_backgrounds_the_preview_names_are_read_out_of_a_pack()
    {
        // The pack has a background for a tile the model does not know a key for (Live TV has no home background) and one
        // for a screen of its own: neither is read.
        PreviewModel model = PreviewModel.Of(Manifest([
            Bg(AssetRoots.HubBackground, "assets/hub.zst"),
            Bg("vsh_asset/bg_NPXS40305.dds.zst", "assets/tv.zst"),
            Bg("vsh_asset/bg_image_load_error.dds.zst", "assets/error.zst"),
        ]));

        PreviewAssets assets = PreviewAssets.Load(model, Archive(Manifest(), ("assets/hub.zst", [1]), ("assets/tv.zst", [2]), ("assets/error.zst", [3])));

        Assert.Equal(["assets/hub.zst"], model.BackgroundFiles);
        Assert.NotNull(assets.Get("assets/hub.zst"));
        Assert.Null(assets.Get("assets/tv.zst"));
        Assert.Null(assets.Get("assets/error.zst"));
    }
}
