using System;
using System.Collections.Generic;
using SharpProspero.Graphics;

/// <summary>
/// A small JPEG decoder for the PC preview: baseline and progressive Huffman JPEGs of 8-bit grey or YCbCr, any sampling,
/// restart intervals. The PS5 app decodes with the console's JpegDec module and the SDK's PC texture library has no JPEG
/// reader, so the preview brings its own to show a pack's pictures. Chroma is upsampled by repeating samples, which is
/// plenty for a preview. Anything it does not understand (arithmetic coding, 12 bits, CMYK) throws.
/// </summary>
internal static class JpegDecoder
{
    private static readonly int[] Zigzag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private sealed class Huffman
    {
        public readonly int[] MinCode = new int[17], MaxCode = new int[18], ValPtr = new int[17];
        public byte[] Values = [];
    }

    private sealed class Component
    {
        public int Id, H, V, Tq, BlocksPerLine, BlocksPerColumn, DcTable, AcTable, Pred;
        public short[] Blocks = [];
    }

    public static PixelBuffer Decode(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) throw new InvalidDataException("Not a JPEG.");
        var quant = new int[4][];
        var dc = new Huffman?[4];
        var ac = new Huffman?[4];
        var components = new List<Component>();
        int width = 0, height = 0, restart = 0, hmax = 1, vmax = 1, mcusX = 0, mcusY = 0, adobe = -1;
        bool progressive = false;
        int pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) { pos++; continue; }
            int marker = data[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7 || marker == 0) { pos += 2; continue; }
            if (marker == 0xD9) break;
            int length = (data[pos + 2] << 8) | data[pos + 3];
            int body = pos + 4, end = pos + 2 + length;
            if (end > data.Length) throw new InvalidDataException("Truncated JPEG.");
            switch (marker)
            {
                case 0xDB:   // quantisation tables
                    while (body < end)
                    {
                        int pq = data[body] >> 4, tq = data[body] & 15;
                        body++;
                        var table = new int[64];
                        for (int i = 0; i < 64; i++)
                        {
                            table[i] = pq == 0 ? data[body++] : (data[body++] << 8) | data[body++];
                        }
                        if (tq < 4) quant[tq] = table;
                    }
                    break;
                case 0xC4:   // Huffman tables
                    while (body < end)
                    {
                        int tcth = data[body++];
                        var counts = new int[17];
                        int total = 0;
                        for (int i = 1; i <= 16; i++) { counts[i] = data[body++]; total += counts[i]; }
                        var table = new Huffman { Values = data.AsSpan(body, total).ToArray() };
                        body += total;
                        int code = 0, k = 0;
                        for (int l = 1; l <= 16; l++)
                        {
                            table.ValPtr[l] = k;
                            table.MinCode[l] = code;
                            code += counts[l];
                            k += counts[l];
                            table.MaxCode[l] = counts[l] > 0 ? code - 1 : -1;
                            code <<= 1;
                        }
                        table.MaxCode[17] = int.MaxValue;
                        if ((tcth & 15) < 4) ((tcth >> 4) == 0 ? dc : ac)[tcth & 15] = table;
                    }
                    break;
                case 0xDD:
                    restart = (data[body] << 8) | data[body + 1];
                    break;
                case 0xEE:   // Adobe: the colour transform flag
                    if (length >= 14 && data[body] == 'A' && data[body + 1] == 'd' && data[body + 2] == 'o' && data[body + 3] == 'b' && data[body + 4] == 'e')
                        adobe = data[body + 11];
                    break;
                case 0xC0 or 0xC1 or 0xC2:
                    if (data[body] != 8) throw new NotSupportedException("Only 8-bit JPEGs.");
                    progressive = marker == 0xC2;
                    height = (data[body + 1] << 8) | data[body + 2];
                    width = (data[body + 3] << 8) | data[body + 4];
                    int count = data[body + 5];
                    if (width == 0 || height == 0 || count is not (1 or 3)) throw new NotSupportedException("Unsupported JPEG layout.");
                    for (int i = 0; i < count; i++)
                    {
                        int at = body + 6 + i * 3;
                        components.Add(new Component { Id = data[at], H = data[at + 1] >> 4, V = data[at + 1] & 15, Tq = data[at + 2] });
                    }
                    foreach (Component c in components) { hmax = Math.Max(hmax, c.H); vmax = Math.Max(vmax, c.V); }
                    mcusX = (width + 8 * hmax - 1) / (8 * hmax);
                    mcusY = (height + 8 * vmax - 1) / (8 * vmax);
                    foreach (Component c in components)
                    {
                        c.BlocksPerLine = mcusX * c.H;
                        c.BlocksPerColumn = mcusY * c.V;
                        c.Blocks = new short[c.BlocksPerLine * c.BlocksPerColumn * 64];
                    }
                    break;
                case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    throw new NotSupportedException("Unsupported JPEG coding.");
                case 0xDA:   // a scan: its header, then the entropy-coded data
                    if (components.Count == 0) throw new InvalidDataException("Scan before frame.");
                    int ns = data[body];
                    var scan = new List<Component>();
                    for (int i = 0; i < ns; i++)
                    {
                        int id = data[body + 1 + i * 2], tables = data[body + 2 + i * 2];
                        Component c = components.Find(x => x.Id == id) ?? throw new InvalidDataException("Scan names an unknown component.");
                        c.DcTable = tables >> 4;
                        c.AcTable = tables & 15;
                        scan.Add(c);
                    }
                    int ss = data[body + 1 + ns * 2], se = data[body + 2 + ns * 2], ah = data[body + 3 + ns * 2] >> 4, al = data[body + 3 + ns * 2] & 15;
                    var reader = new Scan(data, end, dc, ac);
                    DecodeScan(reader, scan, progressive, ss, se, ah, al, restart, width, height, hmax, vmax, mcusX, mcusY);
                    pos = reader.Position;
                    continue;
            }
            pos = end;
        }
        if (components.Count == 0) throw new InvalidDataException("No frame in the JPEG.");
        return Finish(components, quant, width, height, hmax, vmax, adobe);
    }

    // ---- Entropy decoding ------------------------------------------------------------------------------------------

    private sealed class Scan(byte[] data, int start, Huffman?[] dc, Huffman?[] ac)
    {
        private int _bits, _count;
        public int Position = start;
        public bool Marker;
        public int EobRun;
        public readonly Huffman?[] Dc = dc, Ac = ac;

        public int Bit()
        {
            if (_count == 0)
            {
                _bits = NextByte();
                _count = 8;
            }
            _count--;
            return (_bits >> _count) & 1;
        }

        private int NextByte()
        {
            if (Position >= data.Length) { Marker = true; return 0; }
            int b = data[Position];
            if (b != 0xFF) { Position++; return b; }
            int next = Position + 1 < data.Length ? data[Position + 1] : 0xD9;
            if (next == 0) { Position += 2; return 0xFF; }
            Marker = true;   // a real marker: stay on it and feed zeros
            return 0;
        }

        public int Receive(int n)
        {
            int v = 0;
            for (; n > 0; n--) v = (v << 1) | Bit();
            return v;
        }

        public int Extend(int v, int n) => v < (1 << (n - 1)) ? v - (1 << n) + 1 : v;

        public int Decode(Huffman? table)
        {
            if (table is null) throw new InvalidDataException("Missing Huffman table.");
            int code = 0, l = 1;
            code = Bit();
            while (l <= 16 && code > table.MaxCode[l])
            {
                code = (code << 1) | Bit();
                l++;
            }
            if (l > 16) throw new InvalidDataException("Bad Huffman code.");
            return table.Values[table.ValPtr[l] + code - table.MinCode[l]];
        }

        // Drops the rest of the byte and the restart marker after it, if the data has one here.
        public void Restart()
        {
            _count = 0;
            while (Position + 1 < data.Length && !(data[Position] == 0xFF && data[Position + 1] is >= 0xD0 and <= 0xD7))
            {
                if (data[Position] == 0xFF && data[Position + 1] != 0 && data[Position + 1] != 0xFF) return;   // some other marker: leave it
                Position++;
            }
            if (Position + 1 < data.Length) Position += 2;
            Marker = false;
            EobRun = 0;
        }
    }

    private static void DecodeScan(Scan s, List<Component> scan, bool progressive, int ss, int se, int ah, int al, int restart,
        int width, int height, int hmax, int vmax, int mcusX, int mcusY)
    {
        foreach (Component c in scan) c.Pred = 0;
        s.EobRun = 0;

        void Block(Component c, int row, int col)
        {
            int off = (row * c.BlocksPerLine + col) * 64;
            short[] b = c.Blocks;
            if (!progressive) { Baseline(s, c, b, off); return; }
            if (ss == 0)
            {
                if (ah == 0)
                {
                    int t = s.Decode(s.Dc[c.DcTable]);
                    c.Pred += t == 0 ? 0 : s.Extend(s.Receive(t), t);
                    b[off] = (short)(c.Pred << al);
                }
                else if (s.Bit() == 1) b[off] |= (short)(1 << al);
            }
            else if (ah == 0) AcFirst(s, c, b, off, ss, se, al);
            else AcRefine(s, c, b, off, ss, se, al);
        }

        int total, perMcu = 0;
        if (scan.Count == 1)
        {
            Component c = scan[0];
            int bw = ((width * c.H + hmax - 1) / hmax + 7) / 8, bh = ((height * c.V + vmax - 1) / vmax + 7) / 8;
            total = bw * bh;
            for (int n = 0; n < total; n++)
            {
                if (restart > 0 && n > 0 && n % restart == 0) { RestartAll(s, scan); }
                Block(c, n / bw, n % bw);
            }
            return;
        }
        foreach (Component c in scan) perMcu += c.H * c.V;
        total = mcusX * mcusY;
        for (int n = 0; n < total; n++)
        {
            if (restart > 0 && n > 0 && n % restart == 0) RestartAll(s, scan);
            int my = n / mcusX, mx = n % mcusX;
            foreach (Component c in scan)
                for (int v = 0; v < c.V; v++)
                    for (int h = 0; h < c.H; h++)
                        Block(c, my * c.V + v, mx * c.H + h);
        }
    }

    private static void RestartAll(Scan s, List<Component> scan)
    {
        s.Restart();
        foreach (Component c in scan) c.Pred = 0;
    }

    private static void Baseline(Scan s, Component c, short[] b, int off)
    {
        int t = s.Decode(s.Dc[c.DcTable]);
        c.Pred += t == 0 ? 0 : s.Extend(s.Receive(t), t);
        b[off] = (short)c.Pred;
        for (int k = 1; k < 64;)
        {
            int rs = s.Decode(s.Ac[c.AcTable]), size = rs & 15, run = rs >> 4;
            if (size == 0)
            {
                if (run < 15) break;
                k += 16;
                continue;
            }
            k += run;
            if (k > 63) break;
            b[off + Zigzag[k]] = (short)s.Extend(s.Receive(size), size);
            k++;
        }
    }

    private static void AcFirst(Scan s, Component c, short[] b, int off, int ss, int se, int al)
    {
        if (s.EobRun > 0) { s.EobRun--; return; }
        for (int k = ss; k <= se;)
        {
            int rs = s.Decode(s.Ac[c.AcTable]), size = rs & 15, run = rs >> 4;
            if (size == 0)
            {
                if (run < 15)
                {
                    s.EobRun = s.Receive(run) + (1 << run) - 1;
                    break;
                }
                k += 16;
                continue;
            }
            k += run;
            if (k > 63) break;
            b[off + Zigzag[k]] = (short)(s.Extend(s.Receive(size), size) * (1 << al));
            k++;
        }
    }

    private static void AcRefine(Scan s, Component c, short[] b, int off, int ss, int se, int al)
    {
        int p1 = 1 << al, m1 = -1 << al;
        int k = ss;
        if (s.EobRun == 0)
        {
            for (; k <= se; k++)
            {
                int rs = s.Decode(s.Ac[c.AcTable]), size = rs & 15, run = rs >> 4, value = 0;
                if (size != 0) value = s.Bit() == 1 ? p1 : m1;
                else if (run != 15)
                {
                    s.EobRun = 1 << run;
                    if (run > 0) s.EobRun += s.Receive(run);
                    break;
                }
                // Over the coefficients that are already non-zero (they get a correction bit) and `run` zeros.
                do
                {
                    int z = off + Zigzag[k];
                    if (b[z] != 0)
                    {
                        if (s.Bit() == 1 && (b[z] & p1) == 0) b[z] = (short)(b[z] + (b[z] >= 0 ? p1 : m1));
                    }
                    else if (--run < 0) break;
                    k++;
                } while (k <= se);
                if (value != 0 && k <= 63) b[off + Zigzag[k]] = (short)value;
            }
        }
        if (s.EobRun > 0)
        {
            for (; k <= se; k++)
            {
                int z = off + Zigzag[k];
                if (b[z] != 0 && s.Bit() == 1 && (b[z] & p1) == 0) b[z] = (short)(b[z] + (b[z] >= 0 ? p1 : m1));
            }
            s.EobRun--;
        }
    }

    // ---- Pixels ----------------------------------------------------------------------------------------------------

    private static readonly float[] Cos = BuildCos();

    private static float[] BuildCos()
    {
        var t = new float[64];
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++)
                t[x * 8 + u] = (float)(Math.Cos((2 * x + 1) * u * Math.PI / 16) * (u == 0 ? Math.Sqrt(0.125) : 0.5));
        return t;
    }

    private static unsafe PixelBuffer Finish(List<Component> components, int[][] quant, int width, int height, int hmax, int vmax, int adobe)
    {
        var planes = new byte[components.Count][];
        var strides = new int[components.Count];
        var tmp = new float[64];
        var block = new float[64];
        for (int ci = 0; ci < components.Count; ci++)
        {
            Component c = components[ci];
            int[] q = quant[c.Tq] ?? throw new InvalidDataException("Missing quantisation table.");
            int stride = c.BlocksPerLine * 8;
            strides[ci] = stride;
            var plane = new byte[stride * c.BlocksPerColumn * 8];
            for (int row = 0; row < c.BlocksPerColumn; row++)
            {
                for (int col = 0; col < c.BlocksPerLine; col++)
                {
                    int off = (row * c.BlocksPerLine + col) * 64;
                    // Dequantise (the table is in zigzag order, the coefficients in natural order).
                    for (int k = 0; k < 64; k++) block[Zigzag[k]] = c.Blocks[off + Zigzag[k]] * q[k];
                    // Rows, then columns: out[x] = sum over u of C(x, u) * in[u].
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            float sum = 0;
                            for (int u = 0; u < 8; u++) sum += Cos[x * 8 + u] * block[y * 8 + u];
                            tmp[y * 8 + x] = sum;
                        }
                    for (int x = 0; x < 8; x++)
                        for (int y = 0; y < 8; y++)
                        {
                            float sum = 0;
                            for (int v = 0; v < 8; v++) sum += Cos[y * 8 + v] * tmp[v * 8 + x];
                            plane[(row * 8 + y) * stride + col * 8 + x] = (byte)Math.Clamp((int)MathF.Round(sum + 128f), 0, 255);
                        }
                }
            }
            planes[ci] = plane;
        }

        var image = new PixelBuffer(width, height);
        uint* pixels = image.AsSurface().Pixels;
        bool ycc = components.Count == 3 && adobe != 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int v0 = Sample(components, planes, strides, 0, x, y, hmax, vmax);
                int r, g, b;
                if (components.Count == 1) r = g = b = v0;
                else
                {
                    int v1 = Sample(components, planes, strides, 1, x, y, hmax, vmax), v2 = Sample(components, planes, strides, 2, x, y, hmax, vmax);
                    if (ycc)
                    {
                        float cb = v1 - 128f, cr = v2 - 128f;
                        r = Math.Clamp((int)MathF.Round(v0 + 1.402f * cr), 0, 255);
                        g = Math.Clamp((int)MathF.Round(v0 - 0.344136f * cb - 0.714136f * cr), 0, 255);
                        b = Math.Clamp((int)MathF.Round(v0 + 1.772f * cb), 0, 255);
                    }
                    else (r, g, b) = (v0, v1, v2);
                }
                pixels[y * width + x] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
            }
        }
        return image;
    }

    private static int Sample(List<Component> components, byte[][] planes, int[] strides, int ci, int x, int y, int hmax, int vmax)
    {
        Component c = components[ci];
        return planes[ci][(y * c.V / vmax) * strides[ci] + x * c.H / hmax];
    }
}
