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

    /// <summary>
    /// The footer hints of the store: Update instead of Download for a theme with a newer version, Unlike instead of
    /// Like for one this console liked. With no list there is nothing to download or like, only to refresh.
    /// </summary>
    internal HintItem[] CommunityHints(ThemeBrowser browser)
    {
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
    // The sort control sits under the eyebrow row; the grid starts below it with room for the focus glow (12 px).
    private const int CommunitySortY = Theme.ContentTop + 36, CommunitySortH = 44;
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
    private const byte CommunityFlagLiked = 1, CommunityFlagUpdate = 2, CommunityFlagInLibrary = 4, CommunityFlagPreview = 8;
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
    private int[]? _communitySortWidth;
    private string _communityCountText = "";
    private int _communityCountFor = -1, _communityCountSort = -1;
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
        if (count > 0) DrawRight(_small, s, CommunityCountText(count, browser), Theme.ContentRight, y, Muted);

        // The three sorts as a row of capsules: the active one white, the others glass. The Square hint follows on the same line.
        if (_communitySortWidth is null) LayoutCommunitySort();
        int x = Theme.SafeX, top = CommunitySortY + enter;
        for (int i = 0; i < _communitySortWidth!.Length; i++)
        {
            bool on = i == browser.CommunitySort;
            int w = _communitySortWidth[i];
            if (on) Paint.Capsule(s, x, top, w, CommunitySortH, White);
            else
            {
                Paint.Capsule(s, x, top, w, CommunitySortH, Theme.Panel);
                Paint.StrokeRoundedRect(s, x, top, w, CommunitySortH, CommunitySortH / 2, 1, Theme.PanelStroke);
            }
            DrawMid(_label, s, CommunityList.Label((CommunitySort)i), x + w / 2, top + (CommunitySortH - _label.LineHeight) / 2, on ? PillInk : Muted);
            x += w + Theme.Unit;
        }
        Hint(s, PadButton.Square, "Sort", x + Theme.Unit, top + (CommunitySortH - _small.LineHeight) / 2);
    }

    private void LayoutCommunitySort()
    {
        _communitySortWidth = new int[3];
        for (int i = 0; i < 3; i++) _communitySortWidth[i] = _label.MeasureText(CommunityList.Label((CommunitySort)i)) + 2 * 24;
    }

    // "24 themes · Top rated", from the sort the list was loaded with; formatted again only when either changes.
    private string CommunityCountText(int count, ThemeBrowser browser)
    {
        int sort = CommunityListSort is >= 0 and <= 2 ? CommunityListSort : browser.CommunitySort;
        if (count != _communityCountFor || sort != _communityCountSort)
        {
            _communityCountFor = count;
            _communityCountSort = sort;
            _communityCountText = Plural(count, "theme") + " · " + CommunityList.Label((CommunitySort)sort);
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
            if (index == selected) { focusShown = true; focusX = x - CommunityBandLeft; focusY = y - CommunityBandTop; }
        }
        // After every card, so the glow lies over the neighbours instead of under them.
        if (focusShown) Paint.FocusRing(band, focusX, focusY, CommunityCardW, CommunityCardH, Theme.RadiusCard);
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

    // ---- States -----------------------------------------------------------------------------------------------------------

    // With no list there is one centred message: what is going on, and what to press.
    private void DrawCommunityState(Surface s, ThemeBrowser browser, int enter)
    {
        // Without a store nothing is loading, whatever else the app is busy with.
        string heading, body;
        if (browser.StoreUrl.Length == 0) { heading = "No store set"; body = "Set the store address on the System tab."; }
        else if (Busy) { heading = "Loading themes…"; body = "Getting the list from the store."; }
        else if (CommunityError) { heading = "Can’t load the themes"; body = CommunityStatus; }
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
        bool preview = DrawCommunityPicture(c, t, wantPreview);

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

        DrawCommunityPanel(c, t, update, liked);
        Paint.StrokeRoundedRect(c, 0, 0, CommunityCardW, CommunityCardH, Theme.RadiusCard, 1, Theme.PanelStroke);
        _communityStamps[t.Id] = new CommunityStamp(t, preview ? flags : (byte)(flags & ~CommunityFlagPreview));
        return _communityImages.Add(t.Id, card, CommunityCardW, CommunityCardH, Theme.RadiusCard);
    }

    // The picture: the store preview cropped to the picture's shape and scaled once, or the shared placeholder until it has
    // arrived (or when it cannot be decoded). Returns whether the preview is what was drawn.
    private bool DrawCommunityPicture(Surface card, CommunityTheme t, bool wantPreview)
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
