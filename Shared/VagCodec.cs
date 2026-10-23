using System;

namespace ProsperoThemes.Shared;

/// <summary>
/// Standard PS-ADPCM (VAG with predictors 0 to 4): decoding, encoding and changing the volume of a mono VAG file.
/// The encoder is a port of <c>ThemePackCreator/vag.py</c> and gives the same bytes for the same samples: the best
/// filter and shift of each 28-sample frame is searched while tracking the decoder's own state, so rounding never
/// drifts, and no flags are set. Ties in the quantiser round to the even value, as Python's <c>round</c> does.
/// </summary>
/// <remarks>
/// Written for the console's NativeAOT app: plain loops over spans and one scratch buffer, no LINQ. The stock clips
/// are HEVAG and use more predictors than these five, so this is for the mono VAG files themes carry, not for them.
/// </remarks>
public static class VagCodec
{
    /// <summary>Bytes in one frame: a shift and filter byte, a flag byte and 14 bytes of 4-bit samples.</summary>
    public const int FrameSize = 16;

    /// <summary>Samples in one frame.</summary>
    public const int FrameSamples = 28;

    private const int HeaderSize = 0x30;

    // Standard VAG predictors, in 1/64.
    private static readonly int[] Coef1 = { 0, 60, 115, 98, 122 };
    private static readonly int[] Coef2 = { 0, 0, -52, -55, -60 };

    /// <summary>
    /// Decodes PS-ADPCM frames to samples, up to an end-marker frame (flag 7) or the last whole frame. A predictor above
    /// 4 is read as 4, as <c>vag.py</c> does.
    /// </summary>
    public static short[] Decode(ReadOnlySpan<byte> frames)
    {
        int count = SoundFrames(frames);
        var samples = new short[count * FrameSamples];
        Decode(frames, samples);
        return samples;
    }

    /// <summary>Like <see cref="Decode(ReadOnlySpan{byte})"/> into a buffer that holds every sample; returns how many were written.</summary>
    public static int Decode(ReadOnlySpan<byte> frames, Span<short> samples)
    {
        int count = SoundFrames(frames);
        if (samples.Length < count * FrameSamples)
            throw new ArgumentException("the buffer is too small for the decoded samples", nameof(samples));
        int s1 = 0, s2 = 0, at = 0;
        for (int f = 0; f < count; f++)
        {
            int o = f * FrameSize;
            int shift = frames[o] & 15, filter = Math.Min(frames[o] >> 4, 4);
            int c1 = Coef1[filter], c2 = Coef2[filter];
            for (int b = o + 2; b < o + FrameSize; b++)
            {
                for (int half = 0; half < 2; half++)
                {
                    int n = half == 0 ? frames[b] & 15 : frames[b] >> 4;
                    if (n >= 8) n -= 16;
                    int v = Clamp16(((n << 12) >> shift) + ((s1 * c1 + s2 * c2 + 32) >> 6));
                    s2 = s1;
                    s1 = v;
                    samples[at++] = (short)v;
                }
            }
        }
        return at;
    }

    /// <summary>
    /// Encodes mono samples to PS-ADPCM frames, 28 samples each (the last frame is padded with zeros). No flags are set.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<short> samples)
    {
        var frames = new byte[(samples.Length + FrameSamples - 1) / FrameSamples * FrameSize];
        Encode(samples, frames);
        return frames;
    }

    /// <summary>Like <see cref="Encode(ReadOnlySpan{short})"/> into a buffer of exactly the frames' size.</summary>
    public static void Encode(ReadOnlySpan<short> samples, Span<byte> frames)
    {
        int count = (samples.Length + FrameSamples - 1) / FrameSamples;
        if (frames.Length != count * FrameSize)
            throw new ArgumentException("the buffer is not the size of the encoded frames", nameof(frames));

        Span<int> block = stackalloc int[FrameSamples];
        Span<byte> nibbles = stackalloc byte[FrameSamples], bestNibbles = stackalloc byte[FrameSamples];
        int s1 = 0, s2 = 0;
        for (int f = 0; f < count; f++)
        {
            int start = f * FrameSamples;
            for (int i = 0; i < FrameSamples; i++)
                block[i] = start + i < samples.Length ? samples[start + i] : 0;

            long bestErr = long.MaxValue;
            int bestFilter = 0, bestShift = 0, bestS1 = 0, bestS2 = 0;
            for (int filter = 0; filter < 5; filter++)
            {
                int c1 = Coef1[filter], c2 = Coef2[filter];
                for (int shift = 0; shift < 13; shift++)
                {
                    int a1 = s1, a2 = s2;
                    long err = 0;
                    int i = 0;
                    for (; i < FrameSamples; i++)
                    {
                        int x = block[i];
                        int p = (a1 * c1 + a2 * c2 + 32) >> 6;
                        int q = Quantize((x - p) * (1 << shift));
                        int v = Clamp16(((q << 12) >> shift) + p);
                        err += (long)(v - x) * (v - x);
                        a2 = a1;
                        a1 = v;
                        nibbles[i] = (byte)(q & 15);
                        if (err >= bestErr) break;
                    }
                    if (i == FrameSamples && err < bestErr)
                    {
                        bestErr = err;
                        bestFilter = filter;
                        bestShift = shift;
                        bestS1 = a1;
                        bestS2 = a2;
                        nibbles.CopyTo(bestNibbles);
                    }
                }
            }
            s1 = bestS1;
            s2 = bestS2;
            int at = f * FrameSize;
            frames[at] = (byte)((bestFilter << 4) | bestShift);
            frames[at + 1] = 0;
            for (int k = 0; k < FrameSamples; k += 2)
                frames[at + 2 + k / 2] = (byte)(bestNibbles[k] | (bestNibbles[k + 1] << 4));
        }
    }

    /// <summary>
    /// A mono VAG file at another volume: <paramref name="factor"/> is the amplitude (1 is the file as it is, 0.5 half
    /// the amplitude, 0 silence; outside 0 to 1 it is cut to it, and NaN is read as 1). The file is decoded, each sample
    /// scaled with rounding and clamping, and encoded again. The header, the frames' flags, the end-marker frame and
    /// everything after it stay as they are, so the result has the file's length. At 1 the file is returned unchanged
    /// (a copy of the same bytes, not re-encoded).
    /// </summary>
    /// <param name="vag">The VAG file as a pack stores it, with its 0x30-byte header.</param>
    /// <exception cref="ThemePackException">It is not a mono VAG file, or it uses a predictor standard VAG lacks.</exception>
    public static byte[] ScaleVolume(ReadOnlySpan<byte> vag, double factor)
    {
        byte[] result = vag.ToArray();
        if (double.IsNaN(factor) || factor >= 1.0) return result;
        if (factor < 0) factor = 0;

        (int count, _) = RcoVag.Check(vag);
        if (count == 0) return result;

        ReadOnlySpan<byte> frames = vag.Slice(HeaderSize, count * FrameSize);
        var samples = new short[count * FrameSamples];
        Decode(frames, samples);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Clamp16((int)Math.Round(samples[i] * factor, MidpointRounding.AwayFromZero));

        var encoded = new byte[count * FrameSize];
        Encode(samples, encoded);
        for (int f = 0; f < count; f++)
        {
            int at = f * FrameSize;
            encoded[at + 1] = frames[at + 1];   // flags (the end of the sound, loops) stay
            encoded.AsSpan(at, FrameSize).CopyTo(result.AsSpan(HeaderSize + at));
        }
        return result;
    }

    // Number of frames before an end-marker frame; a trailing partial frame is not one.
    private static int SoundFrames(ReadOnlySpan<byte> frames)
    {
        int count = 0;
        for (int o = 0; o + FrameSize <= frames.Length; o += FrameSize, count++)
        {
            if ((frames[o + 1] & 7) == 7) break;
        }
        return count;
    }

    // num / 4096 rounded to the nearest integer, ties to even (Python's round), then cut to a 4-bit sample.
    private static int Quantize(int num)
    {
        int floor = num >> 12, rest = num & 4095;
        int q = rest > 2048 ? floor + 1 : rest < 2048 ? floor : floor + (floor & 1);
        return q < -8 ? -8 : q > 7 ? 7 : q;
    }

    private static int Clamp16(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;
}
