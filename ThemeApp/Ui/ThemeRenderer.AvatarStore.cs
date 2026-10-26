using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

public sealed partial class ThemeRenderer
{
    // ---- State the app feeds in ---------------------------------------------------------------------------------------

    /// <summary>The store's avatar list on the Avatars tab, newest answer from the server (cards in this order).</summary>
    public IReadOnlyList<StoreAvatar> AvatarStore { get; set; } = Array.Empty<StoreAvatar>();
    /// <summary>The 440x440 preview JPEGs by store id, filled in as they arrive; decoded here on first draw.</summary>
    public Dictionary<string, byte[]> AvatarStorePreviews { get; } = new();
    /// <summary>Store ids whose pack is already in the app's avatars (<see cref="Library.AvatarStore.InLibrary"/>): "In library" badge.</summary>
    public IReadOnlySet<string> AvatarStoreInLibrary { get; set; } = new HashSet<string>();
    /// <summary>Store ids this console has liked (avatar-likes.json): a heart on the card, Unlike in the footer.</summary>
    public IReadOnlySet<string> AvatarStoreLiked { get; set; } = new HashSet<string>();
    /// <summary>The sort (a <see cref="CommunitySort"/> value) the list on screen was loaded with, or -1 before any list.</summary>
    public int AvatarStoreListSort { get; set; } = -1;
    /// <summary>The search term the list on screen was loaded with ("" for none), like <see cref="AvatarStoreListSort"/>; read once a list was loaded.</summary>
    public string AvatarStoreListQuery { get; set; } = "";
    /// <summary>The Avatars tab's empty or error text ("Loading avatars…", "Can't reach the store: …"); drawn only while the list is empty.</summary>
    public string AvatarStoreStatus { get; set; } = "";
    /// <summary>Whether <see cref="AvatarStoreStatus"/> reports a failed load.</summary>
    public bool AvatarStoreError { get; set; }
    /// <summary>Whether the app is still fetching card previews (a card waiting for its picture may show a placeholder meanwhile).</summary>
    public bool AvatarStorePicturesLoading { get; set; }

    /// <summary>Drops the Avatars tab's card images and previews (a new list was loaded).</summary>
    public void ResetAvatarStore()
    {
        AvatarStorePreviews.Clear();
        ClearAvatarStoreImages();
    }

    // ---- Layout -----------------------------------------------------------------------------------------------------------

    // Six columns fill the width between the safe margins; two rows fill the height like the Community tab's cards (same grid
    // top and card height, so both tabs sit on the same lines and the sort row is the same).
    private const int AvatarStoreColumns = ThemeBrowser.AvatarStoreColumns, AvatarStoreGap = 24, AvatarStoreVisibleRows = 2;
    internal const int AvatarStoreCardW = (Theme.ContentWidth - (AvatarStoreColumns - 1) * AvatarStoreGap) / AvatarStoreColumns;   // 268
    internal const int AvatarStoreCardH = CommunityCardH;   // 319
    private const int AvatarStoreRowStride = AvatarStoreCardH + CommunityRowGap;
    // The round picture; the name and the stats line sit under it, the three centred as one block.
    internal const int AvatarStoreFace = 184;
    private const int AvatarStoreFaceGap = 16, AvatarStoreTextGap = 4, AvatarStoreBadgeInset = 12, AvatarStoreDecodesPerFrame = 2;
    private const int AvatarStoreStatGap = 14, AvatarStoreHeart = 16, AvatarStoreArrow = 14, AvatarStoreIconGap = 7;

    // What a baked card shows besides the avatar itself: the flags below.
    private const byte AvatarStoreFlagLiked = 1, AvatarStoreFlagInLibrary = 2, AvatarStoreFlagFace = 4;
    private readonly record struct AvatarStoreStamp(StoreAvatar Avatar, byte Flags);
    private readonly Dictionary<string, AvatarStoreStamp> _avatarStoreStamps = new();
    // Its own caches, so a store list never wipes the Library's avatar row and the other way round.
    private readonly ImageCache _avatarStoreImages = new(maxEntries: 30, maxBytes: 12L << 20);
    // The pictures as circles (scaled once), by store id, and the ids whose preview did not decode.
    private readonly Dictionary<string, CachedImage> _avatarStoreFaces = new();
    private readonly HashSet<string> _avatarStoreFailed = new();
    private int _avatarStoreDecodes;
    private bool _avatarStorePending;
    // Whether the grid is still sliding or a card still waits for its picture: a dialog's dimmed snapshot is not kept then.
    private bool _avatarStoreMoving;
    private float _avatarStoreScroll;
    private int _avatarStoreTop;
    private bool _avatarStoreScrollReady;
    private string _avatarStoreCountText = "", _avatarStoreCountQuery = "";
    private int _avatarStoreCountFor = -1, _avatarStoreCountSort = -1;
    private readonly SearchChipLabel _avatarStoreChip = new();
    /// <summary>The text on the Avatars tab's search chip as last drawn ("Search", or the term with a cross); for the tests.</summary>
    internal string AvatarStoreChipText => _avatarStoreChip.Text;
    private string? _avatarStoreStateHeading, _avatarStoreStateText;
    private IReadOnlyList<string> _avatarStoreStateBody = Array.Empty<string>();
    // The line under the name in the download question, for the avatar it was made for.
    private StoreAvatar? _dialogStoreAvatar;
    private string _dialogStoreAvatarLine = "";

    private void ClearAvatarStoreImages()
    {
        _avatarStoreImages.Clear();
        _avatarStoreStamps.Clear();
        foreach (CachedImage face in _avatarStoreFaces.Values) face.Dispose();
        _avatarStoreFaces.Clear();
        _avatarStoreFailed.Clear();
    }

    private void ReleaseAvatarStore() => ClearAvatarStoreImages();

    // ---- Footer -----------------------------------------------------------------------------------------------------------

    // On a card: Download, Like (Unlike), Sort, Refresh; on the top row of cards a fifth hint says that Up reaches the search chip.
    // Indexed by [liked][topRow], made once so the footer allocates nothing.
    private static readonly HintItem[][][] AvatarStoreCardHints = BuildAvatarStoreCardHints();

    private static HintItem[][][] BuildAvatarStoreCardHints()
    {
        var sets = new HintItem[2][][];
        for (int liked = 0; liked < 2; liked++)
        {
            sets[liked] = new HintItem[2][];
            for (int top = 0; top < 2; top++)
            {
                var hints = new List<HintItem>
                {
                    new(PadButton.Cross, "Download"), new(PadButton.Triangle, liked == 1 ? "Unlike" : "Like"),
                    new(PadButton.Square, "Sort"), new(PadButton.Options, "Refresh"),
                };
                if (top == 1) hints.Add(new(PadButton.DPadUp, "Search"));
                sets[liked][top] = hints.ToArray();
            }
        }
        return sets;
    }

    private static readonly HintItem[] AvatarStoreEmptyHints =
        [new(PadButton.Options, "Refresh"), new(PadButton.Square, "Sort"), new(PadButton.DPadUp, "Search"), new(PadButton.Circle, "Back")];
    // With the search chip focused: X opens the keyboard, or takes the term off when there is one.
    private static readonly HintItem[] AvatarStoreSearchHints =
        [new(PadButton.Cross, "Search"), new(PadButton.Square, "Sort"), new(PadButton.Options, "Refresh"), new(PadButton.Circle, "Back")];
    private static readonly HintItem[] AvatarStoreClearHints =
        [new(PadButton.Cross, "Clear search"), new(PadButton.Square, "Sort"), new(PadButton.Options, "Refresh"), new(PadButton.Circle, "Back")];

    /// <summary>The store avatar under the cursor on the Avatars tab, or null when the list is empty.</summary>
    internal StoreAvatar? FocusedStoreAvatar(ThemeBrowser browser) =>
        browser.AvatarStoreSelected >= 0 && browser.AvatarStoreSelected < AvatarStore.Count ? AvatarStore[browser.AvatarStoreSelected] : null;

    /// <summary>
    /// The footer hints of the Avatars tab: Download, Like (Unlike for one this console liked), Sort and Refresh on a card, with the way to the
    /// search chip on the top row; with the chip focused, its own; with no list, only Refresh, Sort, Search and Back.
    /// </summary>
    internal HintItem[] AvatarStoreHints(ThemeBrowser browser)
    {
        if (browser.AvatarSearchFocused) return browser.AvatarStoreQuery.Length > 0 ? AvatarStoreClearHints : AvatarStoreSearchHints;
        if (FocusedStoreAvatar(browser) is not { } avatar) return AvatarStoreEmptyHints;
        return AvatarStoreCardHints[AvatarStoreLiked.Contains(avatar.Id) ? 1 : 0][browser.AvatarStoreSelected < ThemeBrowser.AvatarStoreColumns ? 1 : 0];
    }

    // ---- Screen -----------------------------------------------------------------------------------------------------------

    private void DrawAvatarStore(Surface s, ThemeBrowser browser, int enter)
    {
        IReadOnlyList<StoreAvatar> items = AvatarStore;
        DrawAvatarStoreHeader(s, browser, items.Count, enter);
        if (items.Count == 0)
        {
            _avatarStoreMoving = false;
            DrawAvatarStoreState(s, browser, enter);
        }
        else DrawAvatarStoreGrid(s, browser, items, enter);
    }

    // The eyebrow with the count of the loaded list, and the three sorts under it as pills (Square steps through them).
    private void DrawAvatarStoreHeader(Surface s, ThemeBrowser browser, int count, int enter)
    {
        int y = Theme.ContentTop + enter;
        DrawLeft(_small, s, "COMMUNITY AVATARS", Theme.SafeX, y, Accent);
        if (count > 0) DrawRight(_small, s, AvatarStoreCountText(count, browser), Theme.ContentRight, y, Muted);

        if (_communitySortWidth is null) LayoutCommunityRow();
        int x = Theme.SafeX, top = CommunitySortY + enter;
        for (int i = 0; i < _communitySortWidth!.Length; i++)
        {
            int w = _communitySortWidth[i];
            DrawCommunityPill(s, x, top, w, i == browser.AvatarStoreSort, false, CommunityList.Label((CommunitySort)i));
            x += w + Theme.Unit;
        }
        // Square sorts on every card here (it previews nothing), so the hint always follows the pills.
        Hint(s, PadButton.Square, "Sort", x + Theme.Unit, top + (CommunitySortH - _small.LineHeight) / 2);
        DrawAvatarStoreChip(s, browser, top);
    }

    // The search chip after the Sort hint, where the Themes tab's chips begin: "Search", or the term with a cross that says X removes it.
    private void DrawAvatarStoreChip(Surface s, ThemeBrowser browser, int top)
    {
        _avatarStoreChip.Update(_label, browser.AvatarStoreQuery);
        int left = _communityChipsLeft, w = _avatarStoreChip.Width;
        Paint.FillRect(s, left - CommunityChipsGap / 2, top + 8, 1, CommunitySortH - 16, White.WithAlpha(36));
        bool focused = browser.AvatarSearchFocused;
        DrawCommunityPill(s, left, top, w, browser.AvatarStoreQuery.Length > 0, focused, _avatarStoreChip.Text);
        if (!focused) return;
        // The ring and its glow reach 12 px past the chip, so they get a band a little wider than the chip.
        const int room = 14;
        Surface ring = s.Region(left - room, top - room, w + 2 * room, CommunitySortH + 2 * room);
        Paint.FocusRing(ring, room, room, w, CommunitySortH, CommunitySortH / 2, Accent);
    }

    // The search term the list on screen was loaded with; before the app has loaded any list (the tests draw one directly) the term in force.
    private string AvatarStoreLoadedQuery(ThemeBrowser browser) => AvatarStoreListSort is >= 0 and <= 2 ? AvatarStoreListQuery : browser.AvatarStoreQuery;

    // "6 avatars · Top rated" (with the term: "2 avatars · “house” · Top rated"), from the sort the list was loaded with (the pills may have moved on); formatted again only when it changes.
    internal string AvatarStoreCountText(int count, ThemeBrowser browser)
    {
        int sort = AvatarStoreListSort is >= 0 and <= 2 ? AvatarStoreListSort : browser.AvatarStoreSort;
        string query = AvatarStoreLoadedQuery(browser);
        if (count != _avatarStoreCountFor || sort != _avatarStoreCountSort || query != _avatarStoreCountQuery)
        {
            _avatarStoreCountFor = count;
            _avatarStoreCountSort = sort;
            _avatarStoreCountQuery = query;
            _avatarStoreCountText = Plural(count, "avatar") + (query.Length > 0 ? " · “" + query + "”" : "") + " · " + CommunityList.Label((CommunitySort)sort);
        }
        return _avatarStoreCountText;
    }

    private void DrawAvatarStoreGrid(Surface s, ThemeBrowser browser, IReadOnlyList<StoreAvatar> items, int enter)
    {
        _avatarStoreDecodes = 0;
        _avatarStorePending = false;
        int count = items.Count, rows = (count + AvatarStoreColumns - 1) / AvatarStoreColumns;
        int selected = Math.Clamp(browser.AvatarStoreSelected, 0, count - 1), selectedRow = selected / AvatarStoreColumns;

        // As on the Community tab: the window of two rows only moves when the focus leaves it, and a jump of more than one row
        // (Square resets to the first card) snaps instead of gliding past every card in between.
        int maxTop = Math.Max(0, rows - AvatarStoreVisibleRows);
        int top = Math.Clamp(_avatarStoreTop, 0, maxTop);
        if (selectedRow < top) top = selectedRow;
        else if (selectedRow >= top + AvatarStoreVisibleRows) top = selectedRow - AvatarStoreVisibleRows + 1;
        top = Math.Clamp(top, 0, maxTop);
        float target = top * AvatarStoreRowStride;
        if (!_avatarStoreScrollReady || Math.Abs(top - _avatarStoreTop) > 1) { _avatarStoreScroll = target; _avatarStoreScrollReady = true; }
        else
        {
            _avatarStoreScroll += (target - _avatarStoreScroll) * CommunityEase;
            if (MathF.Abs(target - _avatarStoreScroll) < 0.5f) _avatarStoreScroll = target;
        }
        _avatarStoreTop = top;
        int offset = (int)MathF.Round(_avatarStoreScroll);

        Surface band = s.Region(CommunityBandLeft, CommunityBandTop, CommunityBandRight - CommunityBandLeft, CommunityBandBottom - CommunityBandTop);
        int firstRow = Math.Max(0, offset / AvatarStoreRowStride);
        int firstIndex = firstRow * AvatarStoreColumns, endIndex = Math.Min(count, (firstRow + AvatarStoreVisibleRows + 1) * AvatarStoreColumns);
        int focusX = 0, focusY = 0;
        bool focusShown = false;
        for (int index = firstIndex; index < endIndex; index++)
        {
            int x = Theme.SafeX + index % AvatarStoreColumns * (AvatarStoreCardW + AvatarStoreGap);
            int y = CommunityGridTop + index / AvatarStoreColumns * AvatarStoreRowStride - offset + enter;
            if (y + AvatarStoreCardH <= CommunityBandTop || y >= CommunityBandBottom) continue;
            AvatarStoreCard(items[index]).Draw(band, x - CommunityBandLeft, y - CommunityBandTop);
            if (index == selected) { focusShown = true; focusX = x - CommunityBandLeft; focusY = y - CommunityBandTop; }
        }
        // After every card, so the glow lies over the neighbours instead of under them.
        if (focusShown && !browser.AvatarSearchFocused) Paint.FocusRing(band, focusX, focusY, AvatarStoreCardW, AvatarStoreCardH, Theme.RadiusCard);
        PrefetchAvatarStoreCards(items, top, rows);
        _avatarStoreMoving = _avatarStoreScroll != target || _avatarStorePending;
    }

    // Bakes one card a frame of the rows next to the visible ones while nothing else is pending, so a row that scrolls in is
    // already made instead of costing a hitch of six cards at the moment it appears.
    private void PrefetchAvatarStoreCards(IReadOnlyList<StoreAvatar> items, int top, int rows)
    {
        if (_avatarStoreDecodes > 0) return;   // this frame already decoded a picture
        int firstRow = Math.Max(0, top - 1), lastRow = Math.Min(rows - 1, top + AvatarStoreVisibleRows);
        for (int index = firstRow * AvatarStoreColumns; index < Math.Min(items.Count, (lastRow + 1) * AvatarStoreColumns); index++)
        {
            StoreAvatar a = items[index];
            if (AvatarStoreCardCurrent(a, AvatarStoreFlags(a)) is not null) continue;
            if (AvatarStorePreviews.ContainsKey(a.Id) && !_avatarStoreFaces.ContainsKey(a.Id) && !_avatarStoreFailed.Contains(a.Id))
                AvatarStoreFaceOf(a);   // the card is made with its picture
            BakeAvatarStoreCard(a, AvatarStoreFlags(a));
            return;
        }
    }

    // ---- States -----------------------------------------------------------------------------------------------------------

    // With no list there is one centred message: what is going on, and what to press.
    private void DrawAvatarStoreState(Surface s, ThemeBrowser browser, int enter)
    {
        // Without a store nothing is loading, whatever else the app is busy with.
        string heading, body;
        if (browser.StoreUrl.Length == 0) { heading = "No store set"; body = "Set the store address on the System tab."; }
        else if (Busy) { heading = "Loading avatars…"; body = "Getting the list from the store."; }
        else if (AvatarStoreError) { heading = "Can’t load the avatars"; body = AvatarStoreStatus; }
        else if (AvatarStoreListSort is >= 0 and <= 2 && AvatarStoreListQuery.Length > 0)
        {
            // A search that found nothing: the chip with the term is how to get every avatar back.
            heading = "No avatars match “" + AvatarStoreListQuery + "”";
            body = "Try other words, or press X on the search chip to see every avatar.";
        }
        else if (AvatarStoreListSort is >= 0 and <= 2) { heading = "No avatars in the store yet"; body = "Press Options to check again."; }
        else { heading = "No avatars loaded"; body = "Press Options to refresh."; }
        bool failed = !Busy && browser.StoreUrl.Length > 0 && AvatarStoreError;

        // The body is wrapped once per text; a failure also says how to retry.
        if (_avatarStoreStateHeading != heading || _avatarStoreStateText != body)
        {
            _avatarStoreStateHeading = heading;
            _avatarStoreStateText = body;
            IReadOnlyList<string> wrapped = _body.Wrap(body, 900, 2);
            _avatarStoreStateBody = failed ? [.. wrapped, "Press Options to try again."] : wrapped;
        }
        int height = _heading.LineHeight + 12 + _avatarStoreStateBody.Count * _body.LineHeight;
        int top = (CommunityGridTop + Theme.ContentBottom - height) / 2 - 24 + enter;
        const int centre = Theme.ScreenWidth / 2;
        DrawMid(_heading, s, heading, centre, top, White);
        top += _heading.LineHeight + 12;
        for (int i = 0; i < _avatarStoreStateBody.Count; i++)
        {
            DrawMid(_body, s, _avatarStoreStateBody[i], centre, top, Muted);
            top += _body.LineHeight;
        }
    }

    // ---- Cards ------------------------------------------------------------------------------------------------------------
    // A card (round picture, name, author with likes and downloads, badges, edge) is drawn once into one opaque image with its
    // corners rounded by the cache, and from then on is a copy; only the focus ring is drawn per frame. A card is made again
    // when its avatar object is replaced (a like or a download count came in), when a flag changes, or when its picture arrives.

    private byte AvatarStoreFlags(StoreAvatar a)
    {
        byte flags = 0;
        if (AvatarStoreLiked.Contains(a.Id)) flags |= AvatarStoreFlagLiked;
        if (AvatarStoreInLibrary.Contains(a.Id)) flags |= AvatarStoreFlagInLibrary;
        if (_avatarStoreFaces.ContainsKey(a.Id)) flags |= AvatarStoreFlagFace;
        return flags;
    }

    private CachedImage? AvatarStoreCardCurrent(StoreAvatar a, byte flags) =>
        _avatarStoreStamps.TryGetValue(a.Id, out AvatarStoreStamp stamp) && ReferenceEquals(stamp.Avatar, a) && stamp.Flags == flags
            ? _avatarStoreImages.TryGet(a.Id, AvatarStoreCardW, AvatarStoreCardH, Theme.RadiusCard)
            : null;

    private CachedImage AvatarStoreCard(StoreAvatar a)
    {
        // Decoding a preview is the costly part, so a frame decodes only a couple; a card still waiting shows its letter disc
        // until a later frame has a slot for the picture.
        if (AvatarStorePreviews.ContainsKey(a.Id) && !_avatarStoreFaces.ContainsKey(a.Id) && !_avatarStoreFailed.Contains(a.Id))
        {
            if (_avatarStoreDecodes < AvatarStoreDecodesPerFrame) AvatarStoreFaceOf(a);
            else _avatarStorePending = true;
        }
        byte flags = AvatarStoreFlags(a);
        return AvatarStoreCardCurrent(a, flags) ?? BakeAvatarStoreCard(a, flags);
    }

    private CachedImage BakeAvatarStoreCard(StoreAvatar a, byte flags)
    {
        if (_avatarStoreStamps.Count >= 128) _avatarStoreStamps.Clear();
        using var buffer = new PixelBuffer(AvatarStoreCardW, AvatarStoreCardH);
        Surface c = buffer.AsSurface();
        c.FillRect(0, 0, AvatarStoreCardW, AvatarStoreCardH, CommunityCardInk);

        int textBlock = _label.LineHeight + AvatarStoreTextGap + _small.LineHeight;
        int faceTop = Math.Max(AvatarStoreBadgeInset, (AvatarStoreCardH - AvatarStoreFace - AvatarStoreFaceGap - textBlock) / 2);
        int faceX = (AvatarStoreCardW - AvatarStoreFace) / 2;
        float cx = AvatarStoreCardW / 2f, cy = faceTop + AvatarStoreFace / 2f;
        if ((flags & AvatarStoreFlagFace) != 0 && _avatarStoreFaces.TryGetValue(a.Id, out CachedImage? face)) face.Draw(c, faceX, faceTop);
        else
        {
            // Waiting for its picture, or one that does not decode: a plain disc with the first letter of the name.
            Paint.FillCircle(c, cx, cy, AvatarStoreFace / 2f, AvatarFaceFill);
            if (a.Name.Length > 0) DrawMid(_title, c, a.Name[..1].ToUpperInvariant(), (int)cx, (int)cy - _title.LineHeight / 2, Muted);
        }

        // Badges: the heart of a liked avatar at the left, the pack's state at the right.
        bool liked = (flags & AvatarStoreFlagLiked) != 0;
        if (liked)
        {
            Paint.FillCircle(c, AvatarStoreBadgeInset + 20f, AvatarStoreBadgeInset + 20f, 20f, Theme.Ink.WithAlpha(210));
            DrawCommunityHeart(c, AvatarStoreBadgeInset + (40 - 22) / 2, AvatarStoreBadgeInset + (40 - CommunityHeartHeight(22)) / 2, 22, filled: true, CommunityHeart);
        }
        if ((flags & AvatarStoreFlagInLibrary) != 0)
            Badge(c, "In library", AvatarStoreCardW - (_small.MeasureText("In library") + 52) - AvatarStoreBadgeInset, AvatarStoreBadgeInset, Accent);

        // The name, then the author with the likes and downloads on one line, centred.
        int nameY = faceTop + AvatarStoreFace + AvatarStoreFaceGap, room = AvatarStoreCardW - 24;
        DrawMid(_label, c, FitCommunityLine(_label, a.Name, room), AvatarStoreCardW / 2, nameY, White);
        DrawAvatarStoreStats(c, a, liked, nameY + _label.LineHeight + AvatarStoreTextGap, room);

        Paint.StrokeRoundedRect(c, 0, 0, AvatarStoreCardW, AvatarStoreCardH, Theme.RadiusCard, 1, Theme.PanelStroke);
        _avatarStoreStamps[a.Id] = new AvatarStoreStamp(a, flags);
        return _avatarStoreImages.Add(a.Id, buffer, AvatarStoreCardW, AvatarStoreCardH, Theme.RadiusCard);
    }

    // "author ♡12 ⤓340" as one centred line: the author gets what the counts leave, and is left out when that is too little.
    private void DrawAvatarStoreStats(Surface c, StoreAvatar a, bool liked, int y, int room)
    {
        string likes = CommunityCompactCount(a.Likes), downloads = CommunityCompactCount(a.Downloads);
        int statsW = AvatarStoreHeart + AvatarStoreIconGap + _small.MeasureText(likes) + AvatarStoreStatGap
            + AvatarStoreArrow + AvatarStoreIconGap + _small.MeasureText(downloads);
        string author = "";
        int authorW = 0;
        if (a.Author.Length > 0)
        {
            int authorRoom = room - statsW - AvatarStoreStatGap;
            if (authorRoom >= 56)
            {
                author = FitCommunityLine(_small, a.Author, authorRoom);
                authorW = _small.MeasureText(author) + AvatarStoreStatGap;
            }
        }
        int x = (AvatarStoreCardW - (authorW + statsW)) / 2;
        int baseline = y + Ascent(_small), iconMid = baseline - 8;   // the middle of a capital letter above the baseline
        if (authorW > 0) { DrawLeft(_small, c, author, x, y, Muted); x += authorW; }
        DrawCommunityHeart(c, x, iconMid - CommunityHeartHeight(AvatarStoreHeart) / 2, AvatarStoreHeart, filled: liked, liked ? CommunityHeart : Muted);
        x += AvatarStoreHeart + AvatarStoreIconGap;
        DrawLeft(_small, c, likes, x, y, liked ? White : Muted);
        x += _small.MeasureText(likes) + AvatarStoreStatGap;
        DrawCommunityArrow(c, x, iconMid - AvatarStoreArrow / 2, AvatarStoreArrow, Muted);
        x += AvatarStoreArrow + AvatarStoreIconGap;
        DrawLeft(_small, c, downloads, x, y, Muted);
    }

    // The avatar's preview as a circle, decoded and scaled once; null when it has none or it does not decode.
    private CachedImage? AvatarStoreFaceOf(StoreAvatar a)
    {
        if (_avatarStoreFaces.TryGetValue(a.Id, out CachedImage? face)) return face;
        if (_avatarStoreFailed.Contains(a.Id) || !AvatarStorePreviews.TryGetValue(a.Id, out byte[]? preview)) return null;
        _avatarStoreDecodes++;
        try
        {
            using PixelBuffer decoded = _decode(preview);
            // Far more than a list holds: start over rather than grow without end.
            if (_avatarStoreFaces.Count >= 96) ClearAvatarStoreImages();
            return _avatarStoreFaces[a.Id] = ImageCache.Prepare(decoded, AvatarStoreFace, AvatarStoreFace, AvatarStoreFace / 2);
        }
        catch
        {
            _avatarStoreFailed.Add(a.Id);
            return null;
        }
    }
}
