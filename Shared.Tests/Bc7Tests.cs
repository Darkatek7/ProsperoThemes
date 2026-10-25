using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The BC7 decoder against the store's TypeScript decoder (<c>texture/src/bc7-decode.ts</c> in the store repository, which was checked against
/// Pillow on console textures). <c>fixtures/bc7/expected.json</c> holds what that decoder makes of: a synthetic image with
/// 31 random blocks of each of the eight modes and some reserved ones, the real random-bit blocks of
/// <c>fixtures/zstd/bc7-l19.zst</c> (all eight modes occur), and the 3840x2160 backgrounds of two fixture packs;
/// <c>make-expected.mjs</c> there writes it.
/// </summary>
public class Bc7Tests
{
    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);

    private static JsonElement Expected(string name)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Fixture("bc7", "expected.json")));
        return doc.RootElement.GetProperty(name).Clone();
    }

    private static byte[] Modes() => File.ReadAllBytes(Fixture("bc7", "modes.bc7"));
    private static byte[] Real() => Zstd.Decompress(File.ReadAllBytes(Fixture("zstd", "bc7-l19.zst")));

    /// <summary>The pixels as the TypeScript decoder returns them: R, G, B, A bytes.</summary>
    private static byte[] Rgba(uint[] argb)
    {
        var bytes = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++)
        {
            bytes[i * 4] = (byte)(argb[i] >> 16);
            bytes[i * 4 + 1] = (byte)(argb[i] >> 8);
            bytes[i * 4 + 2] = (byte)argb[i];
            bytes[i * 4 + 3] = (byte)(argb[i] >> 24);
        }
        return bytes;
    }

    private static uint[] Decode(byte[] blocks, int width, int height, bool opaque = false)
    {
        var pixels = new uint[width * height];
        Bc7.Decode(blocks, width, height, pixels, width, opaque);
        return pixels;
    }

    private static uint[] DecodeHalf(byte[] blocks, int width, int height, bool opaque = false)
    {
        var pixels = new uint[width / 2 * (height / 2)];
        Bc7.DecodeHalf(blocks, width, height, pixels, width / 2, opaque);
        return pixels;
    }

    /// <summary>The definition of the half-size image: the average of every 2x2 group, per channel, rounded half up.</summary>
    private static uint[] Halved(uint[] full, int width, int height)
    {
        var half = new uint[width / 2 * (height / 2)];
        for (int y = 0; y < height / 2; y++)
            for (int x = 0; x < width / 2; x++)
            {
                uint pixel = 0;
                for (int shift = 0; shift < 32; shift += 8)
                {
                    uint sum = 2;
                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) }) sum += (full[(y * 2 + dy) * width + x * 2 + dx] >> shift) & 0xFF;
                    pixel |= (sum >> 2) << shift;
                }
                half[y * (width / 2) + x] = pixel;
            }
        return half;
    }

    private static void AssertPixels(JsonElement expected, uint[] pixels, int width)
    {
        foreach (JsonProperty sample in expected.GetProperty("pixels").EnumerateObject())
        {
            string[] at = sample.Name.Split(',');
            int x = int.Parse(at[0]), y = int.Parse(at[1]);
            byte[] rgba = sample.Value.EnumerateArray().Select(v => (byte)v.GetInt32()).ToArray();
            Assert.Equal(rgba, Rgba([pixels[y * width + x]]));
        }
    }

    [Fact]
    public void Every_mode_decodes_the_way_the_store_decoder_does()
    {
        JsonElement expected = Expected("modes");
        uint[] pixels = Decode(Modes(), 64, 64);

        Assert.Equal(expected.GetProperty("sha256").GetString(), ThemePack.Hash(Rgba(pixels)));
        AssertPixels(expected, pixels, 64);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void A_single_block_of_each_mode_decodes_the_way_the_store_decoder_does(int mode)
    {
        // Block <mode> of the synthetic image is mode <mode>: said by the lowest set bit of its first byte.
        byte[] block = Modes().AsSpan(mode * 16, 16).ToArray();
        Assert.Equal(mode, System.Numerics.BitOperations.TrailingZeroCount((int)block[0]));
        Span<uint> pixels = stackalloc uint[16];

        Bc7.DecodeBlock(block, pixels);

        Assert.Equal(Expected("modeBlocks").GetProperty(mode.ToString()).GetString(), ThemePack.Hash(Rgba(pixels.ToArray())));
    }

    [Fact]
    public void The_modes_image_really_has_every_mode_and_reserved_blocks()
    {
        byte[] blocks = Modes();
        var seen = new bool[9];
        for (int i = 0; i < blocks.Length; i += 16) seen[blocks[i] == 0 ? 8 : System.Numerics.BitOperations.TrailingZeroCount((int)blocks[i])] = true;
        Assert.All(seen, Assert.True);
    }

    [Fact]
    public void A_reserved_block_is_transparent_black()
    {
        var pixels = new uint[16];
        Array.Fill(pixels, 0xDEADBEEFu);
        Bc7.DecodeBlock(new byte[16], pixels);
        Assert.All(pixels, p => Assert.Equal(0u, p));
    }

    [Fact]
    public void The_real_blocks_decode_the_way_the_store_decoder_does_whole_and_cropped()
    {
        byte[] blocks = Real();
        JsonElement whole = Expected("real");
        uint[] pixels = Decode(blocks, 800, 400);
        Assert.Equal(whole.GetProperty("sha256").GetString(), ThemePack.Hash(Rgba(pixels)));
        AssertPixels(whole, pixels, 800);

        JsonElement cropped = Expected("realCropped");
        uint[] crop = Decode(blocks, 797, 391);
        Assert.Equal(cropped.GetProperty("sha256").GetString(), ThemePack.Hash(Rgba(crop)));
        AssertPixels(cropped, crop, 797);
    }

    [Fact]
    public void Opaque_sets_every_alpha_and_leaves_the_colours()
    {
        byte[] blocks = Modes();
        uint[] plain = Decode(blocks, 64, 64), opaque = Decode(blocks, 64, 64, opaque: true);
        Assert.Contains(plain, p => p >> 24 != 0xFF);   // the image does have other alphas
        Assert.Equal(plain.Select(p => p | 0xFF000000u), opaque);
    }

    [Fact]
    public void The_half_size_image_is_the_store_decoders_image_averaged_in_twos()
    {
        Assert.Equal(Expected("modes").GetProperty("halfSha256").GetString(), ThemePack.Hash(Rgba(DecodeHalf(Modes(), 64, 64))));
        Assert.Equal(Expected("real").GetProperty("halfSha256").GetString(), ThemePack.Hash(Rgba(DecodeHalf(Real(), 800, 400))));
    }

    [Theory]
    [InlineData(64, 64, false)]
    [InlineData(64, 64, true)]
    [InlineData(800, 400, false)]
    [InlineData(797, 391, true)]   // odd sizes: the last odd row and column are left out
    [InlineData(30, 22, false)]
    [InlineData(6, 4, false)]
    public void The_half_size_decode_equals_the_full_decode_averaged(int width, int height, bool opaque)
    {
        byte[] blocks = width == 64 ? Modes() : Real();
        uint[] full = Decode(blocks, width, height, opaque);
        uint[] half = DecodeHalf(blocks, width, height, opaque);

        Assert.Equal(Halved(full, width, height), half);
    }

    [Fact]
    public void The_half_size_decode_writes_into_a_wider_buffer_without_touching_the_rest()
    {
        byte[] blocks = Modes();
        uint[] expected = DecodeHalf(blocks, 64, 64);
        var wide = new uint[40 * 32];
        Array.Fill(wide, 0x12345678u);

        Bc7.DecodeHalf(blocks, 64, 64, wide, 40, opaque: false);

        for (int y = 0; y < 32; y++)
        {
            Assert.Equal(expected.AsSpan(y * 32, 32).ToArray(), wide.AsSpan(y * 40, 32).ToArray());
            Assert.All(wide.AsSpan(y * 40 + 32, 8).ToArray(), p => Assert.Equal(0x12345678u, p));
        }
    }

    [Fact]
    public void Data_that_is_too_short_or_a_buffer_that_is_too_small_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Decode(new byte[16 * 3], 8, 8));
        Assert.Throws<InvalidDataException>(() => DecodeHalf(new byte[16 * 3], 8, 8));
        Assert.Throws<ArgumentException>(() => Bc7.Decode(new byte[16 * 4], 8, 8, new uint[63], 8));
        Assert.Throws<ArgumentException>(() => Bc7.DecodeHalf(new byte[16 * 4], 8, 8, new uint[15], 4, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => DecodeHalf(new byte[16], 1, 4));
    }

    [Theory]
    [InlineData("two-screen.p5theme", "two-screen")]
    [InlineData("web-creator-export.p5theme", "web-creator-export")]
    public void The_real_4K_backgrounds_of_the_fixture_packs_decode_the_way_the_store_decoder_does(string packName, string name)
    {
        ThemePack pack = ThemePack.OpenFile(Fixture(packName));
        ThemePackTarget target = pack.Manifest.Targets.Single(t => t.Path == AssetRoots.HubBackground);
        byte[] dds = Zstd.Decompress(pack.Payload(target));
        DdsInfo info = Dds.Parse(dds);
        Assert.True(info.IsBc7);
        Assert.Equal((3840, 2160, 148), (info.Width, info.Height, info.DataOffset));
        JsonElement expected = Expected(name);
        ReadOnlySpan<byte> blocks = dds.AsSpan(info.DataOffset);

        var half = new uint[1920 * 1080];
        Bc7.DecodeHalf(blocks, 3840, 2160, half, 1920, opaque: false);
        Assert.Equal(expected.GetProperty("halfSha256").GetString(), ThemePack.Hash(Rgba(half)));

        var full = new uint[3840 * 2160];
        Bc7.Decode(blocks, 3840, 2160, full, 3840);
        Assert.Equal(expected.GetProperty("sha256").GetString(), ThemePack.Hash(Rgba(full)));
        AssertPixels(expected, full, 3840);
    }

    // ---- The DDS header ---------------------------------------------------------------------------------------------

    private static byte[] Header(int width, int height, string fourCc, int dxgi = 0)
    {
        var dds = new byte[fourCc == "DX10" ? 148 : 128];
        "DDS "u8.CopyTo(dds);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80), 4);
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(dds, 84);
        if (fourCc == "DX10") BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(128), (uint)dxgi);
        return dds;
    }

    [Theory]
    [InlineData(97)]
    [InlineData(98)]
    [InlineData(99)]
    public void A_dx10_header_names_the_size_the_format_and_where_the_data_starts(int dxgi)
    {
        DdsInfo info = Dds.Parse(Header(3840, 2160, "DX10", dxgi));
        Assert.Equal(new DdsInfo(3840, 2160, dxgi, "DX10", 148), info);
        Assert.True(info.IsBc7);
    }

    [Fact]
    public void Other_formats_are_read_but_are_not_bc7()
    {
        DdsInfo legacy = Dds.Parse(Header(256, 128, "DXT5"));
        Assert.Equal(new DdsInfo(256, 128, 0, "DXT5", 128), legacy);
        Assert.False(legacy.IsBc7);
        Assert.False(Dds.Parse(Header(16, 16, "DX10", 71)).IsBc7);   // BC1
    }

    [Fact]
    public void Anything_that_is_not_a_whole_dds_header_is_refused()
    {
        byte[] good = Header(16, 16, "DX10", 98);
        Assert.Throws<InvalidDataException>(() => Dds.Parse(good.AsSpan(0, 100)));
        Assert.Throws<InvalidDataException>(() => Dds.Parse(good.AsSpan(0, 140)));   // the DX10 part is cut off
        Assert.Throws<InvalidDataException>(() => Dds.Parse("not a dds file at all, only text"u8.ToArray().Concat(new byte[200]).ToArray()));
        byte[] zero = (byte[])good.Clone();
        zero[16] = zero[17] = zero[18] = zero[19] = 0;
        Assert.Throws<InvalidDataException>(() => Dds.Parse(zero));
        byte[] huge = Header(20000, 16, "DX10", 98);
        Assert.Throws<InvalidDataException>(() => Dds.Parse(huge));
        Assert.True(Dds.HasMagic(good));
        Assert.False(Dds.HasMagic("DDS"u8));
    }
}
