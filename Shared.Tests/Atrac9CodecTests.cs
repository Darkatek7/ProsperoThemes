using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The ATRAC9 codec behind the home music volume. It is a port of <c>web/atrac9/vendor/atrac9.js</c>, so the expected
/// bytes come from that file: <c>web/atrac9/tools/atrac9-reference.mjs fixtures</c> wrote <c>fixtures/atrac9/reference.json</c>
/// (SHA-256 of what the JavaScript makes of the test signals below) and the two files next to it. The C# decoder, encoder
/// and volume change have to reproduce those byte for byte. The files are our own output from synthetic audio (no Sony data).
/// The tests on the console's own music need files that are not in the repository and are skipped without them: set
/// <c>PROSPERO_AT9_REF</c> to a folder with <c>stock-bgm_home.at9</c> and <c>kanto-bgm_home.at9</c>, or keep them in <c>_ref/</c>
/// above the solution.
/// </summary>
public class Atrac9CodecTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "atrac9", name);

    private static byte[] Read(string name) => File.ReadAllBytes(Fixture(name));

    private static JsonElement Reference() => JsonDocument.Parse(File.ReadAllText(Fixture("reference.json"))).RootElement;

    private static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string HashOf(float[] samples) => Sha256Hex(MemoryMarshal.AsBytes(samples.AsSpan()));

    /// <summary>The test signal of <c>atrac9-synth.mjs</c>: triangle waves and noise in 16-bit steps with a loudness pattern, by integer arithmetic only.</summary>
    public static float[] Synth(int n, int ch)
    {
        var output = new float[n];
        uint s = (uint)(12345 + 7919 * ch);
        int[][] periods = { new[] { 191, 487 }, new[] { 233, 613 }, new[] { 149, 541 }, new[] { 307, 421 }, new[] { 163, 701 }, new[] { 251, 463 } };
        int[] amps = { 0, 4000, 9000, 2500, 12000, 6000, 0, 20000, 1500, 15000 };
        int[] p = periods[ch % 6];
        static int Tri(int i, int period) => Math.Abs(i % period * 4 - 2 * period) - period;
        for (int i = 0; i < n; i++)
        {
            s = unchecked(s * 1103515245u + 12345u) & 0x7fffffff;
            int noise = (int)(s >> 8) % 2001 - 1000;
            int k = i / 12000 % amps.Length, gain = amps[k];
            int v = Tri(i, p[0]) * gain / 191 + Tri(i, p[1]) * (gain >> 1) / 487 + (gain != 0 ? noise * (1 + (k & 3)) : 0);
            if (k == 7) v *= 2;
            v = Math.Clamp(v, -32768, 32767);
            output[i] = v / 32768f;
        }
        return output;
    }

    private static double Rms(float[][] channels, params int[] which)
    {
        double sum = 0;
        long n = 0;
        foreach (int c in which)
        {
            foreach (float v in channels[c]) sum += (double)v * v;
            n += channels[c].Length;
        }
        return Math.Sqrt(sum / n);
    }

    // ---- against the JavaScript ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("intro0", 0)]
    [InlineData("intro30000", 30000)]
    public void Encode_with_the_default_profile_gives_the_bytes_of_the_javascript(string key, int intro)
    {
        JsonElement expected = Reference().GetProperty("encode").GetProperty(key);
        int n = Reference().GetProperty("samples").GetInt32();

        Atrac9Encoded result = Atrac9Codec.Encode(Read("synth-template.at9"), Synth(n, 0), Synth(n, 1), intro);

        Assert.Equal(expected.GetProperty("length").GetInt32(), result.Bytes.Length);
        Assert.Equal(expected.GetProperty("sha256").GetString(), Sha256Hex(result.Bytes));
        Assert.Equal(expected.GetProperty("samples").GetInt32(), result.Samples);
        Assert.Equal(expected.GetProperty("loop").GetProperty("start").GetInt32(), result.LoopStart);
        Assert.Equal(expected.GetProperty("loop").GetProperty("end").GetInt32(), result.LoopEnd);
    }

    [Fact]
    public void Encode_with_the_profile_of_a_file_gives_the_bytes_of_the_javascript()
    {
        JsonElement expected = Reference().GetProperty("encode").GetProperty("withFileProfile");
        int n = Reference().GetProperty("samples").GetInt32();

        Atrac9Encoded result = Atrac9Codec.Encode(Read("synth-6ch.at9"), Synth(n, 0), Synth(n, 1));

        Assert.Equal(expected.GetProperty("length").GetInt32(), result.Bytes.Length);
        Assert.Equal(expected.GetProperty("sha256").GetString(), Sha256Hex(result.Bytes));
    }

    [Fact]
    public void The_six_channel_fixture_is_what_the_javascript_wrote()
    {
        JsonElement expected = Reference().GetProperty("sixChannels");
        byte[] file = Read("synth-6ch.at9");
        Assert.Equal(expected.GetProperty("length").GetInt32(), file.Length);
        Assert.Equal(expected.GetProperty("sha256").GetString(), Sha256Hex(file));
    }

    [Fact]
    public void Decode_gives_the_samples_of_the_javascript()
    {
        JsonElement expected = Reference().GetProperty("decode").GetProperty("channels");

        float[][] channels = Atrac9Codec.Decode(Read("synth-6ch.at9"));

        Assert.Equal(expected.GetArrayLength(), channels.Length);
        for (int c = 0; c < channels.Length; c++)
        {
            Assert.Equal(expected[c].GetProperty("length").GetInt32(), channels[c].Length);
            Assert.Equal(expected[c].GetProperty("sha256").GetString(), HashOf(channels[c]));
        }
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("0.1", 0.1)]
    public void ScaleVolume_gives_the_bytes_of_the_javascript(string key, double factor)
    {
        JsonElement expected = Reference().GetProperty("rescale").GetProperty(key);

        byte[] scaled = Atrac9Codec.ScaleVolume(Read("synth-6ch.at9"), factor);

        Assert.Equal(expected.GetProperty("length").GetInt32(), scaled.Length);
        Assert.Equal(expected.GetProperty("sha256").GetString(), Sha256Hex(scaled));
    }

    [Fact]
    public void ScaleVolume_of_a_stereo_theme_keeps_its_silent_channels_silent_like_the_javascript()
    {
        JsonElement expected = Reference().GetProperty("rescaleStereo");
        int n = Reference().GetProperty("samples").GetInt32();
        byte[] stereo = Atrac9Codec.Encode(Read("synth-template.at9"), Synth(n, 0), Synth(n, 1), 0).Bytes;

        byte[] scaled = Atrac9Codec.ScaleVolume(stereo, 0.1);

        Assert.Equal(expected.GetProperty("sha256").GetString(), Sha256Hex(scaled));
        float[][] channels = Atrac9Codec.Decode(scaled);
        for (int c = 2; c < channels.Length; c++) Assert.All(channels[c], v => Assert.True(Math.Abs(v) < 1e-6));
    }

    // ---- the volume change ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ScaleVolume_gives_the_same_array_back_when_there_is_nothing_to_lower(double factor)
    {
        byte[] file = Read("synth-6ch.at9");
        Assert.Same(file, Atrac9Codec.ScaleVolume(file, factor));
    }

    [Fact]
    public void ScaleVolume_keeps_the_layout_and_halves_the_level()
    {
        byte[] file = Read("synth-6ch.at9");
        var steps = new System.Collections.Generic.List<double>();

        byte[] half = Atrac9Codec.ScaleVolume(file, 0.5, steps.Add);

        // Same size and everything but the audio data as it was: the console reads a fixed layout.
        Assert.Equal(file.Length, half.Length);
        At9Layout(file, out int dataAt, out int dataLength);
        Assert.Equal(file.AsSpan(0, dataAt).ToArray(), half.AsSpan(0, dataAt).ToArray());
        Assert.Equal(file.AsSpan(dataAt + dataLength).ToArray(), half.AsSpan(dataAt + dataLength).ToArray());
        Assert.NotEqual(file.AsSpan(dataAt, dataLength).ToArray(), half.AsSpan(dataAt, dataLength).ToArray());
        Assert.Equal(1.0, steps[^1]);
        Assert.Equal(steps.OrderBy(x => x).ToArray(), steps.ToArray());

        // Every channel but the LFE is half as loud (coding noise adds a little); the LFE stays silent.
        float[][] before = Atrac9Codec.Decode(file), after = Atrac9Codec.Decode(half);
        foreach (int c in new[] { 0, 1, 2, 4, 5 })
        {
            double ratio = Rms(after, c) / Rms(before, c);
            Assert.InRange(ratio, 0.46, 0.54);
        }
        Assert.True(after[3].All(v => Math.Abs(v) < 1e-6));
        Assert.Equal(before[0].Length, after[0].Length);
    }

    [Fact]
    public void ScaleVolume_to_zero_gives_silence_and_below_zero_is_zero()
    {
        byte[] file = Read("synth-6ch.at9");
        byte[] zero = Atrac9Codec.ScaleVolume(file, 0.0);
        Assert.Equal(zero, Atrac9Codec.ScaleVolume(file, -3.0));
        float[][] channels = Atrac9Codec.Decode(zero);
        Assert.All(channels, c => Assert.True(c.All(v => Math.Abs(v) < 1e-5)));
    }

    [Fact]
    public void ScaleVolume_can_be_applied_again_and_again_to_its_own_output()
    {
        byte[] once = Atrac9Codec.ScaleVolume(Read("synth-6ch.at9"), 0.5);
        byte[] twice = Atrac9Codec.ScaleVolume(once, 0.5);
        Assert.Equal(once.Length, twice.Length);
        Assert.InRange(Rms(Atrac9Codec.Decode(twice), 0, 1) / Rms(Atrac9Codec.Decode(once), 0, 1), 0.44, 0.56);
    }

    [Fact]
    public void ScaleVolume_does_not_hold_the_track_as_audio()
    {
        byte[] file = Read("synth-6ch.at9");
        Atrac9Codec.ScaleVolume(file, 0.5);   // tables and so on are built once

        long before = GC.GetAllocatedBytesForCurrentThread();
        Atrac9Codec.ScaleVolume(file, 0.5);
        long used = GC.GetAllocatedBytesForCurrentThread() - before;

        // The output (one more file) and the work buffers, a fixed 0.9 MB or so; 6 channels of 120000 samples as audio would be 2.9 MB.
        Assert.True(used < 1_500_000, $"allocated {used} bytes for a {file.Length}-byte file");
    }

    [Fact]
    public void Files_that_are_not_ATRAC9_throw()
    {
        Assert.Throws<ThemePackException>(() => Atrac9Codec.ScaleVolume(new byte[100], 0.5));
        Assert.Throws<ThemePackException>(() => Atrac9Codec.Decode(Read("reference.json")));
        byte[] truncated = Read("synth-6ch.at9").AsSpan(0, 5000).ToArray();
        Assert.Throws<ThemePackException>(() => Atrac9Codec.ScaleVolume(truncated, 0.5));
    }

    [Fact]
    public void A_file_without_audio_is_returned_as_it_is()
    {
        byte[] empty = Read("synth-template.at9");
        Assert.Same(empty, Atrac9Codec.ScaleVolume(empty, 0.5));
    }

    private static void At9Layout(byte[] file, out int dataAt, out int dataLength)
    {
        dataAt = dataLength = 0;
        for (int p = 12; p + 8 <= file.Length;)
        {
            int size = BitConverter.ToInt32(file, p + 4);
            if (file[p] == 'd' && file[p + 1] == 'a' && file[p + 2] == 't' && file[p + 3] == 'a')
            {
                dataAt = p + 8;
                dataLength = size;
                return;
            }
            p += 8 + size + (size & 1);
        }
        throw new InvalidOperationException("no data chunk");
    }

    // ---- the console's own files (not in the repository) ---------------------------------------------------------

    private static byte[]? ConsoleFile(string name)
    {
        string? dir = Environment.GetEnvironmentVariable("PROSPERO_AT9_REF");
        if (string.IsNullOrEmpty(dir))
        {
            for (string? up = AppContext.BaseDirectory; up is not null; up = Path.GetDirectoryName(up))
            {
                if (Directory.Exists(Path.Combine(up, "_ref"))) { dir = Path.Combine(up, "_ref"); break; }
            }
        }
        string path = Path.Combine(dir ?? "", name);
        return dir is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [Fact]
    public void ScaleVolume_of_a_theme_file_keeps_its_layout_and_lowers_all_channels()
    {
        // A theme's music from another encoder: gradient mode 2, joint stereo, sound in the centre and surround channels.
        const double factor = 0.5;
        byte[]? file = ConsoleFile("kanto-bgm_home.at9");
        if (file is null) return;   // not available here

        byte[] scaled = Atrac9Codec.ScaleVolume(file, factor);

        Assert.Equal(file.Length, scaled.Length);
        At9Layout(file, out int dataAt, out _);
        Assert.Equal(file.AsSpan(0, dataAt).ToArray(), scaled.AsSpan(0, dataAt).ToArray());
        float[][] before = Atrac9Codec.Decode(file), after = Atrac9Codec.Decode(scaled);
        foreach (int c in new[] { 0, 1, 2, 4, 5 })
        {
            // The file's first and last frames hold the codec's start-up and padding; the music is in between.
            Assert.InRange(Rms(after, c) / Rms(before, c), factor * 0.9, factor * 1.1);
        }
    }

    [Fact]
    public void ScaleVolume_of_the_system_music_keeps_its_layout()
    {
        // 4 minutes of 6-channel music: about 40 s in a debug build, so only when asked for with PROSPERO_AT9_SLOW=1.
        byte[]? file = Environment.GetEnvironmentVariable("PROSPERO_AT9_SLOW") == "1" ? ConsoleFile("stock-bgm_home.at9") : null;
        if (file is null) return;   // not available here

        byte[] scaled = Atrac9Codec.ScaleVolume(file, 0.5);

        Assert.Equal(file.Length, scaled.Length);
        At9Layout(file, out int dataAt, out _);
        Assert.Equal(file.AsSpan(0, dataAt).ToArray(), scaled.AsSpan(0, dataAt).ToArray());
    }
}
