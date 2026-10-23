using System;
using System.Collections.Generic;
using System.Linq;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>The same 1920x1080 composition is drawn on the console and in the PC preview.</summary>
public sealed partial class ThemeRenderer : IDisposable
{
    // The colours live in Theme; these short names keep the drawing code readable.
    private static readonly Color Ink = Theme.Ink;
    private static readonly Color White = Theme.White;
    private static readonly Color Muted = Theme.Muted;
    private static readonly Color Accent = Theme.Accent;
    private static readonly Color PillInk = Theme.PillInk;
    private static readonly Color Warn = Theme.Warn;
    private static readonly Color KeyFill = Theme.KeyFill;
    private const int GlyphSize = 30, GlyphGap = 12, HintGap = 32;
    // The type scale (Theme.TypeScale): Caption 22/400, Body 28/400, Section 36/600, Hero 76/600, Heading 48/600, Label 28/600.
    private readonly AtlasFont _small, _body, _heading, _title, _screenHeading, _label;
    private readonly Func<byte[], PixelBuffer> _decode;
    // Covers scaled once to the size they are drawn at, with rounded corners; library thumbnails (also the dimmed
    // variant), the detail cover, and the store cards. Keyed by pack path / store id.
    private readonly ImageCache _libraryImages = new(maxEntries: 48, maxBytes: 24L << 20);
    private readonly ImageCache _communityImages = new(maxEntries: 24, maxBytes: 12L << 20);
    private const string FallbackKey = "<fallback>";
    private readonly Dictionary<string, PixelBuffer> _covers = new();
    private readonly Queue<string> _coverOrder = new();
    private readonly HashSet<string> _failedCovers = new();
    private readonly PixelBuffer _fallback;
    // The sharp full-screen cover of the selected theme (with or without the Library's scrim); one slot, rebuilt when the
    // selection or the look changes. The other screens use the blurred backdrops of _libraryImages instead.
    private PixelBuffer? _backdrop;
    private string? _backdropPath;
    private bool _backdropScrim;
    // The blurred backdrop with the keyboard screen's calming wash already applied; derived from it, so it is rebuilt
    // whenever the selection changes. Saves a full-screen blend on every frame of the screen where the user types.
    private PixelBuffer? _calmBackdrop;
    private string? _calmBackdropPath;
    private ThemeView _previousView;
    private double _viewStart;

    /// <summary>A short status line from the app. The header no longer draws it (it only shows a spinner while <see cref="Busy"/>); kept so callers need no change.</summary>
    public string Status { get; set; } = "Local library";
    public bool Busy { get; set; }
    /// <summary>The result of the last import, shown on the USB Import screen. Other outcomes go to <see cref="Notify"/>.</summary>
    public string LibraryStatus { get; set; } = "";
    /// <summary>
    /// The manifest id of the theme the console currently shows (from state.json), or null when none is applied
    /// (never applied, or restored since). Library cards and the detail cover of that pack carry an "Applied" badge.
    /// </summary>
    public string? AppliedId { get; set; }
    /// <summary>The background-scope setting shown on the System screen (see ThemeApplier.IncludeAllApps).</summary>
    public bool AllAppsBackgrounds { get; set; }
    /// <summary>The earlier-themes setting shown on the System screen (see ThemeApplier.KeepEarlierThemes).</summary>
    public bool ReplaceEarlierThemes { get; set; }
    /// <summary>The Music and sounds setting (System tab): whether themes may change them.</summary>
    public bool Sounds { get; set; } = true;
    /// <summary>The system software version as the console reports it ("13.000.000"); empty when it could not be read.</summary>
    public string Firmware { get; set; } = "";

    /// <summary>The community store's current list (Community tab), newest answer from the server.</summary>
    public IReadOnlyList<CommunityTheme> Community { get; set; } = Array.Empty<CommunityTheme>();
    /// <summary>Preview PNGs by theme id, filled in as they arrive; decoded here on first draw.</summary>
    public Dictionary<string, byte[]> CommunityPreviews { get; } = new();
    /// <summary>Store ids whose pack is already in the library; their cards carry an "In library" badge.</summary>
    public IReadOnlySet<string> CommunityInLibrary { get; set; } = new HashSet<string>();
    /// <summary>Store ids with a newer version than the library holds; their cards carry an "Update" badge.</summary>
    public IReadOnlySet<string> CommunityUpdates { get; set; } = new HashSet<string>();
    /// <summary>The community card under the cursor, for the footer's Download/Update hint.</summary>
    public string? SelectedCommunityId { get; set; }
    /// <summary>The Community tab's empty or error state ("No themes loaded", "Can't reach the store"); drawn only while the list is empty.</summary>
    public string CommunityStatus { get; set; } = "";
    private readonly HashSet<string> _failedThumbs = new();
    // The one-line "what it changes" text of store cards, by theme object: a theme that is replaced (a like or a
    // download count came in) is a new object and is formatted again.
    private readonly Dictionary<CommunityTheme, string> _communityContents = new(ReferenceEqualityComparer.Instance);
    // PackSummary of the library entries, by path; dropped with the covers whenever the library is scanned again.
    private readonly Dictionary<string, PackSummary> _summaries = new();

    /// <summary>Drops the store's card images and any pending previews (a new list was loaded).</summary>
    public void ResetCommunity()
    {
        _communityImages.Clear();
        _failedThumbs.Clear();
        _communityContents.Clear();
        CommunityPreviews.Clear();
    }

    private string ContentsOf(CommunityTheme theme)
    {
        if (_communityContents.TryGetValue(theme, out string? text)) return text;
        if (_communityContents.Count >= 96) _communityContents.Clear();
        return _communityContents[theme] = theme.Contents;
    }

    private PackSummary SummaryOf(ThemeLibraryEntry entry)
    {
        if (_summaries.TryGetValue(entry.Path, out PackSummary? summary)) return summary;
        return _summaries[entry.Path] = PackSummary.Of(entry.Manifest);
    }

    public ThemeRenderer(Func<string, byte[]> readAsset, Func<byte[], PixelBuffer> decode)
    {
        _small = new AtlasFont(readAsset(Theme.TypeScale.Caption.File));
        _body = new AtlasFont(readAsset(Theme.TypeScale.Body.File));
        _heading = new AtlasFont(readAsset(Theme.TypeScale.Section.File));
        _title = new AtlasFont(readAsset(Theme.TypeScale.Hero.File));
        _screenHeading = new AtlasFont(readAsset(Theme.TypeScale.Heading.File));
        _label = new AtlasFont(readAsset(Theme.TypeScale.Label.File));
        _decode = decode;
        _fallback = new PixelBuffer(960, 540);
        var s = _fallback.AsSurface();
        s.FillVerticalGradient(0, 0, 960, 540, Color.FromRgb(19, 37, 60), Ink);
        Paint.StrokeCircle(s, 675f, 240f, 112f, 3f, Color.FromRgb(48, 78, 110));
    }

    public void ResetCovers()
    {
        ReleasePreview();
        _libraryImages.Clear();
        _summaries.Clear();
        ResetLibraryState();
        ReleaseDialogBase();
        foreach (var cover in _covers.Values) cover.Dispose();
        _covers.Clear();
        _coverOrder.Clear();
        _failedCovers.Clear();
        _backdrop?.Dispose();
        _backdrop = null;
        _backdropPath = null;
        _calmBackdrop?.Dispose();
        _calmBackdrop = null;
        _calmBackdropPath = null;
    }

    private PixelBuffer Cover(ThemeLibraryEntry entry)
    {
        if (_covers.TryGetValue(entry.Path, out var cover)) return cover;
        if (entry.Preview is null || _failedCovers.Contains(entry.Path)) return _fallback;
        try
        {
            // Bounded LRU-like cache: no more than five decoded 1080p covers.
            if (_covers.Count >= 5)
            {
                string oldest = _coverOrder.Dequeue();
                _covers[oldest].Dispose();
                _covers.Remove(oldest);
            }
            cover = _decode(entry.Preview);
            _covers.Add(entry.Path, cover);
            _coverOrder.Enqueue(entry.Path);
            return cover;
        }
        catch { _failedCovers.Add(entry.Path); return _fallback; }
    }

    // ---- Backdrops --------------------------------------------------------------------------------------------------

    /// <summary>How far the blurred backdrop of the quiet views is washed toward ink (alpha of the wash).</summary>
    private const byte BlurredDim = 160;

    /// <summary>
    /// The picture behind a view. Library: the sharp cover of the selected theme under a left and bottom scrim, so the
    /// hero text and the collection row stay readable on bright art. Every other view: the cover blurred and dimmed, so
    /// panels and cards read cleanly on it (the keyboard screen calms it further). The preview does not come here: it
    /// draws its own pictures (see <see cref="DrawPreview"/>).
    /// </summary>
    private void Backdrop(Surface s, ThemeView view, ThemeLibraryEntry? selected)
    {
        PixelBuffer picture = view switch
        {
            // With no theme the placeholder art would only compete with the centred message, so it is blurred like the quiet views'.
            ThemeView.Library => selected is null ? BlurredBackdrop(null) : SharpBackdrop(selected, scrim: true),
            ThemeView.StoreAddress or ThemeView.Search => CalmBackdrop(selected),
            _ => BlurredBackdrop(selected),
        };
        s.Blit(picture.AsSurface(), 0, 0);
    }

    // One slot: the buffer is reused when the selection or the look changes, so a selection change allocates nothing.
    private PixelBuffer SharpBackdrop(ThemeLibraryEntry? selected, bool scrim)
    {
        string key = selected?.Path ?? "empty";
        if (_backdrop is not null && _backdropPath == key && _backdropScrim == scrim) return _backdrop;
        _backdrop ??= new PixelBuffer(Theme.ScreenWidth, Theme.ScreenHeight);
        Surface b = _backdrop.AsSurface();
        Surface source = (selected is null ? _fallback : Cover(selected)).AsSurface();
        if (scrim) ScaleAndWash(b, source, LibraryColumnKeep, LibraryRowKeep, Ink);
        else b.BlitScaled(source, 0, 0, Theme.ScreenWidth, Theme.ScreenHeight);
        _backdropPath = key;
        _backdropScrim = scrim;
        return _backdrop;
    }

    private PixelBuffer BlurredBackdrop(ThemeLibraryEntry? selected) =>
        _libraryImages.GetBackdrop(selected?.Path ?? FallbackKey, selected is null ? _fallback : Cover(selected), dim: BlurredDim);

    private PixelBuffer CalmBackdrop(ThemeLibraryEntry? selected)
    {
        string key = selected?.Path ?? "empty";
        if (_calmBackdrop is not null && _calmBackdropPath == key) return _calmBackdrop;
        _calmBackdrop ??= new PixelBuffer(Theme.ScreenWidth, Theme.ScreenHeight);
        Surface b = _calmBackdrop.AsSurface();
        b.Blit(BlurredBackdrop(selected).AsSurface(), 0, 0);
        // Calms the cover behind the keyboard so the keys read the same on any theme; the header above stays as it is.
        Paint.FillRect(b, 0, Theme.HeaderRuleY + 1, Theme.ScreenWidth, Theme.ScreenHeight - Theme.HeaderRuleY - 1, Ink.WithAlpha(110));
        _calmBackdropPath = key;
        return _calmBackdrop;
    }

    // The Library scrim as how much of the cover stays (0 to 256) per column and per row: dark under the hero text on the
    // left, easing out to the right, plus a soft cap at the top for the header and a ramp at the bottom for the cards.
    private static readonly ushort[] LibraryColumnKeep = BuildColumnKeep(), LibraryRowKeep = BuildRowKeep();

    private static float Smooth(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }
    private static ushort Keep(float alpha) => (ushort)Math.Round(256f * (1f - alpha / 255f));

    private static ushort[] BuildColumnKeep()
    {
        const float solidTo = 560f, clearAt = 1500f, peak = 205f, floor = 48f;
        var keep = new ushort[Theme.ScreenWidth];
        for (int x = 0; x < keep.Length; x++)
            keep[x] = Keep(floor + (peak - floor) * (1f - Smooth((x - solidTo) / (clearAt - solidTo))));
        return keep;
    }

    private static ushort[] BuildRowKeep()
    {
        var keep = new ushort[Theme.ScreenHeight];
        for (int y = 0; y < keep.Length; y++)
        {
            float top = 150f * (1f - Smooth(y / 220f)), bottom = 232f * Smooth((y - 560f) / 340f);
            keep[y] = Keep(255f * (1f - (1f - top / 255f) * (1f - bottom / 255f)));
        }
        return keep;
    }

    /// <summary>
    /// Scales <paramref name="source"/> to fill <paramref name="target"/> (nearest neighbour, like the SDK's scaled blit)
    /// and washes it toward <paramref name="ink"/> in the same pass: each pixel keeps (column keep x row keep) / 256 of
    /// itself, the two profiles being 0 to 256 per target column and row. Runs once per cover, never per frame.
    /// </summary>
    private static unsafe void ScaleAndWash(Surface target, Surface source, ushort[] columnKeep, ushort[] rowKeep, Color ink)
    {
        uint inkRb = ink.Value & 0x00FF00FFu, inkG = (ink.Value >> 8) & 0xFFu;
        int width = Math.Min(target.Width, columnKeep.Length), height = Math.Min(target.Height, rowKeep.Length);
        Span<int> sourceX = stackalloc int[width];
        for (int x = 0; x < width; x++) sourceX[x] = (int)((long)x * source.Width / width);
        fixed (ushort* columns = columnKeep)
        fixed (int* map = sourceX)
        {
            for (int y = 0; y < height; y++)
            {
                uint* from = source.Pixels + (long)(int)((long)y * source.Height / height) * source.Stride;
                uint* row = target.Pixels + (long)y * target.Stride;
                uint rowK = rowKeep[y];
                for (int x = 0; x < width; x++)
                {
                    uint p = from[map[x]];
                    uint keep = (columns[x] * rowK) >> 8;
                    if (keep >= 256) { row[x] = p | 0xFF000000u; continue; }
                    uint wash = 256 - keep;
                    uint rb = (((p & 0x00FF00FFu) * keep + inkRb * wash) >> 8) & 0x00FF00FFu;
                    uint g = (((p >> 8) & 0xFFu) * keep + inkG * wash) & 0xFF00u;
                    row[x] = 0xFF000000u | rb | g;
                }
            }
        }
    }

    // ---- Frame ------------------------------------------------------------------------------------------------------

    // The frame time of the current Draw, for the screens that ease without being handed it.
    private double _frameDelta = 1.0 / 60;
    // The time of the frame being drawn, for the few animations drawn deep inside a screen (the store's shimmer).
    private double _now;

    // The selected library entry of the current Draw, for the footer hints.
    private ThemeLibraryEntry? _selected;

    public void Draw(Surface surface, ThemeBrowser browser, IReadOnlyList<ThemeLibraryEntry> entries, double seconds, double delta = 1.0 / 60)
    {
        _frameDelta = delta;
        _now = seconds;
        UpdateCreator(browser.StoreUrl);
        if (_noticeFresh) { _noticeAt = seconds; _noticeFresh = false; }
        if (browser.View != _previousView) { _previousView = browser.View; _viewStart = seconds; }
        var selected = browser.Selected >= 0 && browser.Selected < entries.Count ? entries[browser.Selected] : null;
        _selected = selected;
        bool dialog = browser.Dialog != DialogKind.None;
        if (dialog && _dialogBase is not null && _dialogKey == DialogBaseKey(browser, selected, entries.Count))
        {
            // The screen under an open dialog does not change: its dimmed picture is copied instead of drawn again.
            surface.Blit(_dialogBase.AsSurface(), 0, 0);
            if (Busy) DrawBusySpinner(surface, seconds, dimmed: true);
            DrawDialog(surface, browser, selected);
            DrawFooter(surface, browser);
            DrawNotice(surface, seconds);
            return;
        }
        if (browser.View == ThemeView.Preview)
        {
            DrawPreview(surface, browser, selected, seconds);
            return;
        }
        ReleasePreview();
        Backdrop(surface, browser.View, selected);
        DrawHeader(surface, browser.View);
        int enter = (int)(16 * Math.Exp(-Math.Max(0, seconds - _viewStart) * 9));
        switch (browser.View)
        {
            case ThemeView.Library: DrawLibrary(surface, browser, entries, selected, enter, seconds, delta); break;
            case ThemeView.Detail: if (selected is not null) DrawDetail(surface, browser, selected, enter); break;
            case ThemeView.Community: DrawCommunity(surface, browser, enter); break;
            case ThemeView.Import: DrawImport(surface, enter); break;
            case ThemeView.System: DrawSystem(surface, browser, entries.Count, enter); break;
            case ThemeView.StoreAddress: DrawStoreAddress(surface, browser.StoreEditor, enter, seconds); break;
            case ThemeView.Search: DrawStoreAddress(surface, browser.SearchEditor, enter, seconds); break;
        }
        if (dialog) DrawDialogScrim(surface, browser, selected, entries.Count, seconds);
        else ReleaseDialogBase();
        // After the scrim, so the spinner keeps turning under a dialog instead of freezing in its snapshot.
        if (Busy) DrawBusySpinner(surface, seconds, dimmed: dialog);
        if (dialog) DrawDialog(surface, browser, selected);
        DrawFooter(surface, browser);
        DrawNotice(surface, seconds);
    }

    // ---- Text placement ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The font atlases carry 4 px of padding left of every glyph (tools/bake-assets.py), so text drawn at x shows up at
    /// x + 4. The helpers below take the x the text should visibly start (or end, or centre) at and compensate.
    /// </summary>
    private const int GlyphPad = 4;

    private static void DrawLeft(AtlasFont font, Surface s, string text, int x, int y, Color color) =>
        font.DrawText(s, text, x - GlyphPad, y, color);

    private static void ClipLeft(AtlasFont font, Surface s, string text, int x, int y, Color color, int maxWidth) =>
        font.DrawClipped(s, text, x - GlyphPad, y, color, maxWidth);

    private static void DrawRight(AtlasFont font, Surface s, string text, int right, int y, Color color) =>
        font.DrawText(s, text, right - font.MeasureText(text) - GlyphPad, y, color);

    private static void DrawMid(AtlasFont font, Surface s, string text, int centerX, int y, Color color) =>
        font.DrawText(s, text, centerX - font.MeasureText(text) / 2 - GlyphPad, y, color);

    /// <summary>Distance from the top of an atlas line to its baseline: lines of different sizes share a baseline when drawn at y = baseline - Ascent.</summary>
    private static int Ascent(AtlasFont font) => (int)Math.Round((font.LineHeight - 4) * 0.8);

    // Glyph top that centres a 30px icon on a text line drawn at y in the given font.
    private static int GlyphTop(AtlasFont font, int y) => y + (font.LineHeight - GlyphSize) / 2;

    /// <summary>Draws a button glyph followed by its label; y is the label's text line. Returns the x for the next hint.</summary>
    private int Hint(Surface s, PadButton button, string label, int x, int y, Color? color = null)
    {
        Color c = color ?? Muted;
        // Round buttons are a filled disc in c with a dark symbol; d-pad and options are plain outlines in c.
        Color fg = button is PadButton.DPad or PadButton.Options ? c : Ink;
        int w = ButtonGlyph.Draw(s, button, x, GlyphTop(_small, y), GlyphSize, fg, c, _small);
        int labelX = x + w + GlyphGap;
        DrawLeft(_small, s, label, labelX, y, c);
        return labelX + _small.MeasureText(label) + HintGap;
    }

    /// <summary>White (or muted when inactive) pill with a button glyph (Cross unless told) and a dark label. Returns the pill width.</summary>
    private int PillButton(Surface s, int x, int y, string label, bool active, PadButton button = PadButton.Cross)
    {
        const int height = 64, glyph = 30, pad = 24, gap = 14;
        int width = pad + glyph + gap + _label.MeasureText(label) + pad;
        Paint.Capsule(s, x, y, width, height, active ? White : Theme.PillOff);
        int cy = y + height / 2;
        ButtonGlyph.Draw(s, button, x + pad, cy - glyph / 2, glyph, active ? White : Muted, PillInk);
        DrawLeft(_label, s, label, x + pad + glyph + gap, cy - _label.LineHeight / 2, active ? PillInk : Muted);
        return width;
    }

    /// <summary>The quiet counterpart of <see cref="PillButton"/>: a glass pill (panel fill, hairline edge) with a light glyph disc and a white label. Returns the pill width.</summary>
    private int GlassPill(Surface s, int x, int y, string label, PadButton button)
    {
        const int height = 64, glyph = 30, pad = 24, gap = 14;
        int width = pad + glyph + gap + _label.MeasureText(label) + pad;
        Paint.Capsule(s, x, y, width, height, Theme.Panel);
        Paint.StrokeRoundedRect(s, x, y, width, height, height / 2, 1, Theme.PanelStroke);
        int cy = y + height / 2;
        ButtonGlyph.Draw(s, button, x + pad, cy - glyph / 2, glyph, Ink, White);
        DrawLeft(_label, s, label, x + pad + glyph + gap, cy - _label.LineHeight / 2, White);
        return width;
    }

    /// <summary>"13.000.000" as the console's settings show it ("13.00"); anything else as it came, "unknown" when empty.</summary>
    public static string FirmwareLabel(string version)
    {
        string[] parts = version.Split('.');
        if (parts.Length == 3 && parts[1].Length == 3 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out _))
            return $"{major}.{parts[1][..2]}";
        return version.Length == 0 ? "unknown" : version;
    }

    /// <summary>"1 theme", "3 themes": the count with a regular English plural.</summary>
    public static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public void Dispose()
    {
        ReleaseDialogBase();
        ReleaseCreator();
        ResetCovers();
        ResetCommunity();
        _fallback.Dispose();
        ButtonGlyph.Clear();
    }
}
