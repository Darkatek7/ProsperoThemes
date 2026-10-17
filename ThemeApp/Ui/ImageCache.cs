using System;
using System.Collections.Generic;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// An image scaled once to the size it is drawn at, with antialiased rounded corners, ready to blit.
/// Obtained from <see cref="ImageCache"/>; a cache owns it and may dispose it when it adds another image,
/// so draw it right after getting it and do not keep it.
/// </summary>
/// <remarks>
/// How the corners work on the console's opaque back buffer: the pixels are stored with alpha 255 except in the
/// four r x r corner squares, where alpha is the corner's coverage (straight alpha, colours untouched). Drawing is
/// then plain row copies for everything between the corner squares and a blended blit of just those four small
/// squares, so a card costs about what an unmasked copy would, and the corners are exactly as smooth as a
/// <see cref="Paint.FillRoundedRect"/> of the same radius.
/// </remarks>
public sealed class CachedImage : IDisposable
{
    private PixelBuffer? _pixels;

    internal CachedImage(PixelBuffer pixels, int radius)
    {
        _pixels = pixels;
        Width = pixels.Width;
        Height = pixels.Height;
        Radius = Math.Clamp(radius, 0, Math.Min(Width, Height) / 2);
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>The corner radius baked into the pixels.</summary>
    public int Radius { get; }
    /// <summary>Memory the pixels take.</summary>
    public long Bytes => (long)Width * Height * 4;

    /// <summary>Draws the image with its top-left at (x, y); clips like any blit.</summary>
    public void Draw(Surface target, int x, int y)
    {
        Surface image = (_pixels ?? throw new ObjectDisposedException(nameof(CachedImage))).AsSurface();
        int r = Radius;
        if (r == 0) { target.Blit(image, x, y); return; }
        int w = Width, h = Height;
        target.Blit(image.Region(0, r, w, h - 2 * r), x, y + r);                 // between the corner rows
        target.Blit(image.Region(r, 0, w - 2 * r, r), x + r, y);                 // top edge between the corners
        target.Blit(image.Region(r, h - r, w - 2 * r, r), x + r, y + h - r);     // bottom edge
        target.BlitBlended(image.Region(0, 0, r, r), x, y);
        target.BlitBlended(image.Region(w - r, 0, r, r), x + w - r, y);
        target.BlitBlended(image.Region(0, h - r, r, r), x, y + h - r);
        target.BlitBlended(image.Region(w - r, h - r, r, r), x + w - r, y + h - r);
    }

    public void Dispose()
    {
        _pixels?.Dispose();
        _pixels = null;
    }

    internal PixelBuffer Pixels => _pixels ?? throw new ObjectDisposedException(nameof(CachedImage));
}

/// <summary>
/// A bounded least-recently-used cache of <see cref="CachedImage"/>s plus a blurred, dimmed backdrop builder.
/// Scaling a cover down with the SDK's nearest-neighbour blit every frame aliases; here each image is reduced once,
/// by averaging 2x2 blocks until it is within 2x of its target and then a bilinear step, and every frame after
/// that is a copy. Not thread safe: use it from the frame thread.
/// </summary>
/// <remarks>
/// Entries are identified by name, size, radius and dim (all four), so one source can be cached as a thumbnail and
/// as a big cover. The cache stores only copies and never keeps the source buffer. It holds at most
/// <c>maxEntries</c> images and <c>maxBytes</c> of pixels; adding past either limit drops the least recently used.
/// When a source changes under the same name, call <see cref="Remove"/> or <see cref="Clear"/>.
/// </remarks>
public sealed class ImageCache : IDisposable
{
    private readonly record struct Key(string Name, int Width, int Height, int Radius, byte Dim);
    private readonly record struct BackdropKey(string Name, int Blur, byte Dim);
    private sealed class Entry(CachedImage image) { public readonly CachedImage Image = image; public ulong Stamp; }
    private sealed class Backdrop(PixelBuffer pixels) { public readonly PixelBuffer Pixels = pixels; public ulong Stamp; }

    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly Dictionary<BackdropKey, Backdrop> _backdrops = new();
    private readonly int _maxEntries, _maxBackdrops;
    private readonly long _maxBytes;
    private long _bytes;
    private ulong _clock;

    /// <param name="maxEntries">Most images kept.</param>
    /// <param name="maxBytes">Most pixel memory kept for images (default 24 MiB); an image bigger than this is still kept alone.</param>
    /// <param name="maxBackdrops">Most 1920x1080 backdrops kept (each is 8 MiB).</param>
    public ImageCache(int maxEntries = 24, long maxBytes = 24L << 20, int maxBackdrops = 2)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes = Math.Max(1, maxBytes);
        _maxBackdrops = Math.Max(1, maxBackdrops);
    }

    public int Count => _entries.Count;
    /// <summary>Pixel memory held by cached images (not backdrops).</summary>
    public long Bytes => _bytes;

    /// <summary>The cached image, or null when it has not been added (or was dropped). A hit marks it recently used.</summary>
    public CachedImage? TryGet(string name, int width, int height, int radius = 0, byte dim = 0)
    {
        if (!_entries.TryGetValue(new Key(name, width, height, radius, dim), out var entry)) return null;
        entry.Stamp = ++_clock;
        return entry.Image;
    }

    /// <summary>
    /// Scales <paramref name="source"/> to width x height, rounds its corners, optionally dims it toward
    /// <see cref="Theme.Ink"/> (<paramref name="dim"/> is the alpha of that wash), and caches the copy. A source
    /// with transparency is flattened over <see cref="Theme.Ink"/>. The source is not kept.
    /// </summary>
    public CachedImage Add(string name, PixelBuffer source, int width, int height, int radius = 0, byte dim = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var key = new Key(name, width, height, radius, dim);
        if (_entries.Remove(key, out var old)) { _bytes -= old.Image.Bytes; old.Image.Dispose(); }

        PixelBuffer pixels = Scaled(source, width, height);
        Flatten(pixels, dim);
        var image = new CachedImage(pixels, radius);
        ApplyCorners(pixels, image.Radius);

        while (_entries.Count > 0 && (_entries.Count >= _maxEntries || _bytes + image.Bytes > _maxBytes)) EvictOldest();
        _entries[key] = new Entry(image) { Stamp = ++_clock };
        _bytes += image.Bytes;
        return image;
    }

    /// <summary>Drops every cached size and variant of <paramref name="name"/>, thumbnails and backdrops alike.</summary>
    public void Remove(string name)
    {
        foreach (var key in new List<Key>(_entries.Keys))
            if (key.Name == name) { _bytes -= _entries[key].Image.Bytes; _entries[key].Image.Dispose(); _entries.Remove(key); }
        foreach (var key in new List<BackdropKey>(_backdrops.Keys))
            if (key.Name == name) { _backdrops[key].Pixels.Dispose(); _backdrops.Remove(key); }
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values) entry.Image.Dispose();
        _entries.Clear();
        foreach (var backdrop in _backdrops.Values) backdrop.Pixels.Dispose();
        _backdrops.Clear();
        _bytes = 0;
    }

    public void Dispose() => Clear();

    /// <summary>
    /// A full-screen (1920x1080) backdrop made from <paramref name="source"/>: reduced to 480x270, blurred twice with
    /// a box blur of <paramref name="blurRadius"/> (in those 480-wide pixels, so 4x on screen), scaled back up smoothly
    /// and washed with <see cref="Theme.Ink"/> at <paramref name="dim"/>. Cached by name; the buffer belongs to the
    /// cache (blit it, do not dispose it) and stays valid until it is dropped. Building costs tens of milliseconds
    /// on a PC and several times that on the console, so build it when the selection changes, never every frame.
    /// </summary>
    public PixelBuffer GetBackdrop(string name, PixelBuffer source, int blurRadius = 5, byte dim = 150)
    {
        var key = new BackdropKey(name, blurRadius, dim);
        if (_backdrops.TryGetValue(key, out var cached)) { cached.Stamp = ++_clock; return cached.Pixels; }

        using PixelBuffer small = Scaled(source, 480, 270);
        Flatten(small, 0);
        small.AsSurface().BoxBlur(blurRadius);
        small.AsSurface().BoxBlur(blurRadius);
        var full = new PixelBuffer(Theme.ScreenWidth, Theme.ScreenHeight);
        full.AsSurface().BlitScaledSmooth(small.AsSurface(), 0, 0, Theme.ScreenWidth, Theme.ScreenHeight);
        Paint.FillRect(full.AsSurface(), 0, 0, Theme.ScreenWidth, Theme.ScreenHeight, Theme.Ink.WithAlpha(dim));

        while (_backdrops.Count >= _maxBackdrops)
        {
            BackdropKey oldest = default; ulong stamp = ulong.MaxValue;
            foreach (var (k, b) in _backdrops) if (b.Stamp < stamp) { stamp = b.Stamp; oldest = k; }
            _backdrops[oldest].Pixels.Dispose();
            _backdrops.Remove(oldest);
        }
        _backdrops[key] = new Backdrop(full) { Stamp = ++_clock };
        return full;
    }

    private void EvictOldest()
    {
        Key oldest = default; ulong stamp = ulong.MaxValue;
        foreach (var (k, e) in _entries) if (e.Stamp < stamp) { stamp = e.Stamp; oldest = k; }
        var entry = _entries[oldest];
        _entries.Remove(oldest);
        _bytes -= entry.Image.Bytes;
        entry.Image.Dispose();
    }

    // ---- Pixel work (once per image) --------------------------------------------------------------------------------

    /// <summary>A new buffer of width x height holding <paramref name="source"/> scaled to it; the source is untouched.</summary>
    internal static PixelBuffer Scaled(PixelBuffer source, int width, int height)
    {
        PixelBuffer? halved = null;
        PixelBuffer current = source;
        try
        {
            // A bilinear step only looks at 2x2 source pixels, so it aliases when it shrinks by more than 2x.
            // Averaging 2x2 blocks first brings the image within 2x of the target.
            while (current.Width >= 2 * width && current.Height >= 2 * height)
            {
                PixelBuffer next = Halve(current);
                halved?.Dispose();
                halved = next;
                current = next;
            }
            var result = new PixelBuffer(width, height);
            if (current.Width == width && current.Height == height) result.AsSurface().Blit(current.AsSurface(), 0, 0);
            else result.AsSurface().BlitScaledSmooth(current.AsSurface(), 0, 0, width, height);
            return result;
        }
        finally { halved?.Dispose(); }
    }

    // Half the size, each pixel the average of a 2x2 block. Colours are weighted by alpha, so transparent
    // pixels (whose colour is meaningless) do not darken a soft edge. Opaque blocks, the usual case, take the cheap path.
    private static unsafe PixelBuffer Halve(PixelBuffer source)
    {
        int w = source.Width / 2, h = source.Height / 2;
        var result = new PixelBuffer(w, h);
        Surface src = source.AsSurface(), dst = result.AsSurface();
        for (int y = 0; y < h; y++)
        {
            uint* a = src.Pixels + (long)(2 * y) * src.Stride;
            uint* b = a + src.Stride;
            uint* o = dst.Pixels + (long)y * dst.Stride;
            for (int x = 0; x < w; x++)
            {
                uint p0 = a[2 * x], p1 = a[2 * x + 1], p2 = b[2 * x], p3 = b[2 * x + 1];
                uint a0 = p0 >> 24, a1 = p1 >> 24, a2 = p2 >> 24, a3 = p3 >> 24;
                if ((a0 & a1 & a2 & a3) == 255)
                {
                    uint r = (((p0 >> 16) & 0xFF) + ((p1 >> 16) & 0xFF) + ((p2 >> 16) & 0xFF) + ((p3 >> 16) & 0xFF) + 2) >> 2;
                    uint g = (((p0 >> 8) & 0xFF) + ((p1 >> 8) & 0xFF) + ((p2 >> 8) & 0xFF) + ((p3 >> 8) & 0xFF) + 2) >> 2;
                    uint bl = ((p0 & 0xFF) + (p1 & 0xFF) + (p2 & 0xFF) + (p3 & 0xFF) + 2) >> 2;
                    o[x] = 0xFF000000u | (r << 16) | (g << 8) | bl;
                    continue;
                }
                uint total = a0 + a1 + a2 + a3;
                if (total == 0) { o[x] = 0; continue; }
                uint rr = (((p0 >> 16) & 0xFF) * a0 + ((p1 >> 16) & 0xFF) * a1 + ((p2 >> 16) & 0xFF) * a2 + ((p3 >> 16) & 0xFF) * a3 + total / 2) / total;
                uint gg = (((p0 >> 8) & 0xFF) * a0 + ((p1 >> 8) & 0xFF) * a1 + ((p2 >> 8) & 0xFF) * a2 + ((p3 >> 8) & 0xFF) * a3 + total / 2) / total;
                uint bb = ((p0 & 0xFF) * a0 + (p1 & 0xFF) * a1 + (p2 & 0xFF) * a2 + (p3 & 0xFF) * a3 + total / 2) / total;
                o[x] = (((total + 2) >> 2) << 24) | (rr << 16) | (gg << 8) | bb;
            }
        }
        return result;
    }

    // Makes every pixel opaque by laying it over Theme.Ink, then washes it toward Ink by dim/255.
    private static unsafe void Flatten(PixelBuffer buffer, byte dim)
    {
        Surface s = buffer.AsSurface();
        uint ink = Theme.Ink.Value;
        uint keep = 255u - dim, ir = (ink >> 16) & 0xFF, ig = (ink >> 8) & 0xFF, ib = ink & 0xFF;
        for (int y = 0; y < s.Height; y++)
        {
            uint* row = s.Pixels + (long)y * s.Stride;
            for (int x = 0; x < s.Width; x++)
            {
                uint p = row[x];
                if (p >> 24 != 255) p = Paint.BlendOver(p, 0xFF000000u | (ink & 0x00FFFFFFu));
                if (dim != 0)
                {
                    uint r = ((((p >> 16) & 0xFF) * keep) + ir * dim + 127) / 255;
                    uint g = ((((p >> 8) & 0xFF) * keep) + ig * dim + 127) / 255;
                    uint b = (((p & 0xFF) * keep) + ib * dim + 127) / 255;
                    p = 0xFF000000u | (r << 16) | (g << 8) | b;
                }
                row[x] = p;
            }
        }
    }

    // Lowers alpha in the four corner squares to the corner's coverage; colours stay straight (not premultiplied).
    private static unsafe void ApplyCorners(PixelBuffer buffer, int radius)
    {
        if (radius <= 0) return;
        Surface s = buffer.AsSurface();
        int w = s.Width, h = s.Height;
        for (int j = 0; j < radius; j++)
        {
            for (int i = 0; i < radius; i++)
            {
                float coverage = Paint.CornerCoverage(radius, i, j);
                if (coverage >= 1f) break;   // the rest of this row is inside
                uint alpha = (uint)(255 * coverage + 0.5f);
                SetAlpha(s, i, j, alpha); SetAlpha(s, w - 1 - i, j, alpha);
                SetAlpha(s, i, h - 1 - j, alpha); SetAlpha(s, w - 1 - i, h - 1 - j, alpha);
            }
        }
    }

    private static unsafe void SetAlpha(Surface s, int x, int y, uint alpha)
    {
        uint* p = s.Pixels + (long)y * s.Stride + x;
        *p = (*p & 0x00FFFFFFu) | (alpha << 24);
    }
}
