using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace ProsperoThemes.Shared;

/// <summary>
/// A Zstandard decoder (RFC 8878). Firmware before 13.00 keeps the home backgrounds as plain DDS
/// (<c>bg_*.dds</c>) where 13.00 has <c>bg_*.dds.zst</c>, and the console has no zstd library the app can call,
/// so a theme's compressed background is unpacked here for those consoles. Frames without a dictionary only;
/// skippable frames are passed over and a frame's content checksum, when it has one, is checked.
/// </summary>
public static class Zstd
{
    /// <summary>The most a payload may decode to (a 3840x2160 BC7 texture is 8 MB).</summary>
    public const int MaxOutput = 64 * 1024 * 1024;

    private const uint FrameMagic = 0xFD2FB528;
    private const int MaxBlockSize = 128 * 1024;
    private const int MaxHuffmanBits = 11;

    /// <summary>Decodes every frame in <paramref name="data"/>.</summary>
    /// <exception cref="InvalidDataException">The data is not a valid frame, or decodes to more than <paramref name="maxOutput"/>.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> data, int maxOutput = MaxOutput)
    {
        var output = new Output(maxOutput);
        int at = 0;
        if (data.Length == 0) throw Corrupt("no data");
        while (at < data.Length)
        {
            Need(data, at, 4);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            if ((magic & 0xFFFFFFF0) == 0x184D2A50)
            {
                Need(data, at, 8);
                long skip = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
                if (at + 8 + skip > data.Length) throw Corrupt("skippable frame is cut off");
                at += 8 + (int)skip;
                continue;
            }
            if (magic != FrameMagic) throw Corrupt("not a Zstandard frame");
            at = DecodeFrame(data, at + 4, output);
        }
        return output.ToArray();
    }

    private static int DecodeFrame(ReadOnlySpan<byte> data, int at, Output output)
    {
        Need(data, at, 1);
        byte descriptor = data[at++];
        int sizeFlag = descriptor >> 6;
        bool singleSegment = (descriptor & 0x20) != 0;
        if ((descriptor & 0x08) != 0) throw Corrupt("reserved frame header bit is set");
        bool hasChecksum = (descriptor & 0x04) != 0;
        int dictionaryBytes = (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        // The window descriptor only bounds how far back a match may reach; the whole output stays in memory here.
        if (!singleSegment) at++;
        Need(data, at, dictionaryBytes);
        uint dictionary = 0;
        for (int i = 0; i < dictionaryBytes; i++) dictionary |= (uint)data[at + i] << (8 * i);
        if (dictionary != 0) throw Corrupt("frames with a dictionary are not supported");
        at += dictionaryBytes;

        int sizeBytes = sizeFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        Need(data, at, sizeBytes);
        long contentSize = sizeBytes switch
        {
            0 => -1,
            1 => data[at],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) + 256,
            4 => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]),
            _ => (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(data[at..]), long.MaxValue),
        };
        at += sizeBytes;
        if (contentSize >= 0) output.Reserve(contentSize);

        int frameStart = output.Length;
        var state = new FrameState();
        bool last;
        do
        {
            Need(data, at, 3);
            int header = data[at] | data[at + 1] << 8 | data[at + 2] << 16;
            at += 3;
            last = (header & 1) != 0;
            int size = header >> 3;
            switch ((header >> 1) & 3)
            {
                case 0:
                    Need(data, at, size);
                    output.Append(data.Slice(at, size));
                    at += size;
                    break;
                case 1:
                    Need(data, at, 1);
                    output.Fill(data[at], size);
                    at += 1;
                    break;
                case 2:
                    if (size > MaxBlockSize) throw Corrupt("compressed block is larger than 128 KiB");
                    Need(data, at, size);
                    DecodeBlock(data.Slice(at, size), output, state, frameStart);
                    at += size;
                    break;
                default:
                    throw Corrupt("reserved block type");
            }
        } while (!last);

        if (contentSize >= 0 && output.Length - frameStart != contentSize)
            throw Corrupt($"frame decoded to {output.Length - frameStart} bytes, its header says {contentSize}");
        if (hasChecksum)
        {
            Need(data, at, 4);
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            if ((uint)XxHash64(output.Since(frameStart)) != expected) throw Corrupt("content checksum does not match");
            at += 4;
        }
        return at;
    }

    // ---- Blocks ----

    private sealed class FrameState
    {
        public Huffman? Literals;
        public Fse? LiteralLengths, Offsets, MatchLengths;
        public readonly uint[] Repeat = [1, 4, 8];
    }

    private static void DecodeBlock(ReadOnlySpan<byte> block, Output output, FrameState state, int frameStart)
    {
        byte[] literals = DecodeLiterals(block, out int used, state);
        DecodeSequences(block[used..], literals, output, state, frameStart);
    }

    private static byte[] DecodeLiterals(ReadOnlySpan<byte> block, out int used, FrameState state)
    {
        Need(block, 0, 1);
        byte first = block[0];
        int type = first & 3, sizeFormat = (first >> 2) & 3;
        if (type < 2)
        {
            int size, header;
            switch (sizeFormat)
            {
                case 1:
                    Need(block, 0, 2);
                    size = (first >> 4) + (block[1] << 4);
                    header = 2;
                    break;
                case 3:
                    Need(block, 0, 3);
                    size = (first >> 4) + (block[1] << 4) + (block[2] << 12);
                    header = 3;
                    break;
                default:
                    size = first >> 3;
                    header = 1;
                    break;
            }
            if (size > MaxBlockSize) throw Corrupt("literals are larger than a block");
            var raw = new byte[size];
            if (type == 0)
            {
                Need(block, header, size);
                block.Slice(header, size).CopyTo(raw);
                used = header + size;
            }
            else
            {
                Need(block, header, 1);
                raw.AsSpan().Fill(block[header]);
                used = header + 1;
            }
            return raw;
        }

        int headerLength = sizeFormat switch { 0 or 1 => 3, 2 => 4, _ => 5 };
        int sizeBits = sizeFormat switch { 0 or 1 => 10, 2 => 14, _ => 18 };
        Need(block, 0, headerLength);
        ulong fields = 0;
        for (int i = 0; i < headerLength; i++) fields |= (ulong)block[i] << (8 * i);
        ulong mask = (1UL << sizeBits) - 1;
        int regenerated = (int)((fields >> 4) & mask);
        int compressed = (int)((fields >> (4 + sizeBits)) & mask);
        if (regenerated > MaxBlockSize) throw Corrupt("literals are larger than a block");
        Need(block, headerLength, compressed);
        ReadOnlySpan<byte> payload = block.Slice(headerLength, compressed);
        used = headerLength + compressed;

        if (type == 2)
        {
            int tableBytes = ReadHuffman(payload, out Huffman table);
            state.Literals = table;
            payload = payload[tableBytes..];
        }
        Huffman huffman = state.Literals ?? throw Corrupt("literals reuse a Huffman table that was never sent");

        var literals = new byte[regenerated];
        if (sizeFormat == 0)
        {
            DecodeHuffmanStream(huffman, payload, literals);
            return literals;
        }
        Need(payload, 0, 6);
        int s1 = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        int s2 = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        int s3 = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        int s4 = payload.Length - 6 - s1 - s2 - s3;
        if (s4 < 0) throw Corrupt("literal streams are longer than their section");
        int segment = (regenerated + 3) / 4;
        if (3 * segment > regenerated) throw Corrupt("too few literals for four streams");
        ReadOnlySpan<byte> streams = payload[6..];
        DecodeHuffmanStream(huffman, streams[..s1], literals.AsSpan(0, segment));
        DecodeHuffmanStream(huffman, streams.Slice(s1, s2), literals.AsSpan(segment, segment));
        DecodeHuffmanStream(huffman, streams.Slice(s1 + s2, s3), literals.AsSpan(2 * segment, segment));
        DecodeHuffmanStream(huffman, streams.Slice(s1 + s2 + s3, s4), literals.AsSpan(3 * segment));
        return literals;
    }

    private static readonly int[] LiteralLengthBase =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512,
        1024, 2048, 4096, 8192, 16384, 32768, 65536,
    ];

    private static readonly byte[] LiteralLengthExtra =
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private static readonly int[] MatchLengthBase =
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539,
    ];

    private static readonly byte[] MatchLengthExtra =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2,
        2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    ];

    private static readonly Fse DefaultLiteralLengths = BuildFse(
    [
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1,
    ], 6);

    private static readonly Fse DefaultMatchLengths = BuildFse(
    [
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1,
    ], 6);

    private static readonly Fse DefaultOffsets = BuildFse(
    [
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
    ], 5);

    private static void DecodeSequences(ReadOnlySpan<byte> src, byte[] literals, Output output, FrameState state, int frameStart)
    {
        Need(src, 0, 1);
        int at = 1, count;
        byte first = src[0];
        if (first < 128)
        {
            count = first;
        }
        else if (first < 255)
        {
            Need(src, 1, 1);
            count = ((first - 128) << 8) + src[1];
            at = 2;
        }
        else
        {
            Need(src, 1, 2);
            count = src[1] + (src[2] << 8) + 0x7F00;
            at = 3;
        }
        if (count == 0)
        {
            output.Append(literals);
            return;
        }

        Need(src, at, 1);
        byte modes = src[at++];
        if ((modes & 3) != 0) throw Corrupt("reserved sequence mode bits are set");
        Fse lengths = state.LiteralLengths = ReadSequenceTable(src, ref at, (modes >> 6) & 3, DefaultLiteralLengths, 9, 35, state.LiteralLengths);
        Fse offsets = state.Offsets = ReadSequenceTable(src, ref at, (modes >> 4) & 3, DefaultOffsets, 8, 31, state.Offsets);
        Fse matches = state.MatchLengths = ReadSequenceTable(src, ref at, (modes >> 2) & 3, DefaultMatchLengths, 9, 52, state.MatchLengths);

        var bits = new BackwardBits(src[at..]);
        int lengthState = (int)bits.Read(lengths.Log);
        int offsetState = (int)bits.Read(offsets.Log);
        int matchState = (int)bits.Read(matches.Log);
        uint[] repeat = state.Repeat;
        int literal = 0;
        for (int i = 0; i < count; i++)
        {
            int offsetCode = offsets.Symbols[offsetState];
            int lengthCode = lengths.Symbols[lengthState];
            int matchCode = matches.Symbols[matchState];
            if (offsetCode > 31 || lengthCode > 35 || matchCode > 52) throw Corrupt("sequence code out of range");
            uint offsetValue = (1u << offsetCode) + (uint)bits.Read(offsetCode);
            int matchLength = MatchLengthBase[matchCode] + (int)bits.Read(MatchLengthExtra[matchCode]);
            int literalLength = LiteralLengthBase[lengthCode] + (int)bits.Read(LiteralLengthExtra[lengthCode]);
            if (i < count - 1)
            {
                lengthState = lengths.Base[lengthState] + (int)bits.Read(lengths.Bits[lengthState]);
                matchState = matches.Base[matchState] + (int)bits.Read(matches.Bits[matchState]);
                offsetState = offsets.Base[offsetState] + (int)bits.Read(offsets.Bits[offsetState]);
            }

            if (literalLength > literals.Length - literal) throw Corrupt("a sequence uses more literals than there are");
            output.Append(literals.AsSpan(literal, literalLength));
            literal += literalLength;

            uint offset;
            if (offsetValue > 3)
            {
                offset = offsetValue - 3;
                repeat[2] = repeat[1];
                repeat[1] = repeat[0];
                repeat[0] = offset;
            }
            else
            {
                uint index = offsetValue - 1;
                if (literalLength == 0) index++;
                if (index == 0)
                {
                    offset = repeat[0];
                }
                else
                {
                    offset = index < 3 ? repeat[index] : repeat[0] - 1;
                    if (index > 1) repeat[2] = repeat[1];
                    repeat[1] = repeat[0];
                    repeat[0] = offset;
                }
            }
            if (offset == 0 || offset > output.Length - frameStart) throw Corrupt("a match reaches before the start of the frame");
            output.CopyMatch((int)offset, matchLength);
        }
        if (bits.Offset != 0) throw Corrupt("sequence bit stream was not used up exactly");
        output.Append(literals.AsSpan(literal));
    }

    private static Fse ReadSequenceTable(ReadOnlySpan<byte> src, ref int at, int mode, Fse predefined, int maxLog, int maxSymbol, Fse? previous)
    {
        switch (mode)
        {
            case 0:
                return predefined;
            case 1:
                Need(src, at, 1);
                byte symbol = src[at++];
                if (symbol > maxSymbol) throw Corrupt("sequence symbol out of range");
                return new Fse(0, [symbol], [0], [0]);
            case 2:
                Fse table = ReadFse(src[at..], maxLog, maxSymbol, out int used);
                at += used;
                return table;
            default:
                return previous ?? throw Corrupt("a sequence table repeats one that was never sent");
        }
    }

    // ---- Finite State Entropy ----

    private sealed record Fse(int Log, byte[] Symbols, byte[] Bits, ushort[] Base);

    /// <summary>Reads an FSE table description (normalized counts) from the front of <paramref name="src"/>.</summary>
    private static Fse ReadFse(ReadOnlySpan<byte> src, int maxLog, int maxSymbol, out int used)
    {
        long bit = 0;
        int log = (int)ReadForward(src, ref bit, 4) + 5;
        if (log > maxLog) throw Corrupt("FSE table is more precise than allowed");
        int remaining = 1 << log;
        Span<short> counts = stackalloc short[256];
        int symbol = 0;
        while (remaining > 0 && symbol <= maxSymbol)
        {
            int width = BitOperations.Log2((uint)(remaining + 1)) + 1;
            int value = (int)ReadForward(src, ref bit, width);
            int lowerMask = (1 << (width - 1)) - 1;
            int threshold = (1 << width) - 1 - (remaining + 1);
            if ((value & lowerMask) < threshold)
            {
                bit--;
                value &= lowerMask;
            }
            else if (value > lowerMask)
            {
                value -= threshold;
            }
            int probability = value - 1;
            remaining -= probability < 0 ? -probability : probability;
            counts[symbol++] = (short)probability;
            if (probability != 0) continue;
            while (true)
            {
                int zeros = (int)ReadForward(src, ref bit, 2);
                for (int i = 0; i < zeros && symbol <= maxSymbol; i++) counts[symbol++] = 0;
                if (zeros != 3) break;
            }
        }
        if (remaining != 0) throw Corrupt("FSE table counts do not add up");
        used = (int)((bit + 7) >> 3);
        if (used > src.Length) throw Corrupt("FSE table description is cut off");
        return BuildFse(counts[..symbol], log);
    }

    private static Fse BuildFse(ReadOnlySpan<short> counts, int log)
    {
        int size = 1 << log;
        var symbols = new byte[size];
        var bits = new byte[size];
        var bases = new ushort[size];
        Span<int> next = stackalloc int[256];
        int high = size - 1;
        for (int s = 0; s < counts.Length; s++)
        {
            if (counts[s] != -1) continue;
            if (high < 0) throw Corrupt("FSE table has too many rare symbols");
            symbols[high--] = (byte)s;
            next[s] = 1;
        }
        int step = (size >> 1) + (size >> 3) + 3, mask = size - 1, position = 0;
        for (int s = 0; s < counts.Length; s++)
        {
            if (counts[s] <= 0) continue;
            next[s] = counts[s];
            for (int i = 0; i < counts[s]; i++)
            {
                symbols[position] = (byte)s;
                do position = (position + step) & mask; while (position > high);
            }
        }
        if (position != 0) throw Corrupt("FSE table counts do not fill the table");
        for (int i = 0; i < size; i++)
        {
            int n = next[symbols[i]]++;
            int width = log - BitOperations.Log2((uint)n);
            bits[i] = (byte)width;
            bases[i] = (ushort)((n << width) - size);
        }
        return new Fse(log, symbols, bits, bases);
    }

    // ---- Huffman ----

    private sealed record Huffman(int MaxBits, byte[] Symbols, byte[] Bits);

    private static int ReadHuffman(ReadOnlySpan<byte> src, out Huffman table)
    {
        Need(src, 0, 1);
        int header = src[0];
        Span<byte> weights = stackalloc byte[256];
        int count, used;
        if (header >= 128)
        {
            count = header - 127;
            int bytes = (count + 1) / 2;
            Need(src, 1, bytes);
            for (int i = 0; i < count; i++)
            {
                byte packed = src[1 + i / 2];
                weights[i] = (byte)(i % 2 == 0 ? packed >> 4 : packed & 15);
            }
            used = 1 + bytes;
        }
        else
        {
            Need(src, 1, header);
            count = DecodeWeights(src.Slice(1, header), weights);
            used = 1 + header;
        }
        table = BuildHuffman(weights[..count]);
        return used;
    }

    /// <summary>Huffman weights compressed with FSE: two interleaved states over one stream.</summary>
    private static int DecodeWeights(ReadOnlySpan<byte> src, Span<byte> weights)
    {
        Fse table = ReadFse(src, 6, 255, out int used);
        var bits = new BackwardBits(src[used..]);
        int state1 = (int)bits.Read(table.Log), state2 = (int)bits.Read(table.Log);
        int count = 0;
        while (true)
        {
            if (count > 253) throw Corrupt("too many Huffman weights");
            weights[count++] = Decode(table, ref state1, ref bits);
            if (bits.Offset < 0)
            {
                weights[count++] = table.Symbols[state2];
                break;
            }
            weights[count++] = Decode(table, ref state2, ref bits);
            if (bits.Offset < 0)
            {
                weights[count++] = table.Symbols[state1];
                break;
            }
        }
        return count;
    }

    private static byte Decode(Fse table, ref int state, ref BackwardBits bits)
    {
        byte symbol = table.Symbols[state];
        state = table.Base[state] + (int)bits.Read(table.Bits[state]);
        return symbol;
    }

    /// <summary>Builds the decoding table from the sent weights; the last symbol's weight is implied.</summary>
    private static Huffman BuildHuffman(ReadOnlySpan<byte> weights)
    {
        if (weights.Length == 0 || weights.Length > 255) throw Corrupt("bad Huffman weight count");
        long total = 0;
        foreach (byte w in weights)
        {
            if (w > MaxHuffmanBits) throw Corrupt("Huffman weight too large");
            if (w > 0) total += 1L << (w - 1);
        }
        if (total == 0) throw Corrupt("Huffman weights are all zero");
        int maxBits = BitOperations.Log2((ulong)total) + 1;
        if (maxBits > MaxHuffmanBits) throw Corrupt("Huffman codes are too long");
        long leftOver = (1L << maxBits) - total;
        if ((leftOver & (leftOver - 1)) != 0) throw Corrupt("Huffman weights do not complete a tree");
        int lastWeight = BitOperations.Log2((ulong)leftOver) + 1;

        int symbolCount = weights.Length + 1;
        Span<byte> lengths = stackalloc byte[256];
        for (int i = 0; i < weights.Length; i++) lengths[i] = (byte)(weights[i] > 0 ? maxBits + 1 - weights[i] : 0);
        lengths[weights.Length] = (byte)(maxBits + 1 - lastWeight);

        int size = 1 << maxBits;
        var symbols = new byte[size];
        var bits = new byte[size];
        Span<int> rankCount = stackalloc int[MaxHuffmanBits + 2];
        Span<int> rankStart = stackalloc int[MaxHuffmanBits + 2];
        for (int i = 0; i < symbolCount; i++) rankCount[lengths[i]]++;
        rankStart[maxBits] = 0;
        for (int length = maxBits; length >= 1; length--)
        {
            rankStart[length - 1] = rankStart[length] + rankCount[length] * (1 << (maxBits - length));
            if (rankStart[length - 1] > size) throw Corrupt("Huffman table overflows");
            bits.AsSpan(rankStart[length], rankStart[length - 1] - rankStart[length]).Fill((byte)length);
        }
        if (rankStart[0] != size) throw Corrupt("Huffman table is not full");
        for (int i = 0; i < symbolCount; i++)
        {
            int length = lengths[i];
            if (length == 0) continue;
            int code = rankStart[length], span = 1 << (maxBits - length);
            symbols.AsSpan(code, span).Fill((byte)i);
            rankStart[length] += span;
        }
        return new Huffman(maxBits, symbols, bits);
    }

    private static void DecodeHuffmanStream(Huffman table, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var bits = new BackwardBits(src);
        int mask = (1 << table.MaxBits) - 1;
        int state = (int)bits.Read(table.MaxBits);
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = table.Symbols[state];
            int width = table.Bits[state];
            state = ((state << width) | (int)bits.Read(width)) & mask;
        }
        if (bits.Offset != -table.MaxBits) throw Corrupt("literal stream was not used up exactly");
    }

    // ---- Bits ----

    /// <summary>Reads <paramref name="count"/> bits front to back, least significant first; past the end reads zeros.</summary>
    private static ulong ReadForward(ReadOnlySpan<byte> src, ref long bit, int count)
    {
        ulong value = 0;
        for (int i = 0; i < count; i++, bit++)
        {
            long index = bit >> 3;
            if (index < src.Length && (src[(int)index] >> (int)(bit & 7) & 1) != 0) value |= 1UL << i;
        }
        return value;
    }

    /// <summary>
    /// A stream read from its end towards its start. The last byte's highest set bit marks where the data ends;
    /// bits asked for before the start read as zero and leave <see cref="Offset"/> negative.
    /// </summary>
    private ref struct BackwardBits
    {
        private readonly ReadOnlySpan<byte> _src;
        public long Offset;

        public BackwardBits(ReadOnlySpan<byte> src)
        {
            if (src.Length == 0 || src[^1] == 0) throw Corrupt("bit stream has no end marker");
            _src = src;
            Offset = src.Length * 8L - (8 - BitOperations.Log2(src[^1]));
        }

        public ulong Read(int count)
        {
            if (count == 0) return 0;
            Offset -= count;
            long start = Offset;
            int available = count;
            if (start < 0)
            {
                available += (int)start;
                start = 0;
            }
            if (available <= 0) return 0;
            int index = (int)(start >> 3), shift = (int)(start & 7);
            int bytes = (shift + available + 7) >> 3;
            ulong value = 0;
            for (int i = 0; i < bytes; i++) value |= (ulong)_src[index + i] << (8 * i);
            value = (value >> shift) & ((1UL << available) - 1);
            return Offset < 0 ? value << (int)-Offset : value;
        }
    }

    // ---- Output ----

    private sealed class Output(int max)
    {
        private byte[] _buffer = new byte[Math.Min(max, 1 << 16)];

        public int Length { get; private set; }

        public void Reserve(long total)
        {
            if (Length + total > max) throw new InvalidDataException($"the data decodes to more than {max} bytes");
            if (Length + total > _buffer.Length) Array.Resize(ref _buffer, (int)(Length + total));
        }

        private void Ensure(int extra)
        {
            long needed = (long)Length + extra;
            if (needed > max) throw new InvalidDataException($"the data decodes to more than {max} bytes");
            if (needed > _buffer.Length) Array.Resize(ref _buffer, (int)Math.Max(needed, Math.Min(max, _buffer.Length * 2L)));
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            Ensure(data.Length);
            data.CopyTo(_buffer.AsSpan(Length));
            Length += data.Length;
        }

        public void Fill(byte value, int count)
        {
            Ensure(count);
            _buffer.AsSpan(Length, count).Fill(value);
            Length += count;
        }

        /// <summary>Copies <paramref name="length"/> bytes from <paramref name="offset"/> back; the source may overlap the copy.</summary>
        public void CopyMatch(int offset, int length)
        {
            Ensure(length);
            int from = Length - offset;
            if (offset >= length)
            {
                _buffer.AsSpan(from, length).CopyTo(_buffer.AsSpan(Length));
            }
            else
            {
                for (int i = 0; i < length; i++) _buffer[Length + i] = _buffer[from + i];
            }
            Length += length;
        }

        public ReadOnlySpan<byte> Since(int start) => _buffer.AsSpan(start, Length - start);

        public byte[] ToArray() => _buffer.Length == Length ? _buffer : _buffer.AsSpan(0, Length).ToArray();
    }

    // ---- Checksum ----

    private const ulong Prime1 = 11400714785074694791, Prime2 = 14029467366897019727, Prime3 = 1609587929392839161,
        Prime4 = 9650029242287828579, Prime5 = 2870177450012600261;

    /// <summary>XXH64 with seed 0, as the frame checksum uses (its low 32 bits).</summary>
    internal static ulong XxHash64(ReadOnlySpan<byte> data)
    {
        int at = 0;
        ulong hash;
        if (data.Length >= 32)
        {
            ulong v1 = unchecked(Prime1 + Prime2), v2 = Prime2, v3 = 0, v4 = unchecked(0 - Prime1);
            for (; at + 32 <= data.Length; at += 32)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[at..]));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[(at + 8)..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[(at + 16)..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[(at + 24)..]));
            }
            hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            hash = Merge(Merge(Merge(Merge(hash, v1), v2), v3), v4);
        }
        else
        {
            hash = Prime5;
        }
        hash += (ulong)data.Length;
        for (; at + 8 <= data.Length; at += 8)
            hash = BitOperations.RotateLeft(hash ^ Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data[at..])), 27) * Prime1 + Prime4;
        if (at + 4 <= data.Length)
        {
            hash = BitOperations.RotateLeft(hash ^ BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) * Prime1, 23) * Prime2 + Prime3;
            at += 4;
        }
        for (; at < data.Length; at++)
            hash = BitOperations.RotateLeft(hash ^ data[at] * Prime5, 11) * Prime1;
        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        hash ^= hash >> 32;
        return hash;

        static ulong Round(ulong acc, ulong lane) => BitOperations.RotateLeft(acc + lane * Prime2, 31) * Prime1;
        static ulong Merge(ulong acc, ulong value) => (acc ^ Round(0, value)) * Prime1 + Prime4;
    }

    private static void Need(ReadOnlySpan<byte> data, int at, int count)
    {
        if (count < 0 || at < 0 || at > data.Length - count) throw Corrupt("data is cut off");
    }

    private static InvalidDataException Corrupt(string reason) => new($"Zstandard: {reason}.");
}
