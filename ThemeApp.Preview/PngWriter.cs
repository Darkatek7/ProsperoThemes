using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using SharpProspero.Graphics;

/// <summary>
/// Writes a drawing surface as an 8-bit RGB PNG for the screenshots. SharpProspero's <c>PngEncoder</c> calls the
/// PS5's PNG module (libScePngEnc), which a PC has not got, so the preview encodes its own: filter-0 rows in one
/// zlib stream, then the standard chunks. Alpha is dropped, as the BMP writer drops it, so every shot is opaque.
/// </summary>
internal static unsafe class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(Surface surface)
    {
        int width = surface.Width, height = surface.Height, stride = surface.Stride;
        if (width <= 0 || height <= 0) throw new ArgumentException("The surface has no pixels to encode.", nameof(surface));

        // Each row: a filter byte (0, none) then red, green, blue. Pixels are 0xAARRGGBB, blue in the low byte.
        var raw = new byte[(long)height * (1 + 3L * width)];
        int at = 0;
        for (int y = 0; y < height; y++)
        {
            raw[at++] = 0;
            uint* row = surface.Pixels + (long)y * stride;
            for (int x = 0; x < width; x++)
            {
                uint p = row[x];
                raw[at++] = (byte)(p >> 16);
                raw[at++] = (byte)(p >> 8);
                raw[at++] = (byte)p;
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;   // bit depth
        header[9] = 2;   // colour type: RGB
        // compression, filter and interlace stay 0

        var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        byte[] name = Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(name, data));
        stream.Write(crc);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>CRC-32 (IEEE) over the chunk type and data, as PNG requires.</summary>
    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
