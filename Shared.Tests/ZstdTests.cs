using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The decoder against frames from the reference library: <c>fixtures/zstd</c> was written with Python's
/// zstandard (levels 1 to 22, raw, RLE and compressed blocks, with and without a content size or checksum,
/// several frames with a skippable one between them), and <c>expected.json</c> holds each decoded hash.
/// </summary>
public class ZstdTests
{
    private static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);

    public static TheoryData<string> Vectors()
    {
        var data = new TheoryData<string>();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Fixture("zstd", "expected.json")));
        foreach (JsonProperty vector in doc.RootElement.EnumerateObject()) data.Add(vector.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Decodes_what_the_reference_library_wrote(string name)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Fixture("zstd", "expected.json")));
        JsonElement expected = doc.RootElement.GetProperty(name);

        byte[] decoded = Zstd.Decompress(File.ReadAllBytes(Fixture("zstd", name + ".zst")));

        Assert.Equal(expected.GetProperty("bytes").GetInt32(), decoded.Length);
        Assert.Equal(expected.GetProperty("sha256").GetString(), ThemePack.Hash(decoded));
    }

    [Theory]
    [InlineData("two-screen.p5theme")]
    [InlineData("web-creator-export.p5theme")]
    public void Unpacks_the_backgrounds_both_pack_writers_compress(string pack)
    {
        ThemePack theme = ThemePack.OpenFile(Fixture(pack));
        var compressed = theme.Manifest.Targets.Where(t => t.Path.EndsWith(".dds.zst", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(compressed);
        foreach (ThemePackTarget target in compressed)
        {
            byte[] payload = theme.Payload(target);
            byte[] dds = Zstd.Decompress(payload);
            Assert.Equal(PayloadCompat.Read(payload).ContentSize, dds.Length);
            Assert.Equal("dds", PayloadCompat.Read(dds).Kind);
        }
    }

    [Fact]
    public void A_flipped_byte_fails_the_checksum_or_the_stream_check()
    {
        byte[] frame = File.ReadAllBytes(Fixture("zstd", "text-l3.zst"));
        frame[frame.Length / 2] ^= 0x40;
        Assert.Throws<InvalidDataException>(() => Zstd.Decompress(frame));
    }

    [Fact]
    public void A_cut_off_frame_is_refused()
    {
        byte[] frame = File.ReadAllBytes(Fixture("zstd", "bc7-l19.zst"));
        Assert.Throws<InvalidDataException>(() => Zstd.Decompress(frame.AsSpan(0, frame.Length - 100)));
    }

    [Fact]
    public void Output_beyond_the_limit_is_refused()
    {
        byte[] frame = File.ReadAllBytes(Fixture("zstd", "rle.zst"));
        Assert.Throws<InvalidDataException>(() => Zstd.Decompress(frame, maxOutput: 1000));
    }

    [Fact]
    public void Other_data_is_not_a_frame()
    {
        Assert.Throws<InvalidDataException>(() => Zstd.Decompress("DDS not a frame"u8));
        Assert.Throws<InvalidDataException>(() => Zstd.Decompress(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void The_checksum_is_xxhash64()
    {
        // Reference values from the xxHash test suite, seed 0.
        Assert.Equal(0xEF46DB3751D8E999UL, Zstd.XxHash64(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x44BC2CF5AD770999UL, Zstd.XxHash64("abc"u8));
    }
}
