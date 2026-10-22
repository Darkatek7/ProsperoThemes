using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

public sealed partial class ThemeRenderer
{
    /// <summary>
    /// The app's own version (<c>contentVersion</c> of <c>sce_sys/param.json</c>), shown on the System screen. Empty
    /// when it could not be read; the version line is then left out.
    /// </summary>
    public string AppVersion { get; set; } = "";

    // ---- Shared by System, USB Import and the store-address keyboard ------------------------------------------------

    // Every one of these screens opens with the same block at the left safe edge: an eyebrow in the accent colour over
    // the screen heading, as the Library's hero does. The body starts on the 8 px grid below it.
    private const int SysEyebrowY = Theme.ContentTop + 4, SysHeadingY = Theme.ContentTop + 40;
    internal const int SysBodyTop = 312;
    // The 48 px heading's first letter has a little more side bearing than the small text; this pulls it to the margin.
    private const int SysHeadingOptical = 2;

    private void DrawSysHeading(Surface s, string eyebrow, string heading, int enter)
    {
        DrawLeft(_small, s, eyebrow, Theme.SafeX, SysEyebrowY + enter, Accent);
        DrawLeft(_screenHeading, s, heading, Theme.SafeX - SysHeadingOptical, SysHeadingY + enter, White);
    }

    /// <summary>
    /// A glass panel like <see cref="Paint.Panel"/> (ink at about 75 percent, a faint edge), made cheaper: the body between
    /// the corner rows is dimmed with <see cref="Paint.Veil"/>, a pass of shifts, and only the corner rows are blended.
    /// The System screen is mostly such panels, and a full blend of that much area is the bulk of its frame time on the console.
    /// </summary>
    private static void SysGlass(Surface s, int x, int y, int width, int height, int radius = Theme.RadiusPanel)
    {
        int r = Math.Clamp(radius, 0, Math.Min(width, height) / 2);
        Color glass = Theme.Ink.WithAlpha(191);
        Paint.Veil(s, x, y + r, width, height - 2 * r, Theme.Ink);
        // Each corner band is a rounded rectangle twice its height, seen through a window one band high.
        Paint.FillRoundedRect(s.Region(x, y, width, r), 0, 0, width, 2 * r, r, glass);
        Paint.FillRoundedRect(s.Region(x, y + height - r, width, r), 0, -r, width, 2 * r, r, glass);
        Paint.StrokeRoundedRect(s, x, y, width, height, radius, 1, Theme.PanelStroke);
    }

    // ---- Footer hints -----------------------------------------------------------------------------------------------

    private static readonly HintItem[] SystemChangeHints = [new(PadButton.Cross, "Change"), new(PadButton.Circle, "Back")];
    private static readonly HintItem[] SystemChooseHints = [new(PadButton.Cross, "Choose"), new(PadButton.Circle, "Back")];
    // The button on the screen already says "Import from USB", so the footer only adds the way out.
    private static readonly HintItem[] ImportHintItems = [new(PadButton.Circle, "Library")];
    private static readonly HintItem[] StoreAddressHintItems =
        [new(PadButton.Cross, "Type"), new(PadButton.Square, "Delete"), new(PadButton.Circle, "Cancel"), new(PadButton.Options, "Save")];

    /// <summary>The footer hints of the System list: a row that holds a value says "Change", the others "Choose".</summary>
    private HintItem[] SystemHints(ThemeBrowser browser) =>
        (SystemRow)browser.SystemSelected is SystemRow.Backgrounds or SystemRow.EarlierThemes or SystemRow.Sounds or SystemRow.StoreAddress
            ? SystemChangeHints : SystemChooseHints;

    private HintItem[] ImportHints(ThemeBrowser browser) => ImportHintItems;

    private HintItem[] StoreAddressHints(ThemeBrowser browser) => StoreAddressHintItems;

    // ---- USB Import -------------------------------------------------------------------------------------------------

    // One centred glass card: the heading, one line of explanation, three numbered steps, the button and the last result.
    internal const int ImportCardWidth = 1000, ImportCardPad = 56;
    private const int ImportBadge = 40, ImportStepGap = 24, ImportStatusLines = 2;
    private static readonly string[] ImportSteps =
    [
        "Put .p5theme, .zip or .tar files on your USB drive.",
        "Use the main folder or a folder named prospero-themes.",
        "Plug in the drive and start the import.",
    ];
    private static readonly string[] ImportDigits = ["1", "2", "3"];
    // The last result, wrapped once per message (it changes only when an import or a scan finishes).
    private string? _importStatusFor;
    private IReadOnlyList<string> _importStatusLines = Array.Empty<string>();

    /// <summary>
    /// Where the import card sits: centred on the screen, between the header rule and the footer (its top on the 8 px
    /// grid). Its height comes from the fonts it holds: padding, heading, explanation, steps, rule, button, result, padding.
    /// </summary>
    internal (int X, int Y, int Width, int Height) ImportCard()
    {
        int height = 48 + _screenHeading.LineHeight + 8 + _body.LineHeight + 32 + 3 * ImportBadge + 2 * ImportStepGap + 32 + 1 + 32 + 64 + 24
            + ImportStatusLines * _small.LineHeight + 40;
        return ((Theme.ScreenWidth - ImportCardWidth) / 2, Theme.ContentTop + (Theme.ContentBottom - Theme.ContentTop - height) / 16 * 8, ImportCardWidth, height);
    }

    private void DrawImport(Surface s, int enter)
    {
        ReleasePreviewStrips();
        var (x, cardY, _, cardHeight) = ImportCard();
        int y = cardY + enter;
        SysGlass(s, x, y, ImportCardWidth, cardHeight);

        int left = x + ImportCardPad, inner = ImportCardWidth - 2 * ImportCardPad;
        int top = y + 48;
        ClipLeft(_screenHeading, s, "Import from USB", left - SysHeadingOptical, top, White, inner);
        top += _screenHeading.LineHeight + 8;
        ClipLeft(_body, s, "Copy theme packs from a USB drive into your library.", left, top, Muted, inner);
        top += _body.LineHeight + 32;

        // The digit and the step text share a baseline that runs through the middle of the badge.
        int textTop = ImportBadge / 2 + 10 - Ascent(_body);
        for (int i = 0; i < ImportSteps.Length; i++)
        {
            Paint.FillCircle(s, left + ImportBadge / 2f, top + ImportBadge / 2f, ImportBadge / 2f, Accent);
            DrawMid(_label, s, ImportDigits[i], left + ImportBadge / 2, top + ImportBadge / 2 + 10 - Ascent(_label), PillInk);
            ClipLeft(_body, s, ImportSteps[i], left + ImportBadge + 24, top + textTop, White, inner - ImportBadge - 24);
            top += ImportBadge + ImportStepGap;
        }

        top += 32 - ImportStepGap;
        Paint.FillRect(s, left, top, inner, 1, Theme.PanelStroke);
        top += 1 + 32;
        PillButton(s, left, top, Busy ? "Importing…" : "Import from USB", !Busy);
        top += 64 + 24;

        if (!ReferenceEquals(_importStatusFor, LibraryStatus))
        {
            _importStatusFor = LibraryStatus;
            _importStatusLines = _small.Wrap(LibraryStatus, inner, ImportStatusLines);
        }
        for (int i = 0; i < _importStatusLines.Count; i++)
            DrawLeft(_small, s, _importStatusLines[i], left, top + i * _small.LineHeight, Muted);
    }

    // ---- System -----------------------------------------------------------------------------------------------------

    private static readonly string[] SystemLabels = ["App backgrounds", "Earlier themes", "Music and sounds", "Store address", "Firmware report", "Restore defaults"];
    private static readonly string[] SystemDescriptions =
    [
        "Which apps show a theme’s background image",
        "Whether a new theme keeps what earlier themes changed",
        "Whether themes may change the music and sounds",
        "Where the Community tab loads themes from",
        "Save a list of the console’s theme files",
        "Put every background, icon and sound back to the original",
    ];

    internal const int SysRowsTop = SysBodyTop, SysRowH = 64, SysRowGap = 8;
    private const int SysRowPad = 32, SysLabelWidth = 288;
    // Equal rows, then the info panel below them.
    private const int SysInfoTop = SysRowsTop + ThemeBrowser.SystemRowCount * SysRowH + (ThemeBrowser.SystemRowCount - 1) * SysRowGap + 32, SysInfoPad = 28, SysInfoPitch = 40, SysInfoLabelWidth = 176;
    private const int SysSegmentHeight = 48, SysSegmentInset = 4, SysSegmentPad = 20, SysUrlMaxWidth = 640;
    // A focused row is lighter than the glass of the others; its text steps up a little with it.
    internal static readonly Color SysRowFocusFill = Color.FromRgb(46, 62, 84);
    private static readonly Color SysFocusText = Color.FromRgb(196, 208, 226);
    private static readonly Color SysSegmentTrack = Theme.White.WithAlpha(26);

    // The two states of the App backgrounds and Earlier themes rows, as segmented controls. Measured once, with the
    // font they are drawn in.
    private const string SysSegmentOwn = "Without own image", SysSegmentAll = "All apps";
    private const string SysSegmentKeep = "Combine", SysSegmentReplace = "Replace", SysSegmentOn = "On", SysSegmentOff = "Off";
    private int _sysSegmentOwnWidth, _sysSegmentAllWidth, _sysSegmentKeepWidth, _sysSegmentReplaceWidth, _sysSegmentOnWidth, _sysSegmentOffWidth;
    // Texts of the info panel, rebuilt only when what they show changes.
    private string _sysCountText = "", _sysFirmwareText = "";
    private int _sysCountFor = -1;
    private string? _sysFirmwareFor;
    // The store address as the row shows it: its width, measured once per address.
    private string? _sysUrlFor;
    private int _sysUrlWidth;

    private void DrawSystem(Surface s, ThemeBrowser browser, int count, int enter)
    {
        ReleasePreviewStrips();
        DrawSysHeading(s, "PROSPERO THEMES", "System", enter);

        if (_sysSegmentOwnWidth == 0)
        {
            _sysSegmentOwnWidth = _small.MeasureText(SysSegmentOwn) + 2 * SysSegmentPad;
            _sysSegmentAllWidth = _small.MeasureText(SysSegmentAll) + 2 * SysSegmentPad;
            _sysSegmentKeepWidth = _small.MeasureText(SysSegmentKeep) + 2 * SysSegmentPad;
            _sysSegmentReplaceWidth = _small.MeasureText(SysSegmentReplace) + 2 * SysSegmentPad;
            // On and Off are as wide as each other, so the thumb does not change size.
            _sysSegmentOnWidth = _sysSegmentOffWidth = Math.Max(_small.MeasureText(SysSegmentOn), _small.MeasureText(SysSegmentOff)) + 2 * SysSegmentPad + 16;
        }

        // Every row first, then the one focus ring: its glow reaches past the gap into the neighbours.
        int right = Theme.ContentRight - SysRowPad;
        for (int i = 0; i < SystemLabels.Length; i++)
        {
            int y = SysRowsTop + i * (SysRowH + SysRowGap) + enter;
            bool focus = i == browser.SystemSelected;
            if (focus) Paint.FillRoundedRect(s, Theme.SafeX, y, Theme.ContentWidth, SysRowH, Theme.RadiusKey, SysRowFocusFill);
            else SysGlass(s, Theme.SafeX, y, Theme.ContentWidth, SysRowH, Theme.RadiusKey);

            bool restore = i == (int)SystemRow.Restore;
            Color text = focus ? SysFocusText : Muted;
            int textY = y + (SysRowH - _label.LineHeight) / 2;
            DrawLeft(_label, s, SystemLabels[i], Theme.SafeX + SysRowPad, textY, restore ? Theme.Danger : White);

            // The affordance at the right edge; it also says how much room the description has.
            int descriptionRight = right;
            switch ((SystemRow)i)
            {
                case SystemRow.Backgrounds:
                    descriptionRight = DrawSysSegments(s, right, y, SysSegmentOwn, _sysSegmentOwnWidth,
                        SysSegmentAll, _sysSegmentAllWidth, AllAppsBackgrounds) - 24;
                    break;
                case SystemRow.EarlierThemes:
                    descriptionRight = DrawSysSegments(s, right, y, SysSegmentKeep, _sysSegmentKeepWidth,
                        SysSegmentReplace, _sysSegmentReplaceWidth, ReplaceEarlierThemes) - 24;
                    break;
                case SystemRow.Sounds:
                    descriptionRight = DrawSysSegments(s, right, y, SysSegmentOn, _sysSegmentOnWidth,
                        SysSegmentOff, _sysSegmentOffWidth, !Sounds) - 24;
                    break;
                case SystemRow.StoreAddress:
                    descriptionRight = DrawSysUrl(s, browser.StoreUrl, right, y, textY, focus) - 24;
                    break;
                case SystemRow.FirmwareReport:
                    int chevron = DrawSysChevron(s, right, y + SysRowH / 2, focus ? White : Muted);
                    DrawRight(_label, s, "Save", chevron - 12, textY, Accent);
                    descriptionRight = chevron - 12 - _label.MeasureText("Save") - 24;
                    break;
                default:
                    DrawSysChevron(s, right, y + SysRowH / 2, restore ? Theme.Danger : focus ? White : Muted);
                    break;
            }
            int descriptionX = Theme.SafeX + SysRowPad + SysLabelWidth;
            ClipLeft(_body, s, SystemDescriptions[i], descriptionX, textY, text, descriptionRight - descriptionX);
        }
        if (browser.SystemSelected is >= 0 and < ThemeBrowser.SystemRowCount)
        {
            int y = SysRowsTop + browser.SystemSelected * (SysRowH + SysRowGap) + enter;
            Paint.FocusRing(s, Theme.SafeX, y, Theme.ContentWidth, SysRowH, Theme.RadiusKey);
        }

        DrawSysInfo(s, count, enter);
    }

    // A two-state control of a System row: a track with both choices and a white thumb under the active one.
    // Returns its left edge.
    private int DrawSysSegments(Surface s, int right, int rowY, string first, int firstW, string second, int secondW, bool onSecond)
    {
        int trackW = firstW + secondW + 2 * SysSegmentInset;
        int x = right - trackW, y = rowY + (SysRowH - SysSegmentHeight) / 2;
        int thumbH = SysSegmentHeight - 2 * SysSegmentInset;
        Paint.Capsule(s, x, y, trackW, SysSegmentHeight, SysSegmentTrack);
        Paint.Capsule(s, onSecond ? x + SysSegmentInset + firstW : x + SysSegmentInset, y + SysSegmentInset,
            onSecond ? secondW : firstW, thumbH, White);
        int textY = y + SysSegmentInset + (thumbH - _small.LineHeight) / 2;
        DrawMid(_small, s, first, x + SysSegmentInset + firstW / 2, textY, onSecond ? Muted : PillInk);
        DrawMid(_small, s, second, x + SysSegmentInset + firstW + secondW / 2, textY, onSecond ? PillInk : Muted);
        return x;
    }

    // The store address (or that none is set), right-aligned in front of the chevron. Returns the left edge of what it drew.
    private int DrawSysUrl(Surface s, string url, int right, int rowY, int textY, bool focus)
    {
        int chevron = DrawSysChevron(s, right, rowY + SysRowH / 2, focus ? White : Muted);
        bool set = url.Length > 0;
        string shown = set ? url : "Not set";
        if (!ReferenceEquals(_sysUrlFor, shown))
        {
            _sysUrlFor = shown;
            _sysUrlWidth = Math.Min(_body.MeasureText(shown), SysUrlMaxWidth);
        }
        int x = chevron - 16 - _sysUrlWidth;
        ClipLeft(_body, s, shown, x, textY, set ? White : focus ? SysFocusText : Muted, SysUrlMaxWidth);
        return x;
    }

    // A small right-pointing chevron whose tip is at <right>; returns its left edge.
    private static int DrawSysChevron(Surface s, int right, int centerY, Color color)
    {
        const float half = 8f, depth = 7f, thickness = 2.5f;
        float tip = right - thickness / 2f;
        Paint.Line(s, tip - depth, centerY - half, tip, centerY, thickness, color);
        Paint.Line(s, tip, centerY, tip - depth, centerY + half, thickness, color);
        return right - (int)depth - 3;
    }

    // Library count, storage, pack format, firmware and the app's version, as caption rows in two columns of one glass panel.
    private void DrawSysInfo(Surface s, int count, int enter)
    {
        if (_sysCountFor != count) { _sysCountFor = count; _sysCountText = Plural(count, "theme"); }
        if (!ReferenceEquals(_sysFirmwareFor, Firmware)) { _sysFirmwareFor = Firmware; _sysFirmwareText = FirmwareLabel(Firmware); }

        int y = SysInfoTop + enter;
        // The version row is only there when the app could read its version; the panel is as tall as its rows.
        int rows = AppVersion.Length > 0 ? 3 : 2;
        SysGlass(s, Theme.SafeX, y, Theme.ContentWidth, 2 * SysInfoPad + rows * SysInfoPitch - 8);
        int leftX = Theme.SafeX + SysRowPad, rightX = Theme.ScreenWidth / 2;
        int rowY = y + SysInfoPad;
        SysInfoRow(s, "Library", _sysCountText, leftX, rowY, rightX - leftX - SysInfoLabelWidth - 32);
        SysInfoRow(s, "Firmware", _sysFirmwareText, leftX, rowY + SysInfoPitch, rightX - leftX - SysInfoLabelWidth - 32);
        if (AppVersion.Length > 0)
            SysInfoRow(s, "App version", AppVersion, leftX, rowY + 2 * SysInfoPitch, rightX - leftX - SysInfoLabelWidth - 32);
        int valueWidth = Theme.ContentRight - SysRowPad - rightX - SysInfoLabelWidth;
        SysInfoRow(s, "Storage", "/data/prospero-themes/library", rightX, rowY, valueWidth);
        SysInfoRow(s, "Pack format", "Format 2 · ZIP / TAR", rightX, rowY + SysInfoPitch, valueWidth);
    }

    private void SysInfoRow(Surface s, string label, string value, int x, int y, int valueWidth)
    {
        DrawLeft(_small, s, label, x, y, Muted);
        ClipLeft(_small, s, value, x + SysInfoLabelWidth, y, White, valueWidth);
    }

    // ---- Store address ----------------------------------------------------------------------------------------------

    // The key grid fills the width between the safe margins exactly: ten columns and nine gaps.
    internal const int KeyboardGap = 12, KeyboardKeyH = 88, KeyboardKeysTop = 440;
    internal const int KeyboardUnit = (Theme.ContentWidth - 9 * KeyboardGap) / StoreAddressEditor.Columns;
    private const int KeyboardBoxY = 304, KeyboardBoxH = 72, KeyboardBoxPad = 24, KeyboardNoticeY = 388;
    private static readonly Color KeyboardPlaceholder = Color.FromRgb(104, 118, 140);
    // The counter text, and which part of a long address the box shows; both only change when the text does.
    private int _keyboardCountFor = -1;
    private string _keyboardCount = "";
    private string? _keyboardTextFor;
    private int _keyboardStart, _keyboardShownWidth;

    /// <summary>The on-screen keyboard for the store address: input box with caret, key grid, and one line of help.</summary>
    private void DrawStoreAddress(Surface s, StoreAddressEditor editor, int enter, double seconds)
    {
        ReleasePreviewStrips();
        // The cover behind the screen is already calmed (see Backdrop), so the keys read the same on any theme.
        DrawSysHeading(s, "COMMUNITY STORE", "Store address", enter);
        string text = editor.Text;
        if (_keyboardCountFor != text.Length)
        {
            _keyboardCountFor = text.Length;
            _keyboardCount = $"{text.Length} / {StoreAddressEditor.MaxLength}";
        }
        // The counter sits on the heading's baseline at the right safe edge.
        DrawRight(_small, s, _keyboardCount, Theme.ContentRight, SysHeadingY + enter + Ascent(_screenHeading) - Ascent(_small), Muted);

        int boxY = KeyboardBoxY + enter;
        SysGlass(s, Theme.SafeX, boxY, Theme.ContentWidth, KeyboardBoxH, Theme.RadiusKey);
        Paint.StrokeRoundedRect(s, Theme.SafeX, boxY, Theme.ContentWidth, KeyboardBoxH, Theme.RadiusKey, 2, Accent);
        int textY = boxY + (KeyboardBoxH - _body.LineHeight) / 2, textX = Theme.SafeX + KeyboardBoxPad;
        int caretX;
        if (text.Length == 0)
        {
            // The caret waits in front of the placeholder, where the first letter will go, instead of on top of its "h".
            DrawLeft(_body, s, "http://192.168.0.5:8080", textX, textY, KeyboardPlaceholder);
            caretX = textX - 5;
        }
        else
        {
            // Long addresses show their end, behind an ellipsis, so the caret never leaves the box.
            if (!ReferenceEquals(_keyboardTextFor, text)) MeasureKeyboardText(text);
            if (_keyboardStart > 0)
            {
                DrawLeft(_body, s, "…", textX, textY, Muted);
                textX += _body.MeasureText("…");
            }
            _body.DrawText(s, text.AsSpan(_keyboardStart), textX - GlyphPad, textY, White);
            textX += _keyboardShownWidth;
            caretX = textX + 2;
        }
        if (seconds % 1.0 < .6) s.FillRect(caretX, textY + 3, 3, _body.LineHeight - 6, Accent);

        if (editor.Notice.Length > 0) ClipLeft(_small, s, editor.Notice, Theme.SafeX, KeyboardNoticeY + enter, Warn, Theme.ContentWidth);
        else ClipLeft(_small, s, "Leave empty and save to remove the store.", Theme.SafeX, KeyboardNoticeY + enter, Muted, Theme.ContentWidth);

        // Every key first, then the one focus ring: it reaches past the gap into the neighbouring keys.
        int focusX = 0, focusY = 0, focusW = 0;
        for (int row = 0; row < StoreAddressEditor.Layout.Count; row++)
        {
            int column = 0;
            for (int i = 0; i < StoreAddressEditor.Layout[row].Count; i++)
            {
                EditorKey key = StoreAddressEditor.Layout[row][i];
                int x = Theme.SafeX + column * (KeyboardUnit + KeyboardGap), y = KeyboardKeysTop + row * (KeyboardKeyH + KeyboardGap) + enter;
                int w = key.Width * KeyboardUnit + (key.Width - 1) * KeyboardGap;
                bool focus = row == editor.Row && i == editor.Column;
                Paint.FillRoundedRect(s, x, y, w, KeyboardKeyH, Theme.RadiusKey, focus ? White : KeyFill);
                AtlasFont font = key.Width > 1 ? _body : _heading;
                DrawMid(font, s, key.Label, x + w / 2, y + (KeyboardKeyH - font.LineHeight) / 2, focus ? PillInk : key.Width > 1 ? Accent : White);
                if (focus) { focusX = x; focusY = y; focusW = w; }
                column += key.Width;
            }
        }
        if (focusW > 0) Paint.FocusRing(s, focusX, focusY, focusW, KeyboardKeyH, Theme.RadiusKey, Accent);
    }

    // Finds how much of the end of a long address fits the box (behind an ellipsis, when it is cut) and how wide that is.
    private void MeasureKeyboardText(string text)
    {
        _keyboardTextFor = text;
        int room = Theme.ContentWidth - 2 * KeyboardBoxPad - 6, dots = _body.MeasureText("…");
        int total = _body.MeasureText(text);
        if (total <= room) { _keyboardStart = 0; _keyboardShownWidth = total; return; }
        int width = dots, start = text.Length;
        while (start > 0 && width + _body.MeasureText(text.AsSpan(start - 1, 1)) <= room)
            width += _body.MeasureText(text.AsSpan(--start, 1));
        _keyboardStart = start;
        _keyboardShownWidth = width - dots;
    }
}
