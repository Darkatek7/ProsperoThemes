// QR Code encoder for the app, reduced from Nayuki's "QR Code generator library" (C#/Java/TypeScript versions):
// byte mode only, error correction level M, versions 1 to 10, automatic mask selection by penalty score.
//
// Original work Copyright (c) Project Nayuki. (MIT License)
// https://www.nayuki.io/page/qr-code-generator-library
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// - The above copyright notice and this permission notice shall be included in all copies or substantial portions
//   of the Software.
// - The Software is provided "as is", without warranty of any kind, express or implied, including but not limited
//   to the warranties of merchantability, fitness for a particular purpose and noninfringement. In no event shall
//   the authors or copyright holders be liable for any claim, damages or other liability, whether in an action of
//   contract, tort or otherwise, arising from, out of or in connection with the Software or the use or other
//   dealings in the Software.

using System;
using System.Collections.Generic;
using System.Text;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// A QR code symbol: a square grid of dark and light modules. Made by <see cref="Encode"/>, which keeps to what the app
/// needs (a web address on screen): byte mode, error correction M, versions 1 to 10 (up to 213 bytes of text).
/// </summary>
public sealed class QrCode
{
    public const int MinVersion = 1, MaxVersion = 10;

    // Error correction level M (format bits 0), per version (index 0 is unused): codewords of one block's correction code, and block count.
    private static readonly sbyte[] EccCodewordsPerBlock = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly sbyte[] NumErrorCorrectionBlocks = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    // Penalty weights of the mask evaluation.
    private const int PenaltyN1 = 3, PenaltyN2 = 3, PenaltyN3 = 40, PenaltyN4 = 10;

    /// <summary>The version, 1 to 10.</summary>
    public int Version { get; }
    /// <summary>The width and height in modules (17 + 4 x version), without any quiet zone.</summary>
    public int Size { get; }
    /// <summary>The mask pattern the encoder picked, 0 to 7.</summary>
    public int Mask { get; private set; }

    private readonly bool[,] _modules;     // [y, x], true is dark
    private readonly bool[,] _isFunction;  // function modules are not data and are never masked

    /// <summary>The module at column <paramref name="x"/>, row <paramref name="y"/>: true is dark; outside the symbol is light.</summary>
    public bool Get(int x, int y) => (uint)x < (uint)Size && (uint)y < (uint)Size && _modules[y, x];

    /// <summary>
    /// Encodes <paramref name="utf8Text"/> (as UTF-8 bytes) in the smallest version that holds it, or returns null when
    /// it does not fit version 10 (213 bytes).
    /// </summary>
    public static QrCode? Encode(string utf8Text)
    {
        byte[] data = Encoding.UTF8.GetBytes(utf8Text ?? "");
        int version, capacityBits;
        for (version = MinVersion; ; version++)
        {
            capacityBits = NumDataCodewords(version) * 8;
            int used = 4 + (version < 10 ? 8 : 16) + 8 * data.Length;
            if (used <= capacityBits) break;
            if (version == MaxVersion) return null;
        }

        // Mode indicator (byte mode, 0100), character count, the bytes, terminator, padding to a whole byte, then pad bytes.
        var bits = new List<bool>(capacityBits);
        AppendBits(bits, 0x4, 4);
        AppendBits(bits, data.Length, version < 10 ? 8 : 16);
        foreach (byte b in data) AppendBits(bits, b, 8);
        AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count));
        AppendBits(bits, 0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) AppendBits(bits, pad, 8);

        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return new QrCode(version, codewords);
    }

    private QrCode(int version, byte[] dataCodewords)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        DrawFunctionPatterns();
        byte[] all = AddEccAndInterleave(dataCodewords);
        DrawCodewords(all);

        // The mask with the lowest penalty wins (the lowest number on a tie).
        int best = 0, minPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            int penalty = PenaltyScore();
            if (penalty < minPenalty) { best = mask; minPenalty = penalty; }
            ApplyMask(mask);   // undo (the mask is its own inverse)
        }
        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    // ---- Function patterns ------------------------------------------------------------------------------------------

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        int[] positions = AlignmentPositions(Version);
        int count = positions.Length;
        for (int i = 0; i < count; i++)
            for (int j = 0; j < count; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == count - 1 || i == count - 1 && j == 0))   // not on a finder
                    DrawAlignment(positions[i], positions[j]);

        DrawFormatBits(0);   // reserves the format area; the real bits come once the mask is chosen
        DrawVersion();
    }

    private void DrawFormatBits(int mask)
    {
        int data = 0 << 3 | mask;   // level M is 0
        int rem = data;
        for (int i = 0; i < 10; i++) rem = rem << 1 ^ (rem >> 9) * 0x537;
        int bits = (data << 10 | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);   // always dark
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        int rem = Version;
        for (int i = 0; i < 12; i++) rem = rem << 1 ^ (rem >> 11) * 0x1F25;
        int bits = Version << 12 | rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawFinder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if ((uint)x < (uint)Size && (uint)y < (uint)Size) SetFunction(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private static int[] AlignmentPositions(int version)
    {
        if (version == 1) return [];
        int count = version / 7 + 2;
        int size = version * 4 + 17;
        int step = (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;   // ceil((version * 4 + 4) / (count * 2 - 2)) * 2
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    // ---- Codewords --------------------------------------------------------------------------------------------------

    private static int NumRawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int NumDataCodewords(int version) =>
        NumRawDataModules(version) / 8 - EccCodewordsPerBlock[version] * NumErrorCorrectionBlocks[version];

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int blocks = NumErrorCorrectionBlocks[Version], eccLen = EccCodewordsPerBlock[Version];
        int raw = NumRawDataModules(Version) / 8;
        int shortBlocks = blocks - raw % blocks, shortLen = raw / blocks;

        var blockList = new byte[blocks][];
        byte[] divisor = ReedSolomonDivisor(eccLen);
        for (int i = 0, k = 0; i < blocks; i++)
        {
            int dataLen = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
            var block = new byte[shortLen + 1];   // data, a gap byte in the short blocks, then the correction code
            Array.Copy(data, k, block, 0, dataLen);
            byte[] ecc = ReedSolomonRemainder(data, k, dataLen, divisor);
            k += dataLen;
            Array.Copy(ecc, 0, block, block.Length - eccLen, eccLen);
            blockList[i] = block;
        }

        var result = new byte[raw];
        int n = 0;
        for (int i = 0; i < blockList[0].Length; i++)
            for (int j = 0; j < blocks; j++)
                if (i != shortLen - eccLen || j >= shortBlocks)   // skips the gap byte of a short block
                    result[n++] = blockList[j][i];
        return result;
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                    // The remainder bits (0 to 7 of them) stay light.
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !_isFunction[y, x]) _modules[y, x] = !_modules[y, x];
            }
    }

    // ---- Penalty ----------------------------------------------------------------------------------------------------

    private int PenaltyScore()
    {
        int result = 0;
        var history = new int[7];

        // Runs of one colour and finder-like patterns, along rows then along columns.
        for (int y = 0; y < Size; y++)
        {
            bool runColor = false;
            int runLength = 0;
            Array.Clear(history);
            for (int x = 0; x < Size; x++)
            {
                if (_modules[y, x] == runColor)
                {
                    runLength++;
                    if (runLength == 5) result += PenaltyN1;
                    else if (runLength > 5) result++;
                }
                else
                {
                    AddHistory(runLength, history);
                    if (!runColor) result += CountPatterns(history) * PenaltyN3;
                    runColor = _modules[y, x];
                    runLength = 1;
                }
            }
            result += TerminateAndCount(runColor, runLength, history) * PenaltyN3;
        }
        for (int x = 0; x < Size; x++)
        {
            bool runColor = false;
            int runLength = 0;
            Array.Clear(history);
            for (int y = 0; y < Size; y++)
            {
                if (_modules[y, x] == runColor)
                {
                    runLength++;
                    if (runLength == 5) result += PenaltyN1;
                    else if (runLength > 5) result++;
                }
                else
                {
                    AddHistory(runLength, history);
                    if (!runColor) result += CountPatterns(history) * PenaltyN3;
                    runColor = _modules[y, x];
                    runLength = 1;
                }
            }
            result += TerminateAndCount(runColor, runLength, history) * PenaltyN3;
        }

        // 2 x 2 blocks of one colour.
        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool color = _modules[y, x];
                if (color == _modules[y, x + 1] && color == _modules[y + 1, x] && color == _modules[y + 1, x + 1]) result += PenaltyN2;
            }

        // Balance of dark and light modules.
        int dark = 0;
        foreach (bool module in _modules) if (module) dark++;
        int total = Size * Size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += k * PenaltyN4;
        return result;
    }

    // Finder-like 1:1:3:1:1 patterns with a light run of 4 or more beside them, found in the last seven runs of a line.
    private static int CountPatterns(int[] history)
    {
        int n = history[1];
        bool core = n > 0 && history[2] == n && history[3] == n * 3 && history[4] == n && history[5] == n;
        return (core && history[0] >= n * 4 && history[6] >= n ? 1 : 0) + (core && history[6] >= n * 4 && history[0] >= n ? 1 : 0);
    }

    private int TerminateAndCount(bool runColor, int runLength, int[] history)
    {
        if (runColor)
        {
            AddHistory(runLength, history);   // ends the dark run
            runLength = 0;
        }
        runLength += Size;                     // the light border
        AddHistory(runLength, history);
        return CountPatterns(history);
    }

    private void AddHistory(int runLength, int[] history)
    {
        if (history[0] == 0) runLength += Size;   // the light border before the first run
        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = runLength;
    }

    // ---- Reed-Solomon over GF(2^8) / 0x11D --------------------------------------------------------------------------

    private static byte[] ReedSolomonDivisor(int degree)
    {
        // The product of (x - 2^i) for i = 0 .. degree-1, without the leading 1, highest power first.
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], (byte)root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply((byte)root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, int offset, int length, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        for (int n = 0; n < length; n++)
        {
            byte factor = (byte)(data[offset + n] ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = z << 1 ^ (z >> 7) * 0x11D;
            z ^= (y >> i & 1) * x;
        }
        return (byte)z;
    }

    // ---- Bits -------------------------------------------------------------------------------------------------------

    private static bool Bit(int value, int index) => (value >> index & 1) != 0;

    private static void AppendBits(List<bool> bits, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--) bits.Add(Bit(value, i));
    }
}
