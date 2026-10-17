using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>Antialiased, pre-rasterised Inter text; no device font imports or glyph allocations.</summary>
public sealed class AtlasFont : ITextFont
{
    private readonly record struct Glyph(int X, int Y, int Width, int Advance);
    private readonly Dictionary<char, Glyph> _glyphs = new();
    private readonly byte[] _alpha;
    private readonly int _width;
    public int LineHeight { get; }

    public AtlasFont(byte[] data)
    {
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("FNT1"u8))
            throw new ArgumentException("Invalid font atlas.");
        int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        _width = U16(4);
        int height = U16(6), count = U16(10);
        LineHeight = U16(8);
        int pixelOffset = 12 + count * 10;
        if (_width == 0 || height == 0 || LineHeight == 0 || data.Length != pixelOffset + _width * height)
            throw new ArgumentException("Invalid font atlas dimensions.");
        for (int i = 0; i < count; i++)
        {
            int p = 12 + i * 10;
            var glyph = new Glyph(U16(p + 2), U16(p + 4), U16(p + 6), U16(p + 8));
            if (glyph.X + glyph.Width > _width || glyph.Y + LineHeight > height)
                throw new ArgumentException("Invalid glyph bounds.");
            _glyphs[(char)U16(p)] = glyph;
        }
        _alpha = data.AsSpan(pixelOffset).ToArray();
    }

    public bool HasGlyph(int codepoint) => codepoint <= char.MaxValue && _glyphs.ContainsKey((char)codepoint);
    private Glyph Get(char c) => _glyphs.TryGetValue(c, out var glyph) ? glyph : _glyphs['?'];

    public int MeasureText(ReadOnlySpan<char> text)
    {
        int width = 0;
        foreach (char c in text) width += Get(c).Advance;
        return width;
    }

    public void DrawText(Surface surface, ReadOnlySpan<char> text, int x, int y, Color color)
    {
        foreach (char c in text)
        {
            var glyph = Get(c);
            for (int row = 0; row < LineHeight; row++)
            {
                int p = (glyph.Y + row) * _width + glyph.X;
                for (int col = 0; col < glyph.Width; col++)
                {
                    byte a = _alpha[p + col];
                    if (a != 0) surface.BlendPixel(x + col, y + row, color.Value, (byte)(a * color.A / 255));
                }
            }
            x += glyph.Advance;
        }
    }

    /// <summary>Draws the text with its horizontal centre at <paramref name="centerX"/>; <paramref name="y"/> is the top of the line.</summary>
    public void DrawCentered(Surface surface, ReadOnlySpan<char> text, int centerX, int y, Color color)
        => DrawText(surface, text, centerX - MeasureText(text) / 2, y, color);

    /// <summary>
    /// Breaks <paramref name="text"/> into lines no wider than <paramref name="maxWidth"/> pixels, at most
    /// <paramref name="maxLines"/> of them (<c>int.MaxValue</c> for no limit). Lines break at spaces; a word wider
    /// than a whole line is broken between letters; a '\n' starts a new line and blank lines are kept (except at
    /// the end of the text). When text is left over, the last line ends in '…' and still fits. Nothing to show, or a
    /// non-positive width or line count, gives an empty list. Measured with this font's advances, so the result
    /// matches <see cref="DrawText"/>.
    /// </summary>
    public IReadOnlyList<string> Wrap(string text, int maxWidth, int maxLines)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || maxWidth <= 0 || maxLines <= 0) return lines;
        text = text.TrimEnd();   // a trailing newline is not a reason to truncate
        int space = Get(' ').Advance;
        var line = new StringBuilder();
        int lineWidth = 0;
        // Gathers one line more than asked for, which is how truncation is noticed; returns true once that one exists.
        bool Flush()
        {
            lines.Add(line.ToString());
            line.Clear();
            lineWidth = 0;
            return lines.Count > maxLines;
        }

        bool full = false;
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 && (full = Flush())) break;
            foreach (string word in words)
            {
                int wordWidth = MeasureText(word);
                if (line.Length > 0 && lineWidth + space + wordWidth <= maxWidth)
                {
                    line.Append(' ').Append(word);
                    lineWidth += space + wordWidth;
                    continue;
                }
                if (line.Length > 0 && (full = Flush())) break;
                int start = 0;
                // A word wider than the line goes on in pieces, each as long as fits (at least one letter).
                while (wordWidth > maxWidth)
                {
                    int end = start, width = 0;
                    while (end < word.Length && (end == start || width + Get(word[end]).Advance <= maxWidth)) width += Get(word[end++]).Advance;
                    lines.Add(word[start..end]);
                    if (lines.Count > maxLines) { full = true; break; }
                    wordWidth -= width;
                    start = end;
                }
                if (full) break;
                line.Append(word, start, word.Length - start);
                lineWidth = wordWidth;
            }
            if (full) break;
            if (line.Length > 0 && (full = Flush())) break;
        }
        if (full)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            lines[maxLines - 1] = WithEllipsis(lines[maxLines - 1], maxWidth);
        }
        else if (line.Length > 0) lines.Add(line.ToString());
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // The line with "…" at its end, shortened (and trailing spaces dropped) until both fit in maxWidth.
    private string WithEllipsis(string line, int maxWidth)
    {
        int dots = Get('…').Advance;
        int length = line.Length, width = MeasureText(line);
        while (length > 0 && width + dots > maxWidth) width -= Get(line[--length]).Advance;
        while (length > 0 && line[length - 1] == ' ') length--;
        return line[..length] + "…";
    }

    public void DrawClipped(Surface surface, string text, int x, int y, Color color, int maxWidth)
    {
        int length = 0, width = 0;
        while (length < text.Length && width + Get(text[length]).Advance <= maxWidth)
            width += Get(text[length++]).Advance;
        if (length < text.Length && length > 0)
        {
            int dots = Get('…').Advance;
            while (length > 0 && width + dots > maxWidth) width -= Get(text[--length]).Advance;
            DrawText(surface, text.AsSpan(0, length), x, y, color);
            DrawText(surface, "…", x + width, y, color);
        }
        else DrawText(surface, text, x, y, color);
    }
}
