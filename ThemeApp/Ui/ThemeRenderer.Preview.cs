using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

public sealed partial class ThemeRenderer
{
    // ---- The full-screen preview of a theme -------------------------------------------------------------------------
    //
    // The cover is drawn sharp by Backdrop (it is the picture behind this view). On top of it sit two bands: a soft cap
    // at the top with a simulated home row (Games and Media, five tiles), and a ramp at the bottom with the theme's
    // name and the footer hints. Nothing in either band moves, so each is made once per theme (washed copy of the cover,
    // then the text and shapes drawn into it) and later frames are two plain copies. Washing and drawing text over a
    // 1920 px wide band is several milliseconds on the console, far too much to repeat sixty times a second.

    private const int HomeTopHeight = 352, HomeBottomHeight = 400;
    private const int HomeLabelY = 64, HomeTilesY = 144, HomeTile = 120, HomeTileGap = 24, HomeTitleWidth = 1280;
    // The big title's first letter has more side bearing than the small text has; this pulls it to the margin.
    private const int HomeTitleOptical = 4;
    private static readonly HintItem[] HomeHintItems = [new(PadButton.Cross, "Hide overlay"), new(PadButton.Circle, "Back")];
    private static readonly Color HomeTileFill = Color.FromRgb(20, 30, 46).WithAlpha(214), HomeTileFocusFill = Color.FromRgb(46, 62, 84);

    // How much of the cover stays (0 to 256) under each band, per row; the columns keep it all, so a band is the same across the
    // screen. The rows ease in from the picture, so a band has no visible edge.
    private static readonly ushort[] HomeColumns = BuildHomeColumns(), HomeTopRows = BuildHomeTopRows(), HomeBottomRows = BuildHomeBottomRows();

    // The two bands as pixels, and the theme they were made for (a rescanned library gives new manifest objects).
    private PixelBuffer? _homeTop, _homeBottom;
    private string? _homePath;
    private object? _homeManifest;

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

    private void DrawHomescreen(Surface s, ThemeLibraryEntry? selected, double seconds)
    {
        string key = selected?.Path ?? "empty";
        if (_homeTop is null || _homeBottom is null || _homePath != key || !ReferenceEquals(_homeManifest, selected?.Manifest))
            BuildHomeBands(s, selected, key);
        s.Blit(_homeTop!.AsSurface(), 0, 0);
        s.Blit(_homeBottom!.AsSurface(), 0, Theme.ScreenHeight - HomeBottomHeight);
    }

    /// <summary>Lets go of the two preview bands (about 5 MB); the next preview makes them again. The screens that are not the preview call it.</summary>
    private void ReleasePreviewStrips()
    {
        if (_homeTop is null && _homeBottom is null) return;
        _homeTop?.Dispose();
        _homeBottom?.Dispose();
        _homeTop = _homeBottom = null;
        _homePath = null;
        _homeManifest = null;
    }

    // Makes both bands from what is on <s> now: the sharp cover and nothing else, because Backdrop has just drawn it.
    private void BuildHomeBands(Surface s, ThemeLibraryEntry? selected, string key)
    {
        _homeTop ??= new PixelBuffer(Theme.ScreenWidth, HomeTopHeight);
        _homeBottom ??= new PixelBuffer(Theme.ScreenWidth, HomeBottomHeight);
        int bottomOrigin = Theme.ScreenHeight - HomeBottomHeight;
        Surface top = _homeTop.AsSurface(), bottom = _homeBottom.AsSurface();
        ScaleAndWash(top, s.Region(0, 0, Theme.ScreenWidth, HomeTopHeight), HomeColumns, HomeTopRows, Ink);
        ScaleAndWash(bottom, s.Region(0, bottomOrigin, Theme.ScreenWidth, HomeBottomHeight), HomeColumns, HomeBottomRows, Ink);
        DrawHomeRow(top);
        DrawHomeName(bottom, bottomOrigin, selected?.Manifest.Name ?? "Theme preview");
        // The hints sit where the other screens' footers do, at the right safe edge.
        HintBar(bottom, HomeHintItems, Theme.ContentRight, Theme.FooterY + (Theme.FooterHeight - _small.LineHeight) / 2 - bottomOrigin);
        _homePath = key;
        _homeManifest = selected?.Manifest;
    }

    // The top of a PS5 home screen as a hint of how the theme will look: the two sections, the first one selected, and a
    // row of tiles with the first focused. Plain glass tiles with a simple symbol each; no real titles, no letters.
    private void DrawHomeRow(Surface t)
    {
        int gamesWidth = _heading.MeasureText("Games");
        DrawLeft(_heading, t, "Games", Theme.SafeX, HomeLabelY, White);
        DrawLeft(_heading, t, "Media", Theme.SafeX + gamesWidth + 40, HomeLabelY, Muted);
        Paint.Capsule(t, Theme.SafeX, HomeLabelY + _heading.LineHeight + 4, gamesWidth - 8, 4, White);

        for (int i = 0; i < 5; i++)
        {
            int x = Theme.SafeX + i * (HomeTile + HomeTileGap);
            bool focus = i == 0;
            Paint.FillRoundedRect(t, x, HomeTilesY, HomeTile, HomeTile, Theme.RadiusPanel, focus ? HomeTileFocusFill : HomeTileFill);
            Paint.StrokeRoundedRect(t, x, HomeTilesY, HomeTile, HomeTile, Theme.RadiusPanel, 1, Theme.PanelStroke);
            DrawHomeSymbol(t, i, x + HomeTile / 2f, HomeTilesY + HomeTile / 2f);
        }
        Paint.FocusRing(t, Theme.SafeX, HomeTilesY, HomeTile, HomeTile, Theme.RadiusPanel);

        // The focused tile's name, with a soft shadow so it holds on a bright cover.
        int captionY = HomeTilesY + HomeTile + 16;
        DrawLeft(_small, t, "Prospero Themes", Theme.SafeX + 1, captionY + 2, Ink.WithAlpha(150));
        DrawLeft(_small, t, "Prospero Themes", Theme.SafeX, captionY, White);
    }

    // One simple symbol per tile, about 40 px, centred on (cx, cy): the app's own orbit mark, then a game case, a plus,
    // a play triangle and a grid of apps.
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
            default:
                for (int row = 0; row < 2; row++)
                    for (int column = 0; column < 2; column++)
                        Paint.FillRoundedRect(t, (int)(cx - 20) + column * 24, (int)(cy - 20) + row * 24, 16, 16, 5, ink);
                break;
        }
    }

    // The bottom-left block: the eyebrow over the theme's name, its last line ending on the content's bottom line.
    private void DrawHomeName(Surface bottom, int origin, string name)
    {
        // One line in the big title when it fits; otherwise the next size down, wrapped to two lines.
        AtlasFont font = _title.MeasureText(name) <= HomeTitleWidth ? _title : _screenHeading;
        IReadOnlyList<string> lines = font == _title ? [name] : _screenHeading.Wrap(name, HomeTitleWidth, 2);
        int top = Theme.ContentBottom - lines.Count * font.LineHeight - origin;
        for (int i = 0; i < lines.Count; i++)
            DrawLeft(font, bottom, lines[i], Theme.SafeX - (font == _title ? HomeTitleOptical : 0), top + i * font.LineHeight, White);
        DrawLeft(_small, bottom, "PREVIEW", Theme.SafeX, top - 44, Accent);
    }
}
