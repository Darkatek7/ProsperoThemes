using System;
using System.Collections.Generic;
using System.IO;
using SharpProspero.Graphics;

/// <summary>
/// A baseline JPEG writer for the demo pack's pictures (the PC has no JPEG encoder in the SDK; the console's is a module).
/// YCbCr 4:4:4, one quantisation table, and Huffman tables of fixed-length codes written out in the file: far from small,
/// but valid for any decoder, and the pictures are smooth gradients.
/// </summary>
internal static class DemoJpeg
{
    // Annex K luminance table in zigzag order, scaled for quality 85 (about 0.3).
    private static readonly int[] Quant = BuildQuant();
    private static readonly int[] Zigzag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static int[] BuildQuant()
    {
        int[] baseTable =
        [
            16, 11, 12, 14, 12, 10, 16, 14, 13, 14, 18, 17, 16, 19, 24, 40, 26, 24, 22, 22, 24, 49, 35, 37, 29, 40, 58, 51, 61, 60, 57, 51,
            56, 55, 64, 72, 92, 78, 64, 68, 87, 69, 55, 56, 80, 109, 81, 87, 95, 98, 103, 104, 103, 62, 77, 113, 121, 112, 100, 120, 92, 101, 103, 99,
        ];
        var t = new int[64];
        for (int i = 0; i < 64; i++) t[i] = Math.Clamp((int)Math.Round(baseTable[i] * 0.3), 1, 255);
        return t;
    }

    // cos((2 x + 1) u pi / 16) for the pixel x and the frequency u.
    private static readonly double[] CosTable = BuildCosTable();

    private static double[] BuildCosTable()
    {
        var t = new double[64];
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++) t[x * 8 + u] = Math.Cos((2 * x + 1) * u * Math.PI / 16);
        return t;
    }

    // The AC symbols the file uses: end of block, 16 zeros, and every run (0 to 15) of a size (1 to 10), each a code of 8 bits.
    private static readonly byte[] AcSymbols = BuildAcSymbols();

    private static byte[] BuildAcSymbols()
    {
        var list = new List<byte> { 0x00, 0xF0 };
        for (int run = 0; run < 16; run++)
            for (int size = 1; size <= 10; size++) list.Add((byte)((run << 4) | size));
        return [.. list];
    }

    public static unsafe byte[] Encode(PixelBuffer image)
    {
        int width = image.Width, height = image.Height;
        var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        // Quantisation table 0 (8 bits, zigzag order).
        Marker(output, 0xDB, [[0], [.. Array.ConvertAll(Quant, q => (byte)q)]]);
        // Frame: 8 bits, height, width, three components of 1x1 sampling on table 0.
        Marker(output, 0xC0, [[8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0]]);
        // DC table: twelve codes of four bits (0 to 11). AC table: the 162 symbols above, eight bits each.
        var dcBits = new byte[16];
        dcBits[3] = 12;
        Marker(output, 0xC4, [[0x00], dcBits, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]]);
        var acBits = new byte[16];
        acBits[7] = (byte)AcSymbols.Length;
        Marker(output, 0xC4, [[0x10], acBits, AcSymbols]);
        Marker(output, 0xDA, [[3, 1, 0x00, 2, 0x00, 3, 0x00, 0, 63, 0]]);

        var bits = new BitWriter(output);
        var symbolCode = new Dictionary<byte, int>();
        for (int i = 0; i < AcSymbols.Length; i++) symbolCode[AcSymbols[i]] = i;
        var pred = new int[3];
        var block = new float[64];
        uint* pixels = image.AsSurface().Pixels;
        for (int by = 0; by < (height + 7) / 8; by++)
        {
            for (int bx = 0; bx < (width + 7) / 8; bx++)
            {
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            uint p = pixels[Math.Min(by * 8 + y, height - 1) * image.Width + Math.Min(bx * 8 + x, width - 1)];
                            float r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                            block[y * 8 + x] = (c == 0 ? 0.299f * r + 0.587f * g + 0.114f * b
                                : c == 1 ? -0.168736f * r - 0.331264f * g + 0.5f * b + 128f
                                : 0.5f * r - 0.418688f * g - 0.081312f * b + 128f) - 128f;
                        }
                    var coefficients = Quantise(block);
                    // DC: the difference to the block before, as a size (four-bit code) and its bits.
                    int diff = coefficients[0] - pred[c];
                    pred[c] = coefficients[0];
                    int size = Size(diff);
                    bits.Write(size, 4);
                    if (size > 0) bits.Write(diff < 0 ? diff - 1 : diff, size);
                    // AC: run and size symbols in zigzag order, then the end of the block.
                    int run = 0;
                    for (int k = 1; k < 64; k++)
                    {
                        int value = coefficients[k];
                        if (value == 0) { run++; continue; }
                        while (run > 15) { bits.Write(symbolCode[0xF0], 8); run -= 16; }
                        int s = Size(value);
                        bits.Write(symbolCode[(byte)((run << 4) | s)], 8);
                        bits.Write(value < 0 ? value - 1 : value, s);
                        run = 0;
                    }
                    if (run > 0) bits.Write(symbolCode[0x00], 8);
                }
            }
        }
        bits.Flush();
        output.Write([0xFF, 0xD9]);
        return output.ToArray();
    }

    private static int Size(int value)
    {
        value = Math.Abs(value);
        int size = 0;
        while (value != 0) { size++; value >>= 1; }
        return size;
    }

    // The quantised cosine transform of one 8x8 block, in zigzag order.
    private static int[] Quantise(float[] block)
    {
        var result = new int[64];
        for (int k = 0; k < 64; k++)
        {
            int index = Zigzag[k], v = index / 8, u = index % 8;
            double sum = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    sum += block[y * 8 + x] * CosTable[x * 8 + u] * CosTable[y * 8 + v];
            double scale = (u == 0 ? Math.Sqrt(0.5) : 1.0) * (v == 0 ? Math.Sqrt(0.5) : 1.0) / 4.0;
            result[k] = (int)Math.Round(sum * scale / Quant[k]);
        }
        return result;
    }

    private static void Marker(Stream stream, byte marker, byte[][] parts)
    {
        int length = 2;
        foreach (byte[] part in parts) length += part.Length;
        stream.Write([0xFF, marker, (byte)(length >> 8), (byte)length]);
        foreach (byte[] part in parts) stream.Write(part);
    }

    private sealed class BitWriter(Stream stream)
    {
        private int _bits, _count;

        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _bits = (_bits << 1) | ((value >> i) & 1);
                if (++_count == 8) Emit();
            }
        }

        private void Emit()
        {
            stream.WriteByte((byte)_bits);
            if (_bits == 0xFF) stream.WriteByte(0);
            _bits = _count = 0;
        }

        public void Flush()
        {
            while (_count != 0) Write(1, 1);
        }
    }
}
