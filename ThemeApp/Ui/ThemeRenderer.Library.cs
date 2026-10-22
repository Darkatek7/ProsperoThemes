using System;
using System.Collections.Generic;
using System.Linq;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

public sealed partial class ThemeRenderer
{
    private static readonly HintItem[] LibraryHintItems =
        [new(PadButton.Cross, "Details"), new(PadButton.Square, "Preview"), new(PadButton.Options, "Reload")];
    private static readonly HintItem[] LibraryEmptyHintItems = [new(PadButton.Options, "Reload")];

    /// <summary>The footer hints of the library: with no theme there is nothing to open or preview, only to look for packs again.</summary>
    private HintItem[] LibraryHints(ThemeBrowser browser) => browser.Selected >= 0 ? LibraryHintItems : LibraryEmptyHintItems;

    // ---- Layout -----------------------------------------------------------------------------------------------------

    // The hero is a column at the left safe edge, stacked upwards from its button row so the buttons stay where they are
    // whatever the title and the description need. The collection row sits below it.
    private const int HeroWidth = 1000, HeroTextWidth = 880, HeroButtonsY = 528;
    private const int EyebrowHeight = 40, MetaLine = 30;
    // The big title's first letter has more side bearing than the small text has; this pulls it to the margin.
    private const int TitleOptical = 4;

    // Cards are 16:9 and five of them (with their gaps) fill the width between the safe margins. The focused card is a
    // little bigger, centred on its slot.
    private const int CardW = 320, CardH = 180, CardGap = 32, CardStride = CardW + CardGap;
    private const int FocusW = 344, FocusH = 194;
    private const int CollectionTop = 632, CardY = 696, CardTitleY = CardY + CardH + 24;
    private const byte CardDim = 80;
    // Cards are clipped to the safe margins plus the room the focus ring needs outside the first and last slot, so
    // a card sliding past the edge is cut there and no sliver of the next one shows in the margin.
    private const int RowClipLeft = Theme.SafeX - 24, RowClipRight = Theme.ContentRight + 24, RowClipTop = 640, RowClipBottom = 1000;

    // ---- State ------------------------------------------------------------------------------------------------------

    // Horizontal scroll of the collection row, in pixels: eased toward the offset that keeps the focused card in view.
    private float _scroll;
    private bool _scrollReady;
    // The selection the card pictures were last prepared for (see PrepareCards).
    private int _cardsPreparedFor = int.MinValue;
    // The blurred backdrop is built in the background of a quiet moment, so the next tab does not stall for it.
    private string? _selectedKey, _warmedFor;
    private double _selectedSince;

    // Text of the hero for one pack, wrapped once; rebuilt when another pack is selected or the library is scanned again.
    private sealed class HeroText(string path, object manifest, AtlasFont titleFont, IReadOnlyList<string> title,
        IReadOnlyList<string> description, string contents, string meta)
    {
        public readonly string Path = path;
        public readonly object Manifest = manifest;
        public readonly AtlasFont TitleFont = titleFont;
        public readonly IReadOnlyList<string> Title = title, Description = description;
        public readonly string Contents = contents, Meta = meta;
    }
    private HeroText? _hero;
    private IReadOnlyList<string>? _emptyText;
    private string _countText = "";
    private int _countFor = -1;

    private void ResetLibraryState()
    {
        _scrollReady = false;
        _cardsPreparedFor = int.MinValue;
        _selectedKey = _warmedFor = null;
        _hero = null;
        _countFor = -1;
    }

    private HeroText HeroOf(ThemeLibraryEntry entry)
    {
        if (_hero is { } hero && hero.Path == entry.Path && ReferenceEquals(hero.Manifest, entry.Manifest)) return hero;
        string name = entry.Manifest.Name;
        // One line in the big title when it fits; otherwise the next size down, wrapped to two lines.
        AtlasFont font = _title.MeasureText(name) <= HeroWidth ? _title : _screenHeading;
        IReadOnlyList<string> title = font == _title ? [name] : _screenHeading.Wrap(name, HeroWidth, 2);
        return _hero = new HeroText(entry.Path, entry.Manifest, font, title,
            _body.Wrap(entry.Manifest.Description ?? "", HeroTextWidth, 2),
            SummaryOf(entry).Text, $"{entry.Manifest.Author}  ·  v{entry.Manifest.Version}");
    }

    // ---- Screen -----------------------------------------------------------------------------------------------------

    private void DrawLibrary(Surface s, ThemeBrowser browser, IReadOnlyList<ThemeLibraryEntry> entries, ThemeLibraryEntry? selected,
        int enter, double seconds, double delta)
    {
        if (selected is null) DrawEmptyHero(s, enter);
        else DrawHero(s, selected, enter);
        DrawCollection(s, browser.Selected, entries, delta);
        WarmBackdrop(selected, seconds);
    }

    private void DrawHero(Surface s, ThemeLibraryEntry entry, int enter)
    {
        HeroText hero = HeroOf(entry);
        int buttonsY = HeroButtonsY + enter;
        int metaY = buttonsY - 20 - 2 * MetaLine;
        int descriptionTop = metaY - 12 - hero.Description.Count * _body.LineHeight;
        int titleTop = descriptionTop - 2 - hero.Title.Count * hero.TitleFont.LineHeight;
        int eyebrowTop = titleTop - 8 - EyebrowHeight;

        if (IsApplied(entry)) Badge(s, "Applied", Theme.SafeX, eyebrowTop, Theme.Success);
        else DrawLeft(_small, s, "YOUR LIBRARY", Theme.SafeX, eyebrowTop + (EyebrowHeight - _small.LineHeight) / 2, Accent);
        for (int i = 0; i < hero.Title.Count; i++)
            DrawLeft(hero.TitleFont, s, hero.Title[i], Theme.SafeX - (hero.TitleFont == _title ? TitleOptical : 0), titleTop + i * hero.TitleFont.LineHeight, White);
        for (int i = 0; i < hero.Description.Count; i++)
            DrawLeft(_body, s, hero.Description[i], Theme.SafeX, descriptionTop + i * _body.LineHeight, Muted);
        ClipLeft(_small, s, hero.Contents, Theme.SafeX, metaY, Muted, HeroWidth);
        ClipLeft(_small, s, hero.Meta, Theme.SafeX, metaY + MetaLine, Muted, HeroWidth);

        int primary = PillButton(s, Theme.SafeX, buttonsY, "View theme", true);
        GlassPill(s, Theme.SafeX + primary + 16, buttonsY, "Preview", PadButton.Square);
    }

    // With no theme there is nothing to act on, so the message is centred and names the tabs that fill the library.
    private void DrawEmptyHero(Surface s, int enter)
    {
        IReadOnlyList<string> text = EmptyLibraryText();
        const int centre = Theme.ScreenWidth / 2;
        int top = 296 + enter;
        DrawMid(_small, s, "YOUR LIBRARY", centre, top, Accent);
        top += _small.LineHeight + 8;
        DrawMid(_screenHeading, s, "No themes yet", centre, top, White);
        top += _screenHeading.LineHeight + 12;
        for (int i = 0; i < text.Count; i++)   // indexed: a foreach over the interface allocates every frame
        {
            DrawMid(_body, s, text[i], centre, top, Muted);
            top += _body.LineHeight;
        }
    }

    private void DrawCollection(Surface s, int selected, IReadOnlyList<ThemeLibraryEntry> entries, double delta)
    {
        // "Your collection" and its count share one baseline.
        int baseline = CollectionTop + Ascent(_heading);
        DrawLeft(_heading, s, "Your collection", Theme.SafeX, CollectionTop, White);
        if (_countFor != entries.Count) { _countFor = entries.Count; _countText = Plural(entries.Count, "theme"); }
        DrawLeft(_small, s, _countText, Theme.SafeX + _heading.MeasureText("Your collection") + 16, baseline - Ascent(_small), Muted);

        // Scroll: the focused card stays the third one when the row is long enough (and the row never scrolls past
        // its ends); the offset eases toward that, and settles exactly so a still screen is a still picture.
        int items = entries.Count + 1;   // the last one is the card that points to USB Import
        int maxScroll = Math.Max(0, items * CardStride - CardGap - Theme.ContentWidth);
        float target = selected < 0 ? 0 : Math.Clamp((selected - 2) * CardStride, 0, maxScroll);
        if (!_scrollReady) { _scroll = target; _scrollReady = true; }
        else
        {
            _scroll += (target - _scroll) * (float)(1 - Math.Exp(-Math.Clamp(delta, 0, .1) * 16));
            if (MathF.Abs(target - _scroll) < 0.5f) _scroll = target;
        }
        if (selected != _cardsPreparedFor)
        {
            PrepareCards(entries, selected, Math.Min(_scroll, target), Math.Max(_scroll, target));
            _cardsPreparedFor = selected;
        }
        int offset = (int)MathF.Round(_scroll);

        Surface row = s.Region(RowClipLeft, RowClipTop, RowClipRight - RowClipLeft, RowClipBottom - RowClipTop);
        for (int i = 0; i < items; i++)
        {
            int x = Theme.SafeX + i * CardStride - offset;
            if (x + CardW <= RowClipLeft || x >= RowClipRight) continue;
            int lx = x - RowClipLeft, ly = CardY - RowClipTop;
            if (i == entries.Count) { DrawAddCard(row, lx, ly); continue; }
            ThemeLibraryEntry entry = entries[i];
            bool focused = i == selected;
            if (focused)
            {
                int fx = lx - (FocusW - CardW) / 2, fy = ly - (FocusH - CardH) / 2;
                CardPicture(entry, true).Draw(row, fx, fy);
                if (IsApplied(entry)) Badge(row, "Applied", fx + 16, fy + 16, Theme.Success);
                Paint.FocusRing(row, fx, fy, FocusW, FocusH, Theme.RadiusCard);
            }
            else
            {
                CardPicture(entry, false).Draw(row, lx, ly);
                if (IsApplied(entry)) Badge(row, "Applied", lx + 12, ly + 12, Theme.Success);
            }
            ClipLeft(_body, row, entry.Manifest.Name, lx, CardTitleY - RowClipTop, focused ? White : Muted, CardW);
        }
    }

    // The last card of the row: a quiet card that says where more themes come from. Opaque, so the theme's art behind
    // the row does not show through it.
    private static readonly Color AddCardInk = Color.FromRgb(15, 23, 36);
    private void DrawAddCard(Surface row, int x, int y)
    {
        Paint.FillRoundedRect(row, x, y, CardW, CardH, Theme.RadiusCard, AddCardInk);
        Paint.StrokeRoundedRect(row, x, y, CardW, CardH, Theme.RadiusCard, 1, Theme.PanelStroke);
        float cx = x + CardW / 2f, cy = y + 68f;
        Paint.Line(row, cx - 14f, cy, cx + 14f, cy, 3f, Muted);
        Paint.Line(row, cx, cy - 14f, cx, cy + 14f, 3f, Muted);
        DrawMid(_small, row, "Import from USB", x + CardW / 2, y + 112, Muted);
        ClipLeft(_body, row, "Add themes", x, CardTitleY - RowClipTop, Muted, CardW);
    }

    private CachedImage CardPicture(ThemeLibraryEntry entry, bool focused)
    {
        int w = focused ? FocusW : CardW, h = focused ? FocusH : CardH;
        byte dim = focused ? (byte)0 : CardDim;
        // The cover is scaled once and cached; the wash that dims the other cards is baked in (their own cache entry),
        // so it stays inside the rounded corners.
        return _libraryImages.TryGet(entry.Path, w, h, Theme.RadiusCard, dim)
            ?? _libraryImages.Add(entry.Path, Cover(entry), w, h, Theme.RadiusCard, dim);
    }

    /// <summary>
    /// Makes the pictures of every card that shows at any point of a scroll between two offsets, in the frame the
    /// selection changes. Each one costs a scale (and maybe a cover decode); doing them together keeps the glide that
    /// follows free of work, instead of spreading hitches over it.
    /// </summary>
    private void PrepareCards(IReadOnlyList<ThemeLibraryEntry> entries, int selected, float from, float to)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            float left = Theme.SafeX + i * CardStride;
            // Visible at some offset in [from, to] when it is inside the clip at either end of the glide or between.
            if (left - to >= RowClipRight || left + CardW - from <= RowClipLeft) continue;
            CardPicture(entries[i], i == selected);
        }
    }

    // After the selection has rested for a moment, builds the blurred backdrop the other tabs show, while nothing moves.
    private void WarmBackdrop(ThemeLibraryEntry? selected, double seconds)
    {
        string key = selected?.Path ?? FallbackKey;
        if (key != _selectedKey || seconds < _selectedSince) { _selectedKey = key; _selectedSince = seconds; return; }
        if (seconds - _selectedSince < 0.5 || _warmedFor == key) return;
        _warmedFor = key;
        BlurredBackdrop(selected);
    }
}
