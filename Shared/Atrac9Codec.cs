using System;
using System.Collections.Generic;

namespace ProsperoThemes.Shared;

// ATRAC9 for the console's home music (bgm_home.at9): a decoder, an encoder and the volume change built on them.
//
// This is a port of web/atrac9/vendor/atrac9.js, which is Blaze414's encoder from https://github.com/Blaze414/ps5-theme
// (Copyright (C) 2026 Blaze414, GPL-3.0-or-later) with local changes. It builds on LibAtrac9 (MIT License, (c) 2018 Alex
// Barney), whose bitstream layout and tables it follows (Atrac9Tables.cs is generated from the JavaScript file's tables).
// The port is meant to give the same bytes as the JavaScript for the same input: arithmetic runs in the same order and
// precision, JavaScript's Math.round (half up) is kept, and the tests compare against output of the JavaScript.
//
// What it handles: ATRAC9 in a RIFF .at9 file. The decoder reads everything LibAtrac9 does, band extension and joint
// stereo included (no test file uses band extension). The encoder is narrower, see At9Encoder: gradient mode 3, no joint
// stereo, no band extension, no fine precision, nothing the file it takes its limits from does not use (except that
// gradient mode 3 is always allowed), and the LFE channel always silent. It handles the 256-sample frames of the console's music; high sample rates and other frame sizes throw.
//
// This file is under the GPL-3.0-or-later, like the code it is ported from.
//
// Written for the console's NativeAOT app: no LINQ, reflection or dynamic code, plain arrays, and the work buffers are
// allocated once per call. Changing a file's volume does not hold the track as audio: the file is decoded one frame at a time
// into a window of about five frames per channel, encoded superframe by superframe and checked as it goes.

/// <summary>Block types, and the error the codec raises for a file that is not valid or one it does not handle.</summary>
internal static class At9
{
    public const int Mono = 0, Stereo = 1, Lfe = 2;

    public static ThemePackException Fail(string message) => new(message);
}

/// <summary>What the 4-byte config of an ATRAC9 file says: layout, rate and frame sizes.</summary>
internal sealed class At9Config
{
    public required int SampleRateIndex, ChannelConfigIndex, SampleRate, FrameBytes, FramesPerSuperframe, SuperframeBytes;
    public required int Power, FrameSamples, SuperframeSamples, ChannelCount;
    public required bool High;
    public required int[] Blocks;

    // CHANNEL_CONFIGS of the original: mono, stereo, LFE and so on in block order.
    private static readonly int[][] ChannelConfigs =
    {
        new[] { At9.Mono }, new[] { At9.Mono, At9.Mono }, new[] { At9.Stereo },
        new[] { At9.Stereo, At9.Mono, At9.Lfe, At9.Stereo }, new[] { At9.Stereo, At9.Mono, At9.Lfe, At9.Stereo, At9.Stereo },
        new[] { At9.Stereo, At9.Stereo },
    };

    public static At9Config Read(byte[] b, int o)
    {
        if (b[o] != 0xfe) throw At9.Fail("ATRAC9 config does not start with 0xFE");
        int sri = b[o + 1] >> 4, cci = (b[o + 1] >> 1) & 7;
        if ((b[o + 1] & 1) != 0) throw At9.Fail("ATRAC9 config validation bit is set");
        if (cci > 5) throw At9.Fail("unknown ATRAC9 channel layout");
        int frameBytes = ((b[o + 2] << 3) | (b[o + 3] >> 5)) + 1;
        int sfi = (b[o + 3] >> 3) & 3;
        if ((sfi & 1) != 0) throw At9.Fail("unsupported ATRAC9 superframe size");
        int[] blocks = ChannelConfigs[cci];
        int power = Atrac9Tables.FramePower[sri];
        int channels = 0;
        foreach (int t in blocks) channels += t == At9.Stereo ? 2 : 1;
        return new At9Config
        {
            SampleRateIndex = sri, ChannelConfigIndex = cci, SampleRate = Atrac9Tables.SampleRates[sri], High = sri > 7,
            Blocks = blocks, ChannelCount = channels,
            FrameBytes = frameBytes, FramesPerSuperframe = 1 << sfi, SuperframeBytes = frameBytes << sfi,
            Power = power, FrameSamples = 1 << power, SuperframeSamples = (1 << power) << sfi,
        };
    }
}

/// <summary>A parsed .at9 file (RIFF WAVE, WAVE_FORMAT_EXTENSIBLE with the ATRAC9 subtype): where its chunks are and what its config says.</summary>
internal sealed class At9File
{
    public required byte[] Bytes;
    public required At9Config Config;

    /// <summary>The chunks in file order: id, offset of the body, length of the body.</summary>
    public required List<(string Id, int Offset, int Length)> Chunks;

    /// <summary>The fact chunk's sample count, delay and delay again; null without one.</summary>
    public required uint[]? Fact;

    /// <summary>Start and end sample of the first loop of a smpl chunk; null without one.</summary>
    public required uint[]? Loop;

    public required int DataOffset, DataLength, Superframes;

    private static uint U32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

    public static At9File Parse(byte[] b)
    {
        if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            throw At9.Fail("not a RIFF WAVE file");
        var chunks = new List<(string, int, int)>();
        int fmtAt = -1, fmtLength = 0, dataAt = -1, dataLength = 0;
        uint[]? fact = null, loop = null;
        for (long p = 12; p + 8 <= b.Length;)
        {
            string id = new string(new[] { (char)b[p], (char)b[p + 1], (char)b[p + 2], (char)b[p + 3] });
            long size = U32(b, (int)p + 4);
            if (p + 8 + size > b.Length) throw At9.Fail($"chunk {id} runs past the end of the file");
            int body = (int)p + 8, length = (int)size;
            chunks.Add((id, body, length));
            if (id == "fmt ") { fmtAt = body; fmtLength = length; }
            else if (id == "fact" && length >= 12) fact = new[] { U32(b, body), U32(b, body + 4), U32(b, body + 8) };
            else if (id == "smpl" && length >= 60 && U32(b, body + 28) >= 1) loop = new[] { U32(b, body + 44), U32(b, body + 48) };
            else if (id == "data") { dataAt = body; dataLength = length; }
            p += 8 + size + (size & 1);
        }
        if (fmtAt < 0 || fmtLength < 52 || (b[fmtAt] | b[fmtAt + 1] << 8) != 0xfffe || !GuidMatches(b, fmtAt + 24))
            throw At9.Fail("not an ATRAC9 file");
        if (dataAt < 0) throw At9.Fail("no audio data");
        At9Config config = At9Config.Read(b, fmtAt + 44);
        if ((b[fmtAt + 2] | b[fmtAt + 3] << 8) != config.ChannelCount) throw At9.Fail("channel count does not match the ATRAC9 config");
        if (U32(b, fmtAt + 4) != config.SampleRate) throw At9.Fail("sample rate does not match the ATRAC9 config");
        if ((b[fmtAt + 12] | b[fmtAt + 13] << 8) != config.SuperframeBytes) throw At9.Fail("block size does not match the ATRAC9 config");
        if (dataLength % config.SuperframeBytes != 0) throw At9.Fail("audio data is not a whole number of superframes");
        return new At9File
        {
            Bytes = b, Chunks = chunks, Fact = fact, Loop = loop, Config = config,
            DataOffset = dataAt, DataLength = dataLength, Superframes = dataLength / config.SuperframeBytes,
        };
    }

    private static bool GuidMatches(byte[] b, int o)
    {
        string guid = Atrac9Tables.Guid;
        for (int i = 0; i < 16; i++)
        {
            if (b[o + i] != Convert.ToByte(guid.Substring(i * 2, 2), 16)) return false;
        }
        return true;
    }
}

/// <summary>
/// Which coding features a file uses. A replacement must stay within the system file's profile (see <see cref="FitsProfile"/>):
/// the console decodes with Sony's decoder, which reads parts of the format that Sony's encoder never writes differently
/// from LibAtrac9 and FFmpeg.
/// </summary>
internal sealed class At9Profile
{
    public int Modes, MaxBands, MaxPrecision, LfeReuse, MaxFrame;
    public bool Fine, Bex;

    /// <summary>The byte values found in the unused ends of the superframes, in the order they were first seen.</summary>
    public readonly List<byte> Padding = new();

    private readonly bool[] seen = new bool[256];

    public bool HasPadding(int v) => seen[v];

    public void AddPadding(byte v)
    {
        if (seen[v]) return;
        seen[v] = true;
        Padding.Add(v);
    }

    /// <summary>The limits when the template has no audio to take a profile from (tests): mode 3, 13 bands, precision 7, reused LFE, 0x01 padding.</summary>
    public static At9Profile Default()
    {
        var p = new At9Profile { Modes = 8, MaxBands = 13, MaxPrecision = 7, LfeReuse = 2 };
        p.AddPadding(1);
        return p;
    }

    /// <summary>The byte a file fills the unused end of its superframes with (Sony: 0x01).</summary>
    public byte PaddingByte() => Padding.Count > 0 && !seen[1] ? Padding[0] : (byte)1;

    public void Add(At9Block blk)
    {
        if (blk.Type == At9.Lfe) { LfeReuse |= 1 << blk.LfeReuse; return; }
        Modes |= 1 << blk.GradMode;
        MaxBands = Math.Max(MaxBands, blk.BandCount);
        Bex = Bex || blk.BexEnabled != 0;
        foreach (At9Channel ch in blk.Channels)
        {
            for (int i = 0; i < ch.Coded; i++)
            {
                MaxPrecision = Math.Max(MaxPrecision, ch.Prec[i]);
                if (ch.Fine[i] != 0) Fine = true;
            }
        }
    }

    /// <summary>Why <paramref name="got"/> uses something <paramref name="stock"/> never does, or "" if it does not.</summary>
    public static string FitsProfile(At9Profile got, At9Profile stock, int frameBytes)
    {
        static string Modes(int m)
        {
            var list = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                if ((m & (1 << i)) != 0) list.Add(i.ToString());
            }
            return string.Join(", ", list);
        }
        if ((got.Modes & ~stock.Modes) != 0) return $"uses bit allocation mode {Modes(got.Modes & ~stock.Modes)}, which the system sound never does";
        if (got.MaxBands > stock.MaxBands) return $"uses {got.MaxBands} bands; the system sound uses at most {stock.MaxBands}";
        if (got.MaxPrecision > stock.MaxPrecision) return $"uses precision {got.MaxPrecision}; the system sound uses at most {stock.MaxPrecision}";
        if (got.Fine && !stock.Fine) return "uses fine precision, which the system sound never does";
        if (got.Bex && !stock.Bex) return "uses band extension, which the system sound never does";
        if ((got.LfeReuse & ~stock.LfeReuse) != 0) return "codes the LFE channel differently from the system sound";
        int limit = Math.Max(stock.MaxFrame, (int)Math.Floor(frameBytes * At9Encoder.FrameCap));
        if (got.MaxFrame > limit) return $"has a {got.MaxFrame}-byte frame; the limit is {limit}";
        // Sony fills the unused end of a superframe with 0x01; its decoder minds.
        byte pad = stock.PaddingByte();
        foreach (byte v in got.Padding)
        {
            if (v != pad && !stock.HasPadding(v)) return $"fills unused space with 0x{v:x2} where the system sound uses 0x{pad:x2}";
        }
        return "";
    }
}

/// <summary>Reads bits, most significant first, from a byte array between two bit positions.</summary>
internal sealed class At9BitReader
{
    private readonly byte[] b;
    private int end;

    public int Pos;

    public At9BitReader(byte[] bytes) => b = bytes;

    public void Reset(int startByte, int endByte)
    {
        Pos = startByte * 8;
        end = endByte * 8;
    }

    private uint Peek(int n)
    {
        int p = Pos, i = p >> 3;
        uint w;
        if (i + 3 < b.Length) w = (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
        else
        {
            w = 0;
            for (int k = 0; k < 4; k++) w = w << 8 | (i + k < b.Length ? b[i + k] : 0u);
        }
        return (w << (p & 7)) >> (32 - n);
    }

    public int Read(int n)
    {
        if (n == 0) return 0;
        uint v = Peek(n);
        Pos += n;
        if (Pos > end) throw At9.Fail("frame runs past the end of its superframe");
        return (int)v;
    }

    public int Signed(int n) => Atrac9Codebook.SignExtend(Read(n), n);

    public int Huff(Atrac9Codebook cb)
    {
        int s = cb.Lookup[Peek(cb.MaxBits)];
        if (s < 0) throw At9.Fail("invalid Huffman code");
        Pos += cb.Bits[s];
        if (Pos > end) throw At9.Fail("frame runs past the end of its superframe");
        return s;
    }

    public void Align() => Pos = (Pos + 7) & ~7;
}

/// <summary>Writes bits, most significant first, into a zeroed byte array.</summary>
internal sealed class At9BitWriter
{
    private readonly byte[] b;

    public int Pos;

    public At9BitWriter(byte[] bytes, int startByte)
    {
        b = bytes;
        Pos = startByte * 8;
    }

    public void Write(int v, int n)
    {
        for (int i = n - 1; i >= 0; i--, Pos++)
        {
            if (((v >> i) & 1) != 0) b[Pos >> 3] |= (byte)(0x80 >> (Pos & 7));
        }
    }

    public void Align() => Pos = (Pos + 7) & ~7;
}

/// <summary>The transform behind ATRAC9: LibAtrac9's fast DCT-IV and the windows of the MDCT and its inverse.</summary>
internal static class At9Transform
{
    private const int MaxBits = 8;

    private static readonly double[][] Sin = new double[MaxBits + 1][], Cos = new double[MaxBits + 1][];
    private static readonly int[][] Shuffle = new int[MaxBits + 1][];

    /// <summary>The window of the forward transform and of the inverse, by frame size in bits.</summary>
    public static readonly double[][] Mdct = new double[MaxBits + 1][], Imdct = new double[MaxBits + 1][];

    static At9Transform()
    {
        for (int b = 0; b <= MaxBits; b++)
        {
            int n = 1 << b;
            double[] sin = new double[n], cos = new double[n];
            int[] sh = new int[n];
            for (int i = 0; i < n; i++)
            {
                double v = Math.PI * (4 * i + 1) / (4 * n);
                sin[i] = Math.Sin(v);
                cos[i] = Math.Cos(v);
                int x = i ^ (i >> 1), r = 0;
                for (int k = 0; k < b; k++, x >>= 1) r = (r << 1) | (x & 1);
                sh[i] = r;
            }
            Sin[b] = sin;
            Cos[b] = cos;
            Shuffle[b] = sh;
        }
        for (int bits = 6; bits <= MaxBits; bits++)
        {
            int n = 1 << bits;
            double[] mdct = new double[n], imdct = new double[n];
            for (int i = 0; i < n; i++) mdct[i] = (Math.Sin(((i + 0.5) / n - 0.5) * Math.PI) + 1) * 0.5;
            for (int i = 0; i < n; i++) imdct[i] = mdct[i] / (mdct[n - 1 - i] * mdct[n - 1 - i] + mdct[i] * mdct[i]);
            Mdct[bits] = mdct;
            Imdct[bits] = imdct;
        }
    }

    /// <summary>DCT-IV of 2^bits values (its own inverse up to scale). <paramref name="tmp"/> is a work buffer of the same size.</summary>
    public static void Dct4(int bits, double[] input, double[] output, double[] tmp)
    {
        int size = 1 << bits, last = size - 1, half = size >> 1;
        double[] sin = Sin[bits], cos = Cos[bits];
        for (int i = 0; i < half; i++)
        {
            int i2 = i * 2;
            double a = input[i2], b = input[last - i2];
            tmp[i2] = a * cos[i] + b * sin[i];
            tmp[i2 + 1] = a * sin[i] - b * cos[i];
        }
        int stages = bits - 1;
        for (int stage = 0; stage < stages; stage++)
        {
            int blocks = 1 << stage, sizeBits = stages - stage;
            int bsize = 1 << sizeBits, bhalf = bsize >> 1;
            sin = Sin[sizeBits - 1];
            cos = Cos[sizeBits - 1];
            for (int blk = 0; blk < blocks; blk++)
            {
                for (int i = 0; i < bhalf; i++)
                {
                    int f = (blk * bsize + i) * 2, k = f + bsize;
                    double a = tmp[f] - tmp[k], b = tmp[f + 1] - tmp[k + 1];
                    tmp[f] += tmp[k];
                    tmp[f + 1] += tmp[k + 1];
                    tmp[k] = a * cos[i] + b * sin[i];
                    tmp[k + 1] = a * sin[i] - b * cos[i];
                }
            }
        }
        int[] sh = Shuffle[bits];
        for (int i = 0; i < size; i++) output[i] = tmp[sh[i]];
    }
}

/// <summary>One channel's state in the decoder, and the encoder's per-frame work arrays when it is built for encoding.</summary>
internal sealed class At9Channel
{
    public readonly int Index;
    public readonly int[] Sf = new int[48], SfPrev = new int[48], Prec = new int[48], Fine = new int[48];
    public readonly byte[] Set = new byte[48];
    public readonly int[] Q = new int[256], Qf = new int[256];
    public readonly double[] Spectra = new double[256], Prev = new double[256], Pcm = new double[256];
    public readonly int[] BexValues = new int[4];

    /// <summary>Added to a unit's precision when its scale factor stands out from its neighbours (gradient modes 1 to 3).</summary>
    public readonly int[] Mask = new int[49];
    public int Coded, BexMode;
    public int[]? Rng;

    // Encoder only.
    public readonly double[] X;
    public readonly int[] ScratchQ, MemoBits, BasePrecision;
    public readonly double[] MemoErr;
    public float[]? Src;
    public At9SfPlan SfPlan;

    public At9Channel(int index, bool forEncoder)
    {
        Index = index;
        X = forEncoder ? new double[256] : Array.Empty<double>();
        ScratchQ = forEncoder ? new int[256] : Array.Empty<int>();
        MemoBits = forEncoder ? new int[32 * 16] : Array.Empty<int>();
        BasePrecision = forEncoder ? new int[32] : Array.Empty<int>();
        MemoErr = forEncoder ? new double[32 * 16] : Array.Empty<double>();
    }
}

/// <summary>A scale factor coding chosen by the encoder: <see cref="Kind"/> 0 is the variable-length delta offset, 1 the fixed-length offset, 2 the distance to the first channel.</summary>
internal struct At9SfPlan
{
    public int Bits, Kind, Weights, Base, Len;
}

/// <summary>One block of a frame (a mono or stereo pair of channels, or the LFE) with its band layout and bit allocation.</summary>
internal sealed class At9Block
{
    public readonly int Type;
    public readonly At9Channel[] Channels;
    public bool HaveBands;
    public int BandCount, Units, StereoUnits, BexEnabled, ExtUnit, UnitsPrev, GradMode, Boundary, Primary, HasExt, LfeReuse;
    public readonly int[] Gradient = new int[49], Signs = new int[32];

    // Encoder only.
    public bool Reuse;

    public At9Block(int type, bool forEncoder)
    {
        Type = type;
        Channels = type == At9.Stereo ? new[] { new At9Channel(0, forEncoder), new At9Channel(1, forEncoder) } : new[] { new At9Channel(0, forEncoder) };
    }
}

/// <summary>
/// The decoder: parses a file's frames (checking all of them) and, if asked, synthesizes the channels. It collects the
/// file's coding <see cref="Profile"/> on the way. Frames are decoded in order, one superframe at a time.
/// </summary>
internal sealed class At9Decoder
{
    private readonly At9File file;
    private readonly At9Config cfg;
    private readonly At9Block[] blocks;
    private readonly double[] outBuf = new double[256], work = new double[256];
    private readonly At9BitReader br;
    private int superframeEnd;

    public readonly At9Profile Profile = new();

    public At9Decoder(At9File file)
    {
        this.file = file;
        cfg = file.Config;
        blocks = new At9Block[cfg.Blocks.Length];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = new At9Block(cfg.Blocks[i], false);
        br = new At9BitReader(file.Bytes);
    }

    public void BeginSuperframe(int s)
    {
        int start = file.DataOffset + s * cfg.SuperframeBytes;
        superframeEnd = start + cfg.SuperframeBytes;
        br.Reset(start, superframeEnd);
    }

    /// <summary>The unused end of the superframe goes into the profile.</summary>
    public void EndSuperframe()
    {
        for (int p = br.Pos >> 3; p < superframeEnd; p++) Profile.AddPadding(file.Bytes[p]);
    }

    /// <summary>
    /// Decodes the next frame of the superframe. With <paramref name="dst"/> (one array per channel in file order, null for a
    /// channel not wanted, all or none of a block's channels) it also writes the frame's samples (-1..1, before clamping) from
    /// <paramref name="offset"/>. Without it the frame is only parsed.
    /// </summary>
    public void DecodeFrame(int frameInSuperframe, float[]?[]? dst, int offset)
    {
        int before = br.Pos;
        foreach (At9Block blk in blocks)
        {
            UnpackBlock(blk, frameInSuperframe);
            Profile.Add(blk);
        }
        Profile.MaxFrame = Math.Max(Profile.MaxFrame, (br.Pos - before) >> 3);
        if (dst is null) return;
        int c = 0;
        foreach (At9Block blk in blocks)
        {
            bool need = false;
            for (int i = 0; i < blk.Channels.Length; i++) need |= dst[c + i] is not null;
            if (need) Synthesize(blk);
            foreach (At9Channel ch in blk.Channels)
            {
                float[]? o = dst[c++];
                if (o is null) continue;
                for (int i = 0; i < cfg.FrameSamples; i++) o[offset + i] = (float)(ch.Pcm[i] / 32768);
            }
        }
    }

    // ---- bit allocation -----------------------------------------------------------------------------------------

    private static readonly byte[][] Curves = BuildCurves();

    private static byte[][] BuildCurves()
    {
        int[] baseCurve = Atrac9Tables.BaseCurve;
        var curves = new byte[baseCurve.Length][];
        for (int n = 0; n < curves.Length; n++)
        {
            curves[n] = new byte[n + 1];
            for (int i = 0; i <= n; i++) curves[n][i] = (byte)baseCurve[i * 48 / (n + 1)];
        }
        return curves;
    }

    /// <summary>The bit allocation gradient (shared by the decoder and the encoder).</summary>
    public static void FillGradient(int[] g, int start, int end, int startValue, int endValue, int units)
    {
        int values = endValue - startValue;
        for (int i = 0; i < end; i++) g[i] = startValue;
        for (int i = end; i <= units; i++) g[i] = endValue;
        if (values == 0) return;
        byte[] curve = Curves[end - start - 1];
        double scale = (Math.Abs(values) - 1) / 31.0;
        for (int i = start; i < end; i++)
        {
            int step = (int)(curve[i - start] * scale);
            g[i] = values > 0 ? startValue + 1 + step : startValue - 1 - step;
        }
    }

    private static void MakeGradient(At9Block blk, int start, int end, int startValue, int endValue)
    {
        if (start >= end || end > 31) throw At9.Fail("invalid bit allocation range");
        if (blk.Boundary > blk.Units) throw At9.Fail("invalid bit allocation boundary");
        FillGradient(blk.Gradient, start, end, startValue, endValue, blk.Units);
    }

    private static void Precisions(At9Block blk, At9Channel ch)
    {
        int[] sf = ch.Sf, mask = ch.Mask;
        int n = blk.Units;
        Array.Clear(mask, 0, n + 1);
        if (blk.GradMode != 0)
        {
            for (int i = 1; i < n; i++)
            {
                int d = sf[i] - sf[i - 1];
                if (d > 1) mask[i] += Math.Min(d - 1, 5);
                else if (d < -1) mask[i - 1] += Math.Min(-d - 1, 5);
            }
        }
        for (int i = 0; i < n; i++)
        {
            int p;
            if (blk.GradMode != 0)
            {
                p = sf[i] + mask[i] - blk.Gradient[i];
                if (p > 0) p = blk.GradMode == 1 ? p >> 1 : blk.GradMode == 2 ? (3 * p) >> 3 : p >> 2;
            }
            else
            {
                p = sf[i] - blk.Gradient[i];
            }
            p = Math.Max(p, 1) + (i < blk.Boundary ? 1 : 0);
            if (p > 30) throw At9.Fail("precision out of range");
            ch.Fine[i] = p > 15 ? p - 15 : 0;
            ch.Prec[i] = Math.Min(p, 15);
        }
    }

    /// <summary>Which units use the second set of spectrum codebooks, from the scale factors (as the decoder derives it).</summary>
    public static void CodebookSets(At9Config cfg, At9Channel ch, int units)
    {
        int[] sf = ch.Sf;
        byte[] set = ch.Set;
        Array.Clear(set);
        if (units <= 1 || cfg.High) return;
        int keep = sf[units];
        sf[units] = sf[units - 1];
        int avg = 0;
        if (units > 12)
        {
            for (int i = 0; i < 12; i++) avg += sf[i];
            avg = (avg + 6) / 12;
        }
        int[] unitStart = Atrac9Tables.UnitStart;
        for (int i = 8; i < units; i++)
        {
            int lo = Math.Min(sf[i - 1], sf[i + 1]);
            if (sf[i] - lo >= 3 || sf[i] - sf[i - 1] + sf[i] - sf[i + 1] >= 3) set[i] = 1;
        }
        for (int i = 12; i < units; i++)
        {
            if (set[i] != 0) continue;
            int lo = Math.Min(sf[i - 1], sf[i + 1]);
            if (sf[i] - lo >= 2 && sf[i] >= avg - (unitStart[i + 1] - unitStart[i] == 16 ? 1 : 0)) set[i] = 1;
        }
        sf[units] = keep;
    }

    // ---- parsing ------------------------------------------------------------------------------------------------

    private void ReadScaleFactors(At9Block blk, At9Channel ch, bool first)
    {
        int[] sf = ch.Sf;
        int n = blk.ExtUnit, mode = br.Read(2);
        Array.Clear(sf);
        if (mode == 0)
        {
            int[] weights = Atrac9Tables.SfWeights[br.Read(3)];
            int baseValue = br.Read(5);
            DeltaOffset(sf, weights, baseValue, Atrac9Tables.SfUnsigned[br.Read(2) + 3]!, n);
        }
        else if (ch.Index == 0)
        {
            if (mode == 1)
            {
                int len = br.Read(2) + 2, baseValue = len < 5 ? br.Read(5) : 0;
                for (int i = 0; i < n; i++) sf[i] = br.Read(len) + baseValue;
            }
            else
            {
                if (first) throw At9.Fail("scale factors refer to a previous frame in the first frame");
                if (mode == 2) Distance(sf, ch.SfPrev, blk.UnitsPrev, n);
                else OffsetBaseline(sf, ch.SfPrev, blk.UnitsPrev, n);
            }
        }
        else if (mode == 1)
        {
            Distance(sf, blk.Channels[0].Sf, n, n);
        }
        else if (mode == 2)
        {
            OffsetBaseline(sf, blk.Channels[0].Sf, n, n);
        }
        else
        {
            if (first) throw At9.Fail("scale factors refer to a previous frame in the first frame");
            Distance(sf, ch.SfPrev, blk.UnitsPrev, n);
        }
        for (int i = 0; i < n; i++)
        {
            if (sf[i] < 0 || sf[i] > 31) throw At9.Fail("scale factor out of range");
        }
        Array.Copy(sf, ch.SfPrev, 48);
    }

    private void DeltaOffset(int[] sf, int[]? weights, int baseValue, Atrac9Codebook cb, int count)
    {
        sf[0] = br.Read(cb.ValueBits);
        for (int i = 1; i < count; i++) sf[i] = (sf[i - 1] + br.Huff(cb)) & cb.Mask;
        for (int i = 0; i < count; i++) sf[i] += baseValue - (weights is not null ? weights[i] : 0);
    }

    private void Distance(int[] sf, int[] baseline, int baseLen, int n)
    {
        Atrac9Codebook cb = Atrac9Tables.SfSigned[br.Read(2) + 2]!;
        int count = Math.Min(n, baseLen);
        for (int i = 0; i < count; i++) sf[i] = baseline[i] + Atrac9Codebook.SignExtend(br.Huff(cb), cb.ValueBits);
        for (int i = count; i < n; i++) sf[i] = br.Read(5);
    }

    private void OffsetBaseline(int[] sf, int[] baseline, int baseLen, int n)
    {
        int baseValue = br.Read(5) - 16;
        Atrac9Codebook cb = Atrac9Tables.SfUnsigned[br.Read(2) + 1]!;
        int count = Math.Min(n, baseLen);
        DeltaOffset(sf, null, 0, cb, count);
        for (int i = 0; i < count; i++) sf[i] += baseValue + baseline[i];
        for (int i = count; i < n; i++) sf[i] = br.Read(5);
    }

    private void ReadSpectra(At9Channel ch)
    {
        int maxHuff = cfg.High ? 1 : 7;
        Array.Clear(ch.Q);
        Array.Clear(ch.Qf);
        int[] unitStart = Atrac9Tables.UnitStart;
        for (int i = 0; i < ch.Coded; i++)
        {
            int start = unitStart[i], end = unitStart[i + 1], bits = ch.Prec[i] + 1;
            if (bits <= maxHuff)
            {
                Atrac9Codebook cb = Atrac9Tables.Spectrum[ch.Set[i]][bits][Atrac9Tables.UnitBook[i]] ?? throw At9.Fail("invalid spectrum codebook");
                for (int k = start; k < end; k += cb.ValueCount)
                {
                    int v = br.Huff(cb);
                    for (int j = 0; j < cb.ValueCount; j++, v >>= cb.ValueBits) ch.Q[k + j] = Atrac9Codebook.SignExtend(v & cb.Mask, cb.ValueBits);
                }
            }
            else
            {
                for (int k = start; k < end; k++) ch.Q[k] = br.Signed(bits);
            }
        }
        for (int i = 0; i < ch.Coded; i++)
        {
            if (ch.Fine[i] == 0) continue;
            for (int k = unitStart[i]; k < unitStart[i + 1]; k++) ch.Qf[k] = br.Signed(ch.Fine[i] + 1);
        }
    }

    private void ReadBex(At9Block blk)
    {
        int band = 0;
        if (blk.BexEnabled != 0)
        {
            if (blk.Units < 13 || blk.Units > 20) throw At9.Fail("band extension with an invalid band count");
            band = Atrac9Tables.BexGroups[blk.Units - 13][2];
            if (blk.Type == At9.Stereo)
            {
                int m = br.Read(2);
                blk.Channels[1].BexMode = band > 2 ? m : 4;
            }
            else
            {
                br.Read(1);
            }
        }
        blk.HasExt = br.Read(1);
        if (blk.HasExt == 0) return;
        if (blk.BexEnabled == 0)
        {
            br.Read(2);
            int skip = br.Read(5);
            for (int i = 0; i < skip; i++) br.Read(1);
            return;
        }
        int mode = br.Read(2);
        blk.Channels[0].BexMode = band > 2 ? mode : 4;
        int len = br.Read(5);
        if (len == 0) return;
        int end = br.Pos + len;
        foreach (At9Channel ch in blk.Channels)
        {
            int[] lengths = Atrac9Tables.BexLengths[ch.BexMode][band];
            for (int i = 0; i < Atrac9Tables.BexCounts[ch.BexMode][band]; i++) ch.BexValues[i] = br.Read(lengths[i]);
        }
        if (br.Pos > end) throw At9.Fail("band extension data is longer than declared");
    }

    private void UnpackBlock(At9Block blk, int frameIndex)
    {
        bool first = br.Read(1) == 0;
        int reuse = br.Read(1);
        if (first != (frameIndex == 0)) throw At9.Fail("superframe flag does not match the frame position");
        if (blk.Type == At9.Lfe)
        {
            At9Channel lfe = blk.Channels[0];
            blk.Units = 2;
            lfe.Coded = 2;
            Array.Clear(lfe.Sf);
            lfe.Sf[0] = br.Read(5);
            lfe.Sf[1] = br.Read(5);
            int p = reuse != 0 ? 8 : 4;
            blk.LfeReuse = reuse;
            lfe.Prec[0] = lfe.Prec[1] = p;
            lfe.Fine[0] = lfe.Fine[1] = 0;
            Array.Clear(lfe.Q);
            Array.Clear(lfe.Qf);
            for (int k = 0; k < 4; k++) lfe.Q[k] = br.Signed(p + 1);
            br.Align();
            return;
        }
        if (first && reuse != 0) throw At9.Fail("first frame of a superframe reuses band parameters");
        int minBands = cfg.High ? 1 : 3;
        if (reuse == 0)
        {
            blk.BandCount = br.Read(4) + minBands;
            if (blk.BandCount > Atrac9Tables.MaxBands[cfg.SampleRateIndex]) throw At9.Fail("band count out of range");
            blk.Units = Atrac9Tables.BandUnits[blk.BandCount];
            int stereoBand = blk.Type == At9.Stereo ? br.Read(4) + minBands : blk.BandCount;
            if (stereoBand > blk.BandCount) throw At9.Fail("stereo band out of range");
            blk.StereoUnits = Atrac9Tables.BandUnits[stereoBand];
            blk.BexEnabled = br.Read(1);
            if (blk.BexEnabled != 0)
            {
                int ext = br.Read(4) + minBands;
                if (ext < blk.BandCount || ext > (cfg.High ? 16 : 18)) throw At9.Fail("extension band out of range");
                blk.ExtUnit = Atrac9Tables.BandUnits[ext];
            }
            else
            {
                blk.ExtUnit = blk.Units;
            }
            blk.HaveBands = true;
        }
        else if (!blk.HaveBands)
        {
            throw At9.Fail("block reuses band parameters it never had");
        }

        blk.GradMode = br.Read(2);
        int start, end, startValue, endValue;
        if (blk.GradMode != 0)
        {
            start = br.Read(5); end = 31; startValue = br.Read(5); endValue = 31;
        }
        else
        {
            start = br.Read(6); end = br.Read(6) + 1; startValue = br.Read(5); endValue = br.Read(5);
        }
        blk.Boundary = br.Read(4);
        MakeGradient(blk, start, end, startValue, endValue);

        if (blk.Type == At9.Stereo)
        {
            blk.Primary = br.Read(1);
            int signs = br.Read(1);
            Array.Clear(blk.Signs);
            if (signs != 0)
            {
                for (int i = blk.StereoUnits; i < blk.Units; i++) blk.Signs[i] = br.Read(1);
            }
        }
        else
        {
            blk.Primary = 0;
        }
        ReadBex(blk);

        foreach (At9Channel ch in blk.Channels)
        {
            ch.Coded = ch.Index == blk.Primary ? blk.Units : blk.StereoUnits;
            ReadScaleFactors(blk, ch, first);
            Precisions(blk, ch);
            CodebookSets(cfg, ch, ch.Coded);
            ReadSpectra(ch);
        }
        blk.UnitsPrev = blk.BexEnabled != 0 ? blk.ExtUnit : blk.Units;
        br.Align();
    }

    // ---- synthesis ----------------------------------------------------------------------------------------------

    private static int RngNext(int[] r)
    {
        int t = (r[3] ^ (r[3] << 5)) & 0xffff;
        r[3] = r[2]; r[2] = r[1]; r[1] = r[0];
        r[0] = (t ^ r[0] ^ ((t ^ (r[0] >> 5)) >> 4)) & 0xffff;
        return r[0];
    }

    private void Noise(At9Channel ch, double[] s, int index, int count)
    {
        if (ch.Rng is null)
        {
            int[] sf = ch.Sf;
            int seed = (543 * (sf[8] + sf[12] + sf[15] + 1)) & 0xffff;
            int x = unchecked(0x4d93 * (seed ^ (seed >> 14)));
            ch.Rng = new[] { (3 - x) & 0xffff, (2 - x) & 0xffff, (1 - x) & 0xffff, (0 - x) & 0xffff };
        }
        for (int i = 0; i < count; i++) s[index + i] = RngNext(ch.Rng) / 65535.0 * 2 - 1;
    }

    private static void ScaleUnits(double[] s, double[] scales, int aUnit, int total)
    {
        int[] unitStart = Atrac9Tables.UnitStart;
        for (int i = aUnit; i < total; i++)
        {
            for (int k = unitStart[i]; k < unitStart[i + 1]; k++) s[k] *= scales[i - aUnit];
        }
    }

    private void BandExtension(At9Block blk, At9Channel ch)
    {
        int[] sf = ch.Sf, v = ch.BexValues, unitStart = Atrac9Tables.UnitStart;
        double[] s = ch.Spectra;
        int aUnit = blk.Units;
        int[] group = Atrac9Tables.BexGroups[blk.Units - 13];
        int bUnit = group[0], cUnit = group[1], bands = group[2];
        int total = Math.Max(cUnit, 22), bexUnits = total - aUnit;
        int aBin = unitStart[aUnit], bBin = unitStart[bUnit], cBin = unitStart[cUnit], tBin = unitStart[total];
        for (int i = 0; i < bBin - aBin; i++) s[aBin + i] = s[aBin - i - 1];
        for (int i = 0; i < cBin - bBin; i++) s[bBin + i] = s[bBin - i - 1];
        for (int i = 0; i < tBin - cBin; i++) s[cBin + i] = s[cBin - i - 1];
        double[] scales = new double[6];
        switch (ch.BexMode)
        {
            case 0:
                if (bands == 3 || bands == 4)
                {
                    double[][] t = bands == 3 ? Atrac9Tables.BexMode03 : Atrac9Tables.BexMode04;
                    scales[0] = t[0][v[0]]; scales[1] = t[1][v[0]]; scales[2] = t[2][v[1]];
                    scales[3] = t[3][v[2]]; scales[4] = t[4][v[3]];
                }
                else if (bands == 5)
                {
                    scales[0] = Atrac9Tables.BexMode05[0][v[0]]; scales[1] = Atrac9Tables.BexMode05[1][v[1]]; scales[2] = Atrac9Tables.BexMode05[2][v[1]];
                }
                scales[bexUnits - 1] = Math.ScaleB(1.0, sf[aUnit] - 15);
                Noise(ch, s, unitStart[total - 1], unitStart[total] - unitStart[total - 1]);
                ScaleUnits(s, scales, aUnit, total);
                break;
            case 1:
                for (int i = aUnit; i < total; i++) scales[i - aUnit] = Math.ScaleB(1.0, sf[i] - 15);
                Noise(ch, s, aBin, tBin - aBin);
                ScaleUnits(s, scales, aUnit, total);
                break;
            case 2:
                for (int i = aBin; i < bBin; i++) s[i] *= Atrac9Tables.BexMode2[v[0]];
                for (int i = bBin; i < cBin; i++) s[i] *= Atrac9Tables.BexMode2[v[1]];
                break;
            case 3:
            {
                double rate = Math.Pow(2, Atrac9Tables.BexMode3Rate[v[1]]);
                double scale = Atrac9Tables.BexMode3Initial[v[0]];
                for (int i = aBin; i < tBin; i++) { scale *= rate; s[i] *= scale; }
                break;
            }
            case 4:
            {
                double m = Atrac9Tables.BexMode4[v[0]];
                for (int i = aBin; i < bBin; i++) s[i] *= 0.7079468 * m;
                for (int i = bBin; i < cBin; i++) s[i] *= 0.5011902 * m;
                for (int i = cBin; i < tBin; i++) s[i] *= 0.3548279 * m;
                break;
            }
        }
    }

    private void Imdct(At9Channel ch)
    {
        int size = cfg.FrameSamples, half = size >> 1;
        double[] w = At9Transform.Imdct[cfg.Power], d = outBuf, prev = ch.Prev, pcm = ch.Pcm;
        At9Transform.Dct4(cfg.Power, ch.Spectra, d, work);
        for (int i = 0; i < half; i++)
        {
            pcm[i] = w[i] * d[i + half] + prev[i];
            pcm[i + half] = w[i + half] * -d[size - 1 - i] - prev[i + half];
            prev[i] = w[size - 1 - i] * -d[half - i - 1];
            prev[i + half] = w[half - i - 1] * d[i];
        }
    }

    private void Synthesize(At9Block blk)
    {
        int[] unitStart = Atrac9Tables.UnitStart;
        foreach (At9Channel ch in blk.Channels)
        {
            double[] sp = ch.Spectra;
            Array.Clear(sp);
            for (int i = 0; i < ch.Coded; i++)
            {
                double step = Atrac9Tables.Step[ch.Prec[i]], fine = Atrac9Tables.StepFine[ch.Fine[i]];
                for (int k = unitStart[i]; k < unitStart[i + 1]; k++) sp[k] = ch.Q[k] * step + ch.Qf[k] * fine;
            }
        }
        if (blk.Type == At9.Stereo && blk.StereoUnits < blk.Units)
        {
            At9Channel src = blk.Channels[blk.Primary], dst = blk.Channels[blk.Primary != 0 ? 0 : 1];
            for (int i = blk.StereoUnits; i < blk.Units; i++)
            {
                for (int k = unitStart[i]; k < unitStart[i + 1]; k++) dst.Spectra[k] = blk.Signs[i] != 0 ? -src.Spectra[k] : src.Spectra[k];
            }
        }
        foreach (At9Channel ch in blk.Channels)
        {
            for (int i = 0; i < blk.Units; i++)
            {
                double scale = Math.ScaleB(1.0, ch.Sf[i] - 15);
                for (int k = unitStart[i]; k < unitStart[i + 1]; k++) ch.Spectra[k] *= scale;
            }
            if (blk.Type != At9.Lfe && blk.BexEnabled != 0 && blk.HasExt != 0) BandExtension(blk, ch);
            Imdct(ch);
        }
    }
}

/// <summary>
/// The encoder. It writes only the parts of the format that the reference decoders (LibAtrac9 and FFmpeg) read identically:
/// no intensity stereo, no band extension, scale factors as plain or delta codes, and no flat gradient ramps. It also
/// writes only what the file it takes its profile from uses (see <see cref="At9Profile.FitsProfile"/>): the console decodes
/// with Sony's own decoder, which minds things LibAtrac9 and FFmpeg ignore. Within that, each superframe gets the allocation
/// with the least quantization error that fits it (a Lagrangian search over its frames).
/// </summary>
/// <remarks>
/// Every system ATRAC9 file on 13.42 stays inside one envelope: bit allocation mode 3 (start unit 12 to 22, start value 0 to
/// 11, boundary 0 to 15), no more bands than the system file, no band extension, precisions up to the system file's highest
/// and at most 7, the LFE coded as the system file codes it, frames of at most 1.25 times the nominal size, and the unused
/// end of each superframe filled with the system file's byte (0x01).
/// </remarks>
internal sealed class At9Encoder
{
    public const double FrameCap = 1.25;

    private const int MaxPrecisionLimit = 7;
    private const int SilentStart = 16, SilentValue = 5, SilentBoundary = 0;

    private static readonly int[] GradStarts = { 12, 14, 16, 18, 20, 22 };
    private static readonly int[] Boundaries = { 0, 1, 2, 4, 8, 15 };
    private static readonly int GradValueCount = 12;

    private readonly struct Candidate
    {
        public readonly int Start, Value, Boundary;
        public readonly int[] G;

        public Candidate(int start, int value, int boundary)
        {
            Start = start;
            Value = value;
            Boundary = boundary;
            G = new int[32];
            At9Decoder.FillGradient(G, start, 31, value, 31, 31);
        }
    }

    private static readonly Candidate[] Candidates = BuildCandidates();
    private static readonly int[] SilentGradient = BuildSilentGradient();

    private static Candidate[] BuildCandidates()
    {
        var list = new List<Candidate>();
        foreach (int start in GradStarts)
        {
            for (int value = 0; value < GradValueCount; value++)
            {
                foreach (int boundary in Boundaries) list.Add(new Candidate(start, value, boundary));
            }
        }
        return list.ToArray();
    }

    private static int[] BuildSilentGradient()
    {
        var g = new int[32];
        At9Decoder.FillGradient(g, SilentStart, 31, SilentValue, 31, 31);
        return g;
    }

    private readonly At9Config cfg;
    private readonly int maxBands, maxPrecision, lfeReuse;
    private readonly byte padding;
    private readonly At9Block[][] states;
    private readonly bool[][] audible;
    private readonly double[] win, z, u, work;
    private readonly int budget, cap, fps, frameSamples;

    // Per frame position and candidate: whether it fits, its size in bits and its squared error.
    private readonly bool[][] costValid;
    private readonly int[][] costBits;
    private readonly double[][] costErr;
    private readonly int[] choice, smallest, trial;

    /// <param name="stock">The file whose layout the output has.</param>
    /// <param name="profile">The limits of the output.</param>
    /// <param name="windows">One array per channel of the file (null for a channel that stays silent), which the caller fills
    /// before each <see cref="EncodeSuperframe"/> with the channel's samples from the superframe's first frame on, one frame more than it holds.</param>
    public At9Encoder(At9Config config, At9Profile profile, float[]?[] windows)
    {
        cfg = config;
        maxBands = Math.Min(profile.MaxBands, Atrac9Tables.MaxBands[cfg.SampleRateIndex]);
        maxPrecision = Math.Min(profile.MaxPrecision, MaxPrecisionLimit);
        lfeReuse = (profile.LfeReuse & 2) != 0 ? 1 : 0;
        padding = profile.PaddingByte();
        fps = cfg.FramesPerSuperframe;
        frameSamples = cfg.FrameSamples;
        budget = cfg.SuperframeBytes * 8;
        cap = (int)Math.Floor(cfg.FrameBytes * FrameCap) * 8;
        win = At9Transform.Mdct[cfg.Power];
        z = new double[2 * frameSamples];
        u = new double[frameSamples];
        work = new double[frameSamples];

        states = new At9Block[fps][];
        audible = new bool[fps][];
        for (int fi = 0; fi < fps; fi++)
        {
            states[fi] = new At9Block[cfg.Blocks.Length];
            audible[fi] = new bool[cfg.Blocks.Length];
            int c = 0;
            for (int bi = 0; bi < cfg.Blocks.Length; bi++)
            {
                var blk = new At9Block(cfg.Blocks[bi], true);
                foreach (At9Channel ch in blk.Channels)
                {
                    ch.Src = windows[c++];
                    if (blk.Type != At9.Lfe && ch.Src is not null) audible[fi][bi] = true;
                }
                states[fi][bi] = blk;
            }
        }
        costValid = new bool[fps][];
        costBits = new int[fps][];
        costErr = new double[fps][];
        for (int fi = 0; fi < fps; fi++)
        {
            costValid[fi] = new bool[Candidates.Length];
            costBits[fi] = new int[Candidates.Length];
            costErr[fi] = new double[Candidates.Length];
        }
        choice = new int[fps];
        smallest = new int[fps];
        trial = new int[fps];
    }

    /// <summary>
    /// Encodes superframe <paramref name="s"/> into <paramref name="dst"/> at <paramref name="dstOffset"/>, from the windows given to
    /// the constructor, which hold the samples from the superframe's first frame (samples s * frames * frameSamples) on.
    /// </summary>
    public void EncodeSuperframe(int s, byte[] dst, int dstOffset)
    {
        int minBands = cfg.High ? 1 : 3;
        for (int fi = 0; fi < fps; fi++)
        {
            foreach (At9Block blk in states[fi])
            {
                foreach (At9Channel ch in blk.Channels) Forward(ch, fi * frameSamples);
            }
        }
        bool found = false;
        for (int bandLimit = maxBands; !found; bandLimit--)
        {
            if (bandLimit < minBands) throw At9.Fail("the console's ATRAC9 bitrate is too low for this encoder");
            for (int fi = 0; fi < fps; fi++)
            {
                for (int bi = 0; bi < states[fi].Length; bi++)
                {
                    At9Block blk = states[fi][bi];
                    if (blk.Type == At9.Lfe) continue;
                    int bands = audible[fi][bi] ? BandsFor(blk, bandLimit) : minBands;
                    SetupEncodeBlock(blk, bands, fi > 0 && states[fi - 1][bi].BandCount == bands);
                }
            }
            found = Allocate();
        }

        Array.Clear(dst, dstOffset, cfg.SuperframeBytes);
        var wr = new At9BitWriter(dst, dstOffset);
        for (int fi = 0; fi < fps; fi++)
        {
            int start = wr.Pos;
            Candidate c = Candidates[choice[fi]];
            for (int bi = 0; bi < states[fi].Length; bi++)
            {
                if (audible[fi][bi]) WriteBlock(wr, states[fi][bi], fi, c.Start, c.Value, c.Boundary, c.G);
                else WriteBlock(wr, states[fi][bi], fi, SilentStart, SilentValue, SilentBoundary, SilentGradient);
            }
            if (wr.Pos - start > cap) throw At9.Fail("internal error: frame over its size limit");
        }
        if (wr.Pos > dstOffset * 8 + budget) throw At9.Fail("internal error: superframe over budget");
        // Unused space at the end of the superframe, filled as the system file does.
        Array.Fill(dst, padding, wr.Pos >> 3, dstOffset + cfg.SuperframeBytes - (wr.Pos >> 3));
    }

    // The window's first frame position is `from`; the window's samples are -1..1 and become the decoder's 16-bit scale, clipped to full scale like its output.
    private void Forward(At9Channel ch, int from)
    {
        float[]? src = ch.Src;
        if (src is null) { Array.Clear(ch.X); return; }
        int n = frameSamples, h = n >> 1;
        for (int i = 0; i < 2 * n; i++)
        {
            double v = src[from + i];
            v = v > 1 ? 1 : v < -1 ? -1 : v;
            z[i] = v * 32768 * (i < n ? win[i] : win[2 * n - 1 - i]);
        }
        for (int k = 0; k < h; k++) u[k] = -z[3 * h - 1 - k] - z[3 * h + k];
        for (int k = h; k < n; k++) u[k] = z[k - h] - z[3 * h - 1 - k];
        At9Transform.Dct4(cfg.Power, u, ch.X, work);
        double scale = 2.0 / n;
        for (int k = 0; k < n; k++) ch.X[k] *= scale;
    }

    // Smallest band count (up to the limit) whose units cover every non-silent bin.
    private int BandsFor(At9Block blk, int limit)
    {
        int last = -1;
        foreach (At9Channel ch in blk.Channels)
        {
            for (int k = 255; k > last; k--)
            {
                if (Math.Abs(ch.X[k]) >= 3.0517578125e-5) { last = k; break; }
            }
        }
        int b = cfg.High ? 1 : 3;
        while (b < limit && Atrac9Tables.UnitStart[Atrac9Tables.BandUnits[b]] <= last) b++;
        return b;
    }

    private static int ScaleFactorFor(double peak)
    {
        if (!(peak > 0)) return 0;
        return (int)Math.Max(0, Math.Min(31, Math.Ceiling(Math.Log2(peak) + 15 - 1e-9)));
    }

    private void SetupEncodeBlock(At9Block blk, int bandCount, bool reuse)
    {
        int[] unitStart = Atrac9Tables.UnitStart;
        blk.BandCount = bandCount;
        blk.Units = Atrac9Tables.BandUnits[bandCount];
        blk.StereoUnits = blk.Units;
        blk.ExtUnit = blk.Units;
        blk.Reuse = reuse;
        foreach (At9Channel ch in blk.Channels)
        {
            ch.Coded = blk.Units;
            Array.Clear(ch.Sf);
            for (int i = 0; i < blk.Units; i++)
            {
                double peak = 0;
                for (int k = unitStart[i]; k < unitStart[i + 1]; k++) peak = Math.Max(peak, Math.Abs(ch.X[k]));
                ch.Sf[i] = ScaleFactorFor(peak);
            }
            At9Decoder.CodebookSets(cfg, ch, blk.Units);
            // The precision mask of gradient modes 1-3 (as the decoder derives it).
            Array.Clear(ch.Mask);
            for (int i = 1; i < blk.Units; i++)
            {
                int d = ch.Sf[i] - ch.Sf[i - 1];
                if (d > 1) ch.Mask[i] += Math.Min(d - 1, 5);
                else if (d < -1) ch.Mask[i - 1] += Math.Min(-d - 1, 5);
            }
            Array.Fill(ch.MemoBits, -1);
        }
        foreach (At9Channel ch in blk.Channels) ch.SfPlan = PlanScaleFactors(blk, ch, blk.Channels[0]);
    }

    // ---- scale factors ------------------------------------------------------------------------------------------

    // Cheapest valid scale factor coding for a channel.
    private static At9SfPlan PlanScaleFactors(At9Block blk, At9Channel ch, At9Channel other)
    {
        int[] sf = ch.Sf;
        int n = blk.Units;
        var best = new At9SfPlan { Bits = int.MaxValue };
        bool have = false;

        // VLC delta offset (mode 0), both channels.
        for (int w = 0; w < 8; w++)
        {
            int[] weights = Atrac9Tables.SfWeights[w];
            int lo = 99, hi = -99;
            for (int i = 0; i < n; i++)
            {
                lo = Math.Min(lo, sf[i] + weights[i]);
                hi = Math.Max(hi, sf[i] + weights[i]);
            }
            int baseValue = Math.Min(lo, 31);
            if (baseValue < 0) continue;
            for (int len = 3; len <= 6; len++)
            {
                if (hi - baseValue >= 1 << len) continue;
                Atrac9Codebook cb = Atrac9Tables.SfUnsigned[len]!;
                int bits = 2 + 3 + 5 + 2 + len;
                bool ok = true;
                for (int i = 1; i < n && ok; i++)
                {
                    int d = (sf[i] + weights[i] - (sf[i - 1] + weights[i - 1])) & cb.Mask;
                    if (cb.Bits[d] == 0) ok = false; else bits += cb.Bits[d];
                }
                if (!ok) continue;
                if (!have || bits < best.Bits) { best = new At9SfPlan { Bits = bits, Kind = 0, Weights = w, Base = baseValue, Len = len }; have = true; }
            }
        }
        if (ch.Index == 0)
        {
            // Fixed-length offset (mode 1).
            int lo = 31, hi = 0;
            for (int i = 0; i < n; i++)
            {
                lo = Math.Min(lo, sf[i]);
                hi = Math.Max(hi, sf[i]);
            }
            for (int len = 2; len <= 5; len++)
            {
                int baseValue = len < 5 ? lo : 0;
                if (hi - baseValue >= 1 << len) continue;
                int bits = 2 + 2 + (len < 5 ? 5 : 0) + n * len;
                if (!have || bits < best.Bits) { best = new At9SfPlan { Bits = bits, Kind = 1, Base = baseValue, Len = len }; have = true; }
            }
        }
        else
        {
            // Distance to the first channel (mode 1 for the second channel).
            for (int len = 2; len <= 5; len++)
            {
                Atrac9Codebook cb = Atrac9Tables.SfSigned[len]!;
                int half = 1 << (len - 1);
                int bits = 2 + 2;
                bool ok = true;
                for (int i = 0; i < n && ok; i++)
                {
                    int d = sf[i] - other.Sf[i];
                    if (d < -half || d >= half || cb.Bits[d & cb.Mask] == 0) ok = false; else bits += cb.Bits[d & cb.Mask];
                }
                if (!ok) continue;
                if (!have || bits < best.Bits) { best = new At9SfPlan { Bits = bits, Kind = 2, Len = len }; have = true; }
            }
        }
        if (!have) throw At9.Fail("internal error: no scale factor coding fits");
        return best;
    }

    private static void WriteScaleFactors(At9BitWriter wr, At9Block blk, At9Channel ch)
    {
        At9SfPlan plan = ch.SfPlan;
        int[] sf = ch.Sf;
        int n = blk.Units;
        switch (plan.Kind)
        {
            case 0:
            {
                int[] weights = Atrac9Tables.SfWeights[plan.Weights];
                Atrac9Codebook cb = Atrac9Tables.SfUnsigned[plan.Len]!;
                wr.Write(0, 2); wr.Write(plan.Weights, 3); wr.Write(plan.Base, 5); wr.Write(plan.Len - 3, 2);
                wr.Write(sf[0] + weights[0] - plan.Base, plan.Len);
                for (int i = 1; i < n; i++)
                {
                    int d = (sf[i] + weights[i] - (sf[i - 1] + weights[i - 1])) & cb.Mask;
                    wr.Write(cb.Codes[d], cb.Bits[d]);
                }
                break;
            }
            case 1:
                wr.Write(1, 2); wr.Write(plan.Len - 2, 2);
                if (plan.Len < 5) wr.Write(plan.Base, 5);
                for (int i = 0; i < n; i++) wr.Write(sf[i] - plan.Base, plan.Len);
                break;
            default:
            {
                At9Channel other = blk.Channels[0];
                Atrac9Codebook cb = Atrac9Tables.SfSigned[plan.Len]!;
                wr.Write(1, 2); wr.Write(plan.Len - 2, 2);
                for (int i = 0; i < n; i++)
                {
                    int d = (sf[i] - other.Sf[i]) & cb.Mask;
                    wr.Write(cb.Codes[d], cb.Bits[d]);
                }
                break;
            }
        }
    }

    // ---- quantization and cost ----------------------------------------------------------------------------------

    // JavaScript's Math.round: to the nearest integer, halves up.
    private static double JsRound(double v)
    {
        double f = Math.Floor(v);
        return v - f >= 0.5 ? f + 1 : f;
    }

    // Quantize one unit at precision p (1..15). Returns the coded bits and the squared error; with `store` the values are kept for writing.
    private int QuantizeUnit(At9Channel ch, int unit, int p, bool store, out double err)
    {
        int start = Atrac9Tables.UnitStart[unit], end = Atrac9Tables.UnitStart[unit + 1];
        double scale = Math.ScaleB(1.0, ch.Sf[unit] - 15), step = Atrac9Tables.Step[p];
        int lim = (1 << p) - 1;
        int[] q = store ? ch.Q : ch.ScratchQ;
        double[] x = ch.X;
        for (int k = start; k < end; k++)
        {
            double c = JsRound(x[k] / scale / step);
            q[k] = c > lim ? lim : c < -lim ? -lim : (int)c;
        }
        int bits = 0;
        if (p + 1 <= (cfg.High ? 1 : 7))
        {
            Atrac9Codebook cb = Atrac9Tables.Spectrum[ch.Set[unit]][p + 1][Atrac9Tables.UnitBook[unit]]!;
            int[] near = cb.Near;
            for (int k = start; k < end; k += cb.ValueCount)
            {
                int sym = 0;
                for (int j = 0; j < cb.ValueCount; j++) sym |= (q[k + j] & cb.Mask) << (j * cb.ValueBits);
                if (cb.Bits[sym] == 0)
                {
                    sym = near[sym];
                    for (int j = 0, v = sym; j < cb.ValueCount; j++, v >>= cb.ValueBits) q[k + j] = Atrac9Codebook.SignExtend(v & cb.Mask, cb.ValueBits);
                }
                bits += cb.Bits[sym];
            }
        }
        else
        {
            bits = (end - start) * (p + 1);
        }
        double sum = 0;
        for (int k = start; k < end; k++)
        {
            double e = x[k] - q[k] * step * scale;
            sum += e * e;
        }
        err = sum;
        return bits;
    }

    // Precision of a unit under gradient mode 3, exactly as the decoder derives it.
    private static int UnitPrecision(At9Channel ch, int unit, int[] g, int boundary)
    {
        int p = ch.Sf[unit] + ch.Mask[unit] - g[unit];
        if (p > 0) p >>= 2;
        return Math.Max(p, 1) + (unit < boundary ? 1 : 0);
    }

    private int HeaderBits(At9Block blk)
    {
        if (blk.Type == At9.Lfe) return 2 + 10 + 4 * ((lfeReuse != 0 ? 8 : 4) + 1);
        int bits = 2;
        if (!blk.Reuse) bits += 4 + (blk.Type == At9.Stereo ? 4 : 0) + 1;
        bits += 2 + 5 + 5 + 4;                         // gradient (mode 3)
        if (blk.Type == At9.Stereo) bits += 2;         // primary channel, no joint signs
        return bits + 1;                               // no extension data
    }

    // The precision of each unit under gradient g without the boundary's extra bit, for every channel of an audible block; the six
    // candidates that differ only in their boundary share it.
    private static void PrepareGradient(At9Block blk, int[] g)
    {
        foreach (At9Channel ch in blk.Channels)
        {
            for (int i = 0; i < blk.Units; i++)
            {
                int p = ch.Sf[i] + ch.Mask[i] - g[i];
                if (p > 0) p >>= 2;
                ch.BasePrecision[i] = Math.Max(p, 1);
            }
        }
    }

    // Coded size of an audible block (in whole bytes, as bits) and its squared error for the gradient prepared with PrepareGradient and
    // the given boundary; false if a unit would need more than the allowed precision. The sums run unit by unit, channel by channel.
    private bool BlockCost(At9Block blk, int boundary, out int bytesInBits, out double err)
    {
        int bits = HeaderBits(blk);
        double sum = 0;
        At9Channel[] channels = blk.Channels;
        int units = blk.Units;
        for (int c = 0; c < channels.Length; c++)
        {
            At9Channel ch = channels[c];
            int[] basePrecision = ch.BasePrecision, memoBits = ch.MemoBits;
            double[] memoErr = ch.MemoErr;
            bits += ch.SfPlan.Bits;
            for (int i = 0; i < units; i++)
            {
                int p = basePrecision[i] + (i < boundary ? 1 : 0);
                if (p > maxPrecision) { bytesInBits = 0; err = 0; return false; }
                int key = i * 16 + p;
                int b = memoBits[key];
                if (b < 0)
                {
                    b = memoBits[key] = QuantizeUnit(ch, i, p, false, out double e);
                    memoErr[key] = e;
                }
                bits += b;
                sum += memoErr[key];
            }
        }
        bytesInBits = (bits + 7) / 8 * 8;
        err = sum;
        return true;
    }

    // The size of a block that is not audible (silent or the LFE): coded with the silent gradient and no boundary.
    private int SilentBlockBits(At9Block blk)
    {
        if (blk.Type == At9.Lfe) return (HeaderBits(blk) + 7) / 8 * 8;
        PrepareGradient(blk, SilentGradient);
        if (!BlockCost(blk, 0, out int bits, out _)) throw At9.Fail("internal error: silent block over the precision limit");
        return bits;
    }

    // ---- writing ------------------------------------------------------------------------------------------------

    private void WriteBlock(At9BitWriter wr, At9Block blk, int frameIndex, int planStart, int planValue, int planBoundary, int[] g)
    {
        wr.Write(frameIndex != 0 ? 1 : 0, 1);
        if (blk.Type == At9.Lfe)
        {
            // Silent, coded as the system file codes its LFE (Sony's: reused parameters, precision 8).
            wr.Write(lfeReuse, 1);
            wr.Write(0, 10);
            wr.Write(0, 4 * ((lfeReuse != 0 ? 8 : 4) + 1));
            wr.Align();
            return;
        }
        wr.Write(blk.Reuse ? 1 : 0, 1);
        int minBands = cfg.High ? 1 : 3;
        if (!blk.Reuse)
        {
            wr.Write(blk.BandCount - minBands, 4);
            if (blk.Type == At9.Stereo) wr.Write(blk.BandCount - minBands, 4);
            wr.Write(0, 1);
        }
        int boundary = Math.Min(planBoundary, blk.Units);
        wr.Write(3, 2); wr.Write(planStart, 5); wr.Write(planValue, 5); wr.Write(boundary, 4);
        if (blk.Type == At9.Stereo) { wr.Write(0, 1); wr.Write(0, 1); }
        wr.Write(0, 1);
        int[] unitStart = Atrac9Tables.UnitStart;
        foreach (At9Channel ch in blk.Channels)
        {
            WriteScaleFactors(wr, blk, ch);
            for (int i = 0; i < blk.Units; i++)
            {
                int p = UnitPrecision(ch, i, g, boundary), start = unitStart[i], end = unitStart[i + 1];
                QuantizeUnit(ch, i, p, true, out _);
                if (p + 1 <= (cfg.High ? 1 : 7))
                {
                    Atrac9Codebook cb = Atrac9Tables.Spectrum[ch.Set[i]][p + 1][Atrac9Tables.UnitBook[i]]!;
                    for (int k = start; k < end; k += cb.ValueCount)
                    {
                        int sym = 0;
                        for (int j = 0; j < cb.ValueCount; j++) sym |= (ch.Q[k + j] & cb.Mask) << (j * cb.ValueBits);
                        wr.Write(cb.Codes[sym], cb.Bits[sym]);
                    }
                }
                else
                {
                    for (int k = start; k < end; k++) wr.Write(ch.Q[k] & ((1 << (p + 1)) - 1), p + 1);
                }
            }
        }
        wr.Align();
    }

    // ---- allocation ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Picks one allocation (index into the candidates) per frame into <see cref="choice"/>: the least total squared error with every
    /// frame within the frame cap and the superframe within its budget. False if even the smallest allocations do not fit.
    /// </summary>
    private bool Allocate()
    {
        for (int fi = 0; fi < fps; fi++)
        {
            At9Block[] blocks = states[fi];
            int fixedBits = 0;
            for (int bi = 0; bi < blocks.Length; bi++)
            {
                if (!audible[fi][bi]) fixedBits += SilentBlockBits(blocks[bi]);
            }
            bool[] valid = costValid[fi];
            int[] bitsOf = costBits[fi];
            double[] errOf = costErr[fi];
            for (int ci = 0; ci < Candidates.Length; ci++)
            {
                ref readonly Candidate c = ref Candidates[ci];
                if (ci % Boundaries.Length == 0)
                {
                    for (int bi = 0; bi < blocks.Length; bi++)
                    {
                        if (audible[fi][bi]) PrepareGradient(blocks[bi], c.G);
                    }
                }
                int bits = fixedBits;
                double err = 0;
                bool ok = true;
                for (int bi = 0; bi < blocks.Length; bi++)
                {
                    if (!audible[fi][bi]) continue;
                    if (!BlockCost(blocks[bi], c.Boundary, out int b, out double e)) { ok = false; break; }
                    bits += b;
                    err += e;
                }
                valid[ci] = ok && bits <= cap;
                bitsOf[ci] = bits;
                errOf[ci] = err;
            }
        }
        Pick(0, choice);
        if (Total(choice) <= budget) return true;
        // Smallest allocation per frame: fewest bits, then least error.
        for (int fi = 0; fi < fps; fi++)
        {
            int best = -1;
            for (int ci = 0; ci < Candidates.Length; ci++)
            {
                if (!costValid[fi][ci]) continue;
                if (best < 0 || costBits[fi][ci] < costBits[fi][best] || (costBits[fi][ci] == costBits[fi][best] && costErr[fi][ci] < costErr[fi][best])) best = ci;
            }
            smallest[fi] = best;
        }
        if (Total(smallest) > budget) return false;
        double lo = -12, hi = 12;
        for (int k = 0; k < 48; k++)
        {
            double mid = (lo + hi) / 2;
            Pick(Math.Pow(10, mid), trial);
            if (Total(trial) <= budget) hi = mid; else lo = mid;
        }
        Pick(Math.Pow(10, hi), choice);
        if (Total(choice) > budget) Array.Copy(smallest, choice, fps);
        return true;
    }

    private void Pick(double lambda, int[] into)
    {
        for (int fi = 0; fi < fps; fi++)
        {
            bool[] valid = costValid[fi];
            int[] bitsOf = costBits[fi];
            double[] errOf = costErr[fi];
            int best = -1;
            double score = double.PositiveInfinity;
            for (int ci = 0; ci < valid.Length; ci++)
            {
                if (!valid[ci]) continue;
                double v = errOf[ci] + lambda * bitsOf[ci];
                if (v < score || (v == score && best >= 0 && bitsOf[ci] < bitsOf[best])) { score = v; best = ci; }
            }
            into[fi] = best;
        }
    }

    private long Total(int[] pick)
    {
        long sum = 0;
        for (int fi = 0; fi < fps; fi++)
        {
            if (pick[fi] < 0) return long.MaxValue;
            sum += costBits[fi][pick[fi]];
        }
        return sum;
    }
}

/// <summary>Where the encoder's input comes from: it fills the per-channel windows, one superframe after the other.</summary>
internal abstract class At9WindowFeed
{
    /// <summary>One window per channel of the file, null for a channel without sound (the LFE). A window holds the samples (-1..1) of one superframe and the frame after it, zero outside the music.</summary>
    public float[]?[] Windows { get; protected set; } = null!;

    /// <summary>Fills the windows for superframe <paramref name="s"/>, in order of <paramref name="s"/>.</summary>
    public abstract void Fill(int s);
}

/// <summary>Input from whole arrays: <c>sources[c]</c> holds the channel's <c>n</c> samples of music, which start at sample <c>delay</c>.</summary>
internal sealed class At9ArrayFeed : At9WindowFeed
{
    private readonly float[]?[] sources;
    private readonly int n, delay, superframeSamples;

    public At9ArrayFeed(At9Config cfg, float[]?[] sources, int n, int delay)
    {
        this.sources = sources;
        this.n = n;
        this.delay = delay;
        superframeSamples = cfg.FramesPerSuperframe * cfg.FrameSamples;
        Windows = new float[]?[sources.Length];
        for (int c = 0; c < sources.Length; c++)
        {
            if (sources[c] is not null) Windows[c] = new float[(cfg.FramesPerSuperframe + 1) * cfg.FrameSamples];
        }
    }

    public override void Fill(int s)
    {
        for (int c = 0; c < sources.Length; c++)
        {
            float[]? w = Windows[c], src = sources[c];
            if (w is null || src is null) continue;
            int from = s * superframeSamples - delay;
            for (int i = 0; i < w.Length; i++)
            {
                int t = from + i;
                w[i] = t >= 0 && t < n ? src[t] : 0f;
            }
        }
    }
}

/// <summary>
/// Input from the file itself at another volume: the file is decoded one frame ahead of the encoder. The window of each
/// channel is scaled with <paramref name="factor"/> (rounded to float32 like the decoded samples) and is zero before the
/// file's delay and after its music, as the encoder's input is. The LFE channel is not kept; it has to be silent. Channels
/// without sound have no window, so the encoder writes them silent.
/// </summary>
internal sealed class At9RescaleFeed : At9WindowFeed
{
    internal const double Silence = 1.0 / (1 << 20);

    private readonly At9Decoder decoder;
    private readonly At9Config cfg;
    private readonly double factor;
    private readonly int n, delay, totalFrames;
    private readonly float[]?[] destinations;
    private int nextFrame;

    /// <param name="silent">Channels without sound in the whole file: they get no window, like the LFE.</param>
    public At9RescaleFeed(At9File file, double factor, int n, int delay, bool[] silent)
    {
        this.factor = factor;
        this.n = n;
        this.delay = delay;
        cfg = file.Config;
        decoder = new At9Decoder(file);
        totalFrames = file.Superframes * cfg.FramesPerSuperframe;
        int length = (cfg.FramesPerSuperframe + 1) * cfg.FrameSamples;
        Windows = new float[]?[cfg.ChannelCount];
        destinations = new float[]?[cfg.ChannelCount];
        int c = 0;
        foreach (int type in cfg.Blocks)
        {
            for (int i = 0; i < (type == At9.Stereo ? 2 : 1); i++, c++)
            {
                destinations[c] = new float[length];
                if (type != At9.Lfe && !silent[c]) Windows[c] = destinations[c];
            }
        }
    }

    public override void Fill(int s)
    {
        int fps = cfg.FramesPerSuperframe, n256 = cfg.FrameSamples;
        if (s == 0)
        {
            Decode(0, 0);
        }
        else
        {
            // The last frame of the previous window is the first of this one.
            foreach (float[]? w in Windows)
            {
                if (w is not null) Array.Copy(w, fps * n256, w, 0, n256);
            }
        }
        for (int k = 1; k <= fps; k++) Decode(s * fps + k, k * n256);
    }

    // Decodes frame `frame` of the file into the windows at `offset`, scaled, or zeroes that part of them past the end.
    private void Decode(int frame, int offset)
    {
        int fps = cfg.FramesPerSuperframe, size = cfg.FrameSamples;
        if (frame >= totalFrames)
        {
            foreach (float[]? w in destinations) Array.Clear(w!, offset, size);
            return;
        }
        if (frame != nextFrame) throw new InvalidOperationException("frames are decoded in order");
        if (frame % fps == 0) decoder.BeginSuperframe(frame / fps);
        decoder.DecodeFrame(frame % fps, destinations, offset);
        if (frame % fps == fps - 1) decoder.EndSuperframe();
        nextFrame++;
        int c = 0;
        foreach (int type in cfg.Blocks)
        {
            for (int i = 0; i < (type == At9.Stereo ? 2 : 1); i++, c++)
            {
                float[] w = destinations[c]!;
                if (type == At9.Lfe)
                {
                    for (int k = 0; k < size; k++)
                    {
                        if (Math.Abs((double)w[offset + k]) > Silence) throw At9.Fail("the file has sound in its LFE channel, which the encoder does not write");
                    }
                    continue;
                }
                long t0 = (long)frame * size;
                for (int k = 0; k < size; k++)
                {
                    long t = t0 + k;
                    w[offset + k] = t >= delay && t < (long)delay + n ? (float)(w[offset + k] * factor) : 0f;
                }
            }
        }
    }
}

/// <summary>
/// Checks the encoder's output while it is written: each superframe is decoded again and compared with the encoder's
/// input, overall and in every 1024-sample segment, so one damaged frame cannot hide in a long track. A frame's transform
/// spans 512 samples, so coding noise in segment k comes only from sound in segments k-1..k+1: the noise of segments
/// k-1..k+1 is compared with the sound of k-2..k+2, and a segment whose neighbourhood is silent must stay silent. Channels
/// without a source must decode to silence. (Music measures 20 to 55 dB overall, white noise about 3.5 dB; silent, inverted
/// or shifted output gives 0 dB or less.)
/// </summary>
internal sealed class At9Verifier
{
    private const int Segment = 1024;
    private const double MinSnr = 1, MinSegmentSnr = -3;
    private const double Quiet = 1.0 / (1 << 15);       // passages below this RMS are only checked for added noise
    private const double Silence = 1.0 / (1 << 20);     // silent channels stay below 1/32 of a 16-bit step

    private readonly At9Decoder decoder;
    private readonly At9Config cfg;
    private readonly float[]?[] windows;
    private readonly float[][] output;
    private readonly int n, delay, used;
    private readonly double[] sig, err;

    public At9Verifier(At9File encoded, float[]?[] windows, int n, int delay)
    {
        cfg = encoded.Config;
        decoder = new At9Decoder(encoded);
        this.windows = windows;
        this.n = n;
        this.delay = delay;
        output = new float[cfg.ChannelCount][];
        int frames = cfg.FramesPerSuperframe * cfg.FrameSamples;
        for (int c = 0; c < output.Length; c++)
        {
            output[c] = new float[frames];
            if (windows[c] is not null) used++;
        }
        int segs = (n + Segment - 1) / Segment;
        sig = new double[segs];
        err = new double[segs];
    }

    /// <summary>Decodes superframe <paramref name="s"/> of the encoded file, which has just been written, and measures it against the input.</summary>
    public void Check(int s)
    {
        int fps = cfg.FramesPerSuperframe, size = cfg.FrameSamples;
        decoder.BeginSuperframe(s);
        for (int f = 0; f < fps; f++) decoder.DecodeFrame(f, output, f * size);
        decoder.EndSuperframe();
        long from = (long)s * fps * size;
        for (int c = 0; c < output.Length; c++)
        {
            float[] o = output[c];
            float[]? src = windows[c];
            if (src is null)
            {
                for (int i = 0; i < o.Length; i++)
                {
                    if (Math.Abs((double)o[i]) > Silence) throw At9.Fail("a silent channel decoded to sound");
                }
                continue;
            }
            for (int i = 0; i < o.Length; i++)
            {
                long t = from + i - delay;
                if (t < 0 || t >= n) continue;
                double a = Math.Max(-1.0, Math.Min(1.0, (double)src[i])), e = o[i] - a;
                int seg = (int)(t / Segment);
                sig[seg] += a * a;
                err[seg] += e * e;
            }
        }
    }

    private double Around(double[] arr, int k, int r)
    {
        double sum = 0;
        for (int j = Math.Max(0, k - r); j <= Math.Min(arr.Length - 1, k + r); j++) sum += arr[j];
        return sum;
    }

    private double Samples(int k, int r) => ((double)Math.Min(n, (long)(k + r + 1) * Segment) - Math.Max(0L, (long)(k - r) * Segment)) * used;

    /// <summary>Compares what the output uses with <paramref name="limits"/> and the measured quality with what a working encoder gives; throws if either is off.</summary>
    public (double Snr, double Worst) Finish(At9Profile limits)
    {
        string why = At9Profile.FitsProfile(decoder.Profile, limits, cfg.FrameBytes);
        if (why.Length > 0) throw At9.Fail($"internal error: the encoded music {why}");
        double signal = 0, noise = 0, worst = 99;
        for (int k = 0; k < sig.Length; k++)
        {
            signal += sig[k];
            noise += err[k];
            if (Around(sig, k, 1) / Samples(k, 1) <= Quiet * Quiet)
            {
                if (err[k] / Samples(k, 0) > Quiet * Quiet) throw At9.Fail("a quiet passage decoded with audible noise");
            }
            double s5 = Around(sig, k, 2);
            if (s5 / Samples(k, 2) > Quiet * Quiet) worst = Math.Min(worst, 10 * Math.Log10(s5 / Math.Max(Around(err, k, 1), 1e-30)));
        }
        // A silent input has no meaningful SNR; the quiet-passage check covers it.
        double snr = noise != 0 && signal / ((double)n * used) > Quiet * Quiet ? 10 * Math.Log10(signal / noise) : 99;
        if (!(snr >= MinSnr) || !(worst >= MinSegmentSnr))
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            throw At9.Fail($"the encoded music does not match the input closely enough ({snr.ToString("F1", ci)} dB overall, {worst.ToString("F1", ci)} dB worst passage)");
        }
        return (snr, worst);
    }
}

/// <summary>The result of <see cref="Atrac9Codec.Encode"/>.</summary>
/// <param name="Bytes">The .at9 file.</param>
/// <param name="Samples">Samples of the music itself (without the repeat that follows the loop).</param>
/// <param name="LoopStart">Loop points in samples after the encoder delay; -1 without a loop.</param>
/// <param name="LoopEnd">-1 without a loop.</param>
/// <param name="Snr">Signal-to-noise of the decode check, in dB.</param>
/// <param name="Worst">The same for the worst passage.</param>
public sealed record Atrac9Encoded(byte[] Bytes, int Samples, int LoopStart, int LoopEnd, double Snr, double Worst);

/// <summary>
/// ATRAC9, the format of the console's home music (<c>bgm_home.at9</c>): decoding, encoding and changing the volume of a file.
/// A port of the encoder in <c>web/atrac9/vendor/atrac9.js</c> (Blaze414's ps5-theme, GPL-3.0-or-later; see the notes at the top of this
/// file), which gives the same bytes for the same input.
/// </summary>
public static class Atrac9Codec
{
    private const int LoopStartSeconds = 2;      // seconds into the song where the loop starts
    private const int LoopAfterSeconds = 1;      // seconds of audio after the loop end

    /// <summary>
    /// An ATRAC9 file at another volume: <paramref name="factor"/> is the amplitude (1 is the file as it is, 0.5 half the
    /// amplitude, 0 silence; below 0 it is read as 0). The file is decoded, every sample multiplied with it and the result
    /// encoded again into the file's own layout: the same number of superframes, so the same size, and the header, the fact
    /// and the loop chunks byte for byte as they were. At 1 or more, or with NaN, the same array comes back.
    /// </summary>
    /// <remarks>
    /// Unlike the web builder's encoder, which writes a stereo signal and leaves the other blocks silent, every channel except
    /// the LFE is kept (Sony's music has sound in the centre and surround channels). The limits of the output (bands, precision,
    /// LFE coding, padding) come from the file itself, except that bit allocation mode 3, the one Sony's files use, is always
    /// written: files from other encoders use other modes. The output is decoded again while it is written and compared with
    /// the input; a result that fails the check throws. Memory: the input and the output, and about five frames per channel;
    /// the track is never held as audio.
    /// </remarks>
    /// <param name="at9">An .at9 file with 48 kHz or lower rate and 256-sample frames, as the console's music is.</param>
    /// <param name="progress">Called with 0 to 1 now and then.</param>
    /// <exception cref="ThemePackException">The input is not an ATRAC9 file this codec handles, has sound in its LFE channel, or the encoded result fails its check.</exception>
    public static byte[] ScaleVolume(byte[] at9, double factor, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(at9);
        if (double.IsNaN(factor) || factor >= 1.0) return at9;
        try
        {
            return Rescale(at9, factor < 0 ? 0 : factor, progress);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or NullReferenceException or OverflowException)
        {
            throw new ThemePackException("the ATRAC9 file is damaged", e);   // a parser check the format does not have
        }
    }

    private static byte[] Rescale(byte[] at9, double factor, Action<double>? progress)
    {
        At9File file = At9File.Parse(at9);
        At9Config cfg = file.Config;
        if (cfg.High) throw At9.Fail("this console's music uses a high sample rate the encoder does not support");
        if (cfg.Power != 8) throw At9.Fail("unsupported ATRAC9 frame size");
        if (file.Superframes == 0) return at9;

        // The file's own profile bounds what is written. Every frame is decoded once to find it, and to find the channels
        // without sound: they are written as the encoder writes the silent channels of a stereo theme (the silent plan),
        // as the console's decoder plays them, not as near-silence that takes bits from the others.
        var decoder = new At9Decoder(file);
        var scratch = new float[]?[cfg.ChannelCount];
        for (int c = 0; c < scratch.Length; c++) scratch[c] = new float[cfg.FrameSamples];
        var silent = new bool[cfg.ChannelCount];
        Array.Fill(silent, true);
        for (int s = 0; s < file.Superframes; s++)
        {
            decoder.BeginSuperframe(s);
            for (int f = 0; f < cfg.FramesPerSuperframe; f++)
            {
                decoder.DecodeFrame(f, scratch, 0);
                for (int c = 0; c < scratch.Length; c++)
                {
                    if (!silent[c]) continue;
                    foreach (float v in scratch[c]!)
                    {
                        if (Math.Abs((double)v) > At9RescaleFeed.Silence) { silent[c] = false; break; }
                    }
                }
            }
            decoder.EndSuperframe();
        }
        At9Profile profile = decoder.Profile;
        profile.Modes |= 8;

        long total = (long)file.Superframes * cfg.SuperframeSamples;
        int delay = (int)Math.Min(file.Fact is null ? 0 : file.Fact[2], total);
        int n = (int)Math.Min(file.Fact is null ? total : file.Fact[0], total - delay);

        byte[] output = (byte[])at9.Clone();
        var feed = new At9RescaleFeed(file, factor, n, delay, silent);
        Run(output, profile, feed, n, delay, file.Superframes, progress);
        return output;
    }

    /// <summary>Decodes a whole .at9 file to one array per channel (-1..1, before clamping), in the file's channel order.</summary>
    public static float[][] Decode(byte[] at9)
    {
        ArgumentNullException.ThrowIfNull(at9);
        try
        {
            return DecodeAll(at9);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or NullReferenceException or OverflowException)
        {
            throw new ThemePackException("the ATRAC9 file is damaged", e);
        }
    }

    private static float[][] DecodeAll(byte[] at9)
    {
        At9File file = At9File.Parse(at9);
        At9Config cfg = file.Config;
        long total = (long)file.Superframes * cfg.SuperframeSamples;
        if (total > int.MaxValue) throw At9.Fail("the file is too long to decode in one piece");
        var channels = new float[cfg.ChannelCount][];
        for (int c = 0; c < channels.Length; c++) channels[c] = new float[total];
        var decoder = new At9Decoder(file);
        int pos = 0;
        for (int s = 0; s < file.Superframes; s++)
        {
            decoder.BeginSuperframe(s);
            for (int f = 0; f < cfg.FramesPerSuperframe; f++, pos += cfg.FrameSamples) decoder.DecodeFrame(f, channels, pos);
            decoder.EndSuperframe();
        }
        return channels;
    }

    /// <summary>
    /// The first <paramref name="maxSeconds"/> of the music of an .at9 file (after the encoder delay) as 16-bit stereo PCM,
    /// interleaved left, right, for a preview that plays the excerpt. Multichannel music is mixed down the usual way: the front
    /// pair as it is, the centre channel into both sides at -3 dB, every further pair (surround, rear) into its own side at
    /// -6 dB, and the LFE channel left out (it is not even synthesized); a mono file plays on both sides. The excerpt starts with
    /// a few milliseconds of fade-in and ends with a quarter of a second of fade-out, so that it can be played in a loop without
    /// a click. A file shorter than <paramref name="maxSeconds"/> gives all of it.
    /// </summary>
    /// <remarks>
    /// Only the superframes of the excerpt are decoded, one frame at a time into a scratch window; the memory is the result
    /// (about 5.5 MB for 30 s at 48 kHz) and a few frames per channel. It is CPU work (hundreds of milliseconds per 30 s on
    /// a PC): never call it on a frame thread.
    /// </remarks>
    /// <param name="sampleRate">The file's sample rate.</param>
    /// <exception cref="ThemePackException">The input is not an ATRAC9 file this codec reads, or it is damaged.</exception>
    public static short[] DecodeStereo(byte[] at9, double maxSeconds, out int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(at9);
        try
        {
            return DecodeExcerpt(at9, maxSeconds, out sampleRate);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or NullReferenceException or OverflowException)
        {
            throw new ThemePackException("the ATRAC9 file is damaged", e);
        }
    }

    private const float CentreGain = 0.707f, SurroundGain = 0.5f;
    private const double FadeInSeconds = 0.02, FadeOutSeconds = 0.25;

    private static short[] DecodeExcerpt(byte[] at9, double maxSeconds, out int sampleRate)
    {
        At9File file = At9File.Parse(at9);
        At9Config cfg = file.Config;
        sampleRate = cfg.SampleRate;
        long total = (long)file.Superframes * cfg.SuperframeSamples;
        int delay = (int)Math.Min(file.Fact is null ? 0 : file.Fact[2], total);
        long length = Math.Min(file.Fact is null ? total : file.Fact[0], total - delay);
        long wanted = Math.Min(length, (long)(Math.Max(0, maxSeconds) * cfg.SampleRate));
        if (wanted <= 0 || wanted > int.MaxValue / 2) return [];

        // Which channel goes where: the gains into the left and right side, and the channels that are not wanted (LFE).
        var left = new float[cfg.ChannelCount];
        var right = new float[cfg.ChannelCount];
        var scratch = new float[]?[cfg.ChannelCount];
        int front = Array.IndexOf(cfg.Blocks, At9.Stereo), monos = 0;
        foreach (int type in cfg.Blocks) if (type == At9.Mono) monos++;
        for (int bi = 0, c = 0, mono = 0; bi < cfg.Blocks.Length; bi++)
        {
            switch (cfg.Blocks[bi])
            {
                case At9.Stereo:
                    float gain = bi == front ? 1f : SurroundGain;
                    (left[c], right[c + 1]) = (gain, gain);
                    c += 2;
                    break;
                case At9.Mono:
                    if (front >= 0) (left[c], right[c]) = (CentreGain, CentreGain);
                    else if (monos >= 2) (left[c], right[c]) = mono == 0 ? (1f, 0f) : (0f, 1f);
                    else (left[c], right[c]) = (1f, 1f);
                    mono++;
                    c++;
                    break;
                default:
                    c++;   // the LFE channel
                    break;
            }
        }
        for (int c = 0; c < scratch.Length; c++)
            if (left[c] != 0 || right[c] != 0) scratch[c] = new float[cfg.FrameSamples];

        var output = new short[wanted * 2];
        long superframes = Math.Min(file.Superframes, (delay + wanted + cfg.SuperframeSamples - 1) / cfg.SuperframeSamples);
        var decoder = new At9Decoder(file);
        for (int s = 0; s < superframes; s++)
        {
            decoder.BeginSuperframe(s);
            for (int f = 0; f < cfg.FramesPerSuperframe; f++)
            {
                decoder.DecodeFrame(f, scratch, 0);
                long first = (long)s * cfg.SuperframeSamples + (long)f * cfg.FrameSamples - delay;
                for (int i = Math.Max(0, (int)Math.Min(-first, cfg.FrameSamples)); i < cfg.FrameSamples && first + i < wanted; i++)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < scratch.Length; c++)
                    {
                        if (scratch[c] is not { } samples) continue;
                        l += samples[i] * left[c];
                        r += samples[i] * right[c];
                    }
                    long at = (first + i) * 2;
                    output[at] = ToPcm16(l);
                    output[at + 1] = ToPcm16(r);
                }
            }
        }

        int fadeIn = (int)Math.Min(wanted / 2, (long)(FadeInSeconds * cfg.SampleRate));
        int fadeOut = (int)Math.Min(wanted / 2, (long)(FadeOutSeconds * cfg.SampleRate));
        for (int i = 0; i < fadeIn; i++)
        {
            float k = (float)i / fadeIn;
            output[2 * i] = (short)(output[2 * i] * k);
            output[2 * i + 1] = (short)(output[2 * i + 1] * k);
        }
        for (int i = 0; i < fadeOut; i++)
        {
            long at = (wanted - 1 - i) * 2;
            float k = (float)i / fadeOut;
            output[at] = (short)(output[at] * k);
            output[at + 1] = (short)(output[at + 1] * k);
        }
        return output;
    }

    private static short ToPcm16(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);

    /// <summary>
    /// Encodes stereo PCM into an .at9 laid out like <paramref name="stock"/> (same ATRAC9 config, so the same channel layout, rate
    /// and bitrate; its audio, if it has any, sets the limits). <paramref name="left"/> and <paramref name="right"/> are at the stock
    /// sample rate in -1..1; the first block carries them and every other block is silent. A file with a loop gets the layout of the
    /// console's own music: <paramref name="intro"/> is how many samples play once before the part that loops (0: all of it loops).
    /// A port of the JavaScript's <c>encode</c>, for tests and tools; the app changes volumes with <see cref="ScaleVolume"/>.
    /// </summary>
    public static Atrac9Encoded Encode(byte[] stock, float[] left, float[] right, int intro = 0, Action<double>? progress = null)
    {
        At9File file = At9File.Parse(stock);
        At9Config cfg = file.Config;
        if (cfg.High) throw At9.Fail("this console's music uses a high sample rate the encoder does not support");
        if (cfg.Power != 8) throw At9.Fail("unsupported ATRAC9 frame size");
        int song = Math.Min(left.Length, right.Length);
        if (song == 0) throw At9.Fail("the music is empty");

        // Looping files get the layout of the console's own music: the loop starts inside the audio and more audio follows its end.
        if (!(intro >= 0 && intro <= song - 1)) intro = 0;
        int loopStart = 0, loopEnd = 0, loopFrom = 0, tail = 0;
        bool loops = file.Loop is not null;
        if (loops)
        {
            if (intro > 0)
            {
                int after = Math.Min(LoopAfterSeconds * cfg.SampleRate, (song - intro) / 4);
                (loopStart, loopEnd, loopFrom, tail) = (intro, song - 1, intro, after);
            }
            else
            {
                int start = Math.Min(LoopStartSeconds * cfg.SampleRate, song / 4);
                int after = Math.Min(LoopAfterSeconds * cfg.SampleRate, song / 4);
                (loopStart, loopEnd, loopFrom, tail) = (start, song + start - 1, 0, start + after);
            }
            left = WithTail(left, song, loopFrom, tail);
            right = WithTail(right, song, loopFrom, tail);
        }
        int n = song + (loops ? tail : 0);

        // What the system file uses bounds what the encoder writes.
        At9Profile profile;
        if (file.Superframes > 0)
        {
            var decoder = new At9Decoder(file);
            for (int s = 0; s < file.Superframes; s++)
            {
                decoder.BeginSuperframe(s);
                for (int f = 0; f < cfg.FramesPerSuperframe; f++) decoder.DecodeFrame(f, null, 0);
                decoder.EndSuperframe();
            }
            profile = decoder.Profile;
        }
        else
        {
            profile = At9Profile.Default();
        }
        if ((profile.Modes & 8) == 0) throw At9.Fail("the system sound is coded in a way this encoder does not support");

        int size = cfg.FrameSamples;
        int delay = Math.Max(size, file.Fact is null ? 0 : (int)file.Fact[2]);
        int frames = (delay + n + size - 1) / size;
        int superframes = (frames + cfg.FramesPerSuperframe - 1) / cfg.FramesPerSuperframe;

        // Input as the decoder will see it. Mono layouts get the mid signal.
        var sources = new float[]?[cfg.ChannelCount];
        if (cfg.Blocks[0] == At9.Stereo) { sources[0] = left; sources[1] = right; }
        else if (cfg.Blocks.Length > 1 && cfg.Blocks[1] == At9.Mono)
        {
            sources[0] = left;
            if (sources.Length > 2) sources[2] = right;
        }
        else
        {
            var mid = new float[n];
            for (int i = 0; i < n; i++) mid[i] = (float)(((double)left[i] + right[i]) / 2);
            sources[0] = mid;
        }

        byte[] output = BuildFile(file, superframes, n, delay, loops ? (loopStart, loopEnd) : null);
        var feed = new At9ArrayFeed(cfg, sources, n, delay);
        (double snr, double worst) = Run(output, profile, feed, n, delay, superframes, progress);
        return new Atrac9Encoded(output, song, loops ? loopStart : -1, loops ? loopEnd : -1, snr, worst);
    }

    private static float[] WithTail(float[] x, int song, int from, int tail)
    {
        var result = new float[song + tail];
        Array.Copy(x, result, song);
        Array.Copy(x, from, result, song, tail);
        return result;
    }

    // Encodes `superframes` superframes from the feed into the data chunk of `output` and checks them.
    private static (double Snr, double Worst) Run(byte[] output, At9Profile limits, At9WindowFeed feed, int n, int delay, int superframes, Action<double>? progress)
    {
        At9File outFile = At9File.Parse(output);
        At9Config cfg = outFile.Config;
        var encoder = new At9Encoder(cfg, limits, feed.Windows);
        var verifier = new At9Verifier(outFile, feed.Windows, n, delay);
        for (int s = 0; s < superframes; s++)
        {
            feed.Fill(s);
            encoder.EncodeSuperframe(s, output, outFile.DataOffset + s * cfg.SuperframeBytes);
            verifier.Check(s);
            if (progress is not null && s % 64 == 0) progress((double)s / superframes);
        }
        (double snr, double worst) = verifier.Finish(limits);
        progress?.Invoke(1.0);
        return (snr, worst);
    }

    // The file with `superframes` empty superframes: fmt as it was, fact and smpl with the new sample counts and loop, no other chunks.
    private static byte[] BuildFile(At9File stock, int superframes, int samples, int delay, (int Start, int End)? loop)
    {
        int dataLength = superframes * stock.Config.SuperframeBytes;
        byte[] src = stock.Bytes;
        var parts = new List<(string Id, byte[] Body)>();
        foreach ((string id, int offset, int length) in stock.Chunks)
        {
            if (id == "fmt ") parts.Add((id, src.AsSpan(offset, length).ToArray()));
            else if (id == "fact")
            {
                byte[] body = src.AsSpan(offset, length).ToArray();
                WriteU32(body, 0, (uint)samples);
                if (body.Length >= 12) { WriteU32(body, 4, (uint)delay); WriteU32(body, 8, (uint)delay); }
                parts.Add((id, body));
            }
            else if (id == "smpl" && length >= 60)
            {
                byte[] body = src.AsSpan(offset, length).ToArray();
                WriteU32(body, 44, (uint)(loop is { } l ? l.Start : 0));
                WriteU32(body, 48, (uint)(loop is { } m ? m.End : samples - 1));
                parts.Add((id, body));
            }
            else if (id == "data") parts.Add((id, new byte[dataLength]));
        }
        if (!parts.Exists(p => p.Id == "fact"))
        {
            var body = new byte[12];
            WriteU32(body, 0, (uint)samples); WriteU32(body, 4, (uint)delay); WriteU32(body, 8, (uint)delay);
            parts.Insert(1, ("fact", body));
        }
        int size = 12;
        foreach ((_, byte[] body) in parts) size += 8 + body.Length + (body.Length & 1);
        var result = new byte[size];
        "RIFF"u8.CopyTo(result);
        WriteU32(result, 4, (uint)(size - 8));
        "WAVE"u8.CopyTo(result.AsSpan(8));
        int p = 12;
        foreach ((string id, byte[] body) in parts)
        {
            for (int i = 0; i < 4; i++) result[p + i] = (byte)id[i];
            WriteU32(result, p + 4, (uint)body.Length);
            body.CopyTo(result, p + 8);
            p += 8 + body.Length + (body.Length & 1);
        }
        return result;
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16);
        b[o + 3] = (byte)(v >> 24);
    }
}
