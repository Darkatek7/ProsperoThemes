using System;
using System.Collections.Generic;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>What one card of the Library's avatar row shows: the pack's id, name and author, and its JPEG preview (null when it has none).</summary>
public sealed record AvatarTile(string Id, string Name, string Author, byte[]? Preview);

public sealed partial class ThemeRenderer
{
    /// <summary>The app's avatars, in the order of the row (after the Console picture card). Setting a new list drops its cards.</summary>
    public IReadOnlyList<AvatarTile> Avatars
    {
        get => _avatars;
        set
        {
            _avatars = value;
            _avatarImages.Clear();
            foreach (CachedImage face in _avatarFaces.Values) face.Dispose();
            _avatarFaces.Clear();
            _avatarFailed.Clear();
        }
    }
    private IReadOnlyList<AvatarTile> _avatars = Array.Empty<AvatarTile>();

    /// <summary>Whether <see cref="Avatars"/> has been read once; until then the row says it is loading.</summary>
    public bool AvatarsLoaded { get; set; }

    /// <summary>
    /// The id of the avatar the app set as this user's picture (AvatarApplier's state), or null when the console's own picture is
    /// in place. That card says "In use".
    /// </summary>
    public string? AppliedAvatarId { get; set; }

    /// <summary>
    /// Whether the offer to restart the home screen is about a new profile picture (an avatar was set or reset), not a theme's
    /// icons and sounds: the question then says so.
    /// </summary>
    public bool ShellRestartForAvatar
    {
        get => _shellRestartForAvatar;
        set
        {
            if (value == _shellRestartForAvatar) return;
            _shellRestartForAvatar = value;
            _dialogBody[(int)DialogKind.RestartShell - 1] = null;
            _dialogBody[(int)DialogKind.RestartShellBlocked - 1] = null;
        }
    }
    private bool _shellRestartForAvatar;

    // Each card is baked once into an opaque card (its picture as a circle, the name and a second line), so a frame copies
    // it and only blends the card's corners. Cards are keyed by index and state; a new list clears them.
    internal const int AvatarCardW = 240, AvatarCardH = 248, AvatarGap = 24, AvatarStride = AvatarCardW + AvatarGap, AvatarFace = 144;
    private const int AvatarFaceTop = 20, AvatarDecodesPerFrame = 2;
    private static readonly Color AvatarCardFill = Theme.KeyFill;
    private static readonly Color AvatarFaceFill = Color.FromRgb(52, 68, 92);
    private readonly ImageCache _avatarImages = new(maxEntries: 24, maxBytes: 8L << 20);
    // The pictures as circles (scaled once), by index, and the cards whose preview did not decode.
    private readonly Dictionary<int, CachedImage> _avatarFaces = new();
    private readonly HashSet<int> _avatarFailed = new();
    private CachedImage? _consoleFace;
    // Card names by index and state, made once each so a frame allocates no strings.
    private readonly Dictionary<int, string> _avatarKeys = new();
    // Faces decoded in this frame, and whether a card on screen still waits for its face (the picture is not final then).
    private int _avatarDecodes;
    private bool _avatarPending;
    private string _avatarCountText = "";
    private int _avatarCountFor = -1;
    // Horizontal scroll of the avatar row, in pixels: eased toward the offset that keeps the focused card in view.
    private float _avatarScroll;

    private static readonly HintItem[] AvatarConsoleHintItems = [new(PadButton.Cross, "Use console picture"), new(PadButton.Options, "Reload")];
    private static readonly HintItem[] AvatarHintItems =
        [new(PadButton.Cross, "Set as picture"), new(PadButton.Triangle, "Remove"), new(PadButton.Options, "Reload")];

    /// <summary>The footer hints while the Library's focus is on the avatar row.</summary>
    private static HintItem[] AvatarHints(ThemeBrowser browser) => browser.AvatarSelected == 0 ? AvatarConsoleHintItems : AvatarHintItems;

    // The avatar section of the Library: its heading with the count, then the row of cards, the Console picture card first. It
    // sits below the themes; lift is how far the lower content has slid up (0 while only the heading peeks in above the footer).
    private void DrawAvatarRow(Surface s, ThemeBrowser browser, int lift, double delta, bool snap)
    {
        _avatarDecodes = 0;
        _avatarPending = false;
        int headingY = AvatarHeadingTop - lift, cardsY = AvatarCardsTop - lift;
        // Nothing of the section reaches into the footer row.
        Surface lower = s.Region(0, 0, Theme.ScreenWidth, Theme.FooterY);

        int items = _avatars.Count + 1;
        int focus = Math.Clamp(browser.AvatarSelected, 0, items - 1);
        DrawLeft(_heading, lower, "Your avatars", Theme.SafeX, headingY, White);
        // "3 avatars" shares the heading's baseline; made again only when the list changes.
        bool counted = AvatarsLoaded && _avatars.Count > 0;
        if (_avatarCountFor != _avatars.Count || (_avatarCountText.Length > 0) != counted)
        {
            _avatarCountFor = _avatars.Count;
            _avatarCountText = counted ? Plural(_avatars.Count, "avatar") : "";
        }
        if (counted)
            DrawLeft(_small, lower, _avatarCountText, Theme.SafeX + _heading.MeasureText("Your avatars") + 16, headingY + Ascent(_heading) - Ascent(_small), Muted);

        // Scroll: like the themes' row, the focused card stays the third one when the row is long enough (and the row never
        // scrolls past its ends); the offset eases toward that, and settles exactly so a still screen is a still picture.
        int maxScroll = Math.Max(0, items * AvatarStride - AvatarGap - Theme.ContentWidth);
        float target = Math.Clamp((focus - 2) * AvatarStride, 0, maxScroll);
        if (snap) _avatarScroll = target;
        else
        {
            _avatarScroll += (target - _avatarScroll) * (float)(1 - Math.Exp(-Math.Clamp(delta, 0, .1) * 16));
            if (MathF.Abs(target - _avatarScroll) < 0.5f) _avatarScroll = target;
        }
        if (_avatarScroll != target) _libraryMoving = true;
        int offset = (int)MathF.Round(_avatarScroll);

        int clipTop = cardsY - AvatarRingRoom, clipBottom = Math.Min(cardsY + AvatarCardH + AvatarRingRoom, Theme.FooterY);
        if (clipTop >= clipBottom)
        {
            // Out of sight below the heading: the pictures of the first cards are made in quiet frames, one at a time, so the
            // slide that brings them in is not the frame that decodes them.
            WarmAvatars();
            return;
        }
        Surface row = lower.Region(RowClipLeft, clipTop, RowClipRight - RowClipLeft, clipBottom - clipTop);
        int ly = cardsY - clipTop;
        for (int i = 0; i < items; i++)
        {
            int x = Theme.SafeX + i * AvatarStride - offset;
            if (x + AvatarCardW <= RowClipLeft || x >= RowClipRight) continue;
            int lx = x - RowClipLeft;
            AvatarCard(i).Draw(row, lx, ly);
            if (i == focus && browser.AvatarRowFocused) Paint.FocusRing(row, lx, ly, AvatarCardW, AvatarCardH, Theme.RadiusCard);
        }
        if (_avatarPending) _libraryMoving = true;

        // Without avatars, the rest of the row says why, and where they come from.
        if (_avatars.Count == 0)
        {
            int x = Theme.SafeX + AvatarStride + 8 - RowClipLeft, width = RowClipRight - RowClipLeft - x;
            int mid = ly + AvatarCardH / 2;
            if (!AvatarsLoaded) DrawLeft(_label, row, "Loading your avatars…", x, mid - _label.LineHeight / 2, Muted);
            else
            {
                ClipLeft(_label, row, "No avatars yet", x, mid - _label.LineHeight, White, width);
                ClipLeft(_body, row, "Get some on the Avatars tab, or import .p5avatar files on the USB Import tab.", x, mid + 4, Muted, width);
            }
        }
    }

    // Decodes the picture of the first card that still lacks one (one per frame), among the cards the row shows first.
    private void WarmAvatars()
    {
        int last = Math.Min(_avatars.Count, (Theme.ContentWidth + AvatarGap) / AvatarStride + 1);
        for (int i = 1; i <= last; i++)
        {
            if (_avatarFaces.ContainsKey(i) || _avatarFailed.Contains(i)) continue;
            AvatarFaceOf(i);
            return;
        }
    }

    // The baked card of an avatar card (0 is the console's own picture), made again when what it shows changes.
    private CachedImage AvatarCard(int index)
    {
        bool inUse = index == 0 ? AppliedAvatarId is null : AppliedAvatarId is { } id && id == _avatars[index - 1].Id;
        // Decoding a preview is the costly part, so a frame decodes only a couple; a card still waiting shows its letter card
        // (a state of its own) until a later frame has a slot for it.
        bool face = index == 0 || _avatarFaces.ContainsKey(index) || _avatarFailed.Contains(index);
        if (!face && _avatarDecodes < AvatarDecodesPerFrame)
        {
            AvatarFaceOf(index);
            face = true;
        }
        else if (!face) _avatarPending = true;
        int keyId = index * 4 + (inUse ? 1 : 0) + (face ? 2 : 0);
        if (!_avatarKeys.TryGetValue(keyId, out string? key)) _avatarKeys[keyId] = key = "avatar/" + keyId;
        return _avatarImages.TryGet(key, AvatarCardW, AvatarCardH, Theme.RadiusCard) ?? BakeAvatarCard(index, inUse, face, key);
    }

    private CachedImage BakeAvatarCard(int index, bool inUse, bool picture, string key)
    {
        using var buffer = new PixelBuffer(AvatarCardW, AvatarCardH);
        Surface c = buffer.AsSurface();
        c.FillRect(0, 0, AvatarCardW, AvatarCardH, AvatarCardFill);
        int faceX = (AvatarCardW - AvatarFace) / 2;
        float cx = AvatarCardW / 2f, cy = AvatarFaceTop + AvatarFace / 2f;
        CachedImage? image = index == 0 ? ConsoleFace() : picture ? AvatarFaceOf(index) : null;
        if (image is not null) image.Draw(c, faceX, AvatarFaceTop);
        else
        {
            // Waiting for its picture, or one that does not decode: a plain disc with the first letter of the name.
            Paint.FillCircle(c, cx, cy, AvatarFace / 2f, AvatarFaceFill);
            string name = _avatars[index - 1].Name;
            if (name.Length > 0) DrawMid(_title, c, name[..1].ToUpperInvariant(), (int)cx, (int)cy - _title.LineHeight / 2, Muted);
        }
        if (inUse) Paint.StrokeCircle(c, cx, cy, AvatarFace / 2f + 6, 3f, Accent);

        string title = index == 0 ? "Console picture" : _avatars[index - 1].Name;
        string line = inUse ? "In use" : index == 0 ? "The console’s own" : "by " + _avatars[index - 1].Author;
        int textY = AvatarFaceTop + AvatarFace + 14, room = AvatarCardW - 16;
        DrawCentredClipped(_label, c, title, textY, White, room);
        DrawCentredClipped(_small, c, line, textY + _label.LineHeight + 2, inUse ? Accent : Muted, room);
        Paint.StrokeRoundedRect(c, 0, 0, AvatarCardW, AvatarCardH, Theme.RadiusCard, 1, Theme.PanelStroke);
        return _avatarImages.Add(key, buffer, AvatarCardW, AvatarCardH, Theme.RadiusCard);
    }

    // Centred on the card, or from its left padding with the end cut when it is too wide.
    private static void DrawCentredClipped(AtlasFont font, Surface c, string text, int y, Color color, int room)
    {
        if (font.MeasureText(text) <= room) DrawMid(font, c, text, AvatarCardW / 2, y, color);
        else ClipLeft(font, c, text, (AvatarCardW - room) / 2, y, color, room);
    }

    // The avatar's preview as a circle, decoded and scaled once; null when it has none or it does not decode.
    private CachedImage? AvatarFaceOf(int index)
    {
        if (_avatarFaces.TryGetValue(index, out CachedImage? face)) return face;
        if (_avatarFailed.Contains(index)) return null;
        _avatarDecodes++;
        byte[]? preview = _avatars[index - 1].Preview;
        try
        {
            if (preview is null) throw new InvalidOperationException("no preview");
            using PixelBuffer decoded = _decode(preview);
            if (_avatarFaces.Count >= 32) { foreach (CachedImage old in _avatarFaces.Values) old.Dispose(); _avatarFaces.Clear(); }
            return _avatarFaces[index] = ImageCache.Prepare(decoded, AvatarFace, AvatarFace, AvatarFace / 2);
        }
        catch
        {
            _avatarFailed.Add(index);
            return null;
        }
    }

    // The console's own picture as the PS5 shows a profile without one: a person on a blue disc. Drawn once.
    private CachedImage ConsoleFace()
    {
        if (_consoleFace is not null) return _consoleFace;
        using var buffer = new PixelBuffer(AvatarFace, AvatarFace);
        Surface f = buffer.AsSurface();
        f.FillVerticalGradient(0, 0, AvatarFace, AvatarFace, Color.FromRgb(74, 104, 146), Color.FromRgb(40, 58, 86));
        Color person = Color.FromRgb(196, 210, 230);
        Paint.FillCircle(f, AvatarFace / 2f, AvatarFace * 0.39f, AvatarFace * 0.18f, person);
        Paint.FillCircle(f, AvatarFace / 2f, AvatarFace * 1.02f, AvatarFace * 0.37f, person);
        return _consoleFace = ImageCache.Prepare(buffer, AvatarFace, AvatarFace, AvatarFace / 2);
    }

    private void ReleaseAvatars()
    {
        _avatarImages.Clear();
        foreach (CachedImage face in _avatarFaces.Values) face.Dispose();
        _avatarFaces.Clear();
        _consoleFace?.Dispose();
        _consoleFace = null;
    }
}
