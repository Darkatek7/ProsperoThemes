using System;
using System.Collections.Generic;
using System.Linq;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>The same 1920x1080 composition is drawn on the console and in the PC preview.</summary>
public sealed class ThemeRenderer : IDisposable
{
    private static readonly Color Ink = Color.FromRgb(7, 12, 21);
    private static readonly Color White = Color.FromRgb(242, 246, 253);
    private static readonly Color Muted = Color.FromRgb(154, 169, 190);
    private static readonly Color Accent = Color.FromRgb(155, 209, 255);
    private static readonly Color PillInk = Color.FromRgb(12, 18, 28);
    private static readonly Color Warn = Color.FromRgb(255, 190, 140);
    private static readonly Color KeyFill = Color.FromRgb(30, 40, 54);
    private const int GlyphSize = 30, GlyphGap = 10, HintGap = 36;
    private readonly AtlasFont _small, _body, _heading, _title;
    private readonly Func<byte[], PixelBuffer> _decode;
    private readonly Dictionary<string, PixelBuffer> _covers = new();
    private readonly Queue<string> _coverOrder = new();
    private readonly HashSet<string> _failedCovers = new();
    private readonly PixelBuffer _fallback;
    private PixelBuffer? _backdrop;
    private string? _backdropPath;
    private float _focusX = 80;
    private ThemeView _previousView;
    private double _viewStart;
    private const int ThumbWidth = 310, ThumbGap = 26;

    public string Status { get; set; } = "Local library";
    public bool Busy { get; set; }
    public string LibraryStatus { get; set; } = "";
    /// <summary>Outcome of the last apply or restore, shown on the detail and system screens.</summary>
    public string ActionStatus { get; set; } = "";
    /// <summary>The background-scope setting shown on the System screen (see ThemeApplier.IncludeAllApps).</summary>
    public bool AllAppsBackgrounds { get; set; }
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
    public string CommunityStatus { get; set; } = "";
    private readonly Dictionary<string, PixelBuffer> _communityThumbs = new();
    private readonly HashSet<string> _failedThumbs = new();

    /// <summary>Drops decoded store previews (a new list was loaded).</summary>
    public void ResetCommunity()
    {
        foreach (var t in _communityThumbs.Values) t.Dispose();
        _communityThumbs.Clear();
        _failedThumbs.Clear();
        CommunityPreviews.Clear();
    }

    public ThemeRenderer(Func<string, byte[]> readAsset, Func<byte[], PixelBuffer> decode)
    {
        _small = new AtlasFont(readAsset("inter-22.p5font"));
        _body = new AtlasFont(readAsset("inter-28.p5font"));
        _heading = new AtlasFont(readAsset("inter-36.p5font"));
        _title = new AtlasFont(readAsset("inter-76.p5font"));
        _decode = decode;
        _fallback = new PixelBuffer(960, 540);
        var s = _fallback.AsSurface();
        s.FillVerticalGradient(0, 0, 960, 540, Color.FromRgb(19, 37, 60), Ink);
        s.DrawCircle(675, 240, 110, Color.FromRgb(48, 78, 110));
        s.DrawCircle(675, 240, 112, Color.FromRgb(48, 78, 110));
    }

    public void ResetCovers()
    {
        foreach (var cover in _covers.Values) cover.Dispose();
        _covers.Clear();
        _coverOrder.Clear();
        _failedCovers.Clear();
        _backdrop?.Dispose();
        _backdrop = null;
        _backdropPath = null;
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

    private void Backdrop(Surface s, ThemeLibraryEntry? selected, bool dim)
    {
        string key = (selected?.Path ?? "empty") + (dim ? ":dim" : ":clear");
        if (_backdrop is null || _backdropPath != key)
        {
            _backdrop?.Dispose();
            _backdrop = new PixelBuffer(1920, 1080);
            var b = _backdrop.AsSurface();
            b.BlitScaled(selected is null ? _fallback.AsSurface() : Cover(selected).AsSurface(), 0, 0, 1920, 1080);
            if (dim)
            {
                for (int x = 0; x < 1920; x++)
                {
                    float amount = Math.Clamp(1f - x / 1420f, 0, 1);
                    b.FillRectBlended(x, 0, 1, 1080, Ink.WithAlpha((byte)(65 + amount * 145)));
                }
                b.FillRectBlended(0, 0, 1920, 160, Ink.WithAlpha(100));
                for (int y = 650; y < 1080; y++)
                    b.FillRectBlended(0, y, 1920, 1, Ink.WithAlpha((byte)Math.Clamp((y - 650) * 255 / 165, 0, 252)));
            }
            _backdropPath = key;
        }
        s.Blit(_backdrop.AsSurface(), 0, 0);
    }

    public void Draw(Surface surface, ThemeBrowser browser, IReadOnlyList<ThemeLibraryEntry> entries, double seconds, double delta = 1.0 / 60)
    {
        if (browser.View != _previousView) { _previousView = browser.View; _viewStart = seconds; }
        var selected = browser.Selected >= 0 && browser.Selected < entries.Count ? entries[browser.Selected] : null;
        Backdrop(surface, selected, browser.View != ThemeView.Preview);
        if (browser.View == ThemeView.Preview)
        {
            if (browser.ShowOverlay) DrawHomescreen(surface, selected, seconds);
            return;
        }
        DrawHeader(surface, browser.View);
        int enter = (int)(16 * Math.Exp(-Math.Max(0, seconds - _viewStart) * 9));
        switch (browser.View)
        {
            case ThemeView.Library: DrawLibrary(surface, browser, entries, selected, enter, delta); break;
            case ThemeView.Detail: if (selected is not null) DrawDetail(surface, browser, selected, enter); break;
            case ThemeView.Community: DrawCommunity(surface, browser, enter); break;
            case ThemeView.Import: DrawImport(surface, enter); break;
            case ThemeView.System: DrawSystem(surface, browser, entries.Count, enter); break;
            case ThemeView.StoreAddress: DrawStoreAddress(surface, browser.StoreEditor, enter, seconds); break;
        }
        DrawFooter(surface, browser.View);
        if (browser.Pending != ThemeAction.None) DrawConfirm(surface, browser.Pending, browser.ApplyAsksScope);
    }

    /// <summary>
    /// The armed-action bar over the footer: a second X writes to the system, anything else cancels. A pack with an
    /// all-apps background asks where it goes: X for apps without their own image, Triangle for all apps.
    /// </summary>
    private void DrawConfirm(Surface s, ThemeAction pending, bool asksScope)
    {
        s.FillRect(0, 1010, 1920, 70, Ink);
        s.HLine(80, 1010, 1760, Accent);
        if (pending == ThemeAction.Apply && asksScope)
        {
            int at = Hint(s, PadButton.Cross, "Apply to apps without their own image", 80, 1034, White);
            at = Hint(s, PadButton.Triangle, "Apply to all apps", at, 1034, White);
            Hint(s, PadButton.Circle, "Cancel", at, 1034, Muted);
            return;
        }
        string what = pending == ThemeAction.Apply ? "Apply theme to console now" : "Restore all defaults: backgrounds, icons, sounds";
        int x = Hint(s, PadButton.Cross, what, 80, 1034, White);
        Hint(s, PadButton.Circle, "Cancel", x, 1034, Muted);
    }

    private void DrawHeader(Surface s, ThemeView view)
    {
        // Original mark: an orbit with its own small moon, no platform branding asset.
        s.DrawCircleThick(100, 81, 20, 2, White);
        s.FillCircle(114, 66, 6, Accent);
        _heading.DrawText(s, "prospero", 137, 49, White);
        _small.DrawText(s, "T H E M E S", 313, 65, Muted);
        string[] tabs = ["Library", "Community", "USB Import", "System"];
        int active = view switch { ThemeView.Community => 1, ThemeView.Import => 2, ThemeView.System or ThemeView.StoreAddress => 3, _ => 0 };
        for (int i = 0; i < tabs.Length; i++)
        {
            int x = 600 + i * 215;
            _body.DrawText(s, tabs[i], x, 63, i == active ? White : Muted);
            if (i == active) s.FillRoundedRect(x, 111, _body.MeasureText(tabs[i]), 3, 1, Accent);
        }
        s.FillCircle(1590, 82, 4, Busy ? Accent : Muted);
        _small.DrawClipped(s, Status, 1606, 64, Muted, 225);
        s.HLine(80, 145, 1760, Color.FromRgb(42, 53, 69));
    }

    private void DrawLibrary(Surface s, ThemeBrowser browser, IReadOnlyList<ThemeLibraryEntry> entries, ThemeLibraryEntry? selected, int enter, double delta)
    {
        _small.DrawText(s, selected?.Bundled == true ? "PROSPERO STUDIO  /  BUNDLED" : "YOUR THEME LIBRARY", 80, 239 + enter, Accent);
        _title.DrawClipped(s, selected?.Manifest.Name ?? "Your PS5. Your style.", 75, 282 + enter, White, 920);
        _body.DrawClipped(s, selected?.Manifest.Description is { Length: > 0 } description ? description : "Pick a theme or import your own.", 80, 400 + enter, Muted, 830);
        if (selected is not null)
        {
            int pill = PillButton(s, 80, 485 + enter, "View theme", true);
            Hint(s, PadButton.Square, "Preview", 80 + pill + 26, 517 + enter - _small.LineHeight / 2);
            int count = selected.Manifest.Targets.Count;
            _small.DrawClipped(s, $"{Plural(count, "target")}  ·  {selected.Manifest.Author}  ·  v{selected.Manifest.Version}", 80, 595 + enter, Muted, 880);
        }
        else
        {
            int r1 = ButtonGlyph.Draw(s, PadButton.R1, 80, GlyphTop(_body, 492 + enter), GlyphSize, Ink, White, _small);
            _body.DrawText(s, "opens USB Import", 80 + r1 + GlyphGap, 492 + enter, White);
        }
        _heading.DrawText(s, "Your collection", 80, 687, White);
        _small.DrawText(s, $"{entries.Count:00} {(entries.Count == 1 ? "THEME" : "THEMES")}", 400, 701, Muted);
        int first = Math.Max(0, browser.Selected - 3);
        int visible = Math.Min(5, entries.Count - first);
        int focus = Math.Max(0, browser.Selected - first);
        float target = 80 + focus * (ThumbWidth + ThumbGap);
        _focusX += (target - _focusX) * (float)(1 - Math.Exp(-Math.Clamp(delta, 0, .1) * 16));
        for (int i = 0; i < visible; i++)
        {
            var entry = entries[first + i];
            int x = 80 + i * (ThumbWidth + ThumbGap);
            bool current = first + i == browser.Selected;
            s.BlitScaled(Cover(entry).AsSurface(), x, 756, ThumbWidth, 174);
            if (!current) s.FillRectBlended(x, 756, ThumbWidth, 174, Ink.WithAlpha(75));
            _body.DrawClipped(s, entry.Manifest.Name, x, 946, current ? White : Muted, ThumbWidth);
            if (current) s.DrawRectThick(x - 5, 751, ThumbWidth + 10, 184, 2, White);
        }
        if (selected is not null) s.FillRect((int)_focusX, 991, ThumbWidth, 3, Accent);
        if (visible < 5)
        {
            int x = 80 + visible * (ThumbWidth + ThumbGap);
            s.DrawRect(x, 756, ThumbWidth, 174, Color.FromRgb(52, 64, 79));
            s.HLine(x + 145, 823, 22, Muted);
            s.VLine(x + 156, 812, 22, Muted);
            int labelW = _small.MeasureText("USB Import");
            int tileW = ButtonGlyph.Width(PadButton.R1, GlyphSize) + GlyphGap + labelW;
            Hint(s, PadButton.R1, "USB Import", x + (ThumbWidth - tileW) / 2, 859);
        }
    }

    private void DrawDetail(Surface s, ThemeBrowser browser, ThemeLibraryEntry entry, int enter)
    {
        // Dark panel behind the info column (32px padding) so text stays readable over bright covers.
        // Drawn before the title so the title, which can run past the panel edge, stays on top.
        s.FillRectBlended(1098, 228 + enter, 774, 772, Ink.WithAlpha(200));
        _small.DrawText(s, "THEME DETAILS", 80, 196 + enter, Accent);
        _title.DrawClipped(s, entry.Manifest.Name, 75, 233 + enter, White, 1060);
        s.BlitScaled(Cover(entry).AsSurface(), 80, 369, 960, 540);
        Hint(s, PadButton.Square, "Full-screen preview", 80, 927);
        if (ActionStatus.Length > 0) _small.DrawClipped(s, ActionStatus, 80, 972, Accent, 1000);
        _heading.DrawText(s, "In this pack", 1130, 260 + enter, White);
        _body.DrawClipped(s, entry.Manifest.Description, 1130, 326 + enter, Muted, 680);
        _small.DrawClipped(s, $"{entry.Manifest.Author}  ·  Version {entry.Manifest.Version}", 1130, 380 + enter, Muted, 680);
        s.HLine(1130, 444, 710, Color.FromRgb(53, 66, 83));
        _small.DrawText(s, entry.Report.Issues.Count == 0 ? "Pack checked" : "Pack checked · has warnings", 1130, 464, Accent);
        int offset = Math.Clamp(browser.TargetOffset, 0, Math.Max(0, entry.Manifest.Targets.Count - 5));
        for (int row = 0; row < 5 && offset + row < entry.Manifest.Targets.Count; row++)
        {
            var t = entry.Manifest.Targets[offset + row];
            int y = 538 + row * 82;
            _small.DrawText(s, $"{offset + row + 1:00}", 1130, y, Muted);
            _body.DrawClipped(s, TargetName(t.Path, t.Screen), 1181, y - 5, White, 650);
            _small.DrawClipped(s, t.Path, 1181, y + 30, Muted, 650);
        }
        if (entry.Manifest.Targets.Count > 5)
            _small.DrawText(s, $"{offset + 1}–{Math.Min(offset + 5, entry.Manifest.Targets.Count)} / {entry.Manifest.Targets.Count}  ·  Up/Down", 1130, 950, Muted);
    }

    private void DrawCommunity(Surface s, ThemeBrowser browser, int enter)
    {
        _small.DrawText(s, "COMMUNITY STORE", 80, 190 + enter, Accent);
        string[] sorts = [CommunityList.Label(CommunitySort.Top), CommunityList.Label(CommunitySort.Downloads), CommunityList.Label(CommunitySort.New)];
        int cx = 80;
        for (int i = 0; i < sorts.Length; i++)
        {
            bool on = i == browser.CommunitySort;
            int w = _body.MeasureText(sorts[i]) + 40;
            s.FillRoundedRect(cx, 222 + enter, w, 48, 24, on ? White : Color.FromRgb(30, 40, 54));
            _body.DrawText(s, sorts[i], cx + 20, 230 + enter, on ? PillInk : Muted);
            cx += w + 14;
        }
        Hint(s, PadButton.Square, "Change sort", cx + 16, 236 + enter);

        IReadOnlyList<CommunityTheme> items = Community;
        if (items.Count == 0)
        {
            _body.DrawClipped(s, CommunityStatus.Length > 0 ? CommunityStatus : "No themes loaded yet.", 80, 330 + enter, Muted, 1600);
            return;
        }
        const int columns = 4, cardW = 400, cardH = 225, gapX = 26, rowH = 330;
        int selected = Math.Max(0, browser.CommunitySelected);
        int firstRow = Math.Max(0, selected / columns - 1);
        for (int slot = 0; slot < columns * 2; slot++)
        {
            int index = firstRow * columns + slot;
            if (index >= items.Count) break;
            CommunityTheme t = items[index];
            int x = 80 + (slot % columns) * (cardW + gapX), y = 300 + (slot / columns) * rowH + enter;
            PixelBuffer? thumb = Thumb(t);
            if (thumb is not null) s.BlitScaled(thumb.AsSurface(), x, y, cardW, cardH);
            else s.BlitScaled(_fallback.AsSurface(), x, y, cardW, cardH);
            bool update = CommunityUpdates.Contains(t.Id);
            if (update || CommunityInLibrary.Contains(t.Id))
            {
                string badge = update ? "Update" : "In library";
                int bw = _small.MeasureText(badge) + 52;
                int bx = x + cardW - bw - 14, by = y + 14;
                s.FillRoundedRect(bx, by, bw, 40, 20, update ? Warn : Accent);
                s.FillRoundedRect(bx + 14, by + 14, 12, 12, 6, PillInk);
                _small.DrawText(s, badge, bx + 36, by + 7, PillInk);
            }
            if (index == selected)
            {
                s.DrawRoundedRect(x - 6, y - 6, cardW + 12, cardH + 12, 10, White);
                s.DrawRoundedRect(x - 5, y - 5, cardW + 10, cardH + 10, 10, White);
            }
            string version = t.Version.Length > 0 ? "v" + t.Version : "";
            int versionW = version.Length > 0 ? _small.MeasureText(version) : 0;
            _body.DrawClipped(s, t.Name, x, y + cardH + 12, index == selected ? White : Color.FromRgb(214, 222, 232),
                versionW > 0 ? cardW - versionW - 16 : cardW);
            if (versionW > 0) _small.DrawText(s, version, x + cardW - versionW, y + cardH + 18, update ? Warn : Muted);
            _small.DrawClipped(s, $"{t.Author}  ·  {Plural(t.Likes, "like")}  ·  {Plural(t.Downloads, "download")}", x, y + cardH + 52, Muted, cardW);
            _small.DrawClipped(s, t.Contents, x, y + cardH + 80, Accent, cardW);
        }
        if (CommunityStatus.Length > 0)
            _small.DrawClipped(s, CommunityStatus, 80, 975, Accent, 1700);
    }

    private PixelBuffer? Thumb(CommunityTheme t)
    {
        if (_communityThumbs.TryGetValue(t.Id, out var thumb)) return thumb;
        if (_failedThumbs.Contains(t.Id) || !CommunityPreviews.TryGetValue(t.Id, out byte[]? png)) return null;
        try
        {
            thumb = _decode(png);
            if (_communityThumbs.Count >= 16)
            {
                foreach (var old in _communityThumbs.Values) old.Dispose();
                _communityThumbs.Clear();
            }
            _communityThumbs[t.Id] = thumb;
            return thumb;
        }
        catch
        {
            _failedThumbs.Add(t.Id);
            return null;
        }
    }

    private void DrawImport(Surface s, int enter)
    {
        _small.DrawText(s, "EXPAND YOUR COLLECTION", 80, 249 + enter, Accent);
        _title.DrawText(s, "Bring your style.", 75, 290 + enter, White);
        _body.DrawText(s, "Copy theme packs from a USB drive into your library.", 80, 412 + enter, Muted);
        string[] steps = ["Put .p5theme, .zip or .tar files on your USB drive.", "Use the main folder or a folder named prospero-themes.", "Plug in the drive and start the import."];
        for (int i = 0; i < steps.Length; i++)
        {
            int y = 513 + i * 83;
            _small.DrawText(s, $"0{i + 1}", 80, y + 3, Accent);
            _body.DrawText(s, steps[i], 141, y, White);
        }
        PillButton(s, 80, 808, Busy ? "Importing…" : "Import from USB", !Busy);
        _small.DrawClipped(s, LibraryStatus, 80, 913, Muted, 1690);
    }

    private void DrawSystem(Surface s, ThemeBrowser browser, int count, int enter)
    {
        _small.DrawText(s, "PROSPERO THEMES", 80, 245 + enter, Accent);
        _title.DrawText(s, "Everything in its place.", 75, 286 + enter, White);
        bool hasStore = browser.StoreUrl.Length > 0;
        string[] labels = ["Library", "Storage location", "Pack format", "Target system", "App backgrounds", "Store address"];
        string[] values = [Plural(count, "theme"), "/data/prospero-themes/library", "Format 2  ·  ZIP / TAR", "PS5  ·  Firmware " + FirmwareLabel(Firmware),
            AllAppsBackgrounds ? "All apps, even with their own image" : "Only apps without their own image",
            hasStore ? browser.StoreUrl : "not set"];
        // Six rows at 70 px keep the last line above the Restore button at 892.
        const int rowTop = 404, rowStep = 70;
        for (int i = 0; i < labels.Length; i++)
        {
            int y = rowTop + i * rowStep;
            _body.DrawText(s, labels[i], 80, y, Muted);
            _body.DrawClipped(s, values[i], 520, y, i == labels.Length - 1 && !hasStore ? Muted : White, 940);
            s.HLine(80, y + 58, 1400, Color.FromRgb(44, 55, 71));
        }
        Hint(s, PadButton.Square, "Change", 1500, rowTop + (labels.Length - 1) * rowStep + 4, White);
        PillButton(s, 80, 892, Busy ? "Please wait…" : "Restore defaults", !Busy);
        _small.DrawClipped(s, ActionStatus.Length > 0 ? ActionStatus : LibraryStatus, 80, 976, ActionStatus.Length > 0 ? Accent : Muted, 1680);
    }

    /// <summary>The on-screen keyboard for the store address: input box with caret, key grid, and one line of help.</summary>
    private void DrawStoreAddress(Surface s, StoreAddressEditor editor, int enter, double seconds)
    {
        // Calm the cover behind the screen so the keys read the same on any theme.
        s.FillRectBlended(0, 146, 1920, 874, Ink.WithAlpha(185));
        _small.DrawText(s, "COMMUNITY STORE", 80, 190 + enter, Accent);
        _heading.DrawText(s, "Store address", 80, 222 + enter, White);
        string count = $"{editor.Text.Length} / {StoreAddressEditor.MaxLength}";
        _small.DrawText(s, count, 1840 - _small.MeasureText(count), 238 + enter, Muted);

        const int boxX = 80, boxY = 292, boxW = 1760, boxH = 76, pad = 24;
        s.FillRoundedRect(boxX, boxY + enter, boxW, boxH, 14, Color.FromRgb(13, 21, 33));
        s.DrawRoundedRect(boxX, boxY + enter, boxW, boxH, 14, Accent);
        int textY = boxY + enter + (boxH - _body.LineHeight) / 2, textX = boxX + pad;
        if (editor.Text.Length == 0)
            _body.DrawText(s, "http://192.168.0.5:8080", textX, textY, Color.FromRgb(88, 102, 124));
        else
        {
            // Long addresses show their end, behind an ellipsis, so the caret never leaves the box.
            int room = boxW - 2 * pad - 6, dots = _body.MeasureText("…"), start = 0;
            if (_body.MeasureText(editor.Text) > room)
            {
                int width = dots;
                start = editor.Text.Length;
                while (start > 0 && width + _body.MeasureText(editor.Text.AsSpan(start - 1, 1)) <= room)
                    width += _body.MeasureText(editor.Text.AsSpan(--start, 1));
                _body.DrawText(s, "…", textX, textY, Muted);
                textX += dots;
            }
            string shown = editor.Text[start..];
            _body.DrawText(s, shown, textX, textY, White);
            textX += _body.MeasureText(shown);
        }
        if (seconds % 1.0 < .6) s.FillRect(textX + 2, textY + 3, 3, _body.LineHeight - 6, Accent);

        if (editor.Notice.Length > 0) _small.DrawClipped(s, editor.Notice, 80, 388 + enter, Warn, 1760);
        else _small.DrawClipped(s, "Leave empty and save to remove the store.", 80, 388 + enter, Muted, 1760);

        const int unit = 165, gap = 12, keyH = 92, top = 440;
        for (int row = 0; row < StoreAddressEditor.Layout.Count; row++)
        {
            int column = 0;
            for (int i = 0; i < StoreAddressEditor.Layout[row].Count; i++)
            {
                EditorKey key = StoreAddressEditor.Layout[row][i];
                int x = 80 + column * (unit + gap), y = top + row * (keyH + gap) + enter;
                int w = key.Width * unit + (key.Width - 1) * gap;
                bool focus = row == editor.Row && i == editor.Column;
                s.FillRoundedRect(x, y, w, keyH, 14, focus ? White : KeyFill);
                if (focus)
                {
                    s.DrawRoundedRect(x - 4, y - 4, w + 8, keyH + 8, 17, Accent);
                    s.DrawRoundedRect(x - 5, y - 5, w + 10, keyH + 10, 18, Accent);
                }
                AtlasFont font = key.Width > 1 ? _body : _heading;
                font.DrawText(s, key.Label, x + (w - font.MeasureText(key.Label)) / 2, y + (keyH - font.LineHeight) / 2,
                    focus ? PillInk : key.Width > 1 ? Accent : White);
                column += key.Width;
            }
        }
    }

    private void DrawHomescreen(Surface s, ThemeLibraryEntry? selected, double seconds)
    {
        s.FillRectBlended(0, 0, 1920, 215, Ink.WithAlpha(95));
        _heading.DrawText(s, "Games", 84, 60, White);
        _heading.DrawText(s, "Media", 247, 60, Muted);
        _body.DrawText(s, "Home preview", 1514, 70, White);
        string[] tiles = ["P", "L", "+", "M", "S"];
        for (int i = 0; i < tiles.Length; i++)
        {
            int x = 80 + i * 137;
            s.FillRoundedRect(x, 143, 112, 112, 19, Color.FromRgb((byte)(22 + i * 7), (byte)(38 + i * 5), (byte)(57 + i * 6)));
            _heading.DrawText(s, tiles[i], x + 43, 174, White);
        }
        s.DrawRoundedRect(74, 137, 124, 124, 24, White);
        _small.DrawText(s, "Prospero Themes", 222, 286, White);
        for (int y = 765; y < 1080; y++)
            s.FillRectBlended(0, y, 1920, 1, Ink.WithAlpha((byte)Math.Clamp((y - 765) * 235 / 230, 0, 235)));
        int offset = (int)(10 * Math.Exp(-Math.Max(0, seconds - _viewStart) * 10));
        _title.DrawClipped(s, selected?.Manifest.Name ?? "Theme preview", 75, 830 + offset, White, 1330);
        _small.DrawText(s, "SIMULATED HOME SCREEN", 83, 936, Accent);
        int next = Hint(s, PadButton.Cross, "Hide overlay", 1275, 1012, White);
        Hint(s, PadButton.Circle, "Back", next, 1012, White);
    }

    private void DrawFooter(Surface s, ThemeView view)
    {
        s.HLine(80, 1020, 1760, Color.FromRgb(42, 53, 69));
        const int y = 1034;
        int x;
        switch (view)
        {
            case ThemeView.Library:
                x = Hint(s, PadButton.DPad, "Select", 80, y);
                x = Hint(s, PadButton.Cross, "Details", x, y);
                Hint(s, PadButton.Square, "Preview", x, y);
                break;
            case ThemeView.Detail:
                x = Hint(s, PadButton.Cross, Busy ? "Please wait…" : "Apply", 80, y);
                x = Hint(s, PadButton.Square, "Preview", x, y);
                x = Hint(s, PadButton.DPad, "Targets", x, y);
                Hint(s, PadButton.Circle, "Back", x, y);
                break;
            case ThemeView.Community:
                x = Hint(s, PadButton.Cross,
                    SelectedCommunityId is { } id && CommunityUpdates.Contains(id) ? "Update" : "Download", 80, y);
                x = Hint(s, PadButton.Triangle, "Like", x, y);
                x = Hint(s, PadButton.Options, "Refresh", x, y);
                Hint(s, PadButton.Circle, "Library", x, y);
                break;
            case ThemeView.Import:
                x = Hint(s, PadButton.Cross, "Import from USB", 80, y);
                Hint(s, PadButton.Circle, "Library", x, y);
                break;
            case ThemeView.StoreAddress:
                x = Hint(s, PadButton.DPad, "Select", 80, y);
                x = Hint(s, PadButton.Cross, "Type", x, y);
                x = Hint(s, PadButton.Square, "Delete character", x, y);
                x = Hint(s, PadButton.Circle, "Cancel", x, y);
                Hint(s, PadButton.Options, "Save", x, y);
                return;   // the tabs do not switch while typing
            default:
                x = Hint(s, PadButton.Cross, "Restore defaults", 80, y);
                x = Hint(s, PadButton.Triangle, "App backgrounds", x, y);
                x = Hint(s, PadButton.Square, "Store address", x, y);
                x = Hint(s, PadButton.Options, "Firmware report", x, y);
                Hint(s, PadButton.Circle, "Library", x, y);
                break;
        }

        // "L1 / R1  Switch tab", right-aligned so the label ends at x=1840.
        const string tabs = "Switch tab";
        int tabsX = 1840 - _small.MeasureText(tabs);
        int l1 = ButtonGlyph.Width(PadButton.L1, GlyphSize);
        int shoulderX = tabsX - GlyphGap - 2 * l1 - 6;
        int r1X = ButtonGlyph.Draw(s, PadButton.L1, shoulderX, GlyphTop(_small, y), GlyphSize, Ink, Muted, _small) + shoulderX + 6;
        ButtonGlyph.Draw(s, PadButton.R1, r1X, GlyphTop(_small, y), GlyphSize, Ink, Muted, _small);
        _small.DrawText(s, tabs, tabsX, y, Muted);
    }

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
        _small.DrawText(s, label, labelX, y, c);
        return labelX + _small.MeasureText(label) + HintGap;
    }

    /// <summary>White (or muted when inactive) pill with a Cross glyph and dark label. Returns the pill width.</summary>
    private int PillButton(Surface s, int x, int y, string label, bool active)
    {
        const int height = 64, glyph = 30, pad = 24, gap = 14;
        int width = pad + glyph + gap + _body.MeasureText(label) + pad;
        s.FillRoundedRect(x, y, width, height, height / 2, active ? White : Color.FromRgb(55, 70, 89));
        int cy = y + height / 2;
        ButtonGlyph.Draw(s, PadButton.Cross, x + pad, cy - glyph / 2, glyph, active ? White : Muted, PillInk);
        _body.DrawText(s, label, x + pad + glyph + gap, cy - _body.LineHeight / 2, active ? PillInk : Muted);
        return width;
    }

    /// <summary>"1 theme", "3 themes": the count with a regular English plural.</summary>
    /// <summary>"13.000.000" as the console's settings show it ("13.00"); anything else as it came, "unknown" when empty.</summary>
    public static string FirmwareLabel(string version)
    {
        string[] parts = version.Split('.');
        if (parts.Length == 3 && parts[1].Length == 3 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out _))
            return $"{major}.{parts[1][..2]}";
        return version.Length == 0 ? "unknown" : version;
    }

    public static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public static string TargetName(string path, string? screen) => screen switch
    {
        "hub_default" => "App background",
        "ps_store" => "PlayStation Store",
        "game_library" or "library_bg" => "Game library",
        "ps_plus" => "PlayStation Plus",
        "explore" => "Explore",
        _ => path.StartsWith("appmeta/", StringComparison.Ordinal) ? "App: " + path.Split('/')[1] : screen ?? path.Split('/').Last(),
    };

    public void Dispose() { ResetCovers(); _fallback.Dispose(); }
}


internal enum PadButton { Cross, Circle, Square, Triangle, DPad, Options, L1, R1, R2 }

/// <summary>PlayStation-style controller hint icons, drawn with primitives because the UI fonts have no symbol glyphs.</summary>
internal static class ButtonGlyph
{
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
    /// Round buttons are a filled <paramref name="bg"/> disc with the symbol in <paramref name="fg"/>;
    /// DPad and Options are drawn in <paramref name="fg"/> directly; L1/R1 are a <paramref name="bg"/> pill with
    /// the label in <paramref name="fg"/> (the label is skipped when no font is given).
    /// </summary>
    public static int Draw(Surface s, PadButton button, int x, int y, int size, Color fg, Color bg, AtlasFont? font)
    {
        int width = Width(button, size);
        int r = size / 2, cx = x + r, cy = y + r;
        int d = size * 2 / 9; // symbol half-extent, 6px at size 30

        switch (button)
        {
            case PadButton.Cross:
                s.FillCircle(cx, cy, r, bg);
                s.DrawLine(cx - d, cy - d, cx + d, cy + d, fg, 3);
                s.DrawLine(cx - d, cy + d, cx + d, cy - d, fg, 3);
                break;
            case PadButton.Circle:
                s.FillCircle(cx, cy, r, bg);
                s.DrawCircleThick(cx, cy, d + 1, 2, fg);
                break;
            case PadButton.Square:
                s.FillCircle(cx, cy, r, bg);
                s.DrawRectThick(cx - d, cy - d, 2 * d, 2 * d, 2, fg);
                break;
            case PadButton.Triangle:
                s.FillCircle(cx, cy, r, bg);
                s.DrawLine(cx, cy - d - 1, cx - d - 1, cy + d, fg, 2);
                s.DrawLine(cx - d - 1, cy + d, cx + d + 1, cy + d, fg, 2);
                s.DrawLine(cx + d + 1, cy + d, cx, cy - d - 1, fg, 2);
                break;
            case PadButton.DPad:
            {
                int arm = size / 3;
                s.FillRoundedRect(x, cy - arm / 2, size, arm, 3, fg);
                s.FillRoundedRect(cx - arm / 2, y, arm, size, 3, fg);
                break;
            }
            case PadButton.Options:
            {
                int pillH = size * 2 / 3;
                int top = cy - pillH / 2;
                s.DrawRoundedRect(x, top, width, pillH, pillH / 2, fg);
                s.DrawRoundedRect(x + 1, top + 1, width - 2, pillH - 2, pillH / 2 - 1, fg);
                for (int i = -1; i <= 1; i++)
                    s.FillRect(cx - 8, cy + i * 5 - 1, 16, 2, fg);
                break;
            }
            case PadButton.L1:
            case PadButton.R1:
            case PadButton.R2:
            {
                s.FillRoundedRect(x, y, width, size, size / 3, bg);
                if (font is not null)
                {
                    string label = button switch { PadButton.L1 => "L1", PadButton.R1 => "R1", _ => "R2" };
                    int textX = x + (width - font.MeasureText(label)) / 2;
                    font.DrawText(s, label, textX, y + (size - font.LineHeight) / 2, fg);
                }
                break;
            }
        }
        return width;
    }
}
