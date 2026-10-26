using System;
using System.Buffers.Binary;
using System.IO;

namespace ProsperoThemes.Shared;

/// <summary>
/// A theme's BC7 background in the format of a PS4 title's entry background. PS4 titles installed on a PS5 keep
/// <c>/user/appmeta/&lt;TID&gt;/pic0.dds</c> as 1920x1080 DXT1 (legacy header, 128 + 1920*1080/2 = 1,036,928 bytes; measured on
/// FW 13.00 with CUSA01127, and the home screen reads the file live), not the 3840x2160 BC7 of a PS5 title. Writing the
/// theme's picture there means decoding it to the console file's size and encoding it as BC1 (<see cref="Bc1"/>) under the
/// console's own header, so flags, pitch and mip count stay exactly what the system made.
/// </summary>
public static class TitleBackground
{
    /// <summary>The size of a legacy DDS header, the part of the console's file the conversion copies.</summary>
    public const int HeaderBytes = 128;

    /// <summary>
    /// Whether <paramref name="console"/> (the first bytes of the console's file are enough) is a DXT1 texture this class
    /// can write: a legacy header with the FourCC <c>DXT1</c> (no DX10 part), at most one mip level, and a size that is a
    /// multiple of 4.
    /// </summary>
    public static bool IsDxt1(ReadOnlySpan<byte> console) => TryReadDxt1(console, out _, out _);

    private static bool TryReadDxt1(ReadOnlySpan<byte> console, out int width, out int height)
    {
        width = height = 0;
        if (console.Length < HeaderBytes || !Dds.HasMagic(console) || BinaryPrimitives.ReadUInt32LittleEndian(console[4..]) != 124) return false;
        uint h = BinaryPrimitives.ReadUInt32LittleEndian(console[12..]), w = BinaryPrimitives.ReadUInt32LittleEndian(console[16..]);
        uint mips = BinaryPrimitives.ReadUInt32LittleEndian(console[28..]);
        bool fourCc = (BinaryPrimitives.ReadUInt32LittleEndian(console[80..]) & 4) != 0 && console.Slice(84, 4).SequenceEqual("DXT1"u8);
        if (!fourCc || mips > 1 || w < 4 || h < 4 || w > 16384 || h > 16384 || w % 4 != 0 || h % 4 != 0) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>
    /// Whether a theme file should be converted for this console file: the console's is a DXT1 texture
    /// (<see cref="IsDxt1"/>) and the theme's a BC7 DDS. Anything else is judged by <see cref="PayloadCompat"/> as before.
    /// </summary>
    public static bool Applies(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> console) =>
        IsDxt1(console) && PayloadCompat.Read(payload) is { Kind: "dds", Format: "dxgi97" or "dxgi98" or "dxgi99" };

    /// <summary>The length a DXT1 file of this header's size has: the header and the blocks.</summary>
    public static long Dxt1Length(ReadOnlySpan<byte> console) =>
        TryReadDxt1(console, out int w, out int h) ? HeaderBytes + (long)(w / 4) * (h / 4) * Bc1.BlockBytes : -1;

    /// <summary>
    /// Turns <paramref name="bc7Dds"/> (a BC7 DDS, DX10 header) into the DXT1 file of <paramref name="console"/>: the
    /// console's first 128 bytes, then the picture as BC1 blocks at the console's size. A payload of twice the console's
    /// size (3840x2160 for 1920x1080) is halved while it is decoded, one of the same size is decoded as it is; another
    /// size is refused. The picture is decoded and encoded a row of blocks at a time, so only about 30 KB beyond the result
    /// is held.
    /// </summary>
    /// <param name="refusal">Why nothing was made, when the result is null.</param>
    /// <returns>The file, or null when the payload is not BC7, the console's header is not a DXT1 one this class writes, or the sizes do not fit.</returns>
    public static byte[]? ToDxt1(ReadOnlySpan<byte> bc7Dds, ReadOnlySpan<byte> console, out string refusal)
    {
        refusal = "";
        if (!TryReadDxt1(console, out int width, out int height))
        {
            refusal = "the console's file is not a plain DXT1 texture";
            return null;
        }
        DdsInfo info;
        try
        {
            info = Dds.Parse(bc7Dds);
        }
        catch (InvalidDataException ex)
        {
            refusal = $"the theme's file is not a readable DDS ({ex.Message})";
            return null;
        }
        if (!info.IsBc7)
        {
            refusal = "the theme's file is not BC7";
            return null;
        }
        int scale = info.Width == width && info.Height == height ? 1 : info.Width == 2 * width && info.Height == 2 * height ? 2 : 0;
        if (scale == 0)
        {
            refusal = $"the theme's picture is {info.Width}x{info.Height}, the console's is {width}x{height}";
            return null;
        }

        int sourceBlocksWide = (info.Width + 3) / 4, sourceBlocksHigh = (info.Height + 3) / 4;
        if (bc7Dds.Length - info.DataOffset < (long)sourceBlocksWide * sourceBlocksHigh * Bc7.BlockBytes)
        {
            refusal = "the theme's picture is cut off";
            return null;
        }
        ReadOnlySpan<byte> source = bc7Dds[info.DataOffset..];

        long length = Dxt1Length(console);
        var result = new byte[length];
        console[..HeaderBytes].CopyTo(result);

        // One row of DXT1 blocks (4 rows of pixels) is 4 rows of source blocks halved, or 1 row at full size.
        int sourceRowsPerBand = scale;   // block rows of the source
        int blocksWide = width / 4;
        var band = new uint[width * 4];
        for (int row = 0; row < height / 4; row++)
        {
            ReadOnlySpan<byte> blocks = source.Slice(row * sourceRowsPerBand * sourceBlocksWide * Bc7.BlockBytes, sourceRowsPerBand * sourceBlocksWide * Bc7.BlockBytes);
            if (scale == 2) Bc7.DecodeHalf(blocks, info.Width, 8, band, width, opaque: true);
            else Bc7.Decode(blocks, info.Width, 4, band, width, opaque: true);
            Bc1.Encode(band, width, 4, width, result.AsSpan(HeaderBytes + row * blocksWide * Bc1.BlockBytes, blocksWide * Bc1.BlockBytes));
        }
        return result;
    }
}
