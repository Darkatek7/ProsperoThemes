using System;
using System.Buffers.Binary;

namespace ProsperoThemes.Shared;

/// <summary>What a texture, image or compressed file says about itself in its first bytes.</summary>
/// <param name="Kind">dds, zst or png; empty when the bytes are none of these.</param>
/// <param name="Width">Pixels, 0 when not known.</param>
/// <param name="Height">Pixels, 0 when not known.</param>
/// <param name="Format">The DDS pixel format (DXGI number or FourCC text), empty when not known.</param>
/// <param name="ContentSize">A Zstandard frame's decoded size, -1 when the frame does not record it.</param>
public readonly record struct PayloadFacts(string Kind, int Width, int Height, string Format, long ContentSize)
{
    public override string ToString() => Kind switch
    {
        "dds" => Format.Length > 0 ? $"dds {Width}x{Height} {Format}" : $"dds {Width}x{Height}",
        "png" => $"png {Width}x{Height}",
        "zst" => ContentSize >= 0 ? $"zst {ContentSize} bytes decoded" : "zst",
        _ => "unknown",
    };
}

/// <summary>
/// Whether a payload fits the file it replaces on this console. Another firmware can ship a file under the same
/// name in another size or format; the shell then shows garbage or nothing. So before anything is written the
/// payload is compared with the console's own file (its backup when there is one, as the live file may hold an
/// earlier theme): same DDS size and pixel format, same PNG size, same decoded size for a Zstandard frame (on
/// 13.00 every 3840x2160 BC7 background decodes to 8,294,548 bytes; the 1920x1080 error picture to 4,147,328).
/// </summary>
/// <remarks>
/// Only facts both sides carry are compared: a frame without a recorded size, or a file this class cannot read,
/// passes, and the magic-byte check in <see cref="PayloadFormat"/> still applies to the payload.
/// </remarks>
public static class PayloadCompat
{
    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> Zstd => [0x28, 0xB5, 0x2F, 0xFD];

    /// <summary>Reads the facts from the first bytes of a file (148 are enough for every kind).</summary>
    public static PayloadFacts Read(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 128 && data[..4].SequenceEqual("DDS "u8))
        {
            int height = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
            int width = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
            ReadOnlySpan<byte> fourCc = data.Slice(84, 4);
            string format = fourCc.SequenceEqual("DX10"u8) && data.Length >= 132
                ? "dxgi" + BinaryPrimitives.ReadUInt32LittleEndian(data[128..])
                : fourCc.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E) ? "" : System.Text.Encoding.ASCII.GetString(fourCc);
            return new PayloadFacts("dds", width, height, format, -1);
        }
        if (data.Length >= 24 && data[..8].SequenceEqual(Png) && data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return new PayloadFacts("png", BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]), "", -1);
        if (data.Length >= 5 && data[..4].SequenceEqual(Zstd))
            return new PayloadFacts("zst", 0, 0, "", ContentSize(data));
        return new PayloadFacts("", 0, 0, "", -1);
    }

    /// <summary>Why <paramref name="payload"/> does not fit where <paramref name="console"/> is, or null when it does.</summary>
    /// <param name="sameSize">
    /// False where the shell scales the picture: a title's own entry background (pic0.dds) comes in 1920x1080 as
    /// well as 3840x2160, and a 4K theme background shows fine on either. The pixel format still has to match.
    /// </param>
    public static string? Mismatch(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> console, bool sameSize = true)
    {
        PayloadFacts mine = Read(payload), theirs = Read(console);
        if (mine.Kind.Length == 0 || theirs.Kind.Length == 0) return null;
        if (mine.Kind != theirs.Kind) return $"the console's file is {theirs}, the theme's is {mine}";
        bool differs = mine.Kind switch
        {
            "dds" => sameSize && (mine.Width != theirs.Width || mine.Height != theirs.Height)
                || (mine.Format.Length > 0 && theirs.Format.Length > 0 && mine.Format != theirs.Format),
            "png" => sameSize && (mine.Width != theirs.Width || mine.Height != theirs.Height),
            _ => mine.ContentSize >= 0 && theirs.ContentSize >= 0 && mine.ContentSize != theirs.ContentSize,
        };
        return differs ? $"the console's file is {theirs}, the theme's is {mine}" : null;
    }

    /// <summary>The Frame_Content_Size of a Zstandard frame header (RFC 8878 3.1.1.1), or -1 when it is absent.</summary>
    private static long ContentSize(ReadOnlySpan<byte> frame)
    {
        byte descriptor = frame[4];
        int sizeFlag = descriptor >> 6;
        bool singleSegment = (descriptor & 0x20) != 0;
        int at = 5 + (singleSegment ? 0 : 1) + (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        int length = sizeFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        if (length == 0 || frame.Length < at + length) return -1;
        ReadOnlySpan<byte> field = frame.Slice(at, length);
        return length switch
        {
            1 => field[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(field) + 256,
            4 => BinaryPrimitives.ReadUInt32LittleEndian(field),
            _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(field),
        };
    }
}
