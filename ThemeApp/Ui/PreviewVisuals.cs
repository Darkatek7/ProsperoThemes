using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// One picture behind the preview, made once at the size of the screen: the sharp picture, and the two washed bands that
/// the overlay lays over its top and bottom (already mixed with the ink, so the overlay costs a copy and not a blend).
/// Built by <see cref="ThemeRenderer.BuildPreviewPicture"/>, on a worker for a pack's pictures.
/// </summary>
public sealed class PreviewPicture : IDisposable
{
    public PreviewPicture(PixelBuffer full, PixelBuffer top, PixelBuffer bottom)
    {
        Full = full;
        Top = top;
        Bottom = bottom;
    }

    /// <summary>1920x1080, as the picture is.</summary>
    public PixelBuffer Full { get; }
    /// <summary>The first rows of the picture under the top cap (the home row's band).</summary>
    public PixelBuffer Top { get; }
    /// <summary>The last rows of the picture under the bottom ramp (the name and the hints).</summary>
    public PixelBuffer Bottom { get; }

    public void Dispose()
    {
        Full.Dispose();
        Top.Dispose();
        Bottom.Dispose();
    }
}

/// <summary>The art of one tile of the home row: the pack's icon scaled once, or a drawn stand-in. Drawing it is a blit.</summary>
public sealed class TileArt : IDisposable
{
    private readonly CachedImage? _image;
    private readonly PixelBuffer? _sprite;

    public TileArt(CachedImage image) => _image = image;

    /// <summary>A sprite with transparent corners (alpha on its edge), blended onto the screen.</summary>
    public TileArt(PixelBuffer sprite) => _sprite = sprite;

    public void Draw(Surface target, int x, int y)
    {
        if (_image is not null) _image.Draw(target, x, y);
        else if (_sprite is not null) target.BlitBlended(_sprite.AsSurface(), x, y);
    }

    public void Dispose()
    {
        _image?.Dispose();
        _sprite?.Dispose();
    }
}

/// <summary>What <see cref="PreviewVisuals.Load"/> made of a pack's icons, ready to be handed to the renderer on the frame thread.</summary>
public sealed class PreviewLoad : IDisposable
{
    /// <summary>Tile art by tile id: the pack's icon, or for an app tile its background cropped square.</summary>
    public Dictionary<string, TileArt> Tiles { get; } = new(StringComparer.Ordinal);
    /// <summary>Control-centre icons by slot: white on transparent, <see cref="PreviewVisuals.GlyphSize"/> pixels square.</summary>
    public Dictionary<string, PixelBuffer> Glyphs { get; } = new(StringComparer.Ordinal);

    /// <summary>Lets go of what was never handed over (the preview closed while a worker was making it).</summary>
    public void Dispose()
    {
        foreach (TileArt art in Tiles.Values) art.Dispose();
        foreach (PixelBuffer glyph in Glyphs.Values) glyph.Dispose();
        Tiles.Clear();
        Glyphs.Clear();
    }
}

/// <summary>
/// The pixels of the preview that are not the cover: the tiles' art, the control centre's icons and a few pictures behind
/// the tiles (a least-recently-used set, since each is 14 MB). It only holds them; the frame thread adds and reads, a worker
/// builds them (<see cref="Load"/>, <see cref="ThemeRenderer.BuildPreviewPicture"/>) and hands them over. The renderer says
/// which picture it wants next (<see cref="Wanted"/>) and the app loads that one.
/// </summary>
public sealed class PreviewVisuals : IDisposable
{
    /// <summary>Tile art is 128 px square; control-centre icons 64 px.</summary>
    public const int TileSize = 128, GlyphSize = 64;
    /// <summary>
    /// Pictures kept at once: the cover, which is never the one to go (it is the only picture made on the frame thread, and
    /// every system tile comes back to it), and four others, the least recently used of which makes room for a new one.
    /// </summary>
    public const int MaxPictures = 5;

    private readonly Dictionary<string, TileArt> _tiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PixelBuffer> _glyphs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PreviewPicture Picture, ulong Stamp)> _pictures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private ulong _clock;

    /// <summary>The picture key the renderer needs and does not have (and has not failed to get), or null. Read by the app each frame.</summary>
    public string? Wanted { get; set; }

    public int PictureCount => _pictures.Count;
    /// <summary>True when nothing is held: no picture, tile art or icon.</summary>
    public bool IsEmpty => _pictures.Count == 0 && _tiles.Count == 0 && _glyphs.Count == 0;
    public bool HasPicture(string key) => _pictures.ContainsKey(key);
    public bool HasFailed(string key) => _failed.Contains(key);

    /// <summary>The picture for <paramref name="key"/>, marked as just used, or null.</summary>
    public PreviewPicture? Picture(string key)
    {
        if (!_pictures.TryGetValue(key, out var entry)) return null;
        _pictures[key] = (entry.Picture, ++_clock);
        return entry.Picture;
    }

    /// <summary>Keeps <paramref name="picture"/> under <paramref name="key"/>, letting the least recently used one go when there are too many.</summary>
    public void AddPicture(string key, PreviewPicture picture)
    {
        if (_pictures.Remove(key, out var old)) old.Picture.Dispose();
        while (_pictures.Count >= MaxPictures)
        {
            string? oldest = null;
            ulong stamp = ulong.MaxValue;
            foreach (var (k, e) in _pictures) if (k != PreviewModel.CoverKey && e.Stamp < stamp) { stamp = e.Stamp; oldest = k; }
            if (oldest is null) break;   // only the cover is left
            _pictures[oldest].Picture.Dispose();
            _pictures.Remove(oldest);
        }
        _pictures[key] = (picture, ++_clock);
        _failed.Remove(key);
        if (Wanted == key) Wanted = null;
    }

    /// <summary>The picture for <paramref name="key"/> could not be made; the renderer falls back to the next best.</summary>
    public void Fail(string key)
    {
        _failed.Add(key);
        if (Wanted == key) Wanted = null;
    }

    public TileArt? Tile(string id) => _tiles.GetValueOrDefault(id);
    public PixelBuffer? Glyph(string slot) => _glyphs.GetValueOrDefault(slot);

    /// <summary>Keeps art that is not the pack's, e.g. a drawn stand-in, so it is made once.</summary>
    public void SetTile(string id, TileArt art)
    {
        if (_tiles.Remove(id, out var old)) old.Dispose();
        _tiles[id] = art;
    }

    /// <summary>Keeps a drawn stand-in for a control-centre icon, or the pack's render of it (replacing what was there).</summary>
    public void SetGlyph(string slot, PixelBuffer glyph)
    {
        if (_glyphs.Remove(slot, out var old)) old.Dispose();
        _glyphs[slot] = glyph;
    }

    /// <summary>Takes over what a worker made. A tile or icon that is already here is replaced.</summary>
    public void Apply(PreviewLoad load)
    {
        foreach (var (id, art) in load.Tiles) SetTile(id, art);
        foreach (var (slot, glyph) in load.Glyphs) SetGlyph(slot, glyph);
    }

    /// <summary>Lets go of everything (the preview closed, or the library was scanned again).</summary>
    public void Clear()
    {
        foreach (TileArt art in _tiles.Values) art.Dispose();
        foreach (PixelBuffer glyph in _glyphs.Values) glyph.Dispose();
        foreach (var (picture, _) in _pictures.Values) picture.Dispose();
        _tiles.Clear();
        _glyphs.Clear();
        _pictures.Clear();
        _failed.Clear();
        Wanted = null;
    }

    public void Dispose() => Clear();

    // ---- Making the pixels (any thread; nothing here touches the cache) -------------------------------------------

    // Sizes a pack's picture may have before it is decoded: a tile icon is 512 px, a control icon 128, a picture 1280x720.
    private const int MaxTilePixels = 1024, MaxGlyphPixels = 256, MaxPicturePixels = 2560;

    /// <summary>
    /// Decodes and scales the icons of <paramref name="model"/> out of <paramref name="assets"/>: the tile icons (an app's
    /// tile takes its background, cropped square), and the control centre's icons. A file that is missing, too big for what
    /// it is, or that the decoder refuses leaves its tile or button without art; nothing here throws for a bad file.
    /// </summary>
    /// <param name="decode">Turns PNG or JPEG bytes into pixels (the app's decoder; the PC preview's).</param>
    public static PreviewLoad Load(PreviewModel model, PreviewAssets assets, Func<byte[], PixelBuffer> decode)
    {
        var load = new PreviewLoad();
        foreach (PreviewTile tile in AllTiles(model))
        {
            TileArt? art = null;
            if (tile.IconFile is not null && assets.Get(tile.IconFile) is { } png && ImageHeader.PngSize(png) is { } size && size.Width <= MaxTilePixels && size.Height <= MaxTilePixels)
                art = MakeTile(png, decode);
            else if (tile.Kind == PreviewTileKind.App && assets.Get(tile.PictureFile) is { } jpeg && ImageHeader.JpegSize(jpeg) is { } pic && pic.Width <= MaxPicturePixels && pic.Height <= MaxPicturePixels)
                art = MakeTile(jpeg, decode);
            if (art is not null) load.Tiles[tile.Id] = art;
        }
        foreach (PreviewControl control in model.Controls)
        {
            if (assets.Get(control.IconFile) is not { } png || ImageHeader.PngSize(png) is not { } size || size.Width > MaxGlyphPixels || size.Height > MaxGlyphPixels) continue;
            try
            {
                using PixelBuffer source = decode(png);
                // Halving averages the colour by alpha, so the white edge of the icon does not go dark; keep the alpha.
                load.Glyphs[control.Slot] = ImageCache.Scaled(source, GlyphSize, GlyphSize);
            }
            catch { /* the button keeps its drawn stand-in */ }
        }
        return load;
    }

    private static TileArt? MakeTile(byte[] bytes, Func<byte[], PixelBuffer> decode)
    {
        try
        {
            using PixelBuffer source = decode(bytes);
            using PixelBuffer square = CropSquare(source);
            return new TileArt(ImageCache.Prepare(square, TileSize, TileSize, Theme.RadiusPanel));
        }
        catch { return null; }
    }

    /// <summary>The middle square of a picture (the picture itself when it already is one).</summary>
    public static PixelBuffer CropSquare(PixelBuffer source)
    {
        int side = Math.Min(source.Width, source.Height);
        var square = new PixelBuffer(side, side);
        square.AsSurface().Blit(source.AsSurface(), -(source.Width - side) / 2, -(source.Height - side) / 2);
        return square;
    }

    private static IEnumerable<PreviewTile> AllTiles(PreviewModel model)
    {
        foreach (PreviewTile tile in model.Games) yield return tile;
        foreach (PreviewTile tile in model.Media) yield return tile;
    }
}
