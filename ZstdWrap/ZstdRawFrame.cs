using System.Buffers.Binary;

namespace ZstdWrap;

/// <summary>What a frame's header says, and what its blocks are.</summary>
public sealed class ZstdFrameInfo
{
    public int FrameHeaderDescriptor { get; init; }
    public int? WindowLog { get; init; }
    public long? ContentSize { get; init; }
    public bool ContentChecksum { get; init; }
    public bool SingleSegment { get; init; }
    public int HeaderBytes { get; init; }
    public List<(int Type, int Size, int Offset)> Blocks { get; } = new();

    public string Describe()
    {
        string window = WindowLog is null ? "n/a" : $"2^{WindowLog} = {(WindowLog >= 20 ? $"{1 << (WindowLog.Value - 20)} MiB" : $"{1 << WindowLog.Value} B")}";
        string size = ContentSize is null ? "absent" : $"{ContentSize}";
        return $"""
            Frame_Header_Descriptor : 0x{FrameHeaderDescriptor:X2}
            Single_Segment          : {SingleSegment}
            Window_Log              : {window}
            Frame_Content_Size      : {size}
            Content_Checksum        : {ContentChecksum}
            Header length           : {HeaderBytes} bytes
            Blocks                  : {Blocks.Count}
            """;
    }
}

/// <summary>
/// A minimal Zstandard (RFC 8878) frame reader and Raw-block writer.
/// </summary>
/// <remarks>
/// The PS5's system wallpapers are Zstandard frames (<c>bg_*.dds.zst</c>), so writing a theme means
/// producing a frame the shell's decoder accepts. A frame made entirely of <b>Raw</b> blocks is valid
/// Zstandard and needs no compressor: only the magic, a frame header and a 3-byte header per block.
/// That is what <see cref="EncodeRaw"/> emits. Nothing here compresses or decompresses Deflate/compressed
/// blocks; <see cref="Parse"/> reports them but cannot read their contents, which is enough to inspect
/// a system frame's header.
/// </remarks>
public static class ZstdRawFrame
{
    public static readonly byte[] Magic = { 0x28, 0xB5, 0x2F, 0xFD };

    /// <summary>Largest Raw block a decoder must accept: min(Window_Size, 128 KiB).</summary>
    public const int MaxRawBlockSize = 128 * 1024;

    /// <summary>Window descriptor 0x60: exponent 12, mantissa 0 → windowLog 22 (4 MiB).</summary>
    public const byte WindowDescriptor4MiB = 0x60;

    /// <summary>
    /// Wraps <paramref name="content"/> in a Zstandard frame built from Raw blocks.
    /// </summary>
    /// <remarks>
    /// The header mirrors what the console's own wallpaper frames use: a 4-byte Frame_Content_Size, no
    /// Single_Segment flag, a 4 MiB window and no content checksum. Content size is required because
    /// the field is declared; the decoder can still stream, but the value must be truthful.
    /// </remarks>
    public static byte[] EncodeRaw(ReadOnlySpan<byte> content)
    {
        if ((long)content.Length > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(content), "content does not fit the 4-byte Frame_Content_Size field");

        int blockCount = content.Length == 0 ? 1 : (content.Length + MaxRawBlockSize - 1) / MaxRawBlockSize;

        // magic(4) + descriptor(1) + window(1) + content size(4) + 3 bytes per block
        var frame = new byte[4 + 1 + 1 + 4 + blockCount * 3 + content.Length];
        int at = 0;

        Magic.CopyTo(frame.AsSpan(at));
        at += Magic.Length;

        // Frame_Header_Descriptor: FCS flag 2 (4-byte content size), Single_Segment 0,
        // no content checksum, no dictionary.
        frame[at++] = 0x80;
        frame[at++] = WindowDescriptor4MiB;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(at), (uint)content.Length);
        at += 4;

        int written = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int size = Math.Min(MaxRawBlockSize, content.Length - written);
            bool last = i == blockCount - 1;

            // 24-bit little-endian: bit 0 Last_Block, bits 1-2 Block_Type (0 = Raw), bits 3-23 size.
            int header = (size << 3) | (0 << 1) | (last ? 1 : 0);
            frame[at++] = (byte)(header & 0xFF);
            frame[at++] = (byte)((header >> 8) & 0xFF);
            frame[at++] = (byte)((header >> 16) & 0xFF);

            content.Slice(written, size).CopyTo(frame.AsSpan(at));
            at += size;
            written += size;
        }

        return frame;
    }

    /// <summary>Reads the header and walks the block headers of a frame.</summary>
    public static ZstdFrameInfo Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || !frame[..4].SequenceEqual(Magic))
            throw new InvalidDataException("not a Zstandard frame (bad magic)");

        int at = 4;
        byte descriptor = frame[at++];

        int fcsFlag = (descriptor >> 6) & 0x3;
        bool singleSegment = (descriptor & 0x20) != 0;
        bool checksum = (descriptor & 0x04) != 0;
        int dictFlag = descriptor & 0x03;

        int? windowLog = null;
        if (!singleSegment)
        {
            byte windowDescriptor = frame[at++];
            int exponent = windowDescriptor >> 3;
            int mantissa = windowDescriptor & 0x07;
            // windowLog = 10 + exponent; the mantissa adds a fraction of the base, which the log alone
            // does not express. Reported as the exponent's power, which is what matters here.
            windowLog = 10 + exponent;
            _ = mantissa;
        }

        int dictionaryIdBytes = dictFlag switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        at += dictionaryIdBytes;

        // FCS field size: 0 means absent unless Single_Segment, which makes it 1 byte.
        int fcsBytes = fcsFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        long? contentSize = null;
        if (fcsBytes > 0)
        {
            long value = 0;
            for (int i = 0; i < fcsBytes; i++) value |= (long)frame[at + i] << (8 * i);
            at += fcsBytes;
            // A 2-byte field carries a value with 256 added; a 1-byte Single_Segment field does not.
            if (fcsBytes == 2) value += 256;
            contentSize = value;
        }

        var info = new ZstdFrameInfo
        {
            FrameHeaderDescriptor = descriptor,
            WindowLog = windowLog,
            ContentSize = contentSize,
            ContentChecksum = checksum,
            SingleSegment = singleSegment,
            HeaderBytes = at,
        };

        while (true)
        {
            if (at + 3 > frame.Length) throw new InvalidDataException("truncated block header");
            int header = frame[at] | (frame[at + 1] << 8) | (frame[at + 2] << 16);
            at += 3;

            bool last = (header & 1) != 0;
            int type = (header >> 1) & 0x3;
            int size = header >> 3;

            int dataLength = type switch
            {
                0 => size,          // Raw
                1 => 1,             // RLE: one byte, repeated `size` times
                2 => size,          // Compressed
                _ => throw new InvalidDataException("reserved block type"),
            };

            info.Blocks.Add((type, size, at));
            at += dataLength;

            if (last) break;
        }

        return info;
    }

    /// <summary>
    /// Reassembles a frame that consists only of Raw blocks. Throws for any other block type, so a
    /// caller cannot mistake "parsed" for "decoded".
    /// </summary>
    public static byte[] DecodeRaw(ReadOnlySpan<byte> frame)
    {
        ZstdFrameInfo info = Parse(frame);
        var output = new MemoryStream(info.ContentSize is > 0 ? (int)info.ContentSize.Value : 0);

        foreach ((int type, int size, int offset) in info.Blocks)
        {
            if (type != 0)
                throw new NotSupportedException($"block type {type} is not Raw; this reader cannot decode compressed blocks");
            output.Write(frame.Slice(offset, size));
        }

        return output.ToArray();
    }
}
