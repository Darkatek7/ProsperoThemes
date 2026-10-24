using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

public sealed partial class ThemeRenderer
{
    // ---- State the app feeds in ---------------------------------------------------------------------------------------

    /// <summary>Store ids this console has liked (kept in likes.json); their cards carry a heart and the footer offers Unlike.</summary>
    public IReadOnlySet<string> CommunityLiked { get; set; } = new HashSet<string>();
    /// <summary>
    /// The sort (a <see cref="CommunitySort"/> value) the list on screen was loaded with, or -1 before any list. The
    /// header names this one, not the sort the pills have moved to, so "24 themes · Top rated" always describes the list.
    /// </summary>
    public int CommunityListSort { get; set; } = -1;
    /// <summary>
    /// The category (an index into <see cref="CommunityCategories.List"/>, -1 for all themes) the list on screen was loaded
    /// with, like <see cref="CommunityListSort"/>: the count line and the empty state describe the list, not the chip the
    /// cursor has moved to. Only read once <see cref="CommunityListSort"/> says a list was loaded.
    /// </summary>
    public int CommunityListCategory { get; set; } = -1;
    /// <summary>The search term the list on screen was loaded with ("" for none), like <see cref="CommunityListCategory"/>.</summary>
    public string CommunityListQuery { get; set; } = "";
    /// <summary>
    /// Whether the app is fetching the cards' pictures. Meanwhile a card still waiting for its picture shows a plain
    /// picture area with a moving shimmer instead of the placeholder art; once loading ends, the ones still without
    /// (failed, or none in the store) show the placeholder.
    /// </summary>
    public bool CommunityPicturesLoading { get; set; }
    /// <summary>Whether <see cref="CommunityStatus"/> reports a failed load (the empty state then says so and offers a retry).</summary>
    public bool CommunityError { get; set; }

    // ---- Footer hints ---------------------------------------------------------------------------------------------------

    private static readonly HintItem[] CommunityDownloadLikeHints =
        [new(PadButton.Cross, "Download"), new(PadButton.Triangle, "Like"), new(PadButton.Options, "Refresh"), new(PadButton.Circle, "Back")];
    private static readonly HintItem[] CommunityDownloadUnlikeHints =
        [new(PadButton.Cross, "Download"), new(PadButton.Triangle, "Unlike"), new(PadButton.Options, "Refresh"), new(PadButton.Circle, "Back")];
    private static readonly HintItem[] CommunityUpdateLikeHints =
        [new(PadButton.Cross, "Update"), new(PadButton.Triangle, "Like"), new(PadButton.Options, "Refresh"), new(PadButton.Circle, "Back")];
    private static readonly HintItem[] CommunityUpdateUnlikeHints =
        [new(PadButton.Cross, "Update"), new(PadButton.Triangle, "Unlike"), new(PadButton.Options, "Refresh"), new(PadButton.Circle, "Back")];
    private static readonly HintItem[] CommunityEmptyHints = [new(PadButton.Options, "Refresh"), new(PadButton.Circle, "Back")];
    // With the filter chips focused: X picks the chip; the d-pad hint says how to get back to the cards when there are some.
    private static readonly HintItem[] CommunityChipHints =
        [new(PadButton.Cross, "Select"), new(PadButton.DPad, "Move · Down for themes"), new(PadButton.Square, "Sort"), new(PadButton.Circle, "Back")];
    private static readonly HintItem[] CommunityChipEmptyHints =
        [new(PadButton.Cross, "Select"), new(PadButton.DPad, "Move"), new(PadButton.Square, "Sort"), new(PadButton.Circle, "Back")];

    /// <summary>
    /// The footer hints of the store: Update instead of Download for a theme with a newer version, Unlike instead of
    /// Like for one this console liked. With no list there is nothing to download or like, only to refresh. While the
    /// filter chips have the focus the cards are not in play, so the hints are the chips' (also for an empty list: the
    /// chips are how to pick another category).
    /// </summary>
    internal HintItem[] CommunityHints(ThemeBrowser browser)
    {
        if (browser.CommunityChipFocused) return Community.Count == 0 ? CommunityChipEmptyHints : CommunityChipHints;
        if (Community.Count == 0 || SelectedCommunityId is not { } id) return CommunityEmptyHints;
        bool update = CommunityUpdates.Contains(id), liked = CommunityLiked.Contains(id);
        return update ? (liked ? CommunityUpdateUnlikeHints : CommunityUpdateLikeHints)
                      : (liked ? CommunityDownloadUnlikeHints : CommunityDownloadLikeHints);
    }

    // ---- Layout -----------------------------------------------------------------------------------------------------------

    // Four columns fill the width between the safe margins with even gaps. Two rows fill the height from the grid top
    // to the bottom of the content area, so the cards are as tall as the space allows: the picture takes what the
    // three text lines of the panel leave (a centre crop of the preview, about 2:1, never a stretch).
    private const int CommunityColumns = 4, CommunityGap = 24, CommunityVisibleRows = 2;
    internal const int CommunityCardW = (Theme.ContentWidth - (CommunityColumns - 1) * CommunityGap) / CommunityColumns;   // 414
    // The sort pills and the filter chips share one row under the eyebrow row; the grid starts below it with room for the
    // focus glow (12 px). The row is 8 px lower than the sort pills alone were, so the glow of a focused chip (12 px) stays
    // clear of the eyebrow line above, and it ends 18 px above the grid.
    internal const int CommunitySortY = Theme.ContentTop + 44, CommunitySortH = 44;
    internal const int CommunityGridTop = 282;
    internal const int CommunityRowGap = CommunityGap;
    internal const int CommunityCardH = (Theme.ContentBottom - CommunityGridTop - CommunityRowGap) / CommunityVisibleRows;   // 319
    internal const int CommunityRowStride = CommunityCardH + CommunityRowGap;
    private const int CommunityPanelH = 112, CommunityImageH = CommunityCardH - CommunityPanelH;   // 207
    private const int CommunityPad = 18;
    // Cards are clipped to this band: the content area plus the room the focus glow needs, which stays clear of the sort
    // row above (it ends 26 px above the grid) and of the footer below (it starts 40 px below the content area).
    private const int CommunityBandLeft = Theme.SafeX - 16, CommunityBandRight = Theme.ContentRight + 16;
    private const int CommunityBandTop = CommunityGridTop - 14, CommunityBandBottom = Theme.ContentBottom + 14;
    // Eases like the Library row: 1 - exp(-16 * frame time), 0.23 per frame at 60 Hz.
    private float CommunityEase => (float)(1 - Math.Exp(-Math.Clamp(_frameDelta, 0, .1) * 16));

    // The card's own ground: an opaque ink a little lighter than the backdrop, so a baked card copies without blending.
    private static readonly Color CommunityCardInk = Color.FromRgb(15, 23, 36);
    private static readonly Color CommunityHeart = Color.FromRgb(255, 118, 150);

    // ---- State ------------------------------------------------------------------------------------------------------------

    // What a baked card shows, besides the theme itself: the flags below.
    private const byte CommunityFlagLiked = 1, CommunityFlagUpdate = 2, CommunityFlagInLibrary = 4, CommunityFlagPreview = 8, CommunityFlagWaiting = 16;
    private readonly record struct CommunityStamp(CommunityTheme Theme, byte Flags);
    private readonly Dictionary<string, CommunityStamp> _communityStamps = new();
    // Scroll in pixels of the grid (eased toward the row the focus needs), and the first visible row it eases toward.
    private float _communityScroll;
    private int _communityTop;
    private bool _communityScrollReady;
    // Previews decoded so far in this frame (see BakeCommunityCard).
    private const int CommunityDecodesPerFrame = 2;
    private int _communityDecodes;
    /// <summary>How many store cards have been made so far (a card is made once, and again only when it changes); for the tests.</summary>
    internal int CommunityBakes { get; private set; }
    // The row's layout, measured once: the sort pills' widths, then the filter chips. Chip x is relative to the first chip;
    // the chips scroll inside the viewport that starts at _communityChipsLeft and ends at the right safe edge.
    private int[]? _communitySortWidth;
    private int[]? _communityChipX, _communityChipW;
    private int _communityChipsLeft, _communityChipsTotal;
    // Scroll of the chips in pixels (eased toward the position the focused or chosen chip needs), where it is heading, and
    // the view start it was last placed for (a view that is entered again starts in place instead of gliding in).
    private float _communityChipScroll, _communityChipTarget;
    private bool _communityChipReady;
    private double _communityChipViewStart = -1;
    private string _communityCountText = "";
    private int _communityEyebrowWidth;
    private int _communityCountFor = -1, _communityCountSort = -1, _communityCountCategory = -2;
    private string _communityCountQuery = "";
    private string? _communityStateHeading, _communityStateText;
    private IReadOnlyList<string> _communityStateBody = Array.Empty<string>();
    private readonly Dictionary<int, byte[]> _communityHeartMasks = new();

    // ---- Screen -----------------------------------------------------------------------------------------------------------

    private void DrawCommunity(Surface s, ThemeBrowser browser, int enter)
    {
        IReadOnlyList<CommunityTheme> items = Community;
        DrawCommunityHeader(s, browser, items.Count, enter);
        if (items.Count == 0) DrawCommunityState(s, browser, enter);
        else DrawCommunityGrid(s, browser, items, enter);
    }

    /// <summary>The eyebrow with the count of the loaded list, and the sort control under it.</summary>
    private void DrawCommunityHeader(Surface s, ThemeBrowser browser, int count, int enter)
    {
        int y = Theme.ContentTop + enter;
        DrawLeft(_small, s, "COMMUNITY STORE", Theme.SafeX, y, Accent);
        int countLeft = Theme.ContentRight;
        if (count > 0)
        {
            string countText = CommunityCountText(count, browser);
            DrawRight(_small, s, countText, Theme.ContentRight, y, Muted);
            countLeft -= _small.MeasureText(countText);
        }
        // A muted pointer to the web creator between the two, when the store has one (it is not a control).
        _communityEyebrowWidth = _communityEyebrowWidth > 0 ? _communityEyebrowWidth : _small.MeasureText("COMMUNITY STORE");
        DrawCommunityCreator(s, Theme.SafeX + _communityEyebrowWidth, y, countLeft);

        // The three sorts as a row of capsules: the active one white, the others glass. The Square hint follows on the same line.
        if (_communitySortWidth is null) LayoutCommunityRow();
        int x = Theme.SafeX, top = CommunitySortY + enter;
        for (int i = 0; i < _communitySortWidth!.Length; i++)
        {
            bool on = i == browser.CommunitySort;
            int w = _communitySortWidth[i];
            DrawCommunityPill(s, x, top, w, on, false, CommunityList.Label((CommunitySort)i));
            x += w + Theme.Unit;
        }
        Hint(s, PadButton.Square, "Sort", x + Theme.Unit, top + (CommunitySortH - _small.LineHeight) / 2);
        DrawCommunityChips(s, browser, top);
    }

    // One capsule of the row: the active sort or the chosen category white, the others glass. A chip the cursor is on (and
    // that is not the chosen one) reads white on the glass; its ring is drawn separately, over its neighbours.
    private void DrawCommunityPill(Surface s, int x, int top, int w, bool on, bool cursor, string label)
    {
        if (on) Paint.Capsule(s, x, top, w, CommunitySortH, White);
        else
        {
            Paint.Capsule(s, x, top, w, CommunitySortH, Theme.Panel);
            Paint.StrokeRoundedRect(s, x, top, w, CommunitySortH, CommunitySortH / 2, 1, Theme.PanelStroke);
        }
        DrawMid(_label, s, label, x + w / 2, top + (CommunitySortH - _label.LineHeight) / 2, on ? PillInk : cursor ? White : Muted);
    }

    // The sort pills' widths, then the filter chips (All and the twelve categories) after the Sort hint: all measured once.
    private const int CommunityChipPad = 20, CommunityChipsGap = 48, CommunityChipPeek = 80;

    // The search chip says "Search", or shows the term (cut when long) with a cross that says X removes it.
    private string _communityChipQuery = "";
    private string CommunityChipLabel(int chip)
    {
        if (chip == ThemeBrowser.NoAiChip) return "No AI";
        if (chip != ThemeBrowser.SearchChip) return CommunityCategories.LabelAt(ThemeBrowser.CategoryOfChip(chip));
        if (_communityChipQuery.Length == 0) return "Search";
        string term = _communityChipQuery.Length > 20 ? _communityChipQuery[..19] + "…" : _communityChipQuery;
        return "“" + term + "” ×";
    }

    private void LayoutCommunityRow()
    {
        _communitySortWidth = new int[3];
        int x = Theme.SafeX;
        for (int i = 0; i < 3; i++)
        {
            _communitySortWidth[i] = _label.MeasureText(CommunityList.Label((CommunitySort)i)) + 2 * 24;
            x += _communitySortWidth[i] + Theme.Unit;
        }
        // The chips start a little after the Square hint, which sits one unit after the last pill.
        _communityChipsLeft = x + Theme.Unit + HintWidth(new HintItem(PadButton.Square, "Sort")) + CommunityChipsGap;
        int chips = ThemeBrowser.CommunityChipCount;
        _communityChipX = new int[chips];
        _communityChipW = new int[chips];
        int at = 0;
        for (int i = 0; i < chips; i++)
        {
            _communityChipX[i] = at;
            at += (_communityChipW[i] = _label.MeasureText(CommunityChipLabel(i)) + 2 * CommunityChipPad) + Theme.Unit;
        }
        _communityChipsTotal = at - Theme.Unit;
    }

    /// <summary>Screen x of the left edge of chip <paramref name="chip"/> (0 is "All") as the row is scrolled now; for the tests.</summary>
    internal int CommunityChipScreenX(int chip)
    {
        if (_communityChipX is null) LayoutCommunityRow();
        return _communityChipsLeft + _communityChipX![chip] - (int)MathF.Round(_communityChipScroll);
    }

    /// <summary>Width of chip <paramref name="chip"/>; for the tests.</summary>
    internal int CommunityChipWidth(int chip)
    {
        if (_communityChipW is null) LayoutCommunityRow();
        return _communityChipW![chip];
    }

    /// <summary>The viewport the chips scroll in: from <c>Left</c> to the right safe edge.</summary>
    internal (int Left, int Right) CommunityChipViewport
    {
        get
        {
            if (_communityChipX is null) LayoutCommunityRow();
            return (_communityChipsLeft, Theme.ContentRight);
        }
    }

    // The chip that has to stay in view: the one the cursor is on, or else the chosen one.
    private static int CommunityChipInView(ThemeBrowser browser) =>
        Math.Clamp(browser.CommunityChipFocused ? browser.CommunityChip : ThemeBrowser.ChipOfCategory(browser.CommunityCategory), 0, ThemeBrowser.CommunityChipCount - 1);

    // Moves the chips only when the chip in view would otherwise leave the viewport (keeping a neighbour peeking in), eased
    // like the grid; the first draw of a view starts in place.
    private void UpdateCommunityChipScroll(ThemeBrowser browser)
    {
        if (_communityChipViewStart != _viewStart) { _communityChipViewStart = _viewStart; _communityChipReady = false; }
        int viewWidth = Theme.ContentRight - _communityChipsLeft;
        int chip = CommunityChipInView(browser);
        float left = _communityChipX![chip], right = left + _communityChipW![chip];
        float target = _communityChipTarget;
        if (left - target < CommunityChipPeek) target = left - CommunityChipPeek;
        else if (right - target > viewWidth - CommunityChipPeek) target = right - viewWidth + CommunityChipPeek;
        target = Math.Clamp(target, 0f, Math.Max(0, _communityChipsTotal - viewWidth));
        _communityChipTarget = target;
        if (!_communityChipReady) { _communityChipScroll = target; _communityChipReady = true; }
        else
        {
            _communityChipScroll += (target - _communityChipScroll) * CommunityEase;
            if (MathF.Abs(target - _communityChipScroll) < 0.5f) _communityChipScroll = target;
        }
    }

    /// <summary>
    /// The filter chips, to the right of the sort pills and a hairline: the chosen one white, the others glass. They are
    /// cut at the viewport's edges (a half chip says there is more) and scroll to keep the focused or chosen one in view.
    /// The cursor's chip gets the focus ring when the row has the focus, drawn last so its glow lies over the neighbours.
    /// </summary>
    private void DrawCommunityChips(Surface s, ThemeBrowser browser, int top)
    {
        // The search chip is as wide as its term, so the row is measured again when the term changes.
        if (_communityChipQuery != browser.CommunityQuery)
        {
            _communityChipQuery = browser.CommunityQuery;
            LayoutCommunityRow();
        }
        UpdateCommunityChipScroll(browser);
        int left = _communityChipsLeft, viewWidth = Theme.ContentRight - left;
        Paint.FillRect(s, left - CommunityChipsGap / 2, top + 8, 1, CommunitySortH - 16, White.WithAlpha(36));
        Surface strip = s.Region(left, top, viewWidth, CommunitySortH);
        int scroll = (int)MathF.Round(_communityChipScroll);
        int chosen = ThemeBrowser.ChipOfCategory(browser.CommunityCategory), cursor = browser.CommunityChipFocused ? browser.CommunityChip : -1;
        bool searching = browser.CommunityQuery.Length > 0;
        for (int i = 0; i < _communityChipX!.Length; i++)
        {
            int x = _communityChipX[i] - scroll, w = _communityChipW![i];
            if (x + w <= 0 || x >= viewWidth) continue;
            bool on = i == chosen || (searching && i == ThemeBrowser.SearchChip) || (browser.CommunityHideAi && i == ThemeBrowser.NoAiChip);
            DrawCommunityPill(strip, x, 0, w, on, i == cursor, CommunityChipLabel(i));
        }
        if (cursor < 0) return;
        // The ring and its glow reach 12 px past the chip, so they get a band a little wider than the viewport.
        const int room = 14;
        Surface ring = s.Region(left - room, top - room, viewWidth + 2 * room, CommunitySortH + 2 * room);
        Paint.FocusRing(ring, _communityChipX[cursor] - scroll + room, room, _communityChipW![cursor], CommunitySortH, CommunitySortH / 2, Accent);
    }

    // The category the list on screen was loaded with (-1 for all themes). Before the app has loaded any list (the tests
    // draw one directly) it is the chip that is chosen.
    private int CommunityLoadedCategory(ThemeBrowser browser) => CommunityListSort is >= 0 and <= 2 ? CommunityListCategory : browser.CommunityCategory;
    private string CommunityLoadedQuery(ThemeBrowser browser) => CommunityListSort is >= 0 and <= 2 ? CommunityListQuery : browser.CommunityQuery;

    // "24 themes · Anime · Top rated" (without the category for all themes), from the sort and category the list was loaded
    // with; formatted again only when one of them or the count changes.
    private string CommunityCountText(int count, ThemeBrowser browser)
    {
        int sort = CommunityListSort is >= 0 and <= 2 ? CommunityListSort : browser.CommunitySort;
        int category = CommunityLoadedCategory(browser);
        if (category < -1 || category >= CommunityCategories.Count) category = -1;
        string query = CommunityLoadedQuery(browser);
        if (count != _communityCountFor || sort != _communityCountSort || category != _communityCountCategory || query != _communityCountQuery)
        {
            _communityCountFor = count;
            _communityCountSort = sort;
            _communityCountCategory = category;
            _communityCountQuery = query;
            _communityCountText = Plural(count, "theme") + (query.Length > 0 ? " · “" + query + "”" : "")
                + (category >= 0 ? " · " + CommunityCategories.LabelAt(category) : "") + " · " + CommunityList.Label((CommunitySort)sort);
        }
        return _communityCountText;
    }

    private void DrawCommunityGrid(Surface s, ThemeBrowser browser, IReadOnlyList<CommunityTheme> items, int enter)
    {
        _communityDecodes = 0;
        int count = items.Count, rows = (count + CommunityColumns - 1) / CommunityColumns;
        int selected = Math.Clamp(browser.CommunitySelected, 0, count - 1), selectedRow = selected / CommunityColumns;

        // The focused row stays in view with the least movement: the window of two rows only moves when the focus
        // leaves it. A jump of more than one row (Square resets to the first card) snaps instead of gliding, since a
        // glide would show, and so make, every card in between.
        int maxTop = Math.Max(0, rows - CommunityVisibleRows);
        int top = Math.Clamp(_communityTop, 0, maxTop);
        if (selectedRow < top) top = selectedRow;
        else if (selectedRow >= top + CommunityVisibleRows) top = selectedRow - CommunityVisibleRows + 1;
        top = Math.Clamp(top, 0, maxTop);
        float target = top * CommunityRowStride;
        if (!_communityScrollReady || Math.Abs(top - _communityTop) > 1) { _communityScroll = target; _communityScrollReady = true; }
        else
        {
            _communityScroll += (target - _communityScroll) * CommunityEase;
            if (MathF.Abs(target - _communityScroll) < 0.5f) _communityScroll = target;
        }
        _communityTop = top;
        int offset = (int)MathF.Round(_communityScroll);

        Surface band = s.Region(CommunityBandLeft, CommunityBandTop, CommunityBandRight - CommunityBandLeft, CommunityBandBottom - CommunityBandTop);
        int firstRow = Math.Max(0, offset / CommunityRowStride);
        int firstIndex = firstRow * CommunityColumns, endIndex = Math.Min(count, (firstRow + CommunityVisibleRows + 1) * CommunityColumns);
        int focusX = 0, focusY = 0;
        bool focusShown = false;
        for (int index = firstIndex; index < endIndex; index++)
        {
            int x = Theme.SafeX + index % CommunityColumns * (CommunityCardW + CommunityGap);
            int y = CommunityGridTop + index / CommunityColumns * CommunityRowStride - offset + enter;
            if (y + CommunityCardH <= CommunityBandTop || y >= CommunityBandBottom) continue;
            CommunityCard(items[index]).Draw(band, x - CommunityBandLeft, y - CommunityBandTop);
            if (CommunityPicturesLoading && CommunityPictureWaiting(items[index])) DrawCommunityShimmer(band, x - CommunityBandLeft, y - CommunityBandTop);
            if (index == selected) { focusShown = true; focusX = x - CommunityBandLeft; focusY = y - CommunityBandTop; }
        }
        // After every card, so the glow lies over the neighbours instead of under them. Not while the filter chips have the focus.
        if (focusShown && !browser.CommunityChipFocused) Paint.FocusRing(band, focusX, focusY, CommunityCardW, CommunityCardH, Theme.RadiusCard);
        PrefetchCommunityCards(items, top, rows);
    }

    // Bakes one card a frame of the rows next to the visible ones while nothing else is pending, so a row that scrolls in
    // is already made instead of costing a hitch of four cards at the moment it appears.
    private void PrefetchCommunityCards(IReadOnlyList<CommunityTheme> items, int top, int rows)
    {
        if (_communityDecodes > 0) return;   // this frame already made a card with a preview
        int firstRow = Math.Max(0, top - 1), lastRow = Math.Min(rows - 1, top + CommunityVisibleRows);
        for (int index = firstRow * CommunityColumns; index < Math.Min(items.Count, (lastRow + 1) * CommunityColumns); index++)
        {
            CommunityTheme t = items[index];
            if (CommunityCardCurrent(t, CommunityFlags(t)) is not null) continue;
            BakeCommunityCard(t, CommunityFlags(t));
            return;
        }
    }

    // A card whose picture is on its way: the store has one, it has not arrived, and it did not fail to decode.
    private bool CommunityPictureWaiting(CommunityTheme t) =>
        t.PictureUrl is not null && !CommunityPreviews.ContainsKey(t.Id) && !_failedThumbs.Contains(t.Id);

    // A soft light band that sweeps across the picture area of a waiting card, drawn over the baked card every frame. It
    // is a few narrow slices of translucent white, kept clear of the rounded corners, so it costs little per card.
    private const int ShimmerWidth = 160, ShimmerSlice = 8;
    private const double ShimmerPeriod = 1.4;
    private void DrawCommunityShimmer(Surface band, int x, int y)
    {
        int left = x + Theme.RadiusCard, right = x + CommunityCardW - Theme.RadiusCard;
        double phase = _now % ShimmerPeriod / ShimmerPeriod;
        int centre = left - ShimmerWidth / 2 + (int)(phase * (right - left + ShimmerWidth));
        for (int sx = centre - ShimmerWidth / 2; sx < centre + ShimmerWidth / 2; sx += ShimmerSlice)
        {
            int x0 = Math.Max(sx, left), x1 = Math.Min(sx + ShimmerSlice, right);
            if (x1 <= x0) continue;
            float k = 1f - Math.Abs(sx + ShimmerSlice / 2 - centre) / (ShimmerWidth / 2f);
            if (k > 0) Paint.FillRect(band, x0, y, x1 - x0, CommunityImageH, White.WithAlpha((byte)(30 * k)));
        }
    }

    // ---- States -----------------------------------------------------------------------------------------------------------

    // With no list there is one centred message: what is going on, and what to press.
    private void DrawCommunityState(Surface s, ThemeBrowser browser, int enter)
    {
        // Without a store nothing is loading, whatever else the app is busy with.
        string heading, body;
        if (browser.StoreUrl.Length == 0) { heading = "No store set"; body = "Set the store address on the System tab."; }
        else if (Busy) { heading = "Loading themes…"; body = "Getting the list from the store."; }
        else if (CommunityError) { heading = "Can’t load the themes"; body = CommunityStatus; }
        else if (CommunityStatus.Length > 0 && CommunityLoadedQuery(browser).Length > 0)
        {
            // A search that found nothing: the chip with the term is how to get every theme back.
            heading = "No themes match “" + CommunityLoadedQuery(browser) + "”";
            body = "Try other words, or press X on the search chip to see every theme.";
        }
        else if (CommunityStatus.Length > 0 && CommunityLoadedCategory(browser) >= 0)
        {
            // A filtered list that came back empty: the store has themes, only none in this category.
            heading = "No themes in this category yet";
            body = "Pick another category above, or press Options to check again.";
        }
        else if (CommunityStatus.Length > 0) { heading = "No themes in the store yet"; body = "Press Options to check again."; }
        else { heading = "No themes loaded"; body = "Press Options to refresh."; }
        bool failed = !Busy && browser.StoreUrl.Length > 0 && CommunityError;

        // The body is wrapped once per text; a failure also says how to retry.
        if (_communityStateHeading != heading || _communityStateText != body)
        {
            _communityStateHeading = heading;
            _communityStateText = body;
            IReadOnlyList<string> wrapped = _body.Wrap(body, 900, 2);
            _communityStateBody = failed ? [.. wrapped, "Press Options to try again."] : wrapped;
        }
        int height = _heading.LineHeight + 12 + _communityStateBody.Count * _body.LineHeight;
        int top = (CommunityGridTop + Theme.ContentBottom - height) / 2 - 24 + enter;
        const int centre = Theme.ScreenWidth / 2;
        DrawMid(_heading, s, heading, centre, top, White);
        top += _heading.LineHeight + 12;
        foreach (string line in _communityStateBody)
        {
            DrawMid(_body, s, line, centre, top, Muted);
            top += _body.LineHeight;
        }
    }

    // ---- Cards ------------------------------------------------------------------------------------------------------------
    // A card (picture, panel, texts, badges, edge) is drawn once into one image, with its four corners rounded by the cache,
    // and from then on is a copy; only the focus ring is drawn per frame. A card is made again when its theme object is
    // replaced (a like or a download came in), when a flag below changes, or when the preview arrives.

    private byte CommunityFlags(CommunityTheme t)
    {
        byte flags = 0;
        if (CommunityLiked.Contains(t.Id)) flags |= CommunityFlagLiked;
        if (CommunityUpdates.Contains(t.Id)) flags |= CommunityFlagUpdate;
        if (CommunityInLibrary.Contains(t.Id)) flags |= CommunityFlagInLibrary;
        if (CommunityPreviews.ContainsKey(t.Id) && !_failedThumbs.Contains(t.Id)) flags |= CommunityFlagPreview;
        else if (CommunityPicturesLoading && CommunityPictureWaiting(t)) flags |= CommunityFlagWaiting;
        return flags;
    }

    // The baked card when it is still what the theme should look like now; null when it is missing, stale or was evicted.
    private CachedImage? CommunityCardCurrent(CommunityTheme t, byte flags) =>
        _communityStamps.TryGetValue(t.Id, out CommunityStamp stamp) && ReferenceEquals(stamp.Theme, t) && stamp.Flags == flags
            ? _communityImages.TryGet(t.Id, CommunityCardW, CommunityCardH, Theme.RadiusCard)
            : null;

    private CachedImage CommunityCard(CommunityTheme t)
    {
        byte flags = CommunityFlags(t);
        if (CommunityCardCurrent(t, flags) is { } current) return current;
        // Out of decodes for this frame: the card made with the placeholder picture will do until a later frame has a
        // slot for the preview, instead of being made over and over with the placeholder while it waits.
        if ((flags & CommunityFlagPreview) != 0 && _communityDecodes >= CommunityDecodesPerFrame
            && CommunityCardCurrent(t, (byte)(flags & ~CommunityFlagPreview)) is { } waiting)
            return waiting;
        return BakeCommunityCard(t, flags);
    }

    private CachedImage BakeCommunityCard(CommunityTheme t, byte flags)
    {
        CommunityBakes++;
        if (_communityStamps.Count >= 128) _communityStamps.Clear();
        using var card = new PixelBuffer(CommunityCardW, CommunityCardH);
        Surface c = card.AsSurface();
        c.FillRect(0, 0, CommunityCardW, CommunityCardH, CommunityCardInk);
        // Decoding and scaling a preview is the costly part of a card, so a frame makes only a couple of them; a card
        // that has to wait shows the placeholder meanwhile and (its stamp saying so) is made again on a later frame.
        bool wantPreview = (flags & CommunityFlagPreview) != 0 && _communityDecodes < CommunityDecodesPerFrame;
        if (wantPreview) _communityDecodes++;
        bool preview = DrawCommunityPicture(c, t, wantPreview, (flags & CommunityFlagWaiting) != 0);

        // Badges on the picture: the state of the pack at the right, the heart of a liked theme at the left.
        bool update = (flags & CommunityFlagUpdate) != 0;
        if (update || (flags & CommunityFlagInLibrary) != 0)
        {
            string text = update ? "Update" : "In library";
            Badge(c, text, CommunityCardW - (_small.MeasureText(text) + 52) - 14, 14, update ? Warn : Accent);
        }
        bool liked = (flags & CommunityFlagLiked) != 0;
        if (liked)
        {
            Paint.FillCircle(c, 14 + 20f, 14 + 20f, 20f, Theme.Ink.WithAlpha(210));
            DrawCommunityHeart(c, 14 + (40 - 22) / 2, 14 + (40 - CommunityHeartHeight(22)) / 2, 22, filled: true, CommunityHeart);
        }
        // AI-generated pictures (the author, the store's check or a moderator says so), after the heart.
        if (t.Ai) Badge(c, "AI", liked ? 14 + 40 + 8 : 14, 14, Theme.White);

        DrawCommunityCategories(c, t);
        DrawCommunityPanel(c, t, update, liked);
        Paint.StrokeRoundedRect(c, 0, 0, CommunityCardW, CommunityCardH, Theme.RadiusCard, 1, Theme.PanelStroke);
        _communityStamps[t.Id] = new CommunityStamp(t, preview ? flags : (byte)(flags & ~CommunityFlagPreview));
        return _communityImages.Add(t.Id, card, CommunityCardW, CommunityCardH, Theme.RadiusCard);
    }

    // The theme's categories as small ink-glass capsules along the bottom edge of the picture (the badges of the pack's state
    // are at its top): as many whole ones as fit, then "+N" for the rest. Made with the card, so a frame only copies them.
    internal const int CommunityCategoryBadgeH = 32;
    private void DrawCommunityCategories(Surface card, CommunityTheme t)
    {
        IReadOnlyList<string> ids = t.Categories;
        if (ids.Count == 0) return;
        const int pad = 14, gap = 8, edge = 14, available = CommunityCardW - 2 * edge;
        var labels = new string[ids.Count];
        var widths = new int[ids.Count];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = CommunityCategories.LabelAt(CommunityCategories.IndexOf(ids[i]));
            widths[i] = _small.MeasureText(labels[i]) + 2 * pad;
        }
        // The most whole badges that fit with the "+N" the others need.
        int shown = labels.Length;
        while (shown > 0 && BadgesWidth(shown) > available) shown--;
        int BadgesWidth(int n)
        {
            int total = -gap;
            for (int i = 0; i < n; i++) total += widths[i] + gap;
            if (n < labels.Length) total += gap + _small.MeasureText("+" + (labels.Length - n)) + 2 * pad;
            return total;
        }
        int x = edge, top = CommunityImageH - edge - CommunityCategoryBadgeH;
        for (int i = 0; i <= shown && i < labels.Length; i++)
        {
            bool more = i == shown;   // the badge after the last whole one is the "+N"
            string text = more ? "+" + (labels.Length - shown) : labels[i];
            int w = more ? _small.MeasureText(text) + 2 * pad : widths[i];
            Paint.Capsule(card, x, top, w, CommunityCategoryBadgeH, Theme.Ink.WithAlpha(200));
            Paint.StrokeRoundedRect(card, x, top, w, CommunityCategoryBadgeH, CommunityCategoryBadgeH / 2, 1, White.WithAlpha(52));   // the shape still reads on a dark picture
            DrawMid(_small, card, text, x + w / 2, top + (CommunityCategoryBadgeH - _small.LineHeight) / 2, White);
            x += w + gap;
        }
    }

    // The picture: the store preview cropped to the picture's shape and scaled once; a plain area while it is on its way
    // (the shimmer moves over it); the shared placeholder when it cannot be decoded or the store has none. Returns
    // whether the preview is what was drawn.
    private bool DrawCommunityPicture(Surface card, CommunityTheme t, bool wantPreview, bool waiting)
    {
        if (wantPreview && CommunityPreviews.TryGetValue(t.Id, out byte[]? png))
        {
            try
            {
                using PixelBuffer decoded = _decode(png);
                using PixelBuffer? cropped = CommunityCoverCrop(decoded, CommunityCardW, CommunityImageH);
                using PixelBuffer scaled = ImageCache.Scaled(cropped ?? decoded, CommunityCardW, CommunityImageH);
                card.BlitBlended(scaled.AsSurface(), 0, 0);
                return true;
            }
            catch { _failedThumbs.Add(t.Id); }
        }
        if (waiting && !_failedThumbs.Contains(t.Id))
        {
            card.FillRect(0, 0, CommunityCardW, CommunityImageH, KeyFill);
            return false;
        }
        // Drawn right away: adding the next image to the cache may drop this one.
        (_communityImages.TryGet(FallbackKey, CommunityCardW, CommunityImageH) ?? AddCommunityPlaceholder()).Draw(card, 0, 0);
        return false;
    }

    private CachedImage AddCommunityPlaceholder()
    {
        using PixelBuffer? cropped = CommunityCoverCrop(_fallback, CommunityCardW, CommunityImageH);
        return _communityImages.Add(FallbackKey, cropped ?? _fallback, CommunityCardW, CommunityImageH);
    }

    /// <summary>
    /// The middle of <paramref name="source"/> cut to the shape of width x height (the longer side is cropped, so the
    /// picture is never stretched), or null when it already has that shape. Never smaller than one pixel a side.
    /// </summary>
    private static PixelBuffer? CommunityCoverCrop(PixelBuffer source, int width, int height)
    {
        int cw = source.Width, ch = source.Height;
        long wanted = (long)source.Width * height / width;   // the height a full-width cut of the right shape has
        if (wanted < source.Height) ch = (int)Math.Max(1, wanted);
        else if (wanted > source.Height) cw = (int)Math.Max(1, (long)source.Height * width / height);
        if (cw == source.Width && ch == source.Height) return null;
        var result = new PixelBuffer(cw, ch);
        result.AsSurface().Blit(source.AsSurface().Region((source.Width - cw) / 2, (source.Height - ch) / 2, cw, ch), 0, 0);
        return result;
    }

    // Name and version, the author with the likes and downloads, and what the pack changes. Lines share baselines by
    // construction (y = baseline - Ascent), so the version sits on the name's line and the numbers on the author's.
    private void DrawCommunityPanel(Surface c, CommunityTheme t, bool update, bool liked)
    {
        const int inner = CommunityCardW - 2 * CommunityPad, right = CommunityCardW - CommunityPad;
        int nameBase = CommunityImageH + 38, metaBase = nameBase + 30, contentsBase = metaBase + 26;

        string version = t.Version.Length > 0 ? "v" + t.Version : "";
        int versionW = version.Length > 0 ? _small.MeasureText(version) : 0;
        if (versionW > 0) DrawRight(_small, c, version, right, nameBase - Ascent(_small), update ? Warn : Muted);
        string name = FitCommunityLine(_label, t.Name, inner - (versionW > 0 ? versionW + 14 : 0));
        DrawLeft(_label, c, name, CommunityPad, nameBase - Ascent(_label), White);

        // Right to left: downloads (arrow and number), likes (heart and number); the author gets what is left.
        const int heartW = 18, arrowW = 16, iconGap = 7, groupGap = 18;
        int iconMid = metaBase - 8;   // the middle of a capital letter above the baseline
        int textTop = metaBase - Ascent(_small);
        string downloads = CommunityCompactCount(t.Downloads), likes = CommunityCompactCount(t.Likes);
        int downloadsX = right - _small.MeasureText(downloads);
        DrawLeft(_small, c, downloads, downloadsX, textTop, Muted);
        int arrowX = downloadsX - iconGap - arrowW;
        DrawCommunityArrow(c, arrowX, iconMid - arrowW / 2, arrowW, Muted);
        int likesX = arrowX - groupGap - _small.MeasureText(likes);
        DrawLeft(_small, c, likes, likesX, textTop, liked ? White : Muted);
        int heartX = likesX - iconGap - heartW;
        DrawCommunityHeart(c, heartX, iconMid - CommunityHeartHeight(heartW) / 2, heartW, filled: liked, liked ? CommunityHeart : Muted);
        if (t.Author.Length > 0)
            DrawLeft(_small, c, FitCommunityLine(_small, t.Author, heartX - 14 - CommunityPad), CommunityPad, textTop, Muted);

        DrawLeft(_small, c, FitCommunityContents(ContentsOf(t), inner), CommunityPad, contentsBase - Ascent(_small), Accent);
    }

    // One line of text in the width, cut between words with an ellipsis when it is too long.
    internal static string FitCommunityLine(AtlasFont font, string text, int width)
    {
        if (font.MeasureText(text) <= width) return text;
        IReadOnlyList<string> line = font.Wrap(text, Math.Max(1, width), 1);
        if (line.Count == 0) return "";
        // "Name - Subtitle" cut after the dash would end in "-…": the separator goes with the part that is dropped.
        string cut = line[0];
        int end = cut.Length - 1;   // the ellipsis
        while (end > 0 && cut[end - 1] is ' ' or '-' or '–' or '·' or ':' or ',') end--;
        return end > 0 && end < cut.Length - 1 ? cut[..end] + "…" : cut;
    }

    // The "·" separated groups of a pack's contents, as many whole groups as fit and then how many more there are
    // ("Background · App background · +2 more", or just "+2" when "more" does not fit), so a line is never cut inside a word.
    internal string FitCommunityContents(string text, int width)
    {
        if (_small.MeasureText(text) <= width) return text;
        string[] parts = text.Split(" · ");
        for (int keep = parts.Length - 1; keep >= 1; keep--)
        {
            string kept = string.Join(" · ", parts, 0, keep), rest = " · +" + (parts.Length - keep);
            if (_small.MeasureText(kept + rest + " more") <= width) return kept + rest + " more";
            if (_small.MeasureText(kept + rest) <= width) return kept + rest;
        }
        return FitCommunityLine(_small, parts[0], width);
    }

    /// <summary>A count for a small label: exact below a thousand, then "1.2k", "12k", "1.2M".</summary>
    internal static string CommunityCompactCount(int count)
    {
        if (count < 1000) return Math.Max(0, count).ToString();
        if (count < 10_000) return CommunityTenths(count / 100, "k");
        if (count < 1_000_000) return (count / 1000) + "k";
        return CommunityTenths(count / 100_000, "M");
    }

    private static string CommunityTenths(int tenths, string unit) => tenths % 10 == 0 ? tenths / 10 + unit : tenths / 10 + "." + tenths % 10 + unit;

    // ---- Icons ------------------------------------------------------------------------------------------------------------

    // The heart curve (x² + y² − 1)³ = x²y³ and the box it fits in, in its own units (y up).
    private const double CommunityHeartMinX = -1.139, CommunityHeartMaxX = 1.139, CommunityHeartMinY = -1.0, CommunityHeartMaxY = 1.236;

    private static bool InCommunityHeart(double x, double y)
    {
        double a = x * x + y * y - 1;
        return a * a * a - x * x * y * y * y <= 0;
    }

    /// <summary>Height of a heart that is <paramref name="width"/> wide.</summary>
    internal static int CommunityHeartHeight(int width) => (int)Math.Round(width * (CommunityHeartMaxY - CommunityHeartMinY) / (CommunityHeartMaxX - CommunityHeartMinX));

    // The heart's coverage (0 to 255 per pixel) from 4 x 4 samples a pixel, made once per size and style. An outline is the
    // heart minus a smaller one, 2 px of ring at most sizes.
    private byte[] CommunityHeartMask(int width, bool filled)
    {
        int key = width * 2 + (filled ? 1 : 0);
        if (_communityHeartMasks.TryGetValue(key, out byte[]? cached)) return cached;
        int height = CommunityHeartHeight(width);
        var mask = new byte[width * height];
        double unitX = (CommunityHeartMaxX - CommunityHeartMinX) / width, unitY = (CommunityHeartMaxY - CommunityHeartMinY) / height;
        double centreY = (CommunityHeartMaxY + CommunityHeartMinY) / 2;
        double shrink = Math.Clamp(1 - 2.0 * unitX / CommunityHeartMaxX, 0.3, 0.95);
        const int samples = 4;
        for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
            {
                int hits = 0;
                for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                    {
                        double x = CommunityHeartMinX + (px + (sx + 0.5) / samples) * unitX;
                        double y = CommunityHeartMaxY - (py + (sy + 0.5) / samples) * unitY;
                        if (!InCommunityHeart(x, y)) continue;
                        if (!filled && InCommunityHeart(x / shrink, (y - centreY) / shrink + centreY)) continue;
                        hits++;
                    }
                mask[py * width + px] = (byte)(hits * 255 / (samples * samples));
            }
        return _communityHeartMasks[key] = mask;
    }

    // Blends the heart's coverage in the colour; a pixel at a time is fine for a handful of small icons made once per card.
    private void DrawCommunityHeart(Surface s, int x, int y, int width, bool filled, Color color)
    {
        byte[] mask = CommunityHeartMask(width, filled);
        int height = mask.Length / width;
        uint rgb = color.Value & 0x00FFFFFFu;
        for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
            {
                byte coverage = mask[py * width + px];
                if (coverage != 0) s.BlendPixel(x + px, y + py, rgb, (byte)(coverage * color.A / 255));
            }
    }

    // An arrow pointing down onto a line: the download count's icon.
    private static void DrawCommunityArrow(Surface s, int x, int y, int size, Color color)
    {
        float cx = x + size / 2f, stroke = 2f;
        Paint.Line(s, cx, y + 1.5f, cx, y + size * 0.62f, stroke, color);
        Paint.Line(s, cx - size * 0.3f, y + size * 0.36f, cx, y + size * 0.68f, stroke, color);
        Paint.Line(s, cx + size * 0.3f, y + size * 0.36f, cx, y + size * 0.68f, stroke, color);
        Paint.Line(s, x + size * 0.14f, y + size - 1.5f, x + size * 0.86f, y + size - 1.5f, stroke, color);
    }
}
