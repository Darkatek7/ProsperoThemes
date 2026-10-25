using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace ProsperoThemes.Shared;

/// <summary>
/// A BC7 decoder (all eight modes) and the reading of a DDS file's header. A theme's home background is a 3840x2160
/// BC7 texture, and the preview shows it behind the system tiles; the console has no texture decoder an app can call
/// and a 1920x1080 screen needs only a quarter of its pixels, so <see cref="DecodeHalf"/> turns every 4x4 block
/// straight into 2x2 pixels without the 8K-pixel intermediate. Pure computation on spans, nothing allocated per block,
/// so a worker thread can run it. It follows the store's TypeScript decoder (<c>texture/src/bc7-decode.ts</c> in the store repository), which
/// was checked against Pillow on console textures; the tests compare the two.
/// </summary>
/// <remarks>
/// Pixels are 0xAARRGGBB, the layout of the SDK's <c>PixelBuffer</c> (B, G, R, A in memory). A block in a reserved mode
/// decodes to transparent black, as in the reference decoder.
/// </remarks>
public static class Bc7
{
    public const int BlockBytes = 16;

    // Per mode 0..7 (BC7 spec): subsets, partition bits, rotation bits, index-selector bits, colour bits, alpha bits,
    // index bits, bits of the second index set (modes 4 and 5 only). P-bits: one per endpoint in modes 0, 3, 6 and 7, one
    // per subset in mode 1.
    private static ReadOnlySpan<byte> Subsets => [3, 2, 3, 2, 1, 1, 1, 2];
    private static ReadOnlySpan<byte> PartitionBits => [4, 6, 6, 6, 0, 0, 0, 6];
    private static ReadOnlySpan<byte> RotationBits => [0, 0, 0, 0, 2, 2, 0, 0];
    private static ReadOnlySpan<byte> SelectorBits => [0, 0, 0, 0, 1, 0, 0, 0];
    private static ReadOnlySpan<byte> ColorBits => [4, 6, 5, 7, 5, 7, 7, 5];
    private static ReadOnlySpan<byte> AlphaBits => [0, 0, 0, 0, 6, 8, 7, 5];
    private static ReadOnlySpan<byte> IndexBits => [3, 3, 2, 2, 2, 2, 4, 2];
    private static ReadOnlySpan<byte> Index2Bits => [0, 0, 0, 0, 3, 2, 0, 0];

    // Interpolation weights out of 64, by index bit count.
    private static ReadOnlySpan<byte> Weights2 => [0, 21, 43, 64];
    private static ReadOnlySpan<byte> Weights3 => [0, 9, 18, 27, 37, 46, 55, 64];
    private static ReadOnlySpan<byte> Weights4 => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    private static ReadOnlySpan<byte> WeightsOf(int indexBits) => indexBits switch { 2 => Weights2, 3 => Weights3, _ => Weights4 };

    // Subset of every pixel for the 64 two-subset partitions: bit i set = pixel i is in subset 1.
    private static ReadOnlySpan<ushort> P2 =>
    [
        0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80,
        0xc800, 0xffec, 0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000,
        0xf710, 0x008e, 0x7100, 0x08ce, 0x008c, 0x7310, 0x3100, 0x8cce,
        0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0, 0x718e, 0x399c,
        0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a,
        0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660,
        0x0272, 0x04e4, 0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c,
        0x9336, 0x9cc6, 0x817e, 0xe718, 0xccf0, 0x0fcc, 0x7744, 0xee22,
    ];

    // Subset of every pixel for the 64 three-subset partitions, 16 entries each.
    private static ReadOnlySpan<byte> P3 =>
    [
        0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 2,
        0, 0, 0, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 2, 1,
        0, 0, 0, 0, 2, 0, 0, 1, 2, 2, 1, 1, 2, 2, 1, 1,
        0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2,
        0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 2,
        0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2,
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2,
        0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2,
        0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2,
        0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2,
        0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, 1, 2, 2, 2,
        0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, 2, 2, 2, 0,
        0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2,
        0, 1, 1, 1, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0,
        0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2,
        0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1,
        0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2,
        0, 0, 0, 1, 0, 0, 0, 1, 2, 2, 2, 1, 2, 2, 2, 1,
        0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2,
        0, 0, 0, 0, 1, 1, 0, 0, 2, 2, 1, 0, 2, 2, 1, 0,
        0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0,
        0, 0, 1, 2, 0, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2,
        0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, 0, 1, 1, 0,
        0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1,
        0, 0, 2, 2, 1, 1, 0, 2, 1, 1, 0, 2, 0, 0, 2, 2,
        0, 1, 1, 0, 0, 1, 1, 0, 2, 0, 0, 2, 2, 2, 2, 2,
        0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1,
        0, 0, 0, 0, 2, 0, 0, 0, 2, 2, 1, 1, 2, 2, 2, 1,
        0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 2, 2, 2,
        0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 2, 0, 0, 1, 1,
        0, 0, 1, 1, 0, 0, 1, 2, 0, 0, 2, 2, 0, 2, 2, 2,
        0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0,
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0,
        0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0,
        0, 1, 2, 0, 2, 0, 1, 2, 1, 2, 0, 1, 0, 1, 2, 0,
        0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1,
        0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1,
        0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2,
        0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1,
        0, 0, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 1, 1, 2, 2,
        0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 1, 1,
        0, 2, 2, 0, 1, 2, 2, 1, 0, 2, 2, 0, 1, 2, 2, 1,
        0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 0, 1,
        0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2,
        0, 2, 2, 2, 0, 1, 1, 1, 0, 2, 2, 2, 0, 1, 1, 1,
        0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 2, 1, 1, 1, 2,
        0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2,
        0, 2, 2, 2, 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2,
        0, 0, 0, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2,
        0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2,
        0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2,
        0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2, 2,
        0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2,
        0, 0, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2,
        0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1,
        0, 2, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 1, 2, 2, 2,
        0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        0, 1, 1, 1, 2, 0, 1, 1, 2, 2, 0, 1, 2, 2, 2, 0,
    ];

    // Anchor pixel of subset 1 in the two-subset partitions.
    private static ReadOnlySpan<byte> A2 =>
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];

    // Anchor pixels of subsets 1 and 2 in the three-subset partitions.
    private static ReadOnlySpan<byte> A3a =>
    [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
        3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
        3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    ];

    private static ReadOnlySpan<byte> A3b =>
    [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
        15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
        15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    ];

    /// <summary>Reads a 128-bit block from the least significant bit up.</summary>
    private struct Bits(ulong low, ulong high, int position)
    {
        public int Read(int count)
        {
            if (count == 0) return 0;
            ulong value;
            if (position + count <= 64) value = low >> position;
            else if (position >= 64) value = high >> (position - 64);
            else value = (low >> position) | (high << (64 - position));
            position += count;
            return (int)(value & ((1UL << count) - 1));
        }
    }

    /// <summary>Decodes one 16-byte block into 16 pixels, row by row (<paramref name="pixels"/> needs at least 16 entries).</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<uint> pixels)
    {
        byte first = block[0];
        if (first == 0)
        {
            pixels[..16].Clear();
            return;
        }
        int m = BitOperations.TrailingZeroCount((int)first);
        var bits = new Bits(BinaryPrimitives.ReadUInt64LittleEndian(block), BinaryPrimitives.ReadUInt64LittleEndian(block[8..]), m + 1);

        int subsets = Subsets[m];
        int partition = bits.Read(PartitionBits[m]);
        int rotation = bits.Read(RotationBits[m]);
        int selector = bits.Read(SelectorBits[m]);
        int ends = subsets * 2;
        int colorBits = ColorBits[m], alphaBits = AlphaBits[m];

        // Endpoints: ep[end * 4 + channel], red, green, blue, alpha.
        Span<int> ep = stackalloc int[24];
        for (int c = 0; c < 3; c++)
            for (int e = 0; e < ends; e++) ep[e * 4 + c] = bits.Read(colorBits);
        for (int e = 0; e < ends; e++) ep[e * 4 + 3] = alphaBits != 0 ? bits.Read(alphaBits) : 255;

        bool perEndpoint = m is 0 or 3 or 6 or 7;
        if (perEndpoint || m == 1)
        {
            Span<int> pbit = stackalloc int[6];
            if (perEndpoint) for (int e = 0; e < ends; e++) pbit[e] = bits.Read(1);
            else for (int s = 0; s < subsets; s++) pbit[s * 2] = pbit[s * 2 + 1] = bits.Read(1);
            for (int e = 0; e < ends; e++)
            {
                for (int c = 0; c < 3; c++) ep[e * 4 + c] = (ep[e * 4 + c] << 1) | pbit[e];
                if (alphaBits != 0) ep[e * 4 + 3] = (ep[e * 4 + 3] << 1) | pbit[e];
            }
            colorBits++;
            if (alphaBits != 0) alphaBits++;
        }
        // Expand to 8 bits by repeating the top bits.
        for (int e = 0; e < ends; e++)
        {
            for (int c = 0; c < 3; c++)
            {
                int v = ep[e * 4 + c] << (8 - colorBits);
                ep[e * 4 + c] = v | (v >> colorBits);
            }
            if (alphaBits != 0)
            {
                int v = ep[e * 4 + 3] << (8 - alphaBits);
                ep[e * 4 + 3] = v | (v >> alphaBits);
            }
        }

        // Anchors: pixel 0 is the anchor of subset 0, and the other subsets have one each; an anchor's index has one bit less.
        int anchor1 = -1, anchor2 = -1;
        if (subsets == 2) anchor1 = A2[partition];
        else if (subsets == 3) { anchor1 = A3a[partition]; anchor2 = A3b[partition]; }
        int indexBits = IndexBits[m], index2Bits = Index2Bits[m];
        Span<byte> index1 = stackalloc byte[16];
        Span<byte> index2 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            index1[i] = (byte)bits.Read(indexBits - (i == 0 || i == anchor1 || i == anchor2 ? 1 : 0));
        if (index2Bits != 0)
            for (int i = 0; i < 16; i++) index2[i] = (byte)bits.Read(index2Bits - (i == 0 ? 1 : 0));

        // Mode 4's selector swaps which index set drives the colour and which the alpha.
        bool two = index2Bits != 0, swap = selector == 1;
        ReadOnlySpan<byte> colorIndex = two && swap ? index2 : index1, alphaIndex = two && !swap ? index2 : index1;
        ReadOnlySpan<byte> colorWeights = WeightsOf(two && swap ? index2Bits : indexBits), alphaWeights = WeightsOf(two && !swap ? index2Bits : indexBits);

        ReadOnlySpan<ushort> p2 = P2;
        ReadOnlySpan<byte> p3 = P3;
        for (int i = 0; i < 16; i++)
        {
            int subset = subsets == 2 ? (p2[partition] >> i) & 1 : subsets == 3 ? p3[partition * 16 + i] : 0;
            int e0 = subset * 8, e1 = e0 + 4;
            int wc = colorWeights[colorIndex[i]], wa = alphaWeights[alphaIndex[i]];
            int r = ((64 - wc) * ep[e0] + wc * ep[e1] + 32) >> 6;
            int g = ((64 - wc) * ep[e0 + 1] + wc * ep[e1 + 1] + 32) >> 6;
            int b = ((64 - wc) * ep[e0 + 2] + wc * ep[e1 + 2] + 32) >> 6;
            int a = ((64 - wa) * ep[e0 + 3] + wa * ep[e1 + 3] + 32) >> 6;
            // Rotation swaps the alpha with the red (1), green (2) or blue (3) channel.
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }
            pixels[i] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }
    }

    private static void CheckSize(ReadOnlySpan<byte> blocks, int width, int height, out int blocksWide, out int blocksHigh)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        blocksWide = (width + 3) / 4;
        blocksHigh = (height + 3) / 4;
        if ((long)blocksWide * blocksHigh * BlockBytes > blocks.Length) throw new InvalidDataException("the BC7 data is shorter than the image needs");
    }

    /// <summary>
    /// Decodes a <paramref name="width"/> x <paramref name="height"/> image (cropped from whole blocks) into
    /// <paramref name="output"/>, <paramref name="stride"/> pixels a row; <paramref name="opaque"/> sets every alpha to 255.
    /// </summary>
    /// <exception cref="InvalidDataException"><paramref name="blocks"/> is shorter than the image needs.</exception>
    public static void Decode(ReadOnlySpan<byte> blocks, int width, int height, Span<uint> output, int stride, bool opaque = false)
    {
        CheckSize(blocks, width, height, out int blocksWide, out int blocksHigh);
        if (stride < width || output.Length < (long)(height - 1) * stride + width) throw new ArgumentException("the output is too small", nameof(output));
        Span<uint> pixels = stackalloc uint[16];
        for (int by = 0; by < blocksHigh; by++)
            for (int bx = 0; bx < blocksWide; bx++)
            {
                DecodeBlock(blocks.Slice((by * blocksWide + bx) * BlockBytes, BlockBytes), pixels);
                int columns = Math.Min(4, width - bx * 4), rows = Math.Min(4, height - by * 4);
                for (int y = 0; y < rows; y++)
                {
                    Span<uint> row = output.Slice((by * 4 + y) * stride + bx * 4, columns);
                    pixels.Slice(y * 4, columns).CopyTo(row);
                    if (opaque) for (int x = 0; x < columns; x++) row[x] |= 0xFF000000u;
                }
            }
    }

    /// <summary>
    /// Decodes the image at half its size: every output pixel is the average of a 2x2 group of the full-size image, each
    /// channel <c>(a + b + c + d + 2) >> 2</c>, so the result is what <see cref="Decode"/> followed by that average would give.
    /// The output is <c>width / 2</c> x <c>height / 2</c> (an odd last row or column is left out).
    /// </summary>
    /// <param name="opaque">Set the alpha of every pixel to 255 (a background has none).</param>
    /// <exception cref="InvalidDataException"><paramref name="blocks"/> is shorter than the image needs.</exception>
    public static void DecodeHalf(ReadOnlySpan<byte> blocks, int width, int height, Span<uint> output, int stride, bool opaque)
    {
        CheckSize(blocks, width, height, out int blocksWide, out int blocksHigh);
        int halfWidth = width / 2, halfHeight = height / 2;
        if (halfWidth == 0 || halfHeight == 0) throw new ArgumentOutOfRangeException(nameof(width), "the image is too small to halve");
        if (stride < halfWidth || output.Length < (long)(halfHeight - 1) * stride + halfWidth) throw new ArgumentException("the output is too small", nameof(output));
        Span<uint> pixels = stackalloc uint[16];
        uint alpha = opaque ? 0xFF000000u : 0;
        for (int by = 0; by < blocksHigh; by++)
        {
            int outY = by * 2;
            for (int bx = 0; bx < blocksWide; bx++)
            {
                DecodeBlock(blocks.Slice((by * blocksWide + bx) * BlockBytes, BlockBytes), pixels);
                int outX = bx * 2;
                for (int dy = 0; dy < 2 && outY + dy < halfHeight; dy++)
                    for (int dx = 0; dx < 2 && outX + dx < halfWidth; dx++)
                    {
                        int at = dy * 8 + dx * 2;
                        uint average = Average(pixels[at], pixels[at + 1], pixels[at + 4], pixels[at + 5]);
                        output[(outY + dy) * stride + outX + dx] = opaque ? average | alpha : average;
                    }
            }
        }
    }

    // (a + b + c + d + 2) >> 2 for each of the four channels, two channels at a time in the 16-bit lanes of a word (the
    // sum of four bytes is at most 1020, so the lanes never spill into each other).
    private static uint Average(uint a, uint b, uint c, uint d)
    {
        const uint lanes = 0x00FF00FF, round = 0x00020002;
        uint redBlue = (a & lanes) + (b & lanes) + (c & lanes) + (d & lanes) + round;
        uint alphaGreen = ((a >> 8) & lanes) + ((b >> 8) & lanes) + ((c >> 8) & lanes) + ((d >> 8) & lanes) + round;
        return ((redBlue >> 2) & lanes) | (((alphaGreen >> 2) & lanes) << 8);
    }
}

/// <summary>What <see cref="Dds.Parse"/> read of a DDS file's header.</summary>
/// <param name="Format">The DXGI format of a DX10 header (97 to 99 are BC7), 0 for a legacy header.</param>
/// <param name="FourCc">The legacy pixel format's four-character code (<c>DXT5</c>), <c>DX10</c> for an extended header, empty if none.</param>
/// <param name="DataOffset">Where the first mip level starts (128, or 148 with the DX10 part).</param>
public readonly record struct DdsInfo(int Width, int Height, int Format, string FourCc, int DataOffset)
{
    /// <summary>BC7_TYPELESS, BC7_UNORM or BC7_UNORM_SRGB in a DX10 header.</summary>
    public bool IsBc7 => Format is 97 or 98 or 99;
}

/// <summary>Reading the header of a DDS file (DirectDraw Surface), as far as the preview needs it.</summary>
public static class Dds
{
    /// <summary>Whether the data starts with the magic every DDS file has.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ';

    /// <summary>Reads the size and format of a DDS file, legacy header (128 bytes) or DX10 (148).</summary>
    /// <exception cref="InvalidDataException">Not a DDS file, or its header is cut off or has no usable size.</exception>
    public static DdsInfo Parse(ReadOnlySpan<byte> dds)
    {
        if (!HasMagic(dds) || dds.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(dds[4..]) != 124) throw new InvalidDataException("not a DDS file");
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(dds[12..]), width = BinaryPrimitives.ReadUInt32LittleEndian(dds[16..]);
        if (width == 0 || height == 0 || width > 16384 || height > 16384) throw new InvalidDataException("the DDS size is out of range");
        // The pixel format at offset 76: its size, its flags (4 = a four-character code), the code.
        string fourCc = (BinaryPrimitives.ReadUInt32LittleEndian(dds[80..]) & 4) != 0 ? System.Text.Encoding.ASCII.GetString(dds.Slice(84, 4)) : "";
        if (fourCc != "DX10") return new DdsInfo((int)width, (int)height, 0, fourCc, 128);
        if (dds.Length < 148) throw new InvalidDataException("the DDS header is cut off");
        return new DdsInfo((int)width, (int)height, (int)BinaryPrimitives.ReadUInt32LittleEndian(dds[128..]), fourCc, 148);
    }
}
