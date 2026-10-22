using System;
using System.Collections.Generic;
using System.Linq;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>How a notice reads: neutral progress or information, something that worked, something that failed.</summary>
public enum NoticeLevel { Info, Success, Error }

/// <summary>One footer hint: a controller button and what it does. The views build their hint lists from these.</summary>
internal readonly record struct HintItem(PadButton Button, string Label);

public sealed partial class ThemeRenderer
{
    // ---- Notices ----------------------------------------------------------------------------------------------------

    private const double NoticeSeconds = 6.0, NoticeFade = 0.6;
    // A toast starts at the left safe edge. In the footer band (bottom edge on the bottom safe line) when it fits left of
    // the hint bar, which is right-aligned; a long one sits right above the band instead.
    private const int NoticeMaxText = 1300, NoticeBottom = Theme.ScreenHeight - Theme.SafeBottom, NoticeBottomAbove = Theme.FooterY - Theme.Unit * 2;
    private int _hintBarLeft = Theme.ContentRight;
    private IReadOnlyList<string> _noticeLines = Array.Empty<string>();
    private int _noticeTextWidth;
    private NoticeLevel _noticeLevel;
    // The time the notice was posted: set by the first Draw after Notify, since only Draw knows the time.
    private double _noticeAt;
    private bool _noticeFresh;

    /// <summary>
    /// Shows one toast in the footer band, replacing the one on screen. It stays while <see cref="Busy"/> is true and
    /// otherwise fades out about six seconds after it was posted. Text that does not fit in two lines is cut with "…".
    /// </summary>
    public void Notify(string text, NoticeLevel level)
    {
        _noticeLines = _body.Wrap(text, NoticeMaxText, 2);
        int widest = 0;
        foreach (string line in _noticeLines) widest = Math.Max(widest, _body.MeasureText(line));
        _noticeTextWidth = widest;
        _noticeLevel = level;
        _noticeFresh = true;
    }

    /// <summary>Removes the toast at once.</summary>
    public void ClearNotice()
    {
        _noticeLines = Array.Empty<string>();
        _noticeFresh = false;
    }

    private void DrawNotice(Surface s, double seconds)
    {
        if (_noticeLines.Count == 0) return;
        double age = seconds - _noticeAt;
        if (age < 0) { _noticeAt = seconds; age = 0; }   // the clock started over (the PC preview, tests)
        float k = Busy ? 1f : (float)Math.Clamp((NoticeSeconds - age) / NoticeFade, 0, 1);
        if (k <= 0f)
        {
            // Over for good: it must not come back when the next job starts.
            _noticeLines = Array.Empty<string>();
            return;
        }

        const int padX = 32, padY = 12, dot = 16, gap = 16;
        int lineHeight = _body.LineHeight;
        int width = padX + dot + gap + _noticeTextWidth + padX;
        int height = Math.Max(56, _noticeLines.Count * lineHeight + 2 * padY);
        // One line is a pill; two lines are a panel (a pill that tall would look swollen).
        int radius = _noticeLines.Count > 1 ? Theme.RadiusPanel : Theme.RadiusPill(height);
        int x = Theme.SafeX;
        int y = (x + width + 2 * Theme.Unit <= _hintBarLeft ? NoticeBottom : NoticeBottomAbove) - height;
        Paint.FillRoundedRect(s, x, y, width, height, radius, Faded(Ink.WithAlpha(250), k));
        Paint.StrokeRoundedRect(s, x, y, width, height, radius, 1, Faded(Theme.PanelStroke, k));
        Color level = _noticeLevel switch { NoticeLevel.Success => Theme.Success, NoticeLevel.Error => Theme.Danger, _ => Accent };
        Paint.FillCircle(s, x + padX + dot / 2f, y + height / 2f, dot / 2f, Faded(level, k));
        int textTop = y + (height - _noticeLines.Count * lineHeight) / 2;
        for (int i = 0; i < _noticeLines.Count; i++)
            DrawLeft(_body, s, _noticeLines[i], x + padX + dot + gap, textTop + i * lineHeight, Faded(White, k));
    }

    private static Color Faded(Color color, float k) => k >= 1f ? color : color.WithAlpha((byte)(color.A * k));

    // ---- Dialogs ----------------------------------------------------------------------------------------------------

    // The wrapped body text of each dialog; it does not depend on the pack, so it is wrapped once.
    private readonly IReadOnlyList<string>?[] _dialogBody = new IReadOnlyList<string>?[4];
    private const int DialogWidth = 900, DialogPad = 48, DialogRow = 72, DialogGap = 16;

    private static string DialogTitle(DialogKind kind) => kind switch
    {
        DialogKind.Restore => "Restore defaults?",
        DialogKind.Remove => "Remove this theme?",
        _ => "Apply this theme?",
    };

    private static string DialogText(DialogKind kind) => kind switch
    {
        DialogKind.Restore => "Puts the original backgrounds, icons and sounds back on this console. Your theme library stays as it is.",
        DialogKind.ApplyScope => "This theme has a background for apps. Choose where it goes: only apps without their own image, or all apps.",
        DialogKind.Remove => "Takes it out of your library; the file is kept in /data/prospero-themes/attic. If it is applied, it stays on the console until another theme replaces it or you restore the defaults.",
        _ => "This changes the backgrounds, icons and sounds on this console. Restore defaults on the System tab puts the originals back.",
    };

    // What is on screen under a dialog, as it looks once dimmed. The screen behind a dialog is still, so it is dimmed
    // once (a full-screen pass is several milliseconds) and copied on every later frame, until something it shows changes.
    private readonly record struct DialogBase(ThemeView View, string? Path, bool Applied, bool Busy, bool AllApps, bool Replace, int Row,
        string StoreUrl, string Firmware, int Count);
    private PixelBuffer? _dialogBase;
    private DialogBase _dialogKey;

    private DialogBase DialogBaseKey(ThemeBrowser browser, ThemeLibraryEntry? selected, int count) =>
        new(browser.View, selected?.Path, selected is not null && IsApplied(selected), Busy, AllAppsBackgrounds, ReplaceEarlierThemes,
            browser.SystemSelected, browser.StoreUrl, Firmware, count);

    private void ReleaseDialogBase()
    {
        _dialogBase?.Dispose();
        _dialogBase = null;
    }

    /// <summary>Dims the screen drawn so far behind a dialog, and keeps that picture once the view has settled.</summary>
    private void DrawDialogScrim(Surface s, ThemeBrowser browser, ThemeLibraryEntry? selected, int count, double seconds)
    {
        Paint.Veil(s, 0, 0, Theme.ScreenWidth, Theme.ScreenHeight, Ink);
        // A view still sliding in would freeze mid-slide, so it is only kept once it has come to rest.
        if (seconds - _viewStart < 0.6) { ReleaseDialogBase(); return; }
        _dialogBase ??= new PixelBuffer(Theme.ScreenWidth, Theme.ScreenHeight);
        _dialogBase.AsSurface().Blit(s, 0, 0);
        _dialogKey = DialogBaseKey(browser, selected, count);
    }

    /// <summary>The question over Detail or System (on the dimmed screen): the title, what it is about, the options with one focused.</summary>
    private void DrawDialog(Surface s, ThemeBrowser browser, ThemeLibraryEntry? entry)
    {
        DialogKind kind = browser.Dialog;
        IReadOnlyList<string> options = browser.DialogOptions;
        int bodyWidth = DialogWidth - 2 * DialogPad;
        IReadOnlyList<string> body = _dialogBody[(int)kind - 1] ??= _body.Wrap(DialogText(kind), bodyWidth, 5);
        bool named = kind != DialogKind.Restore && entry is not null;

        int height = DialogPad + _screenHeading.LineHeight + 16;
        if (named) height += _label.LineHeight + 8 + _small.LineHeight + 16;
        height += body.Count * _body.LineHeight + 32;
        height += options.Count * DialogRow + (options.Count - 1) * DialogGap + DialogPad;
        int x = (Theme.ScreenWidth - DialogWidth) / 2, y = (Theme.ScreenHeight - height) / 2 - 24;
        Paint.FillRoundedRect(s, x, y, DialogWidth, height, Theme.RadiusPanel, Ink.WithAlpha(244));
        Paint.StrokeRoundedRect(s, x, y, DialogWidth, height, Theme.RadiusPanel, 1, Theme.PanelStroke);

        int tx = x + DialogPad, ty = y + DialogPad;
        ClipLeft(_screenHeading, s, DialogTitle(kind), tx, ty, White, bodyWidth);
        ty += _screenHeading.LineHeight + 16;
        if (named)
        {
            ClipLeft(_label, s, entry!.Manifest.Name, tx, ty, White, bodyWidth);
            ty += _label.LineHeight + 8;
            ClipLeft(_small, s, SummaryOf(entry).Text, tx, ty, Accent, bodyWidth);
            ty += _small.LineHeight + 16;
        }
        foreach (string line in body)
        {
            DrawLeft(_body, s, line, tx, ty, Muted);
            ty += _body.LineHeight;
        }
        ty += 32;
        for (int i = 0; i < options.Count; i++)
        {
            bool focus = i == browser.DialogFocus;
            bool cancel = i == options.Count - 1;
            bool destructive = kind is DialogKind.Restore or DialogKind.Remove && i == 0;
            Paint.FillRoundedRect(s, tx, ty, bodyWidth, DialogRow, Theme.RadiusKey, focus ? White : KeyFill);
            if (focus) Paint.FocusRing(s, tx, ty, bodyWidth, DialogRow, Theme.RadiusKey, Accent);
            Color ink = focus ? PillInk : cancel ? Muted : destructive ? Theme.Danger : White;
            DrawMid(_label, s, options[i], tx + bodyWidth / 2, ty + (DialogRow - _label.LineHeight) / 2, ink);
            ty += DialogRow + DialogGap;
        }
    }

    // ---- Badges -----------------------------------------------------------------------------------------------------

    /// <summary>A small status pill: a dark dot and a caption on a coloured capsule, 40 px high. Returns its width.</summary>
    private int Badge(Surface s, string text, int x, int y, Color fill)
    {
        int width = _small.MeasureText(text) + 52;
        Paint.Capsule(s, x, y, width, 40, fill);
        Paint.FillCircle(s, x + 20f, y + 20f, 6f, PillInk);
        DrawLeft(_small, s, text, x + 36, y + 4, PillInk);
        return width;
    }

    /// <summary>Whether the theme the console shows (<see cref="AppliedId"/>) is this pack.</summary>
    private bool IsApplied(ThemeLibraryEntry entry) => AppliedId is { Length: > 0 } id && id == entry.Manifest.Id;

    // ---- Header and footer ------------------------------------------------------------------------------------------

    /// <summary>Vertical centre of the header row: halfway between the top safe line and the rule.</summary>
    private const int HeaderCenterY = (Theme.SafeTop + Theme.HeaderRuleY) / 2;
    private const int TabHeight = 56, TabPadX = 20, TabGap = 8, TabGlyphGap = 16;
    private static readonly string[] TabNames = ["Library", "Community", "USB Import", "System"];
    // Where the tabs sit: even gaps, the group centred on the screen. Measured once, with the font that is wider (the
    // active tab's), so the tabs do not shift when the active one changes.
    private int[]? _tabX, _tabWidth;
    private int _tabsLeft, _tabsRight;

    private void LayoutTabs()
    {
        _tabX = new int[TabNames.Length];
        _tabWidth = new int[TabNames.Length];
        int total = TabGap * (TabNames.Length - 1);
        for (int i = 0; i < TabNames.Length; i++) total += _tabWidth[i] = _label.MeasureText(TabNames[i]) + 2 * TabPadX;
        int x = _tabsLeft = (Theme.ScreenWidth - total) / 2;
        for (int i = 0; i < TabNames.Length; i++) { _tabX[i] = x; x += _tabWidth[i] + TabGap; }
        _tabsRight = x - TabGap;
    }

    private void DrawHeader(Surface s, ThemeView view)
    {
        // Original mark: an orbit with its own small moon, no platform branding asset. Its left edge is the safe margin.
        Paint.StrokeCircle(s, Theme.SafeX + 20.5f, HeaderCenterY, 20.5f, 2.5f, White);
        Paint.FillCircle(s, Theme.SafeX + 34.5f, HeaderCenterY - 15f, 6.5f, Accent);
        // The wordmark and the tab labels share one baseline.
        int baseline = HeaderCenterY - _label.LineHeight / 2 + Ascent(_label);
        const int wordX = Theme.SafeX + 57;
        DrawLeft(_heading, s, "prospero", wordX, baseline - Ascent(_heading), White);
        DrawLeft(_small, s, "T H E M E S", wordX + _heading.MeasureText("prospero") + 12, baseline - Ascent(_small), Muted);

        if (_tabX is null || _tabWidth is null) LayoutTabs();
        int active = view switch { ThemeView.Community => 1, ThemeView.Import => 2, ThemeView.System or ThemeView.StoreAddress => 3, _ => 0 };
        for (int i = 0; i < TabNames.Length; i++)
        {
            bool on = i == active;
            if (on) Paint.Capsule(s, _tabX![i], HeaderCenterY - TabHeight / 2, _tabWidth![i], TabHeight, White.WithAlpha(34));
            AtlasFont font = on ? _label : _body;
            DrawMid(font, s, TabNames[i], _tabX![i] + _tabWidth![i] / 2, HeaderCenterY - font.LineHeight / 2, on ? White : Muted);
        }
        // L1 and R1 flank the tabs; on the keyboard the tabs do not switch, so the glyphs stay away.
        if (view != ThemeView.StoreAddress)
        {
            int glyphTop = HeaderCenterY - GlyphSize / 2;
            ButtonGlyph.Draw(s, PadButton.L1, _tabsLeft - TabGlyphGap - ButtonGlyph.Width(PadButton.L1, GlyphSize), glyphTop, GlyphSize, Ink, Muted, _small);
            ButtonGlyph.Draw(s, PadButton.R1, _tabsRight + TabGlyphGap, glyphTop, GlyphSize, Ink, Muted, _small);
        }
        Paint.FillRect(s, Theme.SafeX, Theme.HeaderRuleY, Theme.ContentWidth, 1, White.WithAlpha(36));
    }

    /// <summary>
    /// The busy indicator at the right safe edge: eight dots on a ring, brightest at a head that goes round once a
    /// second. The phase is the fraction of <paramref name="seconds"/>, so the same time always draws the same frame.
    /// </summary>
    private void DrawBusySpinner(Surface s, double seconds, bool dimmed)
    {
        const int dots = 8;
        const float ring = 13f, radius = 3f;
        float cx = Theme.ContentRight - 16f, cy = HeaderCenterY;
        double phase = seconds - Math.Floor(seconds);
        for (int i = 0; i < dots; i++)
        {
            double behind = ((phase - (double)i / dots) % 1.0 + 1.0) % 1.0;   // 0 at the head, up to 1 just before it
            double strength = 0.2 + 0.8 * Math.Pow(1.0 - behind, 2.0);
            double angle = i * (2 * Math.PI / dots) - Math.PI / 2;
            byte alpha = (byte)(255 * strength * (dimmed ? 0.4 : 1.0));
            Paint.FillCircle(s, cx + (float)Math.Cos(angle) * ring, cy + (float)Math.Sin(angle) * ring, radius, Accent.WithAlpha(alpha));
        }
    }

    private static readonly HintItem[] DialogHintItems =
        [new(PadButton.DPad, "Select"), new(PadButton.Cross, "OK"), new(PadButton.Circle, "Cancel")];

    /// <summary>The hints while a dialog is open.</summary>
    private HintItem[] DialogHints(ThemeBrowser browser) => DialogHintItems;

    /// <summary>Width of one hint (glyph, gap, label) as <see cref="Hint"/> draws it, without the gap after it.</summary>
    private int HintWidth(HintItem hint) => ButtonGlyph.Width(hint.Button, GlyphSize) + GlyphGap + _small.MeasureText(hint.Label);

    /// <summary>
    /// Draws a row of hints that ends at <paramref name="right"/> (the console's own style: the bar sits at the safe
    /// edge). <paramref name="y"/> is the label text line, as for <see cref="Hint"/>.
    /// </summary>
    private void HintBar(Surface s, ReadOnlySpan<HintItem> hints, int right, int y)
    {
        int total = -HintGap;
        foreach (HintItem hint in hints) total += HintWidth(hint) + HintGap;
        int x = right - total;
        _hintBarLeft = x;
        foreach (HintItem hint in hints) x = Hint(s, hint.Button, hint.Label, x, y);
    }

    /// <summary>
    /// The footer: the hints of the open dialog or of the current view (each view's own <c>...Hints</c> method) as a
    /// bar at the right safe edge. The tabs are not listed: the header shows L1 and R1.
    /// </summary>
    private void DrawFooter(Surface s, ThemeBrowser browser)
    {
        HintItem[] hints = browser.Dialog != DialogKind.None ? DialogHints(browser) : browser.View switch
        {
            ThemeView.Library => LibraryHints(browser),
            ThemeView.Detail => DetailHints(browser),
            ThemeView.Community => CommunityHints(browser),
            ThemeView.Import => ImportHints(browser),
            ThemeView.StoreAddress => StoreAddressHints(browser),
            _ => SystemHints(browser),
        };
        HintBar(s, hints, Theme.ContentRight, Theme.FooterY + (Theme.FooterHeight - _small.LineHeight) / 2);
    }
}
