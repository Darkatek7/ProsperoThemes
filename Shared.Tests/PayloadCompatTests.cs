using System;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class PayloadCompatTests
{
    private static byte[] Dds(int width, int height, uint dxgi)
    {
        var d = new byte[148];
        "DDS "u8.CopyTo(d);
        BitConverter.GetBytes(height).CopyTo(d, 12);
        BitConverter.GetBytes(width).CopyTo(d, 16);
        "DX10"u8.CopyTo(d.AsSpan(84));
        BitConverter.GetBytes(dxgi).CopyTo(d, 128);
        return d;
    }

    private static byte[] Png(int width, int height)
    {
        var d = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(d, 0);
        "IHDR"u8.CopyTo(d.AsSpan(12));
        d[16] = (byte)(width >> 24); d[17] = (byte)(width >> 16); d[18] = (byte)(width >> 8); d[19] = (byte)width;
        d[20] = (byte)(height >> 24); d[21] = (byte)(height >> 16); d[22] = (byte)(height >> 8); d[23] = (byte)height;
        return d;
    }

    [Fact]
    public void Reads_the_content_size_from_every_zstd_header_layout()
    {
        // Single segment with a 4-byte size (zstd's default for one-shot compression).
        byte[] four = [0x28, 0xB5, 0x2F, 0xFD, 0xA0, 0x94, 0x90, 0x7E, 0x00];
        Assert.Equal(8_294_548, PayloadCompat.Read(four).ContentSize);
        // A 2-byte size counts from 256; a window descriptor sits before it when the frame is not single-segment.
        byte[] two = [0x28, 0xB5, 0x2F, 0xFD, 0x40, 0x58, 0x10, 0x00];
        Assert.Equal(16 + 256, PayloadCompat.Read(two).ContentSize);
        // No size recorded: unknown, and so never a mismatch.
        byte[] none = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x58];
        Assert.Equal(-1, PayloadCompat.Read(none).ContentSize);
        Assert.Null(PayloadCompat.Mismatch(none, four));
    }

    [Fact]
    public void Same_kind_and_size_fits_anything_else_is_named()
    {
        Assert.Null(PayloadCompat.Mismatch(Dds(3840, 2160, 98), Dds(3840, 2160, 98)));
        Assert.Contains("dds 1920x1080 dxgi98", PayloadCompat.Mismatch(Dds(3840, 2160, 98), Dds(1920, 1080, 98)));
        Assert.NotNull(PayloadCompat.Mismatch(Dds(3840, 2160, 98), Dds(3840, 2160, 71)));
        // A title's own pic0 is scaled by the shell: only the pixel format has to match.
        Assert.Null(PayloadCompat.Mismatch(Dds(3840, 2160, 98), Dds(1920, 1080, 98), sameSize: false));
        Assert.NotNull(PayloadCompat.Mismatch(Dds(3840, 2160, 98), Dds(1920, 1080, 71), sameSize: false));
        Assert.Null(PayloadCompat.Mismatch(Png(512, 512), Png(512, 512)));
        Assert.Contains("png 256x256", PayloadCompat.Mismatch(Png(512, 512), Png(256, 256)));
        Assert.NotNull(PayloadCompat.Mismatch(Png(512, 512), Dds(512, 512, 98)));
    }

    [Fact]
    public void Files_it_cannot_read_pass()
    {
        Assert.Null(PayloadCompat.Mismatch("RIFF...."u8, "RIFF...."u8));
        Assert.Null(PayloadCompat.Mismatch(Png(512, 512), "not a known format"u8));
        Assert.Equal("unknown", PayloadCompat.Read([]).ToString());
    }
}
