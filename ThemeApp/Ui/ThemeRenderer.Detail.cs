using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

public sealed partial class ThemeRenderer
{
    // The screen shows Apply and Preview as buttons, so the footer only adds removing and the way out. The theme that
    // ships with the app cannot be removed.
    private static readonly HintItem[] DetailHintItems = [new(PadButton.Triangle, "Remove"), new(PadButton.Circle, "Back")];
    private static readonly HintItem[] BundledDetailHintItems = [new(PadButton.Circle, "Back")];

    /// <summary>The footer hints of the theme detail.</summary>
    private HintItem[] DetailHints(ThemeBrowser browser) => _selected is { Bundled: false } ? DetailHintItems : BundledDetailHintItems;

    // ---- Layout -----------------------------------------------------------------------------------------------------

    // The cover on the left (16:9, from the safe edge and the top of the content area); the glass panel on the right,
    // from the same top to the bottom of the content area, so nothing reaches the footer row.
    internal const int DetailCoverW = 1040, DetailCoverH = 585;
    internal const int DetailPanelX = 1176, DetailPanelY = Theme.ContentTop;
    internal const int DetailPanelW = Theme.ContentRight - DetailPanelX, DetailPanelH = Theme.ContentBottom - Theme.ContentTop;
    // Text starts 32 px inside the panel on every side.
    internal const int DetailPad = 32, DetailTextX = DetailPanelX + DetailPad, DetailTextW = DetailPanelW - 2 * DetailPad;
    internal const int DetailTextRight = DetailTextX + DetailTextW;
    // The buttons sit on the panel's bottom padding, the pack check line right above them.
    internal const int DetailPillH = 64, DetailPillGap = 16;
    internal const int DetailPillsY = DetailPanelY + DetailPanelH - DetailPad - DetailPillH;
    internal const int DetailCheckHeight = 32, DetailCheckGap = 8, DetailCheckY = DetailPillsY - DetailCheckGap - DetailCheckHeight;
    // The list of changes may use the space down to a small gap above the check line.
    internal const int DetailRowsBottom = DetailCheckY - 8;
    // One row of the list. Its text line sits 5 px down, which centres the text between the hairlines; the last row has
    // no hairline below it, so it only needs its text line and that offset.
    internal const int DetailRowStep = 48, DetailRowInset = 5, DetailLastRow = DetailRowInset + 39, DetailMoreBlock = 8 + 32;
    // The description shows up to four lines. To fit the whole list it gives lines up, down to two (when it has that many).
    internal const int DetailDescriptionLines = 4, DetailDescriptionMin = 2;

    // ---- Cache ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Everything of the detail panel that depends only on the pack: the wrapped texts, the list of what it changes
    /// with its counts already as text, and where each block sits. Built once when another pack is opened (or when the
    /// library was scanned again, which makes new manifest objects), so a frame only draws.
    /// </summary>
    internal sealed class DetailText(string path, ThemePackManifest manifest, PackReport report)
    {
        public readonly string Path = path;
        public readonly ThemePackManifest Manifest = manifest;
        public readonly PackReport Report = report;
        /// <summary>Name of the panel's picture in the image cache (see <see cref="ThemeRenderer.BuildDetailPanel"/>).</summary>
        public readonly string PanelKey = "detail-panel:" + path;
        /// <summary>The fit the texts were made with; null while it is not known.</summary>
        public PackFit? Fit;
        public string Check = "";
        public bool CheckWarn;
        public bool[] RowShort = Array.Empty<bool>();
        public IReadOnlyList<string> Name = Array.Empty<string>(), Description = Array.Empty<string>();
        public string Meta = "";
        public bool Clean;
        // Y of each block, without the slide-in offset. RuleY is the hairline under the description.
        public int EyebrowTop, NameTop, MetaTop, DescriptionTop, RuleY, LabelTop, RowsTop;
        public string[] RowLabels = Array.Empty<string>(), RowCounts = Array.Empty<string>();
        public int Rows;                 // rows drawn (the first ones, when not all fit)
        public string More = "";         // "+3 more" when rows were left out
        public bool Nothing;             // the pack lists no change at all
    }

    private DetailText? _detail;

    /// <summary>
    /// What the console lacks of each library pack (see <see cref="PackFit"/>), by pack path; set by the app when its
    /// background check is done. A pack without an entry shows no fit.
    /// </summary>
    public Dictionary<string, PackFit> PackFits { get; } = new(StringComparer.Ordinal);
    // Width of the wider label of the Apply button ("Apply theme" or "Please wait…"), measured on first use.
    private int _detailApplyLabelWidth;

    internal DetailText DetailTextOf(ThemeLibraryEntry entry)
    {
        PackFits.TryGetValue(entry.Path, out PackFit? fit);
        if (_detail is { } cached && cached.Path == entry.Path && ReferenceEquals(cached.Manifest, entry.Manifest)
            && ReferenceEquals(cached.Report, entry.Report) && ReferenceEquals(cached.Fit, fit)) return cached;
        // The panel picture of the pack that was open before is of no use any more; keep the image cache free of it.
        if (_detail is { } previous) _libraryImages.Remove(previous.PanelKey);

        var t = new DetailText(entry.Path, entry.Manifest, entry.Report) { Fit = fit };
        PackSummary summary = SummaryOf(entry);
        // The rows of the targets this console has no file for: "12 of 16" instead of "16".
        PackSummary? missing = fit is null ? null : PackSummary.Of(fit.Missing);

        t.Name = _screenHeading.Wrap(entry.Manifest.Name, DetailTextW, 2);
        t.Clean = entry.Report.Issues.Count == 0;
        string author = entry.Manifest.Author, version = entry.Manifest.Version;
        t.Meta = author.Length > 0 && version.Length > 0 ? $"{author}  ·  Version {version}"
            : version.Length > 0 ? "Version " + version : author;

        // The list: the groups, then the files that belong to none of them.
        int groups = summary.Groups.Count, rows = groups + (summary.Other > 0 ? 1 : 0);
        var labels = new string[rows];
        var counts = new string[rows];
        var lacking = new bool[rows];
        for (int i = 0; i < groups; i++)
        {
            (string label, int count) = summary.Groups[i];
            labels[i] = label;
            (counts[i], lacking[i]) = FitCount(count, missing?.Count(summary.Kinds[i]) ?? 0);
        }
        if (summary.Other > 0)
        {
            labels[groups] = summary.Other == 1 ? "Other file" : "Other files";
            (counts[groups], lacking[groups]) = FitCount(summary.Other, missing?.Other ?? 0);
        }
        t.RowLabels = labels;
        t.RowCounts = counts;
        t.RowShort = lacking;

        // The pack check, then what this console has of it once that is known.
        string check = t.Clean ? "Pack checked" : "Pack checked · has warnings";
        int gone = fit?.Missing.Count ?? 0;
        t.Check = fit is null ? check : gone == 0 ? check + " · all files on this console" : $"{check} · {Plural(gone, "file")} not on this console";
        t.CheckWarn = !t.Clean || gone > 0;
        t.Nothing = rows == 0;

        // The list matters more than the description: the description gives up lines (down to two) until every row
        // fits; only when even that is not enough are the last rows replaced by "+N more".
        int available = Math.Min(DetailDescriptionLines, _body.Wrap(entry.Manifest.Description, DetailTextW, DetailDescriptionLines).Count);
        int lines = available;
        while (true)
        {
            PlaceDetail(t, entry.Manifest.Description, lines);
            int room = DetailRowsBottom - t.RowsTop;
            if (rows <= 1 || (rows - 1) * DetailRowStep + DetailLastRow <= room) { t.Rows = rows; break; }
            if (lines > Math.Min(available, DetailDescriptionMin)) { lines--; continue; }
            // Still too many: the rows that fit above the "+N more" line, at least one.
            t.Rows = Math.Clamp((room - DetailMoreBlock) / DetailRowStep, 1, rows - 1);   // rows >= 2 here
            t.More = $"+{rows - t.Rows} more";
            break;
        }
        return _detail = t;
    }

    // The count of a row: "16", or "12 of 16" in the warning colour when the console lacks files of it ("Missing" for a
    // row without a number).
    private static (string Text, bool Short) FitCount(int count, int missing) =>
        missing <= 0 ? (count > 1 ? count.ToString() : "", false)
        : count > 1 ? ($"{count - missing} of {count}", true) : ("Missing", true);

    // Wraps the description to the given number of lines and stacks the blocks of the panel from the top.
    private void PlaceDetail(DetailText t, string description, int lines)
    {
        t.Description = lines > 0 ? _body.Wrap(description, DetailTextW, lines) : Array.Empty<string>();
        int y = t.EyebrowTop = DetailPanelY + 24;
        y += _small.LineHeight;
        t.NameTop = y;
        y += t.Name.Count * _screenHeading.LineHeight;
        t.MetaTop = y;
        if (t.Meta.Length > 0) y += _small.LineHeight;
        if (t.Description.Count > 0)
        {
            t.DescriptionTop = y + 16;
            y = t.DescriptionTop + t.Description.Count * _body.LineHeight;
        }
        t.RuleY = y + 16;
        t.LabelTop = t.RuleY + 1 + 16;
        t.RowsTop = t.LabelTop + _label.LineHeight + 8;
    }

    // ---- Screen -----------------------------------------------------------------------------------------------------

    private void DrawDetail(Surface s, ThemeBrowser browser, ThemeLibraryEntry entry, int enter)
    {
        DetailText t = DetailTextOf(entry);

        (_libraryImages.TryGet(entry.Path, DetailCoverW, DetailCoverH, Theme.RadiusCard)
            ?? _libraryImages.Add(entry.Path, Cover(entry), DetailCoverW, DetailCoverH, Theme.RadiusCard)).Draw(s, Theme.SafeX, Theme.ContentTop);
        if (IsApplied(entry)) Badge(s, "Applied", Theme.SafeX + 16, Theme.ContentTop + 16, Theme.Success);

        // The glass panel is a big translucent fill, the costliest thing on this screen, and it never changes while the
        // pack is open. At rest it is copied from a picture made once (the first frame of the pack, which is already the
        // busy one); while the view slides in, it is drawn directly at its offset.
        CachedImage panel = _libraryImages.TryGet(t.PanelKey, DetailPanelW, DetailPanelH) ?? BuildDetailPanel(entry, t);
        if (enter == 0) panel.Draw(s, DetailPanelX, DetailPanelY);
        else PaintDetailPanel(s, t, 0, enter);

        // The buttons are live (Apply reads "Please wait…" and is dimmed while a job runs), so they are drawn every frame.
        // Preview stays where it is when the label of Apply changes: it sits after the wider of the two.
        string label = Busy ? "Please wait…" : "Apply theme";
        if (_detailApplyLabelWidth == 0) _detailApplyLabelWidth = Math.Max(_label.MeasureText("Apply theme"), _label.MeasureText("Please wait…"));
        int apply = PillButton(s, DetailTextX, DetailPillsY + enter, label, !Busy);
        GlassPill(s, DetailTextX + apply + (_detailApplyLabelWidth - _label.MeasureText(label)) + DetailPillGap, DetailPillsY + enter, "Preview", PadButton.Square);
    }

    /// <summary>
    /// The picture of the resting panel: the panel and its texts painted over the backdrop of this pack, cut to the
    /// panel's rectangle and kept in the image cache (so a library scan, which clears the cache, drops it too).
    /// </summary>
    private CachedImage BuildDetailPanel(ThemeLibraryEntry entry, DetailText t)
    {
        using var picture = new PixelBuffer(DetailPanelW, DetailPanelH);
        Surface canvas = picture.AsSurface();
        // The same backdrop Draw puts under the screen (the buffer of the cache; this does not build one).
        canvas.Blit(BlurredBackdrop(entry).AsSurface().Region(DetailPanelX, DetailPanelY, DetailPanelW, DetailPanelH), 0, 0);
        PaintDetailPanel(canvas, t, -DetailPanelX, -DetailPanelY);
        return _libraryImages.Add(t.PanelKey, picture, DetailPanelW, DetailPanelH);
    }

    /// <summary>
    /// Draws the panel and everything in it except the buttons, with (dx, dy) added to every position: (0, slide-in
    /// offset) on the screen, minus the panel's corner when painting its picture.
    /// </summary>
    private void PaintDetailPanel(Surface s, DetailText t, int dx, int dy)
    {
        int x = DetailTextX + dx, right = DetailTextRight + dx;
        Paint.Panel(s, DetailPanelX + dx, DetailPanelY + dy, DetailPanelW, DetailPanelH);
        DrawLeft(_small, s, "THEME DETAILS", x, t.EyebrowTop + dy, Accent);
        for (int i = 0; i < t.Name.Count; i++)
            DrawLeft(_screenHeading, s, t.Name[i], x, t.NameTop + i * _screenHeading.LineHeight + dy, White);
        if (t.Meta.Length > 0) ClipLeft(_small, s, t.Meta, x, t.MetaTop + dy, Muted, DetailTextW);
        for (int i = 0; i < t.Description.Count; i++)
            DrawLeft(_body, s, t.Description[i], x, t.DescriptionTop + i * _body.LineHeight + dy, Muted);

        Paint.FillRect(s, x, t.RuleY + dy, DetailTextW, 1, Theme.Hairline);
        DrawLeft(_label, s, "What it changes", x, t.LabelTop + dy, White);
        if (t.Nothing) DrawLeft(_body, s, "Nothing listed.", x, t.RowsTop + DetailRowInset + dy, Muted);
        for (int i = 0; i < t.Rows; i++)
        {
            int top = t.RowsTop + i * DetailRowStep + dy;
            DrawLeft(_body, s, t.RowLabels[i], x, top + DetailRowInset, White);
            if (t.RowCounts[i].Length > 0) DrawRight(_label, s, t.RowCounts[i], right, top + DetailRowInset, t.RowShort[i] ? Warn : Accent);
            if (i < t.Rows - 1 || t.More.Length > 0) Paint.FillRect(s, x, top + DetailRowStep - 1, DetailTextW, 1, Theme.Hairline);
        }
        if (t.More.Length > 0) DrawLeft(_small, s, t.More, x, t.RowsTop + t.Rows * DetailRowStep + 8 + dy, Muted);

        // The pack check, with a dot in its colour, right above the buttons.
        Color check = t.CheckWarn ? Warn : Theme.Success;
        Paint.FillCircle(s, x + 5f, DetailCheckY + dy + 14f, 5f, check);
        ClipLeft(_small, s, t.Check, x + 20, DetailCheckY + dy, check, DetailTextW - 20);
    }
}
