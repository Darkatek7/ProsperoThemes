using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
