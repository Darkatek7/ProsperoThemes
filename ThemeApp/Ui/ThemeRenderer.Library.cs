using System;
using System.Collections.Generic;
using System.Linq;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

public sealed partial class ThemeRenderer
{
    private static readonly HintItem[] LibraryHintItems =
        [new(PadButton.Cross, "Details"), new(PadButton.Square, "Preview"), new(PadButton.Triangle, "Sort"), new(PadButton.Options, "Reload")];
    private static readonly HintItem[] LibraryEmptyHintItems = [new(PadButton.Options, "Reload")];
    private static readonly HintItem[] LookCardHintItems =
        [new(PadButton.Cross, "Save as theme"), new(PadButton.Triangle, "Sort"), new(PadButton.Options, "Reload")];

    /// <summary>
    /// The footer hints of the library: those of the avatar row while it has the focus; with no theme there is nothing to open or
    /// preview, only to look for packs again.
    /// </summary>
    private HintItem[] LibraryHints(ThemeBrowser browser) =>
        browser.AvatarRowFocused ? AvatarHints(browser)
        : browser.LookCardFocused ? LookCardHintItems : browser.Selected >= 0 ? LibraryHintItems : LibraryEmptyHintItems;

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
    private const int CollectionTop = 632, CardY = 696, CardTitleY = CardY + CardH + 16;
    private const byte CardDim = 80;
    // Cards are clipped to the safe margins plus the room the focus ring needs outside the first and last slot, so
    // a card sliding past the edge is cut there and no sliver of the next one shows in the margin.
    private const int RowClipLeft = Theme.SafeX - 24, RowClipRight = Theme.ContentRight + 24, RowClipTop = 640, RowClipBottom = 1000;

    // The avatar row is the second row. While the themes have the focus only its heading peeks in above the footer (its cards
    // are below the screen); with the focus on it, everything below the header slides up by AvatarLift, so its heading takes
    // the place of "Your collection" and its cards the place of the theme cards, and the theme row ends up above them.
    private const int AvatarHeadingTop = 944, AvatarCardsTop = AvatarHeadingTop + (CardY - CollectionTop), AvatarRingRoom = 24, AvatarWashPeak = 170;
    private const int AvatarLift = AvatarHeadingTop - CollectionTop;

    // ---- State ------------------------------------------------------------------------------------------------------

    // Horizontal scroll of the collection row, in pixels: eased toward the offset that keeps the focused card in view.
    private float _scroll;
    private bool _scrollReady;
    // How far the lower content has slid up for the avatar row, in pixels (0 to the lift), eased like the scrolls. It snaps
    // when the Library is entered or its state was reset.
    private float _lift;
    private bool _liftReady;
    // Whether anything of the Library is still moving or waiting for a picture in this frame: a dialog does not keep this
    // picture as its backdrop while it is true.
    private bool _libraryMoving;
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

    /// <summary>
    /// The themes the console shows now, bottom first (look.json), for the "Save current look" card; the card shows while
    /// <see cref="ThemeBrowser.LookSavable"/>.
    /// </summary>
    public IReadOnlyList<LookLayer>? Look { get; set; }
    // The text of the look's hero, made again when the look is another one.
    private IReadOnlyList<LookLayer>? _lookFor;
    private IReadOnlyList<string> _lookDescription = [];
    private string _lookContents = "", _lookMeta = "";
    private IReadOnlyList<string>? _emptyText;
    private string _countText = "";
    private int _countFor = -1;
    private LibrarySort _countSort;

    /// <summary>The order of the library row (after the applied theme), named next to the count.</summary>
    public LibrarySort LibrarySort { get; set; }

    /// <summary>The library row is the same packs in another order: the cards are prepared and scrolled again.</summary>
    public void LibraryReordered()
    {
        _scrollReady = false;
        _cardsPreparedFor = int.MinValue;
    }

    private void ResetLibraryState()
    {
        _scrollReady = false;
        _liftReady = false;
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
        _libraryMoving = false;
        // The slide to the avatar row: one eased offset moves everything below the header, the hero twice as fast (it is
        // gone before the rows have arrived).
        bool avatars = browser.AvatarRowFocused;
        float target = avatars ? AvatarLift : 0;
        bool snap = !_liftReady || seconds <= _viewStart;
        if (snap) _lift = target;
        else
        {
            _lift += (target - _lift) * (float)(1 - Math.Exp(-Math.Clamp(delta, 0, .1) * 16));
            if (MathF.Abs(target - _lift) < 0.5f) _lift = target;
        }
        _liftReady = true;
        if (_lift != target) _libraryMoving = true;
        int slide = (int)MathF.Round(_lift), heroShift = 2 * slide;

        // The hero, clipped below the header rule while it slides away. It is drawn in a region that starts there, so its
        // y is shifted by the region's top as well.
        if (heroShift < HeroButtonsY + 64 - Theme.HeaderRuleY)
        {
            const int top = Theme.HeaderRuleY + 1;
            Surface above = s.Region(0, top, Theme.ScreenWidth, Theme.ScreenHeight - top);
            int shifted = enter - heroShift - top;
            if (selected is null) DrawEmptyHero(above, shifted);
            else if (browser.LookCardFocused) DrawLookHero(above, shifted);
            else DrawHero(above, selected, shifted);
        }
        if (slide > 0) DrawLiftWash(s, slide);
        DrawCollection(s, browser.Selected, entries, delta, browser.LookSavable, browser.LookCardFocused, slide, avatars);
        DrawAvatarRow(s, browser, slide, delta, snap);
        WarmBackdrop(selected, seconds);
    }

    // The backdrop's scrim is tuned for text at the hero and the cards' usual place; once the rows have slid up their titles stand
    // on the open part of a bright cover. A band of ink behind them (soft at both edges, stronger the further the slide) keeps them readable.
    private static void DrawLiftWash(Surface s, int lift)
    {
        const int step = 4, soft = 64;
        byte peak = (byte)(AvatarWashPeak * lift / AvatarLift);
        int top = CollectionTop - lift - 40, bottom = CardTitleY - lift + 84;
        for (int y = top; y < bottom; y += step)
        {
            float edge = Smooth(Math.Min(y + step / 2 - top, bottom - y - step / 2) / (float)soft);
            byte alpha = (byte)(peak * edge);
            if (alpha > 0) Paint.FillRect(s, 0, y, Theme.ScreenWidth, Math.Min(step, bottom - y), Ink.WithAlpha(alpha));
        }
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

    // The hero of the "Save current look" card: what saving does, and which themes with which parts go into the mix.
    private void DrawLookHero(Surface s, int enter)
    {
        IReadOnlyList<LookLayer> look = Look ?? [];
        if (!ReferenceEquals(look, _lookFor))
        {
            _lookFor = look;
            _lookDescription = _body.Wrap("Keeps what the console shows now as one theme in your library, to apply again or preview later.", HeroTextWidth, 2);
            var names = new List<string>(look.Count);
            for (int i = look.Count - 1; i >= 0; i--) names.Add(look[i].Name);   // the top layer first
            _lookContents = string.Join("  ·  ", names);
            _lookMeta = Plural(look.Count, "theme") + " combined";
        }
        int buttonsY = HeroButtonsY + enter;
        int metaY = buttonsY - 20 - 2 * MetaLine;
        int descriptionTop = metaY - 12 - _lookDescription.Count * _body.LineHeight;
        int titleTop = descriptionTop - 2 - _title.LineHeight;
        int eyebrowTop = titleTop - 8 - EyebrowHeight;

        DrawLeft(_small, s, "YOUR LOOK", Theme.SafeX, eyebrowTop + (EyebrowHeight - _small.LineHeight) / 2, Accent);
        DrawLeft(_title, s, "Save current look", Theme.SafeX - TitleOptical, titleTop, White);
        for (int i = 0; i < _lookDescription.Count; i++)
            DrawLeft(_body, s, _lookDescription[i], Theme.SafeX, descriptionTop + i * _body.LineHeight, Muted);
        ClipLeft(_small, s, _lookContents, Theme.SafeX, metaY, Muted, HeroWidth);
        ClipLeft(_small, s, _lookMeta, Theme.SafeX, metaY + MetaLine, Muted, HeroWidth);
        PillButton(s, Theme.SafeX, buttonsY, "Save as theme", true);
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

    // lift is how far the row has slid up for the avatar row; with the focus there (avatarRow) the selected pack keeps its place
    // in the row but is drawn like the others, without a ring.
    private void DrawCollection(Surface s, int selected, IReadOnlyList<ThemeLibraryEntry> entries, double delta, bool lookCard, bool lookFocused,
        int lift, bool avatarRow)
    {
        int collectionTop = CollectionTop - lift, cardY = CardY - lift, cardTitleY = CardTitleY - lift, clipTop = RowClipTop - lift;
        // "Your collection" and its count share one baseline.
        int baseline = collectionTop + Ascent(_heading);
        DrawLeft(_heading, s, "Your collection", Theme.SafeX, collectionTop, White);
        if (_countFor != entries.Count || _countSort != LibrarySort)
        {
            _countFor = entries.Count;
            _countSort = LibrarySort;
            _countText = entries.Count > 1 ? Plural(entries.Count, "theme") + "  ·  " + LibraryOrder.Label(LibrarySort)
                : Plural(entries.Count, "theme");
        }
        DrawLeft(_small, s, _countText, Theme.SafeX + _heading.MeasureText("Your collection") + 16, baseline - Ascent(_small), Muted);

        // Scroll: the focused card stays the third one when the row is long enough (and the row never scrolls past
        // its ends); the offset eases toward that, and settles exactly so a still screen is a still picture.
        // After the packs: the card that saves the look (when there is one to save), then the one that points to USB Import.
        int items = entries.Count + (lookCard ? 2 : 1);
        int maxScroll = Math.Max(0, items * CardStride - CardGap - Theme.ContentWidth);
        int focus = lookFocused ? entries.Count : selected;
        float target = focus < 0 ? 0 : Math.Clamp((focus - 2) * CardStride, 0, maxScroll);
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
        if (_scroll != target) _libraryMoving = true;
        int offset = (int)MathF.Round(_scroll);

        Surface row = s.Region(RowClipLeft, clipTop, RowClipRight - RowClipLeft, RowClipBottom - RowClipTop);
        for (int i = 0; i < items; i++)
        {
            int x = Theme.SafeX + i * CardStride - offset;
            if (x + CardW <= RowClipLeft || x >= RowClipRight) continue;
            int lx = x - RowClipLeft, ly = cardY - clipTop, titleY = cardTitleY - clipTop;
            if (i == entries.Count && lookCard) { DrawLookCard(row, lx, ly, titleY, lookFocused && !avatarRow); continue; }
            if (i >= entries.Count) { DrawAddCard(row, lx, ly, titleY); continue; }
            ThemeLibraryEntry entry = entries[i];
            bool focused = i == selected && !lookFocused && !avatarRow;
            if (focused)
            {
                int fx = lx - (FocusW - CardW) / 2, fy = ly - (FocusH - CardH) / 2;
                CardPicture(entry, true).Draw(row, fx, fy);
                if (IsApplied(entry)) Badge(row, "Applied", fx + 16, fy + 16, Theme.Success);
                Paint.FocusRing(row, fx, fy, FocusW, FocusH, Theme.RadiusCard);
            }
            else
            {
                // The selected pack stays bright while the avatars have the focus, so the row still says where it will come back to.
                bool current = i == selected && !lookFocused;
                CardPicture(entry, false, dimmed: !current).Draw(row, lx, ly);
                if (IsApplied(entry)) Badge(row, "Applied", lx + 12, ly + 12, Theme.Success);
                focused = current;
            }
            ClipLeft(_body, row, entry.Manifest.Name, lx, titleY, focused ? White : Muted, CardW);
        }
    }

    // The last card of the row: a quiet card that says where more themes come from. Opaque, so the theme's art behind
    // the row does not show through it.
    private static readonly Color AddCardInk = Color.FromRgb(15, 23, 36);
    private void DrawAddCard(Surface row, int x, int y, int titleY)
    {
        Paint.FillRoundedRect(row, x, y, CardW, CardH, Theme.RadiusCard, AddCardInk);
        Paint.StrokeRoundedRect(row, x, y, CardW, CardH, Theme.RadiusCard, 1, Theme.PanelStroke);
        float cx = x + CardW / 2f, cy = y + 68f;
        Paint.Line(row, cx - 14f, cy, cx + 14f, cy, 3f, Muted);
        Paint.Line(row, cx, cy - 14f, cx, cy + 14f, 3f, Muted);
        DrawMid(_small, row, "Import from USB", x + CardW / 2, y + 112, Muted);
        ClipLeft(_body, row, "Add themes", x, titleY, Muted, CardW);
    }

    // The card that saves the look: two stacked frames for the themes it combines. Focused, it grows like a pack's card.
    private void DrawLookCard(Surface row, int x, int y, int titleY, bool focused)
    {
        int w = focused ? FocusW : CardW, h = focused ? FocusH : CardH;
        int fx = focused ? x - (FocusW - CardW) / 2 : x, fy = focused ? y - (FocusH - CardH) / 2 : y;
        Color ink = focused ? White : Muted;
        Paint.FillRoundedRect(row, fx, fy, w, h, Theme.RadiusCard, AddCardInk);
        Paint.StrokeRoundedRect(row, fx, fy, w, h, Theme.RadiusCard, 1, Theme.PanelStroke);
        float cx = fx + w / 2f, cy = fy + h * 0.38f;
        Paint.StrokeRoundedRect(row, (int)cx - 30, (int)cy - 26, 48, 36, 6, 2, ink);
        Paint.FillRoundedRect(row, (int)cx - 18, (int)cy - 14, 48, 36, 6, AddCardInk);
        Paint.StrokeRoundedRect(row, (int)cx - 18, (int)cy - 14, 48, 36, 6, 2, ink);
        DrawMid(_small, row, "Save current look", fx + w / 2, fy + h * 112 / CardH, ink);
        if (focused) Paint.FocusRing(row, fx, fy, w, h, Theme.RadiusCard);
        ClipLeft(_body, row, "Your look", x, titleY, focused ? White : Muted, CardW);
    }

    private CachedImage CardPicture(ThemeLibraryEntry entry, bool focused, bool dimmed = true)
    {
        int w = focused ? FocusW : CardW, h = focused ? FocusH : CardH;
        byte dim = focused || !dimmed ? (byte)0 : CardDim;
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
            if (i == selected) CardPicture(entries[i], false, dimmed: false);   // the same pack with the focus on the avatars
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
