using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

public sealed partial class ThemeRenderer
{
    // ---- The full-screen preview of a theme -------------------------------------------------------------------------
    //
    // A stand-in for the console's home screen, made of what the pack brings (PreviewModel): a row of tiles with the
    // pack's own icons, the picture behind the focused tile (an app's own background, the all-apps one, or the cover), and
    // the control centre's row of buttons. The d-pad moves the focus, so the row and the picture change as they do on the
    // console.
    //
    // What is expensive is made once and only copied afterwards. A picture is scaled to the screen once, together with the
    // two washed bands that the overlay lays over its top and bottom (PreviewPicture); a frame at rest is three copies of
    // those. The tiles' art is scaled once to its size (PreviewVisuals). What moves, the row's slide, the focus ring and the
    // text, is drawn each frame and is a few hundred small blits. Changing the picture cross-fades in a few steps: each
    // step mixes the two pictures into one buffer, and the frames between the steps copy that buffer.

    internal const int HomeTopHeight = 352, HomeBottomHeight = 400;
    private const int HomeLabelY = 64, HomeTileGap = 24, HomeTitleWidth = 1280;
    internal const int HomeTilesY = 144, HomeTile = PreviewVisuals.TileSize, HomePitch = HomeTile + HomeTileGap;
    // How many tiles fit between the margins; the row scrolls when the focus goes further.
    private const int HomeVisibleTiles = (Theme.ContentWidth + HomeTileGap) / HomePitch;
    // The big title's first letter has more side bearing than the small text has; this pulls it to the margin.
    private const int HomeTitleOptical = 4;
    // The control centre: a glass panel with the twelve buttons of the console's bottom row in it.
    internal const int CcPanelY = 600, CcPanelHeight = 176, CcDisc = 96, CcPitch = Theme.ContentWidth / 12, CcDiscY = CcPanelY + 24;
    // A picture change is mixed in this many steps over this long; the frames between two steps copy the last mix.
    private const int FadeSteps = 6;
    private const double FadeSeconds = 0.24;
    private static readonly Color HomeTileFill = Color.FromRgb(20, 30, 46).WithAlpha(214), HomeTileFocusFill = Color.FromRgb(46, 62, 84);

    // How much of the picture stays (0 to 256) under each band, per row; the columns keep it all, so a band is the same across the
    // screen. The rows ease in from the picture, so a band has no visible edge.
    private static readonly ushort[] HomeColumns = BuildHomeColumns(), HomeTopRows = BuildHomeTopRows(), HomeBottomRows = BuildHomeBottomRows();

    /// <summary>The pictures, tile art and icons of the preview that is open; the app loads them and hands them over (see <see cref="PreviewVisuals"/>).</summary>
    public PreviewVisuals Preview { get; } = new();

    /// <summary>Whether the theme's music is playing, for the hint (the app owns the sound).</summary>
    public bool PreviewMusicPlaying { get; set; }

    // The model of the pack on show, worked out once per manifest object (a rescanned library gives new ones).
    private PreviewModel? _previewModel;
    private object? _previewModelFor;
    // The picture on screen, the one it fades from and to, and the mix between them.
    private string? _shownKey, _fadeFrom, _fadeTo;
    private double _fadeStart;
    private int _fadeStage = -1;
    private bool _fadeOverlay;
    private PixelBuffer? _fadeBuffer;
    // The row's focus as the slide has it (in tiles, eased toward the browser's), and the section it belongs to.
    private float _rowPos;
    private PreviewTab _rowTab;
    private bool _rowSnap = true;

    private static float HomeSmooth(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }
    private static ushort HomeKeep(float alpha) => (ushort)Math.Round(256f * (1f - alpha / 255f));

    private static ushort[] BuildHomeColumns()
    {
        var keep = new ushort[Theme.ScreenWidth];
        Array.Fill(keep, (ushort)256);
        return keep;
    }

    private static ushort[] BuildHomeTopRows()
    {
        var keep = new ushort[HomeTopHeight];
        for (int y = 0; y < keep.Length; y++) keep[y] = HomeKeep(150f * (1f - HomeSmooth(y / (HomeTopHeight - 1f))));
        return keep;
    }

    private static ushort[] BuildHomeBottomRows()
    {
        var keep = new ushort[HomeBottomHeight];
        for (int y = 0; y < keep.Length; y++) keep[y] = HomeKeep(232f * HomeSmooth(y / (HomeBottomHeight - 1f) / 0.55f));
        return keep;
    }

    /// <summary>
    /// What the preview shows for a pack, worked out from its manifest (cached for the manifest object). The app uses the
    /// same model to load the pack's pictures, icons and sounds and to tell the browser how long the rows are.
    /// </summary>
    public PreviewModel PreviewModelFor(ThemeLibraryEntry? entry)
    {
        if (_previewModel is null || !ReferenceEquals(_previewModelFor, entry?.Manifest))
        {
            _previewModel = entry is null ? PreviewModel.Of(NoManifest) : PreviewModel.Of(entry.Manifest);
            _previewModelFor = entry?.Manifest;
        }
        return _previewModel;
    }

    private static readonly ProsperoThemes.Shared.ThemePackManifest NoManifest = new(2, "none", "", "", "", "", "keep", Array.Empty<ProsperoThemes.Shared.ThemePackTarget>());

    private void DrawPreview(Surface s, ThemeBrowser browser, ThemeLibraryEntry? selected, double seconds)
    {
        PreviewModel model = PreviewModelFor(selected);
        IReadOnlyList<PreviewTile> tiles = model.Tiles(browser.PreviewTab);
        int focus = Math.Clamp(browser.PreviewFocus, 0, tiles.Count - 1);
        PreviewTile tile = tiles[focus];

        ShowPicture(s, browser, selected, model, tile, seconds);
        if (!browser.ShowOverlay) return;

        if (browser.PreviewScene == PreviewScene.Home) DrawHomeRow(s, browser.PreviewTab, tiles, focus);
        else DrawControlCentre(s, model, Math.Clamp(browser.PreviewButton, 0, model.Controls.Count - 1));
        DrawHomeName(s, 0, selected?.Manifest.Name ?? "Theme preview");
        HintBar(s, PreviewHints(browser, model), Theme.ContentRight, Theme.FooterY + (Theme.FooterHeight - _small.LineHeight) / 2);
    }

    // The footer: at most four hints. The d-pad moves without being listed; L1 and R1 sit beside the section names. The
    // lists are made once (scene x music present x music playing), so a frame allocates nothing.
    private static readonly HintItem[][] PreviewHintSets = BuildPreviewHints();

    /// <summary>Every footer the preview can show, for the tests that count and read the hints.</summary>
    internal static IReadOnlyList<HintItem[]> AllPreviewHints => PreviewHintSets;

    private static HintItem[][] BuildPreviewHints()
    {
        var sets = new HintItem[2 * 3][];
        for (int scene = 0; scene < 2; scene++)
            for (int music = 0; music < 3; music++)   // 0 none in the pack, 1 can play, 2 playing
            {
                var hints = new List<HintItem>(4)
                {
                    new(PadButton.Cross, "Hide overlay"),
                    new(PadButton.Triangle, scene == 0 ? "Control centre" : "Home screen"),
                };
                if (music > 0) hints.Add(new(PadButton.Square, music == 2 ? "Stop music" : "Music"));
                hints.Add(new(PadButton.Circle, "Back"));
                sets[scene * 3 + music] = [.. hints];
            }
        return sets;
    }

    private HintItem[] PreviewHints(ThemeBrowser browser, PreviewModel model) =>
        PreviewHintSets[(browser.PreviewScene == PreviewScene.Home ? 0 : 3) + (!model.HasMusic ? 0 : PreviewMusicPlaying ? 2 : 1)];

    // ---- The picture -----------------------------------------------------------------------------------------------

    // Decides which picture the focused tile wants, starts a fade when it is not the one on screen, and draws the picture
    // (with the overlay's bands when the overlay is shown). A picture that is not here yet is asked for through
    // Preview.Wanted and the one on screen stays until it arrives.
    private void ShowPicture(Surface s, ThemeBrowser browser, ThemeLibraryEntry? selected, PreviewModel model, PreviewTile tile, double seconds)
    {
        string target = PreviewModel.CoverKey;
        IReadOnlyList<string> keys = tile.PictureKeys;
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i] != PreviewModel.CoverKey && Preview.HasFailed(keys[i])) continue;
            target = keys[i];
            break;
        }
        if (target != PreviewModel.CoverKey && !Preview.HasPicture(target))
        {
            Preview.Wanted = target;
            // The picture on screen stays; before there is one, the cover is shown while this one loads.
            string? hold = _fadeTo ?? _shownKey;
            target = hold is not null && Preview.HasPicture(hold) ? hold : PreviewModel.CoverKey;
        }
        if (target == PreviewModel.CoverKey) EnsureCover(selected);
        bool overlay = browser.ShowOverlay;

        // The first picture appears at once; after that a different target starts a fade from what is on screen.
        if (_shownKey is null || !Preview.HasPicture(_shownKey))
        {
            _shownKey = target;
            _fadeFrom = _fadeTo = null;
        }
        else if (target != (_fadeTo ?? _shownKey))
        {
            if (_fadeTo is not null) _shownKey = _fadeTo;
            if (_shownKey != target && Preview.HasPicture(_shownKey))
            {
                _fadeFrom = _shownKey;
                _fadeTo = target;
                _fadeStart = seconds;
                _fadeStage = -1;
            }
            else
            {
                _shownKey = target;
                _fadeFrom = _fadeTo = null;
            }
        }

        if (_fadeTo is not null)
        {
            int stage = (int)Math.Floor((seconds - _fadeStart) / (FadeSeconds / FadeSteps));
            PreviewPicture? from = Preview.Picture(_fadeFrom!), to = Preview.Picture(_fadeTo);
            if (stage >= FadeSteps || from is null || to is null)
            {
                _shownKey = _fadeTo;
                _fadeFrom = _fadeTo = null;
            }
            else
            {
                stage = Math.Max(stage, 0);
                if (stage != _fadeStage || overlay != _fadeOverlay)
                {
                    _fadeBuffer ??= new PixelBuffer(Theme.ScreenWidth, Theme.ScreenHeight);
                    MixPictures(_fadeBuffer, from, to, 256 * stage / FadeSteps, overlay);
                    _fadeStage = stage;
                    _fadeOverlay = overlay;
                }
                s.Blit(_fadeBuffer!.AsSurface(), 0, 0);
                return;
            }
        }

        PreviewPicture shown = Preview.Picture(_shownKey)!;
        if (!overlay)
        {
            s.Blit(shown.Full.AsSurface(), 0, 0);
            return;
        }
        s.Blit(shown.Top.AsSurface(), 0, 0);
        s.Blit(shown.Full.AsSurface().Region(0, HomeTopHeight, Theme.ScreenWidth, Theme.ScreenHeight - HomeTopHeight - HomeBottomHeight), 0, HomeTopHeight);
        s.Blit(shown.Bottom.AsSurface(), 0, Theme.ScreenHeight - HomeBottomHeight);
    }

    // The theme's cover as the first picture, made on the frame thread like the covers of the other screens (the pack's own
    // pictures are made on the worker). It is a quick nearest-neighbour stretch, so it is here at once; the preview controller
    // replaces it with a smooth one made on the worker (PreviewController.LoadSmoothCover).
    private void EnsureCover(ThemeLibraryEntry? selected)
    {
        if (Preview.HasPicture(PreviewModel.CoverKey)) return;
        Preview.AddPicture(PreviewModel.CoverKey, BuildPreviewPicture(selected is null ? _fallback : Cover(selected), smooth: false));
    }

    /// <summary>
    /// Makes the layers of a preview picture from a decoded image of any size: scaled to the screen (bilinear when
    /// <paramref name="smooth"/>, which is slower but right for a photograph) and its two bands washed. The source is not
    /// kept or changed. Pure computation, so a worker thread may call it.
    /// </summary>
    /// <param name="consume">
    /// The caller does not need <paramref name="source"/> any more: a source that already has the screen's size becomes the
    /// picture's own pixels (no 8 MB copy), any other is disposed once it is scaled, and it is disposed if this throws.
    /// </param>
    internal static PreviewPicture BuildPreviewPicture(PixelBuffer source, bool smooth, bool consume = false)
    {
        PixelBuffer full;
        try
        {
            if (source.Width == Theme.ScreenWidth && source.Height == Theme.ScreenHeight && consume) full = source;
            else if (source.Width == Theme.ScreenWidth && source.Height == Theme.ScreenHeight)
            {
                full = new PixelBuffer(Theme.ScreenWidth, Theme.ScreenHeight);
                full.AsSurface().Blit(source.AsSurface(), 0, 0);
            }
            else if (smooth) full = ImageCache.Scaled(source, Theme.ScreenWidth, Theme.ScreenHeight);
            else
            {
                full = new PixelBuffer(Theme.ScreenWidth, Theme.ScreenHeight);
                full.AsSurface().BlitScaled(source.AsSurface(), 0, 0, Theme.ScreenWidth, Theme.ScreenHeight);
            }
        }
        catch
        {
            if (consume) source.Dispose();
            throw;
        }
        if (consume && !ReferenceEquals(full, source)) source.Dispose();
        int bottomOrigin = Theme.ScreenHeight - HomeBottomHeight;
        var top = new PixelBuffer(Theme.ScreenWidth, HomeTopHeight);
        var bottom = new PixelBuffer(Theme.ScreenWidth, HomeBottomHeight);
        Surface sharp = full.AsSurface();
        ScaleAndWash(top.AsSurface(), sharp.Region(0, 0, Theme.ScreenWidth, HomeTopHeight), HomeColumns, HomeTopRows, Ink);
        ScaleAndWash(bottom.AsSurface(), sharp.Region(0, bottomOrigin, Theme.ScreenWidth, HomeBottomHeight), HomeColumns, HomeBottomRows, Ink);
        return new PreviewPicture(full, top, bottom);
    }

    // Mixes two pictures into <target>, k/256 of the second: rows of the bands from the washed bands, the rest from the
    // sharp pictures (or all rows from the sharp ones when the overlay is hidden). Every pixel is mixed exactly once.
    internal static unsafe void MixPictures(PixelBuffer target, PreviewPicture from, PreviewPicture to, int k, bool overlay)
    {
        int width = Theme.ScreenWidth, bottomOrigin = Theme.ScreenHeight - HomeBottomHeight;
        uint* dst = target.AsSurface().Pixels, a = from.Full.AsSurface().Pixels, b = to.Full.AsSurface().Pixels;
        if (!overlay)
        {
            Mix(dst, a, b, width * Theme.ScreenHeight, (uint)k);
            return;
        }
        Mix(dst, from.Top.AsSurface().Pixels, to.Top.AsSurface().Pixels, width * HomeTopHeight, (uint)k);
        long middle = (long)width * HomeTopHeight;
        Mix(dst + middle, a + middle, b + middle, width * (bottomOrigin - HomeTopHeight), (uint)k);
        long last = (long)width * bottomOrigin;
        Mix(dst + last, from.Bottom.AsSurface().Pixels, to.Bottom.AsSurface().Pixels, width * HomeBottomHeight, (uint)k);
    }

    // out = (a * (256 - k) + b * k) / 256 for k in 0..256, red and blue together and green apart, two pixels per step in
    // one 64-bit word (each 16-bit lane holds at most 255 * 256, so lanes never spill into each other). The pictures are
    // opaque; the result is. <count> is a whole number of rows of 1920, so it is even.
    private static unsafe void Mix(uint* dst, uint* a, uint* b, int count, uint k)
    {
        const ulong redBlue = 0x00FF00FF00FF00FFUL, green = 0x0000FF000000FF00UL, opaque = 0xFF000000FF000000UL;
        ulong keep = 256 - k, weight = k;
        ulong* to = (ulong*)dst, from = (ulong*)a, with = (ulong*)b;
        for (int i = 0; i < count / 2; i++)
        {
            ulong p = from[i], q = with[i];
            ulong rb = (((p & redBlue) * keep + (q & redBlue) * weight) >> 8) & redBlue;
            ulong g = (((p >> 8) & redBlue) * keep + ((q >> 8) & redBlue) * weight) & green;
            to[i] = opaque | rb | g;
        }
    }

    /// <summary>
    /// Lets go of the pictures, tile art, icons and the mix of the preview (about 15 MB a picture); the next preview makes
    /// them again. Drawn views other than the preview do it by themselves; the controller does it when a preview closes, so
    /// a preview of another pack, opened before any other frame is drawn, does not start with this one's cover.
    /// </summary>
    public void ReleasePreview()
    {
        if (_shownKey is null && _fadeBuffer is null && Preview.IsEmpty) return;
        Preview.Clear();
        _fadeBuffer?.Dispose();
        _fadeBuffer = null;
        _shownKey = _fadeFrom = _fadeTo = null;
        _fadeStage = -1;
        _rowSnap = true;
    }

    // ---- The home row ----------------------------------------------------------------------------------------------

    private void DrawHomeRow(Surface s, PreviewTab tab, IReadOnlyList<PreviewTile> tiles, int focus)
    {
        // The sections: the open one white with its underline, the other muted; L1 and R1 switch.
        int gamesWidth = _heading.MeasureText("Games"), mediaWidth = _heading.MeasureText("Media");
        bool games = tab == PreviewTab.Games;
        DrawLeft(_heading, s, "Games", Theme.SafeX, HomeLabelY, games ? White : Muted);
        int mediaX = Theme.SafeX + gamesWidth + 40;
        DrawLeft(_heading, s, "Media", mediaX, HomeLabelY, games ? Muted : White);
        Paint.Capsule(s, games ? Theme.SafeX : mediaX, HomeLabelY + _heading.LineHeight + 4, (games ? gamesWidth : mediaWidth) - 8, 4, White);
        int glyphY = HomeLabelY + (_heading.LineHeight - GlyphSize) / 2;
        int hintX = mediaX + mediaWidth + 40;
        hintX += ButtonGlyph.Draw(s, PadButton.L1, hintX, glyphY, GlyphSize, Ink, Muted, _small) + 12;
        ButtonGlyph.Draw(s, PadButton.R1, hintX, glyphY, GlyphSize, Ink, Muted, _small);

        // The slide: the focus eases toward its tile, and the row scrolls when it goes past the last tile that fits.
        if (_rowSnap || _rowTab != tab) { _rowPos = focus; _rowSnap = false; _rowTab = tab; }
        else
        {
            float distance = focus - _rowPos;
            _rowPos = MathF.Abs(distance) < 0.01f ? focus : _rowPos + distance * (1f - MathF.Exp(-18f * (float)_frameDelta));
        }
        float scroll = MathF.Max(0f, _rowPos - (HomeVisibleTiles - 1));
        for (int i = 0; i < tiles.Count; i++)
        {
            int x = Theme.SafeX + (int)MathF.Round((i - scroll) * HomePitch);
            if (x + HomeTile < 0 || x > Theme.ScreenWidth) continue;
            TileArtFor(tiles[i]).Draw(s, x, HomeTilesY);
        }

        int ringX = Theme.SafeX + (int)MathF.Round((_rowPos - scroll) * HomePitch);
        Paint.FocusRing(s, ringX, HomeTilesY, HomeTile, HomeTile, Theme.RadiusPanel);

        // The focused tile's name under it, centred on the ring but kept between the margins, on a dark pill so it holds on a
        // bright picture (the cap's wash has faded out by this row).
        string name = tiles[focus].Name;
        int width = _body.MeasureText(name);
        int nameX = Math.Clamp(ringX + HomeTile / 2 - width / 2, Theme.SafeX, Theme.ContentRight - width);
        int nameY = HomeTilesY + HomeTile + 24;
        Paint.Capsule(s, nameX - 18, nameY - 2, width + 36, _body.LineHeight + 4, Ink.WithAlpha(120));
        DrawLeft(_body, s, name, nameX, nameY, White);
    }

    // The pack's art for a tile, or a drawn stand-in made once.
    private TileArt TileArtFor(PreviewTile tile)
    {
        if (Preview.Tile(tile.Id) is { } art) return art;
        var sprite = new PixelBuffer(HomeTile, HomeTile);
        Surface t = sprite.AsSurface();
        Color fill = tile.Kind == PreviewTileKind.Demo ? HueColor(tile.Hue) : HomeTileFill;
        Paint.FillRoundedRect(t, 0, 0, HomeTile, HomeTile, Theme.RadiusPanel, fill);
        Paint.StrokeRoundedRect(t, 0, 0, HomeTile, HomeTile, Theme.RadiusPanel, 1, Theme.PanelStroke);
        DrawHomeSymbol(t, tile.Symbol, HomeTile / 2f, HomeTile / 2f);
        art = new TileArt(sprite);
        Preview.SetTile(tile.Id, art);
        return art;
    }

    // A dark, muted colour of the given hue (degrees) for a stand-in app's tile.
    private static Color HueColor(int hue)
    {
        const float saturation = 0.42f, lightness = 0.30f;
        float c = (1f - MathF.Abs(2f * lightness - 1f)) * saturation, h = (hue % 360) / 60f;
        float x = c * (1f - MathF.Abs(h % 2f - 1f)), m = lightness - c / 2f;
        (float r, float g, float b) = (int)h switch
        {
            0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x),
        };
        return Color.FromRgb((byte)((r + m) * 255f), (byte)((g + m) * 255f), (byte)((b + m) * 255f));
    }

    // One simple symbol per stand-in tile, about 40 px, centred on (cx, cy): the app's own orbit mark, a game case, a plus,
    // a play triangle, a grid of apps and a note.
    private static void DrawHomeSymbol(Surface t, int index, float cx, float cy)
    {
        Color ink = Theme.White.WithAlpha(210);
        switch (index)
        {
            case 0:
                Paint.StrokeCircle(t, cx, cy, 20.5f, 2.5f, White);
                Paint.FillCircle(t, cx + 14f, cy - 15f, 6.5f, Accent);
                break;
            case 1:
                Paint.StrokeRoundedRect(t, (int)(cx - 16), (int)(cy - 20), 32, 40, 8, 3, ink);
                Paint.FillCircle(t, cx, cy, 5f, ink);
                break;
            case 2:
                Paint.Line(t, cx - 16f, cy, cx + 16f, cy, 3f, ink);
                Paint.Line(t, cx, cy - 16f, cx, cy + 16f, 3f, ink);
                break;
            case 3:
                Paint.Line(t, cx - 10f, cy - 17f, cx + 17f, cy, 3f, ink);
                Paint.Line(t, cx + 17f, cy, cx - 10f, cy + 17f, 3f, ink);
                Paint.Line(t, cx - 10f, cy + 17f, cx - 10f, cy - 17f, 3f, ink);
                break;
            case 5:
                Paint.FillCircle(t, cx - 8f, cy + 13f, 7f, ink);
                Paint.Line(t, cx - 1f, cy + 13f, cx - 1f, cy - 17f, 4f, ink);
                Paint.Line(t, cx - 1f, cy - 17f, cx + 14f, cy - 9f, 4f, ink);
                break;
            default:
                for (int row = 0; row < 2; row++)
                    for (int column = 0; column < 2; column++)
                        Paint.FillRoundedRect(t, (int)(cx - 20) + column * 24, (int)(cy - 20) + row * 24, 16, 16, 5, ink);
                break;
        }
    }

    // ---- The control centre ----------------------------------------------------------------------------------------

    private void DrawControlCentre(Surface s, PreviewModel model, int focus)
    {
        DrawLeft(_heading, s, "Control centre", Theme.SafeX, HomeLabelY, White);
        Paint.Panel(s, Theme.SafeX, CcPanelY, Theme.ContentWidth, CcPanelHeight);
        for (int i = 0; i < model.Controls.Count; i++)
        {
            float cx = Theme.SafeX + i * CcPitch + CcPitch / 2f;
            int x = (int)(cx - CcDisc / 2f);
            Paint.FillCircle(s, cx, CcDiscY + CcDisc / 2f, CcDisc / 2f, i == focus ? HomeTileFocusFill : Ink.WithAlpha(120));
            Paint.StrokeCircle(s, cx, CcDiscY + CcDisc / 2f, CcDisc / 2f, 1f, Theme.PanelStroke);
            PixelBuffer glyph = GlyphFor(model.Controls[i], i);
            s.BlitBlended(glyph.AsSurface(), (int)(cx - glyph.Width / 2f), CcDiscY + (CcDisc - glyph.Height) / 2);
        }
        float fx = Theme.SafeX + focus * CcPitch + CcPitch / 2f;
        Paint.FocusRing(s, (int)(fx - CcDisc / 2f), CcDiscY, CcDisc, CcDisc, CcDisc / 2);
        string name = model.Controls[focus].Name;
        DrawMid(_small, s, name, (int)fx, CcDiscY + CcDisc + 16, White);
    }

    // The pack's render of the icon, or a drawn stand-in made once.
    private PixelBuffer GlyphFor(PreviewControl control, int index)
    {
        if (Preview.Glyph(control.Slot) is { } glyph) return glyph;
        var sprite = new PixelBuffer(PreviewVisuals.GlyphSize, PreviewVisuals.GlyphSize);
        DrawControlSymbol(sprite.AsSurface(), index, PreviewVisuals.GlyphSize / 2f, PreviewVisuals.GlyphSize / 2f);
        Preview.SetGlyph(control.Slot, sprite);
        return sprite;
    }

    // Neutral stand-ins for the twelve buttons, white and about 36 px, centred on (cx, cy), in the console's order.
    private static void DrawControlSymbol(Surface t, int index, float cx, float cy)
    {
        Color c = White;
        switch (index)
        {
            case 0:   // Home: a house
                Paint.Line(t, cx - 18f, cy - 1f, cx, cy - 18f, 3.5f, c);
                Paint.Line(t, cx, cy - 18f, cx + 18f, cy - 1f, 3.5f, c);
                Paint.StrokeRoundedRect(t, (int)cx - 12, (int)cy - 2, 24, 20, 3, 3, c);
                break;
            case 1:   // Switcher: two cards
                Paint.StrokeRoundedRect(t, (int)cx - 18, (int)cy - 14, 24, 22, 4, 3, c);
                Paint.StrokeRoundedRect(t, (int)cx - 6, (int)cy - 6, 24, 22, 4, 3, c);
                break;
            case 2:   // Notifications: a bell
                Paint.FillRoundedRect(t, (int)cx - 13, (int)cy - 17, 26, 28, 13, c);
                Paint.FillRect(t, (int)cx - 17, (int)cy + 7, 34, 5, c);
                Paint.FillCircle(t, cx, cy + 18f, 4f, c);
                break;
            case 3:   // Game Base: two players
                Paint.StrokeCircle(t, cx - 9f, cy - 6f, 7.5f, 3f, c);
                Paint.StrokeCircle(t, cx + 9f, cy - 6f, 7.5f, 3f, c);
                Paint.StrokeRoundedRect(t, (int)cx - 21, (int)cy + 6, 20, 12, 6, 3, c);
                Paint.StrokeRoundedRect(t, (int)cx + 1, (int)cy + 6, 20, 12, 6, 3, c);
                break;
            case 4:   // Music: a note
                Paint.FillCircle(t, cx - 8f, cy + 12f, 7f, c);
                Paint.Line(t, cx - 1f, cy + 12f, cx - 1f, cy - 18f, 4f, c);
                Paint.Line(t, cx - 1f, cy - 18f, cx + 14f, cy - 10f, 4f, c);
                break;
            case 5:   // Downloads: an arrow down onto a line
                Paint.Line(t, cx, cy - 18f, cx, cy + 8f, 4f, c);
                Paint.Line(t, cx - 10f, cy - 2f, cx, cy + 9f, 4f, c);
                Paint.Line(t, cx + 10f, cy - 2f, cx, cy + 9f, 4f, c);
                Paint.FillRect(t, (int)cx - 16, (int)cy + 16, 32, 4, c);
                break;
            case 6:   // Share Screen: an arrow out of a box
                Paint.StrokeRoundedRect(t, (int)cx - 18, (int)cy - 2, 36, 22, 5, 3, c);
                Paint.Line(t, cx, cy + 8f, cx, cy - 20f, 4f, c);
                Paint.Line(t, cx - 9f, cy - 11f, cx, cy - 20f, 4f, c);
                Paint.Line(t, cx + 9f, cy - 11f, cx, cy - 20f, 4f, c);
                break;
            case 7:   // Sound: a speaker and a wave
                Paint.FillRect(t, (int)cx - 18, (int)cy - 6, 9, 12, c);
                Paint.Line(t, cx - 10f, cy - 6f, cx + 3f, cy - 16f, 3.5f, c);
                Paint.Line(t, cx + 3f, cy - 16f, cx + 3f, cy + 16f, 3.5f, c);
                Paint.Line(t, cx + 3f, cy + 16f, cx - 10f, cy + 6f, 3.5f, c);
                Paint.Line(t, cx + 13f, cy - 8f, cx + 13f, cy + 8f, 3.5f, c);
                break;
            case 8:   // Mic: a microphone
                Paint.FillRoundedRect(t, (int)cx - 7, (int)cy - 19, 14, 26, 7, c);
                Paint.StrokeRoundedRect(t, (int)cx - 14, (int)cy - 8, 28, 22, 11, 3, c);
                Paint.Line(t, cx, cy + 14f, cx, cy + 21f, 3.5f, c);
                Paint.FillRect(t, (int)cx - 8, (int)cy + 20, 16, 3, c);
                break;
            case 9:   // Accessories: a controller
                Paint.StrokeRoundedRect(t, (int)cx - 24, (int)cy - 12, 48, 26, 13, 3, c);
                Paint.Line(t, cx - 13f, cy, cx - 5f, cy, 3f, c);
                Paint.Line(t, cx - 9f, cy - 4f, cx - 9f, cy + 4f, 3f, c);
                Paint.FillCircle(t, cx + 8f, cy - 1f, 2.5f, c);
                Paint.FillCircle(t, cx + 14f, cy + 3f, 2.5f, c);
                break;
            case 10:   // Profile: a head and shoulders
                Paint.FillCircle(t, cx, cy - 8f, 8.5f, c);
                Paint.FillRoundedRect(t, (int)cx - 17, (int)cy + 4, 34, 17, 8, c);
                break;
            default:   // Power: a ring with a bar
                Paint.StrokeCircle(t, cx, cy + 2f, 16f, 3.5f, c);
                Paint.Line(t, cx, cy - 20f, cx, cy - 2f, 4f, c);
                break;
        }
    }

    // The bottom-left block: the eyebrow over the theme's name, its last line ending on the content's bottom line.
    private void DrawHomeName(Surface s, int origin, string name)
    {
        // One line in the big title when it fits; otherwise the next size down, wrapped to two lines. Worked out once per name.
        if (_nameLines is null || _nameFor != name)
        {
            _nameFont = _title.MeasureText(name) <= HomeTitleWidth ? _title : _screenHeading;
            _nameLines = _nameFont == _title ? [name] : _screenHeading.Wrap(name, HomeTitleWidth, 2);
            _nameFor = name;
        }
        AtlasFont font = _nameFont!;
        IReadOnlyList<string> lines = _nameLines;
        int top = Theme.ContentBottom - lines.Count * font.LineHeight - origin;
        for (int i = 0; i < lines.Count; i++)
            DrawLeft(font, s, lines[i], Theme.SafeX - (font == _title ? HomeTitleOptical : 0), top + i * font.LineHeight, White);
        DrawLeft(_small, s, "PREVIEW", Theme.SafeX, top - 44, Accent);
    }

    private string? _nameFor;
    private IReadOnlyList<string>? _nameLines;
    private AtlasFont? _nameFont;
}
