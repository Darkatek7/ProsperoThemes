using System;
using System.Collections.Generic;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

internal enum PadButton { Cross, Circle, Square, Triangle, DPad, Options, L1, R1, R2, DPadUp }

/// <summary>
/// PlayStation-style controller hint icons in one colour, drawn with <see cref="Paint"/> because the UI fonts have
/// no symbol glyphs. Each glyph (button, size, colours) is rendered once into a small sprite with antialiased edges
/// and from then on drawing it is a blit, so a footer full of hints costs almost nothing per frame.
/// </summary>
internal static class ButtonGlyph
{
    private readonly record struct Key(PadButton Button, int Size, uint Fg, uint Bg, AtlasFont? Font);

    private const int MaxSprites = 96;
    private static readonly object Sync = new();
    private static readonly Dictionary<Key, PixelBuffer> Sprites = new();

    /// <summary>Width the glyph occupies at <paramref name="size"/> pixels high.</summary>
    public static int Width(PadButton button, int size) => button switch
    {
        PadButton.L1 or PadButton.R1 or PadButton.R2 => size * 8 / 5,
        PadButton.Options => size * 3 / 2,
        _ => size,
    };

    public static int Draw(Surface s, PadButton button, int x, int y, int size, Color fg, Color bg)
        => Draw(s, button, x, y, size, fg, bg, null);

    /// <summary>
    /// Draws the glyph with its top-left at (x, y) and returns the drawn width.
    /// Round buttons are a filled <paramref name="bg"/> disc with the symbol (an outlined cross, circle, square or
    /// triangle) in <paramref name="fg"/>; DPad and Options are drawn in <paramref name="fg"/> directly (it must be
    /// opaque); DPadUp is the DPad with only its upper arm in <paramref name="fg"/> and the rest of the cross faded toward the ink
    /// (the button that is meant is Up); L1/R1/R2 are a <paramref name="bg"/> pill with the label in <paramref name="fg"/> (the label is
    /// skipped when no font is given).
    /// </summary>
    public static int Draw(Surface s, PadButton button, int x, int y, int size, Color fg, Color bg, AtlasFont? font)
    {
        int width = Width(button, size);
        if (size <= 0) return width;
        bool labelled = button is PadButton.L1 or PadButton.R1 or PadButton.R2;
        // A sprite is a few KB, so the set stays small; a test that builds many fonts just starts the cache over.
        var key = new Key(button, size, fg.Value, bg.Value, labelled ? font : null);
        lock (Sync)
        {
            if (!Sprites.TryGetValue(key, out PixelBuffer? sprite))
            {
                if (Sprites.Count >= MaxSprites) ClearLocked();
                sprite = Render(button, width, size, fg, bg, key.Font);
                Sprites[key] = sprite;
            }
            // Blitting under the lock keeps another thread's Clear() from freeing the pixels mid-copy.
            s.BlitBlended(sprite.AsSurface(), x, y);
        }
        return width;
    }

    /// <summary>Releases every sprite (the renderer's Dispose calls this); drawing again simply renders them anew.</summary>
    public static void Clear()
    {
        lock (Sync) ClearLocked();
    }

    /// <summary>How many sprites are cached right now.</summary>
    internal static int CachedCount { get { lock (Sync) return Sprites.Count; } }

    private static void ClearLocked()
    {
        foreach (var sprite in Sprites.Values) sprite.Dispose();
        Sprites.Clear();
    }

    private static PixelBuffer Render(PadButton button, int width, int size, Color fg, Color bg, AtlasFont? font)
    {
        var sprite = new PixelBuffer(width, size);
        Surface s = sprite.AsSurface();
        float c = size / 2f;                        // centre of the disc, in pixel-edge coordinates
        float stroke = Math.Max(1.5f, size / 12f);  // 2.5 px at 30
        float d = size * 0.2f;                      // symbol half-extent, 6 px at 30

        switch (button)
        {
            case PadButton.Cross:
                Paint.FillCircle(s, c, c, c, bg);
                Paint.Line(s, c - d, c - d, c + d, c + d, stroke, fg);
                Paint.Line(s, c - d, c + d, c + d, c - d, stroke, fg);
                break;
            case PadButton.Circle:
                Paint.FillCircle(s, c, c, c, bg);
                Paint.StrokeCircle(s, c, c, d * 1.25f, stroke, fg);
                break;
            case PadButton.Square:
            {
                Paint.FillCircle(s, c, c, c, bg);
                float q = d * 0.95f;   // the stroke's centre line; round caps give the corners a soft radius
                Paint.Line(s, c - q, c - q, c + q, c - q, stroke, fg);
                Paint.Line(s, c + q, c - q, c + q, c + q, stroke, fg);
                Paint.Line(s, c + q, c + q, c - q, c + q, stroke, fg);
                Paint.Line(s, c - q, c + q, c - q, c - q, stroke, fg);
                break;
            }
            case PadButton.Triangle:
            {
                Paint.FillCircle(s, c, c, c, bg);
                float top = c - d * 1.05f, bottom = c + d * 0.7f, half = d * 1.0f;
                Paint.Line(s, c, top, c - half, bottom, stroke, fg);
                Paint.Line(s, c - half, bottom, c + half, bottom, stroke, fg);
                Paint.Line(s, c + half, bottom, c, top, stroke, fg);
                break;
            }
            case PadButton.DPad:
            {
                int arm = size / 3, offset = (size - arm) / 2;
                int round = Math.Max(1, arm / 3);
                Paint.FillRoundedRect(s, 0, offset, size, arm, round, fg);
                Paint.FillRoundedRect(s, offset, 0, arm, size, round, fg);
                break;
            }
            case PadButton.DPadUp:
            {
                // The whole cross in a faded shade (opaque, so the sprite blends cleanly), then the upper arm in full strength.
                int arm = size / 3, offset = (size - arm) / 2;
                int round = Math.Max(1, arm / 3);
                Color faded = Color.Lerp(fg, Theme.Ink, 0.6f);
                Paint.FillRoundedRect(s, 0, offset, size, arm, round, faded);
                Paint.FillRoundedRect(s, offset, 0, arm, size, round, faded);
                Paint.FillRoundedRect(s, offset, 0, arm, offset + arm, round, fg);
                break;
            }
            case PadButton.Options:
            {
                int pillH = size * 2 / 3, top = (size - pillH) / 2;
                Paint.StrokeRoundedRect(s, 0, top, width, pillH, pillH / 2, 2, fg);
                for (int i = -1; i <= 1; i++)
                    Paint.Line(s, width / 2f - 7f, c + i * 5f, width / 2f + 7f, c + i * 5f, 2f, fg);
                break;
            }
            default:   // L1, R1, R2
            {
                Paint.FillRoundedRect(s, 0, 0, width, size, size * 2 / 5, bg);
                if (font is not null)
                {
                    string label = button switch { PadButton.L1 => "L1", PadButton.R1 => "R1", _ => "R2" };
                    font.DrawText(s, label, (width - font.MeasureText(label)) / 2, (size - font.LineHeight) / 2, fg);
                }
                break;
            }
        }
        return sprite;
    }
}
