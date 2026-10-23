using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The PS-ADPCM codec behind the UI sound volume. The expected bytes come from <c>ThemePackCreator/vag.py</c>
/// (<c>encode</c> and <c>decode</c>) run once on the integer test signals below; the C# encoder has to reproduce them
/// byte for byte. Short signals are kept as hex, long ones as SHA-256 of the frames.
/// </summary>
public class VagCodecTests
{
    // ---- test signals (the same integer arithmetic as the generator that produced the expected bytes) --------------

    /// <summary>"tone": a triangle wave with noise. "quiet": noise of +-3, full of rounding ties. "burst": silence, clipping, a decay.</summary>
    public static short[] Signal(string kind, int n)
    {
        var noise = new int[n];
        long s = 12345;
        for (int i = 0; i < n; i++)
        {
            s = (s * 1103515245 + 12345) & 0x7FFFFFFF;
            noise[i] = (int)(s >> 8);
        }
        var samples = new short[n];
        for (int i = 0; i < n; i++)
        {
            int v;
            if (kind == "tone")
            {
                int x = i * 400 % 24000;
                v = (x < 12000 ? x : 24000 - x) * 2 - 12000 + noise[i] % 201 - 100;
            }
            else if (kind == "quiet")
            {
                v = noise[i] % 7 - 3;
            }
            else
            {
                int a = i < n / 4 ? 0
                    : i < n / 2 ? (i % 5 < 2 ? 32767 : -32768)
                    : 30000 * (n - i) / (n - n / 2) * (i * 37 % 64 - 32) >> 5;
                v = Math.Clamp(a + noise[i] % 5 - 2, -32768, 32767);
            }
            samples[i] = (short)v;
        }
        return samples;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static double Rms(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (short v in samples) sum += (double)v * v;
        return samples.Length == 0 ? 0 : Math.Sqrt(sum / samples.Length);
    }

    /// <summary>A mono VAG file as the pack writers make it: header, sound frames (the last flagged 1), an end-marker frame.</summary>
    public static byte[] MakeVag(ReadOnlySpan<short> samples, int rate = 48000, string name = "tone")
    {
        byte[] sound = VagCodec.Encode(samples);
        if (sound.Length == 0) sound = new byte[VagCodec.FrameSize];
        sound[sound.Length - VagCodec.FrameSize + 1] = 1;
        var vag = new byte[0x30 + sound.Length + VagCodec.FrameSize];
        "VAGp"u8.CopyTo(vag);
        BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(4), 0x20);
        BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(0x0C), sound.Length + VagCodec.FrameSize);
        BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(0x10), rate);
        vag[0x1E] = 1;
        Encoding.ASCII.GetBytes(name).CopyTo(vag, 0x20);
        sound.CopyTo(vag, 0x30);
        vag[0x30 + sound.Length + 1] = 7;
        return vag;
    }

    /// <summary>The samples of a mono VAG file (what its frames decode to before the end marker).</summary>
    public static short[] SamplesOf(byte[] vag) => VagCodec.Decode(vag.AsSpan(0x30));

    // ---- byte-identical to vag.py -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("tone", 336, "31004aeefffee0f0f00000101001122145004b8411f5130201e2f2e000eeffde4500dede7db1e0feef1f2e1e2fe2f4044500222314853f2222202f2f1f1e0dc14500ffedeefc7cc1fedf0fffd1f310101400354555555606f0ffffeeeedeedcd1400cdccbcacbcaa0a1120111232223345003f2f1131314161815f412f21102f4500e0f10feee0cedfbf7feec0efd12f45001d0f0210031222144391403120f24400f2101f1f0ee000fefffe5eff0eff4500e0f0001f3f1f1212323124923004")]
    [InlineData("quiet", 336, "0c00d1ff111e122f00323e1ed031d1030c00310e220d113fff02e32deee3233e0c00df03ef3dddf3f3e023031f0e33210c00d1ee02111e10d20d132df0d2e0010c001100d3ffe3d2ef1d1f0e000e20e20c003d1d13fefe2e1ff2300d320ee3330c00f231e33df020d1e1e3331d0d23ff0c00132323e3313303de3003131d2d210c0012002fdf12d3131f00123e0021e10c00f1dde0e01321222f2eede0130ed00c0003112ed023d203fd01ff00f2022e0c00dfe1efede2dde3df312de1221efd")]
    [InlineData("burst", 336, "0c000e1e1ee10221fffeeee1e21012f00c00f11200ff1e1f1e1eff21011e21020c00f222111ee1feef12e211e10f001f00007887887788788788778878878877000088788788778878878877887887880000778878878877887887887788788700003b5d9fb1e3062a4c6eb0d2f41a3c10007bbbc6b6b76c7b6bccb6c65c6c5c010080b3e617496c8fa2d407295c7ea00100d3f5193b5ea0c2e3052b4d5fc1e20200f7285a7d90c3e5173b5dafc1e3040300385b7ea0d3f41a2d3ed0f101010f")]
    public void The_encoder_gives_the_bytes_vag_py_gives(string kind, int count, string expectedHex)
    {
        byte[] frames = VagCodec.Encode(Signal(kind, count));
        Assert.Equal(expectedHex, Convert.ToHexString(frames).ToLowerInvariant());
    }

    [Theory]
    [InlineData("tone", 3000, "b5c29cf6d660950e0ea115b43c5f7cb89b1553db1da013f53a8a3a8d461cc4b3", "28fdaab934519f27fc0c9bd88e6ef9cceb3814e6128916da95280a1cda975e0a")]
    [InlineData("quiet", 3000, "06aff3a25a8373ccfec2f4ed37f0e38cb3a930634d49b6107a0d4519d4a426e0", "b754b293647ec3f262110887fb5bda13cbd991c4642b17c647a4922331d92ef3")]
    [InlineData("burst", 3000, "e7512cfb09ae5ceebacca10fe985dc0db8c741eb1128d3f0ffbc6a107b84594e", "c224b61f40c3945b8fdc648e39880f65f32447b423727fd024ea6eaeeba97662")]
    public void A_long_signal_encodes_and_decodes_like_vag_py(string kind, int count, string frameHash, string decodedHash)
    {
        byte[] frames = VagCodec.Encode(Signal(kind, count));
        Assert.Equal((count + 27) / 28 * 16, frames.Length);
        Assert.Equal(frameHash, Sha256Hex(frames));

        short[] decoded = VagCodec.Decode(frames);
        Assert.Equal(frames.Length / 16 * 28, decoded.Length);
        var littleEndian = new byte[decoded.Length * 2];
        for (int i = 0; i < decoded.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(littleEndian.AsSpan(i * 2), decoded[i]);
        Assert.Equal(decodedHash, Sha256Hex(littleEndian));
    }

    [Fact]
    public void The_encoder_is_repeatable_and_the_span_overload_matches()
    {
        short[] tone = Signal("tone", 500);
        byte[] first = VagCodec.Encode(tone);
        Assert.Equal(first, VagCodec.Encode(tone));
        var into = new byte[first.Length];
        VagCodec.Encode(tone, into);
        Assert.Equal(first, into);
        Assert.Throws<ArgumentException>(() => VagCodec.Encode(tone, new byte[first.Length - 1]));
    }

    // ---- round trip -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("tone", 0.02)]
    [InlineData("burst", 0.10)]      // full-scale clipping and a sawtooth: the hardest case for 4-bit samples
    public void A_round_trip_stays_close_to_the_input(string kind, double allowed)
    {
        short[] input = Signal(kind, 3000);
        short[] output = VagCodec.Decode(VagCodec.Encode(input));
        Assert.Equal(3024, output.Length);                                // padded to whole frames
        var error = new short[input.Length];
        for (int i = 0; i < input.Length; i++) error[i] = (short)Math.Clamp(output[i] - input[i], short.MinValue, short.MaxValue);
        Assert.True(Rms(error) < allowed * Rms(input), $"error {Rms(error):F1} against signal {Rms(input):F1}");
        for (int i = input.Length; i < output.Length; i++)
            Assert.InRange(output[i], -300, 300);                         // the zero padding stays near silence
    }

    [Fact]
    public void Silence_encodes_to_empty_frames_and_nothing_to_nothing()
    {
        Assert.All(VagCodec.Encode(new short[56]), b => Assert.Equal(0, b));
        Assert.Empty(VagCodec.Encode(ReadOnlySpan<short>.Empty));
        Assert.Empty(VagCodec.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Decoding_stops_at_an_end_marker_and_ignores_a_partial_frame()
    {
        byte[] frames = VagCodec.Encode(Signal("tone", 84));              // three frames
        var withEnd = new byte[frames.Length + 16 + 5];
        frames.CopyTo(withEnd, 0);
        withEnd[frames.Length + 1] = 7;
        Assert.Equal(VagCodec.Decode(frames), VagCodec.Decode(withEnd));
        Assert.Equal(84, VagCodec.Decode(withEnd).Length);
        Assert.Equal(84, VagCodec.Decode(frames).Length);

        Assert.Equal(56, VagCodec.Decode(frames.AsSpan(0, 2 * 16 + 9)).Length);
        Assert.Throws<ArgumentException>(() => VagCodec.Decode(frames, new short[10]));
    }

    // ---- volume -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Full_volume_returns_the_file_unchanged()
    {
        byte[] vag = MakeVag(Signal("tone", 1000));
        Assert.Equal(vag, VagCodec.ScaleVolume(vag, 1.0));
        Assert.Equal(vag, VagCodec.ScaleVolume(vag, 3.0));
        Assert.Equal(vag, VagCodec.ScaleVolume(vag, double.NaN));
        Assert.NotSame(vag, VagCodec.ScaleVolume(vag, 1.0));              // a copy: the caller's bytes are never aliased
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(0.8)]
    public void A_lower_volume_scales_the_amplitude_and_keeps_the_file_layout(double factor)
    {
        byte[] vag = MakeVag(Signal("tone", 1000));
        byte[] quieter = VagCodec.ScaleVolume(vag, factor);

        Assert.Equal(vag.Length, quieter.Length);
        Assert.Equal(vag.AsSpan(0, 0x30).ToArray(), quieter.AsSpan(0, 0x30).ToArray());              // header
        for (int at = 0x30; at < vag.Length; at += 16)
            Assert.Equal(vag[at + 1], quieter[at + 1]);                                              // flags, the end marker
        Assert.Equal(vag.AsSpan(vag.Length - 16).ToArray(), quieter.AsSpan(quieter.Length - 16).ToArray());

        double ratio = Rms(SamplesOf(quieter)) / Rms(SamplesOf(vag));
        Assert.InRange(ratio, factor * 0.97, factor * 1.03);
        (int frames, int rate) = RcoVag.Check(quieter);
        Assert.Equal((36, 48000), (frames, rate));                                                   // still a valid mono VAG
    }

    [Fact]
    public void Half_volume_halves_the_rms_of_a_loud_burst_without_overflowing()
    {
        byte[] vag = MakeVag(Signal("burst", 3000));
        double ratio = Rms(SamplesOf(VagCodec.ScaleVolume(vag, 0.5))) / Rms(SamplesOf(vag));
        Assert.InRange(ratio, 0.48, 0.52);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void Volume_zero_is_silence_of_the_same_length(double factor)
    {
        byte[] vag = MakeVag(Signal("tone", 1000));
        byte[] silent = VagCodec.ScaleVolume(vag, factor);
        Assert.Equal(vag.Length, silent.Length);
        Assert.All(SamplesOf(silent), v => Assert.Equal(0, v));
        Assert.Equal(1, silent[silent.Length - 16 - 16 + 1]);                                        // the last sound frame still ends the sound
        Assert.Equal(7, silent[silent.Length - 16 + 1]);
    }

    [Fact]
    public void Scaling_something_that_is_not_a_standard_mono_vag_is_refused()
    {
        byte[] vag = MakeVag(Signal("tone", 100));
        Assert.Throws<ThemePackException>(() => VagCodec.ScaleVolume(Encoding.ASCII.GetBytes("RIFF not a vag file at all......................."), 0.5));
        vag[0x30] = 0x50;                                                                            // predictor 5: HEVAG only
        Assert.Throws<ThemePackException>(() => VagCodec.ScaleVolume(vag, 0.5));
        Assert.Equal(vag, VagCodec.ScaleVolume(vag, 1.0));                                           // full volume looks at nothing
    }
}
