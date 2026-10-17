using System;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// Antialiased shapes for the software-rendered canvas. The SDK's own rounded rectangles, circles and thick lines
/// have hard, stair-stepped edges; these blend the one-pixel edge band by coverage so pills, rings and symbols look
/// smooth on a TV.
/// </summary>
/// <remarks>
/// Cost matters more here than on a PC, so the interior of every shape is a plain span fill and only the pixels on
/// the curved edge (a few per row) are computed. A colour with alpha below 255 is blended, and every pixel of a
/// shape is touched exactly once, so a translucent shape has no seams. All shapes clip to the surface. The
/// coverage of a pixel is <c>clamp(radius + 0.5 - distance from the pixel's centre to the arc's centre)</c>.
/// Blending happens in the display's sRGB values, like the SDK's own blends.
/// <para>
/// Rectangles take whole pixels; circles and lines take float coordinates in pixel-edge space (the pixel at
/// column 3 covers x from 3.0 to 4.0, so a disc 30 px wide at x = 10 has its centre at 25.0).
/// </para>
/// </remarks>
public static unsafe class Paint
{
    // ---- Rectangles -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fills a rectangle. Unlike the SDK's <c>FillRectBlended</c> this blends a whole span at a time, which is
    /// several times faster for a big translucent area such as a dimming layer or a panel.
    /// </summary>
    public static void FillRect(Surface s, int x, int y, int width, int height, Color color)
    {
        if (width <= 0 || height <= 0 || color.A == 0) return;
        if (color.A == 255) { s.FillRect(x, y, width, height, color); return; }
        BlendRect(s, x, y, width, height, color);
    }

    /// <summary>
    /// Dims a region of an opaque surface (the display) towards <paramref name="ink"/>: a quarter of the picture stays
    /// and three quarters are ink, the look of <see cref="FillRect"/> with ink at alpha 191, but done with shifts in
    /// one pass over the pixels instead of a blend per channel. Made for a full-screen scrim behind a dialog, which
    /// is redrawn every frame. A destination pixel's alpha is replaced by 255, so do not use it on a sprite.
    /// </summary>
    public static void Veil(Surface s, int x, int y, int width, int height, Color ink)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = (int)Math.Min(s.Width, (long)x + width), y1 = (int)Math.Min(s.Height, (long)y + height);
        if (x1 <= x0 || y1 <= y0) return;
        uint tint = 0xFF000000u | (uint)(ink.R * 3 / 4) << 16 | (uint)(ink.G * 3 / 4) << 8 | (uint)(ink.B * 3 / 4);
        int count = x1 - x0;
        for (int py = y0; py < y1; py++)
        {
            uint* row = s.Pixels + (long)py * s.Stride + x0;
            for (int i = 0; i < count; i++) row[i] = ((row[i] >> 2) & 0x003F3F3Fu) + tint;
        }
    }

    /// <summary>
    /// Fills a rectangle with rounded corners. The corners are antialiased; the radius is limited to half the
    /// shorter side. <paramref name="color"/>'s alpha makes the whole shape translucent.
    /// </summary>
    public static void FillRoundedRect(Surface s, int x, int y, int width, int height, int radius, Color color)
    {
        if (width <= 0 || height <= 0 || color.A == 0) return;
        int r = Math.Clamp(radius, 0, Math.Min(width, height) / 2);
        if (r == 0) { FillRect(s, x, y, width, height, color); return; }

        // The rows between the two corner bands are plain rectangle; each corner row is one span plus its edge pixels.
        FillRect(s, x, y + r, width, height - 2 * r, color);
        uint rgb = color.Value & 0x00FFFFFFu;
        for (int j = 0; j < r; j++)
        {
            int yTop = y + j, yBottom = y + height - 1 - j;
            int i = FirstCovered(r, j);
            for (; i < r; i++)
            {
                float coverage = CornerCoverage(r, i, j);
                if (coverage >= 1f) break;
                byte alpha = (byte)(color.A * coverage + 0.5f);
                if (alpha != 0) Plot4(s, x, y, width, height, i, j, rgb, alpha);
            }
            FillRect(s, x + i, yTop, width - 2 * i, 1, color);
            FillRect(s, x + i, yBottom, width - 2 * i, 1, color);
        }
    }

    /// <summary>
    /// Draws a rounded rectangle's outline, <paramref name="thickness"/> pixels wide and inside the given bounds
    /// (the outer edge stays where it was asked for, like the SDK's thick outlines). A rounded outline needs its
    /// radius to be at least its thickness, so a smaller radius is raised to it; a radius of 0 gives sharp corners.
    /// </summary>
    public static void StrokeRoundedRect(Surface s, int x, int y, int width, int height, int radius, int thickness, Color color)
    {
        if (width <= 0 || height <= 0 || thickness <= 0 || color.A == 0) return;
        int half = Math.Min(width, height) / 2;
        if (half == 0) { FillRect(s, x, y, width, height, color); return; }
        int t = Math.Min(thickness, half);
        int r = radius <= 0 ? 0 : Math.Min(Math.Max(radius, t), half);

        if (r == 0)
        {
            // Sharp corners: the top and bottom bars own the corners, the sides run between them.
            FillRect(s, x, y, width, t, color);
            FillRect(s, x, y + height - t, width, t, color);
            FillRect(s, x, y + t, t, height - 2 * t, color);
            FillRect(s, x + width - t, y + t, t, height - 2 * t, color);
            return;
        }

        // Straight sides, between the corner squares.
        FillRect(s, x + r, y, width - 2 * r, t, color);
        FillRect(s, x + r, y + height - t, width - 2 * r, t, color);
        FillRect(s, x, y + r, t, height - 2 * r, color);
        FillRect(s, x + width - t, y + r, t, height - 2 * r, color);

        // Corner arcs: the pixels between radius r - t and r around each corner's centre.
        uint rgb = color.Value & 0x00FFFFFFu;
        for (int j = 0; j < r; j++)
        {
            float dy = r - (j + 0.5f);
            int yTop = y + j, yBottom = y + height - 1 - j;
            // Rows above the inner arc have no hole: once the edge is fully covered the rest of the row is solid.
            bool solidRun = dy > r - t + 0.5f;
            int i = FirstCovered(r, j);
            for (; i < r; i++)
            {
                float dx = r - (i + 0.5f);
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                float outer = Math.Clamp(r + 0.5f - distance, 0f, 1f);
                if (outer >= 1f && solidRun) break;
                float inner = Math.Clamp(r - t + 0.5f - distance, 0f, 1f);
                if (inner >= 1f) break;   // the empty inside; every pixel further in is inside too
                byte alpha = (byte)(color.A * (outer - inner) + 0.5f);
                if (alpha != 0) Plot4(s, x, y, width, height, i, j, rgb, alpha);
            }
            if (solidRun && i < r)
            {
                FillRect(s, x + i, yTop, r - i, 1, color); FillRect(s, x + width - r, yTop, r - i, 1, color);
                FillRect(s, x + i, yBottom, r - i, 1, color); FillRect(s, x + width - r, yBottom, r - i, 1, color);
            }
        }
    }

    /// <summary>A pill: a rounded rectangle whose radius is half its height (or width, when that is smaller).</summary>
    public static void Capsule(Surface s, int x, int y, int width, int height, Color color)
        => FillRoundedRect(s, x, y, width, height, Math.Min(width, height) / 2, color);

    /// <summary>A glass panel: <see cref="Theme.Panel"/> with a faint 1 px edge, for text over a bright cover.</summary>
    public static void Panel(Surface s, int x, int y, int width, int height, int radius = Theme.RadiusPanel)
    {
        FillRoundedRect(s, x, y, width, height, radius, Theme.Panel);
        StrokeRoundedRect(s, x, y, width, height, radius, 1, Theme.PanelStroke);
    }

    /// <summary>
    /// The focus indicator around a card or key: a 3 px ring whose outer edge is 6 px outside the element
    /// (concentric with it, so the ring's radius is <paramref name="radius"/> + 6), and a soft glow of three
    /// 2 px steps fading out beyond the ring. <paramref name="ringColor"/> defaults to <see cref="Theme.White"/>.
    /// </summary>
    public static void FocusRing(Surface s, int x, int y, int width, int height, int radius, Color? ringColor = null)
    {
        const int offset = 6, ring = 3, step = 2;
        // Faintest first, so the stronger steps sit nearer the ring.
        ReadOnlySpan<int> strength = [18, 40, 70];   // percent of Theme.FocusGlow's alpha, for the 3rd, 2nd, 1st step
        for (int n = 3; n >= 1; n--)
        {
            int e = offset + step * n;
            byte alpha = (byte)(Theme.FocusGlow.A * strength[3 - n] / 100);
            StrokeRoundedRect(s, x - e, y - e, width + 2 * e, height + 2 * e, radius + e, step, Theme.FocusGlow.WithAlpha(alpha));
        }
        StrokeRoundedRect(s, x - offset, y - offset, width + 2 * offset, height + 2 * offset, radius + offset, ring, ringColor ?? Theme.White);
    }

    // ---- Circles ----------------------------------------------------------------------------------------------------

    /// <summary>Fills an antialiased disc; <paramref name="cx"/>, <paramref name="cy"/> and <paramref name="radius"/> are in pixel-edge space.</summary>
    public static void FillCircle(Surface s, float cx, float cy, float radius, Color color)
    {
        if (!(radius > 0f) || color.A == 0) return;
        float outer = radius + 0.5f, inner = radius - 0.5f;
        int yMin = Math.Max(0, (int)MathF.Floor(cy - radius - 1f)), yMax = Math.Min(s.Height - 1, (int)MathF.Ceiling(cy + radius + 1f));
        for (int py = yMin; py <= yMax; py++)
        {
            float dy = py + 0.5f - cy, dy2 = dy * dy;
            if (dy2 >= outer * outer) continue;
            float reach = MathF.Sqrt(outer * outer - dy2);
            int first = (int)MathF.Floor(cx - reach - 0.5f), last = (int)MathF.Ceiling(cx + reach - 0.5f);
            int fullFirst = last + 1, fullLast = last;   // no fully covered pixels unless the row is wide enough
            if (inner > 0f && inner * inner > dy2)
            {
                float fullReach = MathF.Sqrt(inner * inner - dy2);
                fullFirst = (int)MathF.Ceiling(cx - fullReach - 0.5f);
                fullLast = (int)MathF.Floor(cx + fullReach - 0.5f);
            }
            for (int px = first; px < fullFirst; px++) EdgePixel(s, px, py, cx, dy2, radius, color);
            if (fullLast >= fullFirst) FillRect(s, fullFirst, py, fullLast - fullFirst + 1, 1, color);
            for (int px = fullLast + 1; px <= last; px++) EdgePixel(s, px, py, cx, dy2, radius, color);
        }
    }

    /// <summary>
    /// Draws an antialiased ring: outer edge at <paramref name="radius"/>, <paramref name="thickness"/> pixels wide,
    /// inside it (like the SDK's thick circle).
    /// </summary>
    public static void StrokeCircle(Surface s, float cx, float cy, float radius, float thickness, Color color)
    {
        if (!(radius > 0f) || !(thickness > 0f) || color.A == 0) return;
        float innerRadius = radius - thickness;
        if (innerRadius <= 0f) { FillCircle(s, cx, cy, radius, color); return; }
        float outer = radius + 0.5f, holeFull = innerRadius - 0.5f;
        int yMin = Math.Max(0, (int)MathF.Floor(cy - radius - 1f)), yMax = Math.Min(s.Height - 1, (int)MathF.Ceiling(cy + radius + 1f));
        for (int py = yMin; py <= yMax; py++)
        {
            float dy = py + 0.5f - cy, dy2 = dy * dy;
            if (dy2 >= outer * outer) continue;
            float reach = MathF.Sqrt(outer * outer - dy2);
            int first = (int)MathF.Floor(cx - reach - 0.5f), last = (int)MathF.Ceiling(cx + reach - 0.5f);
            // The part of the row that is fully inside the hole is skipped without looking at it.
            int holeFirst = 1, holeLast = 0;
            if (holeFull > 0f && holeFull * holeFull > dy2)
            {
                float holeReach = MathF.Sqrt(holeFull * holeFull - dy2);
                holeFirst = (int)MathF.Ceiling(cx - holeReach - 0.5f);
                holeLast = (int)MathF.Floor(cx + holeReach - 0.5f);
            }
            for (int px = first; px <= last; px++)
            {
                if (px >= holeFirst && px <= holeLast) { px = holeLast; continue; }
                float dx = px + 0.5f - cx;
                float distance = MathF.Sqrt(dx * dx + dy2);
                float coverage = Math.Clamp(radius + 0.5f - distance, 0f, 1f) - Math.Clamp(innerRadius + 0.5f - distance, 0f, 1f);
                byte alpha = (byte)(color.A * coverage + 0.5f);
                if (alpha != 0) s.BlendPixel(px, py, color.Value & 0x00FFFFFFu, alpha);
            }
        }
    }

    // ---- Lines ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Draws an antialiased line with round caps between two points (pixel-edge space).
    /// It scans the line's bounding box, so it is meant for icon-sized strokes; use FillRect for a long straight rule.
    /// </summary>
    public static void Line(Surface s, float x0, float y0, float x1, float y1, float thickness, Color color)
    {
        if (!(thickness > 0f) || color.A == 0) return;
        float h = thickness / 2f;
        float reach = h + 1f;
        int xMin = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, x1) - reach)), xMax = Math.Min(s.Width - 1, (int)MathF.Ceiling(MathF.Max(x0, x1) + reach));
        int yMin = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, y1) - reach)), yMax = Math.Min(s.Height - 1, (int)MathF.Ceiling(MathF.Max(y0, y1) + reach));
        float vx = x1 - x0, vy = y1 - y0, length2 = vx * vx + vy * vy;
        float outerLimit = (h + 0.5f) * (h + 0.5f), solidLimit = h > 0.5f ? (h - 0.5f) * (h - 0.5f) : -1f;
        uint rgb = color.Value & 0x00FFFFFFu;
        for (int py = yMin; py <= yMax; py++)
        {
            float qy = py + 0.5f - y0;
            for (int px = xMin; px <= xMax; px++)
            {
                float qx = px + 0.5f - x0;
                float along = length2 > 0f ? Math.Clamp((qx * vx + qy * vy) / length2, 0f, 1f) : 0f;
                float ex = qx - along * vx, ey = qy - along * vy;
                float d2 = ex * ex + ey * ey;
                if (d2 >= outerLimit) continue;
                byte alpha = d2 <= solidLimit ? color.A : (byte)(color.A * Math.Clamp(h + 0.5f - MathF.Sqrt(d2), 0f, 1f) + 0.5f);
                if (alpha != 0) s.BlendPixel(px, py, rgb, alpha);
            }
        }
    }

    // ---- Shared pieces ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Coverage (0 to 1) of pixel (<paramref name="i"/>, <paramref name="j"/>) in the top-left corner square of a
    /// rounded rectangle with the given radius; the arc's centre is the square's far corner. Images that get rounded
    /// corners use this too, so a cover and a pill of the same radius have the same edge.
    /// </summary>
    internal static float CornerCoverage(int radius, int i, int j)
    {
        float dx = radius - (i + 0.5f), dy = radius - (j + 0.5f);
        return Math.Clamp(radius + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
    }

    // First pixel of corner row j that can have any coverage (may be one too early; its coverage is then 0).
    private static int FirstCovered(int r, int j)
    {
        float dy = r - (j + 0.5f);
        float reach2 = (r + 0.5f) * (r + 0.5f) - dy * dy;
        if (reach2 <= 0f) return r;
        return Math.Clamp((int)MathF.Floor(r - 0.5f - MathF.Sqrt(reach2)), 0, r);
    }

    // The same corner pixel at all four corners of the rectangle.
    private static void Plot4(Surface s, int x, int y, int width, int height, int i, int j, uint rgb, byte alpha)
    {
        int left = x + i, right = x + width - 1 - i, top = y + j, bottom = y + height - 1 - j;
        s.BlendPixel(left, top, rgb, alpha); s.BlendPixel(right, top, rgb, alpha);
        s.BlendPixel(left, bottom, rgb, alpha); s.BlendPixel(right, bottom, rgb, alpha);
    }

    private static void EdgePixel(Surface s, int px, int py, float cx, float dy2, float radius, Color color)
    {
        float dx = px + 0.5f - cx;
        byte alpha = (byte)(color.A * Math.Clamp(radius + 0.5f - MathF.Sqrt(dx * dx + dy2), 0f, 1f) + 0.5f);
        if (alpha != 0) s.BlendPixel(px, py, color.Value & 0x00FFFFFFu, alpha);
    }

    // Source-over of one pixel, the same maths as the SDK's own blend for a target with alpha: on the display
    // (alpha always 255) the result is the opaque blend, on a transparent sprite it composes the alpha.
    internal static uint BlendOver(uint src, uint dst)
    {
        uint a = src >> 24;
        if (a == 0) return dst;
        uint da = dst >> 24;
        if (da == 0 || a == 255) return src;
        uint dw = (da * (255 - a) + 127) / 255;
        uint outA = a + dw;
        uint sr = (src >> 16) & 0xFF, sg = (src >> 8) & 0xFF, sb = src & 0xFF;
        uint dr = (dst >> 16) & 0xFF, dg = (dst >> 8) & 0xFF, db = dst & 0xFF;
        uint rr = (sr * a + dr * dw + outA / 2) / outA;
        uint rg = (sg * a + dg * dw + outA / 2) / outA;
        uint rb = (sb * a + db * dw + outA / 2) / outA;
        return (outA << 24) | (rr << 16) | (rg << 8) | rb;
    }

    // Blends a translucent colour over a rectangle, clipped. Premultiplies the source once for the whole fill;
    // a destination pixel that is opaque (every pixel of the display) takes the short path.
    private static void BlendRect(Surface s, int x, int y, int width, int height, Color color)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = (int)Math.Min(s.Width, (long)x + width), y1 = (int)Math.Min(s.Height, (long)y + height);
        if (x1 <= x0 || y1 <= y0) return;
        uint a = color.A, na = 255 - a, src = color.Value;
        uint pr = (uint)color.R * a + 127, pg = (uint)color.G * a + 127, pb = (uint)color.B * a + 127;
        int count = x1 - x0;
        for (int py = y0; py < y1; py++)
        {
            uint* row = s.Pixels + (long)py * s.Stride + x0;
            for (int i = 0; i < count; i++)
            {
                uint d = row[i];
                if ((d >> 24) == 255)
                    row[i] = 0xFF000000u
                        | ((pr + ((d >> 16) & 0xFF) * na) / 255 << 16)
                        | ((pg + ((d >> 8) & 0xFF) * na) / 255 << 8)
                        | ((pb + (d & 0xFF) * na) / 255);
                else row[i] = BlendOver(src, d);
            }
        }
    }
}
