using System;
using System.Buffers.Binary;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The size of a PNG or a JPEG from its header alone, so a picture from a pack is measured before a decoder is asked to
/// make pixels of it. A file that is not what it says, or is cut short, has no size.
/// </summary>
public static class ImageHeader
{
    /// <summary>Width and height of a PNG (from its IHDR chunk), or null.</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        return width is > 0 and <= 65535 && height is > 0 and <= 65535 ? ((int)width, (int)height) : null;
    }

    /// <summary>
    /// Width and height of a JPEG (from its first start-of-frame marker, baseline or progressive), or null. The segments
    /// before it are skipped by their lengths, so a thumbnail inside an EXIF block is not mistaken for the picture.
    /// </summary>
    public static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 4 <= bytes.Length)
        {
            if (bytes[pos] != 0xFF) return null;
            byte marker = bytes[pos + 1];
            if (marker == 0xFF) { pos++; continue; }   // fill bytes
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { pos += 2; continue; }   // markers without a length
            if (marker is 0xD9 or 0xDA) return null;   // end of image, start of scan: no frame header came first
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 2, 2));
            // SOF0 to SOF15, except DHT (C4), JPG (C8) and DAC (CC), which share the range.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8 || pos + 9 > bytes.Length) return null;
                int height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 5, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 7, 2));
                return width > 0 && height > 0 ? (width, height) : null;
            }
            if (length < 2) return null;
            pos += 2 + length;
        }
        return null;
    }
}
