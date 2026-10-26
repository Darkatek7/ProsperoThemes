using System;
using System.Linq;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>The BC1 (DXT1) encoder and decoder, which turn a theme's background into a PS4 title's entry background.</summary>
public class Bc1Tests
{
    private static uint Argb(int r, int g, int b) => 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
    private static int R(uint c) => (int)(c >> 16) & 255;
    private static int G(uint c) => (int)(c >> 8) & 255;
    private static int B(uint c) => (int)c & 255;

    private static uint[] Fill(uint colour) => Enumerable.Repeat(colour, 16).ToArray();

    private static byte[] Encode(uint[] block)
    {
        var bytes = new byte[8];
        Bc1.EncodeBlock(block, bytes);
        return bytes;
    }

    private static uint[] Decode(byte[] block)
    {
        var pixels = new uint[16];
        Bc1.DecodeBlock(block, pixels);
        return pixels;
    }

    private static ushort C0(byte[] block) => BitConverter.ToUInt16(block, 0);
    private static ushort C1(byte[] block) => BitConverter.ToUInt16(block, 2);
    private static uint Indices(byte[] block) => BitConverter.ToUInt32(block, 4);

    // Colours that 5/6/5 bits hold exactly: the expansion of a 5-bit value v is (v << 3) | (v >> 2), of a 6-bit one (v << 2) | (v >> 4).
    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(8, 4, 8)]        // one step above black in 5, 6 and 5 bits
    [InlineData(123, 130, 255)]  // 5-bit 15 expands to 123, 6-bit 32 to 130
    public void A_colour_that_565_can_hold_comes_back_exactly(int r, int g, int b)
    {
        byte[] block = Encode(Fill(Argb(r, g, b)));
        Assert.All(Decode(block), p => Assert.Equal(Argb(r, g, b), p));
    }

    [Fact]
    public void A_solid_grey_stays_within_two_levels_for_every_value()
    {
        int worst = 0;
        for (int v = 0; v < 256; v++)
        {
            uint[] decoded = Decode(Encode(Fill(Argb(v, v, v))));
            Assert.All(decoded, p => Assert.Equal(decoded[0], p));
            worst = Math.Max(worst, Math.Max(Math.Abs(R(decoded[0]) - v), Math.Max(Math.Abs(G(decoded[0]) - v), Math.Abs(B(decoded[0]) - v))));
        }
        Assert.True(worst <= 2, $"worst error {worst}");
    }

    [Fact]
    public void The_block_layout_is_two_little_endian_565_endpoints_then_the_indices_with_pixel_zero_lowest()
    {
        // Left half red, right half blue: red (0xF800) is the larger endpoint, so it is c0 and index 0; blue is c1, index 1.
        uint red = Argb(255, 0, 0), blue = Argb(0, 0, 255);
        uint[] leftRed = new uint[16];
        for (int i = 0; i < 16; i++) leftRed[i] = i % 4 < 2 ? red : blue;
        Assert.Equal(new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0x50, 0x50, 0x50, 0x50 }, Encode(leftRed));

        // The other way round, c0 > c1 still holds and the indices are remapped: blue pixels are index 1 and sit in the low bits.
        uint[] leftBlue = new uint[16];
        for (int i = 0; i < 16; i++) leftBlue[i] = i % 4 < 2 ? blue : red;
        Assert.Equal(new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0x05, 0x05, 0x05, 0x05 }, Encode(leftBlue));
    }

    [Fact]
    public void The_decoder_builds_the_four_colour_palette_of_a_known_block()
    {
        // c0 = red 0xF800, c1 = blue 0x001F; pixels 0..3 use indices 0, 1, 2, 3 and the rest index 0.
        byte[] block = [0x00, 0xF8, 0x1F, 0x00, 0b11_10_01_00, 0, 0, 0];
        uint[] pixels = Decode(block);
        Assert.Equal(Argb(255, 0, 0), pixels[0]);
        Assert.Equal(Argb(0, 0, 255), pixels[1]);
        Assert.Equal(Argb(170, 0, 85), pixels[2]);   // (2*c0 + c1 + 1) / 3
        Assert.Equal(Argb(85, 0, 170), pixels[3]);   // (c0 + 2*c1 + 1) / 3
        Assert.Equal(Argb(255, 0, 0), pixels[4]);
    }

    [Fact]
    public void The_decoder_reads_c0_not_above_c1_as_three_colours_and_transparent_black()
    {
        byte[] block = [0x1F, 0x00, 0x00, 0xF8, 0b11_10_01_00, 0, 0, 0];   // c0 = blue < c1 = red
        uint[] pixels = Decode(block);
        Assert.Equal(Argb(0, 0, 255), pixels[0]);
        Assert.Equal(Argb(255, 0, 0), pixels[1]);
        Assert.Equal(Argb(128, 0, 128), pixels[2]);
        Assert.Equal(0u, pixels[3]);
    }

    [Fact]
    public void Every_block_has_c0_above_c1_or_is_one_colour_with_index_zero()
    {
        var random = new Random(7);
        var pixels = new uint[16];
        for (int n = 0; n < 5000; n++)
        {
            int kind = n % 4;
            for (int i = 0; i < 16; i++)
                pixels[i] = kind switch
                {
                    0 => Argb(random.Next(256), random.Next(256), random.Next(256)),                  // noise
                    1 => Argb(100 + random.Next(3), 100 + random.Next(3), 100 + random.Next(3)),      // nearly one colour
                    2 => random.Next(2) == 0 ? Argb(10, 200, 30) : Argb(200, 10, 90),                 // two colours
                    _ => Argb(Math.Min(255, i * 16), Math.Min(255, 255 - i * 16), 128),               // a ramp
                };
            byte[] block = Encode(pixels);
            if (C0(block) == C1(block)) Assert.Equal(0u, Indices(block));
            else Assert.True(C0(block) > C1(block), $"block {n}: {C0(block):X4} <= {C1(block):X4}");
        }
    }

    [Fact]
    public void Two_colours_in_any_arrangement_round_trip_exactly()
    {
        var random = new Random(11);
        uint a = Argb(255, 0, 0), b = Argb(0, 255, 0);
        for (int n = 0; n < 200; n++)
        {
            uint[] pixels = Enumerable.Range(0, 16).Select(_ => random.Next(2) == 0 ? a : b).ToArray();
            Assert.Equal(pixels, Decode(Encode(pixels)));
        }
    }

    [Fact]
    public void A_smooth_gradient_stays_within_a_small_error()
    {
        const int size = 64;
        var image = new uint[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                image[y * size + x] = Argb(x * 4, y * 4, 60 + (x + y) * 2);
        var blocks = new byte[size / 4 * (size / 4) * Bc1.BlockBytes];
        Bc1.Encode(image, size, size, size, blocks);
        var decoded = new uint[size * size];
        Bc1.Decode(blocks, size, size, decoded, size);

        (double mean, int worst) = Error(image, decoded);
        Assert.True(mean <= 2.5, $"mean absolute error per channel {mean}");
        Assert.True(worst <= 12, $"worst error {worst}");
    }

    [Fact]
    public void Noise_costs_more_but_stays_bounded()
    {
        const int size = 32;
        var random = new Random(3);
        var image = new uint[size * size];
        for (int i = 0; i < image.Length; i++) image[i] = Argb(random.Next(256), random.Next(256), random.Next(256));
        var blocks = new byte[size / 4 * (size / 4) * Bc1.BlockBytes];
        Bc1.Encode(image, size, size, size, blocks);
        var decoded = new uint[size * size];
        Bc1.Decode(blocks, size, size, decoded, size);
        (double mean, _) = Error(image, decoded);
        Assert.True(mean <= 46, $"mean absolute error per channel {mean}");   // uniform noise, 64 off its mean on average: ~42 is what four colours on a line leave
    }

    private static (double Mean, int Worst) Error(uint[] expected, uint[] actual)
    {
        long sum = 0;
        int worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int[] d = [Math.Abs(R(expected[i]) - R(actual[i])), Math.Abs(G(expected[i]) - G(actual[i])), Math.Abs(B(expected[i]) - B(actual[i]))];
            sum += d.Sum();
            worst = Math.Max(worst, d.Max());
        }
        return (sum / (3.0 * expected.Length), worst);
    }

    [Fact]
    public void A_size_that_is_not_a_multiple_of_four_repeats_the_edge_and_decodes_cropped()
    {
        const int width = 6, height = 5;
        var image = new uint[width * height];
        Array.Fill(image, Argb(255, 0, 0));
        var blocks = new byte[2 * 2 * Bc1.BlockBytes];
        Bc1.Encode(image, width, height, width, blocks);
        var decoded = new uint[width * height];
        Bc1.Decode(blocks, width, height, decoded, width);
        Assert.All(decoded, p => Assert.Equal(Argb(255, 0, 0), p));
    }

    [Fact]
    public void Too_small_buffers_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Bc1.Encode(new uint[15], 4, 4, 4, new byte[8]));
        Assert.Throws<ArgumentException>(() => Bc1.Encode(new uint[16], 4, 4, 4, new byte[7]));
        Assert.Throws<System.IO.InvalidDataException>(() => Bc1.Decode(new byte[7], 4, 4, new uint[16], 4));
        Assert.Throws<ArgumentException>(() => Bc1.Decode(new byte[8], 4, 4, new uint[15], 4));
    }
}
