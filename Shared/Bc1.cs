using System;
using System.Buffers.Binary;

namespace ProsperoThemes.Shared;

/// <summary>
/// A BC1 (DXT1) encoder and decoder. The PS4 titles on a PS5 keep their entry background (<c>/user/appmeta/&lt;TID&gt;/pic0.dds</c>)
/// as 1920x1080 DXT1, and the home screen reads that file live, so a theme's BC7 background is turned into DXT1 for them
/// (<see cref="TitleBackground"/>). Pixels are 0xAARRGGBB, the layout <see cref="Bc7"/> decodes to; the alpha is ignored by
/// the encoder (a background is opaque) and the blocks use the four-colour mode only.
/// </summary>
/// <remarks>
/// A block is 8 bytes: two little-endian RGB565 endpoints <c>c0 &gt; c1</c>, then 16 two-bit indices in a little-endian
/// 32-bit word, pixel 0 (top left) in the lowest bits, row by row. Index 0 is c0, 1 is c1, 2 is (2*c0 + c1)/3 and 3 is
/// (c0 + 2*c1)/3. The encoder takes the endpoints from the principal axis of the block's colours, then improves them by
/// least squares on the chosen indices; a block of one colour takes the endpoint pair that approximates the colour best
/// (the tables below). Indices are always chosen against the palette the decoder builds from the 565 endpoints. Nothing
/// is allocated per block.
/// </remarks>
public static class Bc1
{
    public const int BlockBytes = 8;

    private static int Expand5(int v) => (v << 3) | (v >> 2);
    private static int Expand6(int v) => (v << 2) | (v >> 4);

    /// <summary>The nearest 5- and 6-bit value of every 8-bit one (measured after expansion back to 8 bits), and for the
    /// blocks of one colour the endpoint pair (high, low) whose two-thirds point is closest to every 8-bit value.</summary>
    private static class Tables
    {
        public static readonly byte[] Q5 = Nearest(32, Expand5);
        public static readonly byte[] Q6 = Nearest(64, Expand6);
        public static readonly (byte Hi, byte Lo)[] Solid5 = SolidPairs(32, Expand5);
        public static readonly (byte Hi, byte Lo)[] Solid6 = SolidPairs(64, Expand6);

        private static byte[] Nearest(int levels, Func<int, int> expand)
        {
            var table = new byte[256];
            for (int t = 0; t < 256; t++)
            {
                int best = 0, bestError = int.MaxValue;
                for (int q = 0; q < levels; q++)
                {
                    int error = Math.Abs(expand(q) - t);
                    if (error < bestError) { best = q; bestError = error; }
                }
                table[t] = (byte)best;
            }
            return table;
        }

        private static (byte, byte)[] SolidPairs(int levels, Func<int, int> expand)
        {
            var table = new (byte, byte)[256];
            for (int v = 0; v < 256; v++)
            {
                int bestHi = 0, bestLo = 0, bestError = int.MaxValue, bestSpread = int.MaxValue;
                for (int lo = 0; lo < levels; lo++)
                    for (int hi = lo; hi < levels; hi++)
                    {
                        int error = Math.Abs((2 * expand(hi) + expand(lo) + 1) / 3 - v), spread = hi - lo;
                        if (error < bestError || error == bestError && spread < bestSpread)
                        {
                            bestHi = hi; bestLo = lo; bestError = error; bestSpread = spread;
                        }
                    }
                table[v] = ((byte)bestHi, (byte)bestLo);
            }
            return table;
        }
    }

    private static ushort Pack(int r, int g, int b) => (ushort)((Tables.Q5[r] << 11) | (Tables.Q6[g] << 5) | Tables.Q5[b]);

    private static int Clamp255(float v) => v <= 0f ? 0 : v >= 255f ? 255 : (int)(v + 0.5f);

    // ---- decoding ---------------------------------------------------------

    /// <summary>
    /// Decodes one 8-byte block into 16 pixels, row by row (<paramref name="pixels"/> needs at least 16 entries). With
    /// c0 &gt; c1 the block has four colours; otherwise index 2 is the middle of the two and index 3 is transparent black.
    /// </summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<uint> pixels)
    {
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(block), c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        int r0 = Expand5(c0 >> 11), g0 = Expand6((c0 >> 5) & 63), b0 = Expand5(c0 & 31);
        int r1 = Expand5(c1 >> 11), g1 = Expand6((c1 >> 5) & 63), b1 = Expand5(c1 & 31);
        Span<uint> palette = stackalloc uint[4];
        palette[0] = 0xFF000000u | (uint)(r0 << 16) | (uint)(g0 << 8) | (uint)b0;
        palette[1] = 0xFF000000u | (uint)(r1 << 16) | (uint)(g1 << 8) | (uint)b1;
        if (c0 > c1)
        {
            palette[2] = 0xFF000000u | (uint)(((2 * r0 + r1 + 1) / 3) << 16) | (uint)(((2 * g0 + g1 + 1) / 3) << 8) | (uint)((2 * b0 + b1 + 1) / 3);
            palette[3] = 0xFF000000u | (uint)(((r0 + 2 * r1 + 1) / 3) << 16) | (uint)(((g0 + 2 * g1 + 1) / 3) << 8) | (uint)((b0 + 2 * b1 + 1) / 3);
        }
        else
        {
            palette[2] = 0xFF000000u | (uint)(((r0 + r1 + 1) >> 1) << 16) | (uint)(((g0 + g1 + 1) >> 1) << 8) | (uint)((b0 + b1 + 1) >> 1);
            palette[3] = 0;
        }
        for (int i = 0; i < 16; i++) pixels[i] = palette[(int)((bits >> (2 * i)) & 3)];
    }

    /// <summary>Decodes a <paramref name="width"/> x <paramref name="height"/> image (cropped from whole blocks) into <paramref name="output"/>, <paramref name="stride"/> pixels a row.</summary>
    /// <exception cref="InvalidDataException"><paramref name="blocks"/> is shorter than the image needs.</exception>
    public static void Decode(ReadOnlySpan<byte> blocks, int width, int height, Span<uint> output, int stride)
    {
        CheckSize(width, height, out int blocksWide, out int blocksHigh);
        if (blocks.Length < (long)blocksWide * blocksHigh * BlockBytes) throw new System.IO.InvalidDataException("the BC1 data is shorter than the image needs");
        if (stride < width || output.Length < (long)(height - 1) * stride + width) throw new ArgumentException("the output is too small", nameof(output));
        Span<uint> pixels = stackalloc uint[16];
        for (int by = 0; by < blocksHigh; by++)
            for (int bx = 0; bx < blocksWide; bx++)
            {
                DecodeBlock(blocks.Slice((by * blocksWide + bx) * BlockBytes, BlockBytes), pixels);
                int columns = Math.Min(4, width - bx * 4), rows = Math.Min(4, height - by * 4);
                for (int y = 0; y < rows; y++)
                    pixels.Slice(y * 4, columns).CopyTo(output.Slice((by * 4 + y) * stride + bx * 4, columns));
            }
    }

    private static void CheckSize(int width, int height, out int blocksWide, out int blocksHigh)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        blocksWide = (width + 3) / 4;
        blocksHigh = (height + 3) / 4;
    }

    // ---- encoding ---------------------------------------------------------

    /// <summary>
    /// Encodes a <paramref name="width"/> x <paramref name="height"/> image (<paramref name="stride"/> pixels a row) into
    /// <paramref name="blocks"/>, row of blocks after row of blocks. A size that is not a multiple of 4 repeats the last
    /// row and column into the partial blocks.
    /// </summary>
    public static void Encode(ReadOnlySpan<uint> pixels, int width, int height, int stride, Span<byte> blocks)
    {
        CheckSize(width, height, out int blocksWide, out int blocksHigh);
        if (stride < width || pixels.Length < (long)(height - 1) * stride + width) throw new ArgumentException("the pixels are fewer than the image needs", nameof(pixels));
        if (blocks.Length < (long)blocksWide * blocksHigh * BlockBytes) throw new ArgumentException("the output is too small", nameof(blocks));
        Span<uint> tile = stackalloc uint[16];
        Span<int> work = stackalloc int[48 + 12 + 32];   // the block's channels, the palette, two index sets (see EncodeBlock)
        for (int by = 0; by < blocksHigh; by++)
            for (int bx = 0; bx < blocksWide; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    int row = Math.Min(by * 4 + y, height - 1) * stride;
                    for (int x = 0; x < 4; x++) tile[y * 4 + x] = pixels[row + Math.Min(bx * 4 + x, width - 1)];
                }
                EncodeBlock(tile, blocks.Slice((by * blocksWide + bx) * BlockBytes, BlockBytes), work);
            }
    }

    /// <summary>Encodes 16 pixels (row by row) into one 8-byte block.</summary>
    public static void EncodeBlock(ReadOnlySpan<uint> pixels, Span<byte> block)
    {
        Span<int> work = stackalloc int[48 + 12 + 32];
        EncodeBlock(pixels, block, work);
    }

    // work: [0..48) the pixels' red, green, blue; [48..60) a palette; [60..76) the indices tried; [76..92) the best indices.
    private static void EncodeBlock(ReadOnlySpan<uint> pixels, Span<byte> block, Span<int> work)
    {
        Span<int> px = work[..48], palette = work.Slice(48, 12), tried = work.Slice(60, 16), best = work.Slice(76, 16);
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0, sumR = 0, sumG = 0, sumB = 0;
        for (int i = 0; i < 16; i++)
        {
            uint c = pixels[i];
            int r = (int)(c >> 16) & 255, g = (int)(c >> 8) & 255, b = (int)c & 255;
            px[i * 3] = r; px[i * 3 + 1] = g; px[i * 3 + 2] = b;
            if (r < minR) minR = r; if (r > maxR) maxR = r;
            if (g < minG) minG = g; if (g > maxG) maxG = g;
            if (b < minB) minB = b; if (b > maxB) maxB = b;
            sumR += r; sumG += g; sumB += b;
        }

        // One colour: the endpoint pair whose two-thirds point is nearest, every pixel on index 2 (or on 0 when the pair is one colour).
        if (minR == maxR && minG == maxG && minB == maxB)
        {
            var (hr, lr) = Tables.Solid5[minR];
            var (hg, lg) = Tables.Solid6[minG];
            var (hb, lb) = Tables.Solid5[minB];
            int hi = (hr << 11) | (hg << 5) | hb, lo = (lr << 11) | (lg << 5) | lb;   // component-wise hi >= lo, so hi > lo unless equal
            Write(block, hi, lo, hi == lo ? 0u : 0xAAAAAAAAu);
            return;
        }

        float meanR = sumR / 16f, meanG = sumG / 16f, meanB = sumB / 16f;

        // The principal axis of the colours: a few power iterations of the covariance matrix, started along the bounding box's diagonal.
        float cRR = 0, cRG = 0, cRB = 0, cGG = 0, cGB = 0, cBB = 0;
        for (int i = 0; i < 16; i++)
        {
            float dr = px[i * 3] - meanR, dg = px[i * 3 + 1] - meanG, db = px[i * 3 + 2] - meanB;
            cRR += dr * dr; cRG += dr * dg; cRB += dr * db; cGG += dg * dg; cGB += dg * db; cBB += db * db;
        }
        // Started from the covariance's longest column (the bounding box's diagonal can be perpendicular to the true axis, e.g. red and blue).
        float lenR = cRR * cRR + cRG * cRG + cRB * cRB, lenG = cRG * cRG + cGG * cGG + cGB * cGB, lenB = cRB * cRB + cGB * cGB + cBB * cBB;
        float vr, vg, vb;
        if (lenR >= lenG && lenR >= lenB) { vr = cRR; vg = cRG; vb = cRB; }
        else if (lenG >= lenB) { vr = cRG; vg = cGG; vb = cGB; }
        else { vr = cRB; vg = cGB; vb = cBB; }
        for (int k = 0; k < 3; k++)
        {
            float largest = Math.Max(Math.Abs(vr), Math.Max(Math.Abs(vg), Math.Abs(vb)));
            if (largest < 1e-6f) break;
            float nr = cRR * vr + cRG * vg + cRB * vb, ng = cRG * vr + cGG * vg + cGB * vb, nb = cRB * vr + cGB * vg + cBB * vb;
            float next = Math.Max(Math.Abs(nr), Math.Max(Math.Abs(ng), Math.Abs(nb)));
            if (next < 1e-6f) break;
            vr = nr / next; vg = ng / next; vb = nb / next;
        }
        if (Math.Max(Math.Abs(vr), Math.Max(Math.Abs(vg), Math.Abs(vb))) < 1e-6f) { vr = maxR - minR; vg = maxG - minG; vb = maxB - minB; }
        float norm = vr * vr + vg * vg + vb * vb;
        float tMin = float.MaxValue, tMax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float t = (px[i * 3] - meanR) * vr + (px[i * 3 + 1] - meanG) * vg + (px[i * 3 + 2] - meanB) * vb;
            if (t < tMin) tMin = t;
            if (t > tMax) tMax = t;
        }
        float scaleMax = tMax / norm, scaleMin = tMin / norm;
        int bestC0 = Pack(Clamp255(meanR + vr * scaleMax), Clamp255(meanG + vg * scaleMax), Clamp255(meanB + vb * scaleMax));
        int bestC1 = Pack(Clamp255(meanR + vr * scaleMin), Clamp255(meanG + vg * scaleMin), Clamp255(meanB + vb * scaleMin));
        long bestError = Assign(px, ref bestC0, ref bestC1, palette, best);

        // Least squares: with the indices fixed, the endpoints that put the palette closest to the pixels; kept while the error falls.
        for (int round = 0; round < 3 && bestError > 0; round++)
        {
            long s00 = 0, s01 = 0, s11 = 0;
            long pa0 = 0, pa1 = 0, pa2 = 0, pb0 = 0, pb1 = 0, pb2 = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = best[i] switch { 0 => 3, 1 => 0, 2 => 2, _ => 1 }, b = 3 - a;   // thirds of c0 and of c1
                s00 += a * a; s01 += a * b; s11 += b * b;
                pa0 += a * px[i * 3]; pa1 += a * px[i * 3 + 1]; pa2 += a * px[i * 3 + 2];
                pb0 += b * px[i * 3]; pb1 += b * px[i * 3 + 1]; pb2 += b * px[i * 3 + 2];
            }
            long det = s00 * s11 - s01 * s01;
            if (det == 0) break;
            float inv = 3f / det;
            int c0 = Pack(Clamp255((pa0 * s11 - pb0 * s01) * inv), Clamp255((pa1 * s11 - pb1 * s01) * inv), Clamp255((pa2 * s11 - pb2 * s01) * inv));
            int c1 = Pack(Clamp255((s00 * pb0 - s01 * pa0) * inv), Clamp255((s00 * pb1 - s01 * pa1) * inv), Clamp255((s00 * pb2 - s01 * pa2) * inv));
            long error = Assign(px, ref c0, ref c1, palette, tried);
            if (error >= bestError) break;
            bestError = error; bestC0 = c0; bestC1 = c1;
            tried.CopyTo(best);
        }

        uint bits = 0;
        for (int i = 0; i < 16; i++) bits |= (uint)best[i] << (2 * i);
        Write(block, bestC0, bestC1, bits);
    }

    private static void Write(Span<byte> block, int c0, int c1, uint bits)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], bits);
    }

    /// <summary>
    /// Puts the endpoints in the order a four-colour block needs (c0 &gt; c1; the colours are the same either way, only the
    /// indices change, so choosing them after the swap is the swap and the remap in one), picks the nearest palette entry
    /// for every pixel and returns the squared error. Equal endpoints make a one-colour block: every index 0.
    /// </summary>
    private static long Assign(ReadOnlySpan<int> px, ref int c0, ref int c1, Span<int> palette, Span<int> indices)
    {
        if (c0 < c1) (c0, c1) = (c1, c0);
        palette[0] = Expand5(c0 >> 11); palette[1] = Expand6((c0 >> 5) & 63); palette[2] = Expand5(c0 & 31);
        palette[3] = Expand5(c1 >> 11); palette[4] = Expand6((c1 >> 5) & 63); palette[5] = Expand5(c1 & 31);
        for (int c = 0; c < 3; c++)
        {
            palette[6 + c] = (2 * palette[c] + palette[3 + c] + 1) / 3;
            palette[9 + c] = (palette[c] + 2 * palette[3 + c] + 1) / 3;
        }
        int entries = c0 == c1 ? 1 : 4;
        long total = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i * 3], g = px[i * 3 + 1], b = px[i * 3 + 2];
            int bestIndex = 0, bestDistance = int.MaxValue;
            for (int e = 0; e < entries; e++)
            {
                int dr = r - palette[e * 3], dg = g - palette[e * 3 + 1], db = b - palette[e * 3 + 2];
                int distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance) { bestDistance = distance; bestIndex = e; }
            }
            indices[i] = bestIndex;
            total += bestDistance;
        }
        return total;
    }
}
