using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ProsperoThemes.Shared.Tests;

/// <summary>DXT1 files and BC7 pictures made in code, for the tests of the title background conversion.</summary>
internal static class Dxt1Kit
{
    /// <summary>
    /// A PS4 title's pic0.dds as the console keeps it: a legacy header (flags, linear size, one mip, the DXT1 FourCC, caps) and
    /// <paramref name="width"/> x <paramref name="height"/> / 2 bytes of blocks, here a repeating pattern so a copy can be recognised.
    /// The reserved words of the header hold a mark, so a test sees whether the console's header was copied as it is.
    /// </summary>
    public static byte[] Console(int width, int height, byte pattern = 0x5A, string fourCc = "DXT1", uint mips = 1)
    {
        var dds = new byte[128 + width * height / 2];
        "DDS "u8.CopyTo(dds);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(8), 0x00081007);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(20), (uint)(width * height / 2));
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(28), mips);
        for (int i = 32; i < 76; i++) dds[i] = (byte)(0xC0 + i % 16);   // reserved1[11]
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80), 4);
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(dds, 84);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(108), 0x1000);
        for (int i = 128; i < dds.Length; i++) dds[i] = (byte)(pattern + i);
        return dds;
    }

    /// <summary>
    /// A DX10 BC7 DDS (DXGI 98) whose 4x4 block (bx, by) is one colour: <paramref name="colour"/> returns its red, green and blue,
    /// which need the same low bit (mode 6 has one p-bit); alpha is 254.
    /// </summary>
    public static byte[] Bc7(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour)
    {
        byte[] dds = BackgroundKit.Dds(width, height, 0, 0, 0);
        int blocksWide = (width + 3) / 4;
        for (int by = 0; by < (height + 3) / 4; by++)
            for (int bx = 0; bx < blocksWide; bx++)
            {
                var (r, g, b) = colour(bx, by);
                BackgroundKit.SolidBlock(r, g, b, 254).CopyTo(dds, 148 + (by * blocksWide + bx) * 16);
            }
        return dds;
    }

    /// <summary>Colour of block (x, y) of the test pictures: every channel even, so mode 6 holds it.</summary>
    public static (byte R, byte G, byte B) Colour(int x, int y) => ((byte)((x * 34) & 0xFE), (byte)((y * 58) & 0xFE), (byte)((x * 7 + y * 13 + 40) & 0xFE));
}

public class TitleBackgroundTests(ITestOutputHelper output)
{
    private static int Channel(uint pixel, int shift) => (int)(pixel >> shift) & 255;

    /// <summary>The picture of a DXT1 file made by the conversion, decoded.</summary>
    private static uint[] Decode(byte[] dxt1, int width, int height)
    {
        var pixels = new uint[width * height];
        Bc1.Decode(dxt1.AsSpan(128), width, height, pixels, width);
        return pixels;
    }

    private static void AssertNear(int expected, int actual, int tolerance, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    [Fact]
    public void A_payload_of_twice_the_size_is_halved_into_the_console_files_dxt1()
    {
        // 64x32 BC7 with a colour per 8x8 pixels: after halving, a colour per 4x4 block of the 32x16 console file.
        byte[] payload = Dxt1Kit.Bc7(64, 32, (bx, by) => Dxt1Kit.Colour(bx / 2, by / 2));
        byte[] console = Dxt1Kit.Console(32, 16);

        byte[]? dxt1 = TitleBackground.ToDxt1(payload, console, out string refusal);

        Assert.NotNull(dxt1);
        Assert.Equal("", refusal);
        Assert.Equal(128 + 32 * 16 / 2, dxt1.Length);
        Assert.Equal(console.AsSpan(0, 128).ToArray(), dxt1.AsSpan(0, 128).ToArray());
        uint[] pixels = Decode(dxt1, 32, 16);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++)
            {
                var (r, g, b) = Dxt1Kit.Colour(x / 4, y / 4);
                AssertNear(r, Channel(pixels[y * 32 + x], 16), 3, $"red at {x},{y}");
                AssertNear(g, Channel(pixels[y * 32 + x], 8), 3, $"green at {x},{y}");
                AssertNear(b, Channel(pixels[y * 32 + x], 0), 3, $"blue at {x},{y}");
            }
    }

    [Fact]
    public void A_payload_of_the_consoles_size_is_converted_as_it_is()
    {
        byte[] payload = Dxt1Kit.Bc7(32, 16, Dxt1Kit.Colour);
        byte[] console = Dxt1Kit.Console(32, 16);

        byte[]? dxt1 = TitleBackground.ToDxt1(payload, console, out _);

        Assert.NotNull(dxt1);
        Assert.Equal(console.AsSpan(0, 128).ToArray(), dxt1.AsSpan(0, 128).ToArray());
        uint[] pixels = Decode(dxt1, 32, 16);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++)
            {
                var (r, g, b) = Dxt1Kit.Colour(x / 4, y / 4);
                AssertNear(r, Channel(pixels[y * 32 + x], 16), 3, $"red at {x},{y}");
                AssertNear(g, Channel(pixels[y * 32 + x], 8), 3, $"green at {x},{y}");
                AssertNear(b, Channel(pixels[y * 32 + x], 0), 3, $"blue at {x},{y}");
            }
    }

    [Fact]
    public void Only_the_consoles_header_is_taken_from_it_and_the_rest_may_be_left_out()
    {
        byte[] payload = Dxt1Kit.Bc7(64, 32, Dxt1Kit.Colour);
        byte[] console = Dxt1Kit.Console(32, 16);
        byte[]? withBody = TitleBackground.ToDxt1(payload, console, out _);
        byte[]? headerOnly = TitleBackground.ToDxt1(payload, console.AsSpan(0, 128), out _);
        Assert.NotNull(headerOnly);
        Assert.Equal(withBody, headerOnly);
    }

    [Fact]
    public void The_real_3840x2160_background_becomes_the_1036928_bytes_of_a_ps4_title()
    {
        ThemePack pack = ThemePack.OpenFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "two-screen.p5theme"));
        byte[] payload = Zstd.Decompress(pack.Payload(pack.Manifest.Targets.Single(t => t.Path == AssetRoots.HubBackground)));
        byte[] console = Dxt1Kit.Console(1920, 1080);
        Assert.Equal(1_036_928, console.Length);

        var timer = Stopwatch.StartNew();
        byte[]? dxt1 = TitleBackground.ToDxt1(payload, console, out string refusal);
        timer.Stop();
        output.WriteLine($"3840x2160 BC7 -> 1920x1080 DXT1: {timer.Elapsed.TotalMilliseconds:F0} ms");

        Assert.NotNull(dxt1);
        Assert.Equal(1_036_928, dxt1.Length);
        Assert.Equal(console.AsSpan(0, 128).ToArray(), dxt1.AsSpan(0, 128).ToArray());

        // Against the picture the BC7 decoder gives at that size.
        DdsInfo info = Dds.Parse(payload);
        var half = new uint[1920 * 1080];
        Bc7.DecodeHalf(payload.AsSpan(info.DataOffset), 3840, 2160, half, 1920, opaque: true);
        uint[] made = Decode(dxt1, 1920, 1080);
        long sum = 0;
        for (int i = 0; i < half.Length; i++)
            sum += Math.Abs(Channel(half[i], 16) - Channel(made[i], 16)) + Math.Abs(Channel(half[i], 8) - Channel(made[i], 8)) + Math.Abs(Channel(half[i], 0) - Channel(made[i], 0));
        double mean = sum / (3.0 * half.Length);
        output.WriteLine($"mean absolute error per channel: {mean:F2}");
        Assert.True(mean < 6, $"mean absolute error per channel {mean}");
    }

    [Fact]
    public void Anything_the_conversion_cannot_do_is_refused_with_a_reason()
    {
        byte[] bc7 = Dxt1Kit.Bc7(64, 32, Dxt1Kit.Colour);
        byte[] console = Dxt1Kit.Console(32, 16);

        // The theme's file is not BC7: a DXT1 file, a DX10 file in another format, something that is no DDS at all, a cut-off one.
        Refused(Dxt1Kit.Console(64, 32), console, "not BC7");
        byte[] rgba = (byte[])bc7.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(128), 28);   // R8G8B8A8_UNORM
        Refused(rgba, console, "not BC7");
        Refused(new byte[300], console, "readable DDS");
        Refused(bc7.AsSpan(0, bc7.Length - 16).ToArray(), console, "cut off");

        // The console's file is not a plain DXT1: a DX10 header, another FourCC, mips, a size that is not a multiple of 4, no header.
        byte[] dx10 = Dxt1Kit.Console(32, 16, fourCc: "DX10");
        Refused(bc7, dx10, "DXT1");
        Refused(bc7, Dxt1Kit.Console(32, 16, fourCc: "DXT5"), "DXT1");
        Refused(bc7, Dxt1Kit.Console(32, 16, mips: 5), "DXT1");
        Refused(Dxt1Kit.Bc7(60, 28, Dxt1Kit.Colour), Dxt1Kit.Console(30, 14), "DXT1");
        Refused(bc7, new byte[100], "DXT1");
        Refused(bc7, bc7, "DXT1");

        // The sizes: a ratio of 1.5, of 4, a different shape.
        Refused(Dxt1Kit.Bc7(48, 24, Dxt1Kit.Colour), console, "48x24");
        Refused(Dxt1Kit.Bc7(128, 64, Dxt1Kit.Colour), console, "128x64");
        Refused(Dxt1Kit.Bc7(64, 64, Dxt1Kit.Colour), console, "64x64");
    }

    private static void Refused(byte[] payload, byte[] console, string reason)
    {
        Assert.Null(TitleBackground.ToDxt1(payload, console, out string refusal));
        Assert.Contains(reason, refusal);
    }

    [Fact]
    public void Applies_when_the_console_has_dxt1_and_the_theme_has_bc7()
    {
        byte[] bc7 = Dxt1Kit.Bc7(64, 32, Dxt1Kit.Colour);
        Assert.True(TitleBackground.Applies(bc7, Dxt1Kit.Console(32, 16)));
        Assert.False(TitleBackground.Applies(bc7, bc7));                                  // BC7 on both sides: PayloadCompat decides
        Assert.False(TitleBackground.Applies(Dxt1Kit.Console(64, 32), Dxt1Kit.Console(32, 16)));   // DXT1 on both sides
        Assert.False(TitleBackground.Applies([1, 2, 3], Dxt1Kit.Console(32, 16)));
        Assert.Equal(128 + 32 * 16 / 2, TitleBackground.Dxt1Length(Dxt1Kit.Console(32, 16)));
        Assert.Equal(-1, TitleBackground.Dxt1Length(bc7));
    }
}
