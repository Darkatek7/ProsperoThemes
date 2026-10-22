using System;
using System.Collections.Generic;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// The pointers to the store's web creator: a card with a QR code beside the USB Import card, a line in the Community
/// header and a sentence on the empty Library. All of it is static and not focusable. The address is the store address
/// (<see cref="ThemeBrowser.StoreUrl"/>, which the app keeps current) with "/#/creator" behind it; without a store address
/// none of it is shown.
/// </summary>
public sealed partial class ThemeRenderer
{
    /// <summary>Where the web creator lives, relative to the store address.</summary>
    internal const string CreatorPath = "/#/creator";

    /// <summary>The creator's address for a store address, or null when there is no store (nothing to point to).</summary>
    internal static string? CreatorUrlOf(string storeUrl)
    {
        string root = storeUrl.Trim();
        if (root.EndsWith("://", StringComparison.Ordinal)) return null;   // a bare scheme names no store
        root = root.TrimEnd('/');
        return root.Length == 0 ? null : root + CreatorPath;
    }

    /// <summary>An address as it is read aloud: without "https://" or "http://".</summary>
    internal static string CreatorAddressOf(string url)
    {
        foreach (string scheme in new[] { "https://", "http://" })
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return url[scheme.Length..];
        return url;
    }

    // ---- Layout -------------------------------------------------------------------------------------------------------

    // The QR code is drawn on a white plate: the symbol plus a quiet zone of four modules, about 260 px wide (7 px modules) at version 3
    // (the creator's address on the default store), and a smaller module for the longer addresses of a custom store.
    private const int CreatorQuietZone = 4, CreatorPlateMax = 264, CreatorModuleMin = 3, CreatorModuleMax = 8;
    private const int CreatorGap = 32, CreatorBottom = 48, CreatorIntroLines = 3;
    // The texts of the pointers (the tests check them against the font atlas).
    internal const string CreatorHeading = "Make your own theme", CreatorScan = "SCAN WITH YOUR PHONE";
    internal const string CreatorIntro = "Create a theme in your browser with the web creator, download it, then import it here.";
    internal const string CreatorCaptionPrefix = "Make your own theme on the website  ·  ";
    internal const string CreatorEmptyLibrary = "Copy theme packs from a USB drive on the USB Import tab, pick some on the Community tab, or make your own on the website.";
    private static readonly Color CreatorModuleInk = Color.FromRgb(10, 14, 22);

    /// <summary>The web creator's card, right of <see cref="ImportCard"/> and as tall; as of the last <see cref="Draw"/>.</summary>
    internal (int X, int Y, int Width, int Height) CreatorCard()
    {
        var (x, y, width, height) = ImportCard();
        int left = x + width + CreatorGap;
        return (left, y, Theme.ContentRight - left, height);
    }

    // ---- State --------------------------------------------------------------------------------------------------------

    // Everything below is made when the store address changes (never per frame) and dropped with it.
    private string _creatorStoreFor = "";
    private string? _creatorUrl;
    private string _creatorAddress = "", _creatorCaption = "";
    private int _creatorCaptionWidth;
    private CachedImage? _creatorPlate;
    private int _creatorModulePx;
    private IReadOnlyList<string> _creatorIntroText = Array.Empty<string>(), _creatorAddressLines = Array.Empty<string>();

    /// <summary>
    /// What the QR code on the USB Import card encodes: the web creator's address, or null when no store is set (or the
    /// address is too long for a code; the card is then left out, the text pointers stay). For the tests.
    /// </summary>
    internal string? CreatorUrl => _creatorUrl;

    /// <summary>Whether the USB Import card currently has the web creator's card beside it. For the tests.</summary>
    internal bool CreatorCardShown => _creatorPlate is not null;

    private void ReleaseCreator()
    {
        _creatorPlate?.Dispose();
        _creatorPlate = null;
        _creatorModulePx = 0;
        _creatorUrl = null;
        _creatorAddress = _creatorCaption = "";
        _creatorCaptionWidth = 0;
        _creatorIntroText = _creatorAddressLines = Array.Empty<string>();
    }

    /// <summary>Follows the store address: when it differs from the one the pointers were made for, they are made again (or dropped).</summary>
    private void UpdateCreator(string storeUrl)
    {
        if (ReferenceEquals(storeUrl, _creatorStoreFor) || storeUrl == _creatorStoreFor) return;
        _creatorStoreFor = storeUrl;
        ReleaseCreator();
        string? url = CreatorUrlOf(storeUrl);
        if (url is null) return;
        _creatorUrl = url;
        _creatorAddress = CreatorAddressOf(url);
        _creatorCaption = CreatorCaptionPrefix + _creatorAddress;
        _creatorCaptionWidth = _small.MeasureText(_creatorCaption);

        QrCode? code = QrCode.Encode(url);
        if (code is null) return;
        _creatorPlate = BakeCreatorPlate(code, out _creatorModulePx);

        // The card's texts, wrapped once for the width they have.
        int inner = Theme.ContentWidth - ImportPairedWidth - CreatorGap - 2 * ImportCardPad;
        _creatorIntroText = _body.Wrap(CreatorIntro, inner, CreatorIntroLines);
        int beside = inner - _creatorPlate.Width - CreatorGap;
        _creatorAddressLines = _body.MeasureText(_creatorAddress) <= beside ? [_creatorAddress] : SplitAddress(_creatorAddress);
    }

    // A long address goes on two lines, the host and then the rest ("themes.example.com" and "/#/creator").
    private static string[] SplitAddress(string address)
    {
        int slash = address.IndexOf('/');
        return slash <= 0 ? [address] : [address[..slash], address[slash..]];
    }

    // The symbol as dark modules on white with a quiet zone, in one image with rounded corners, made once.
    private static CachedImage BakeCreatorPlate(QrCode code, out int px)
    {
        int modules = code.Size + 2 * CreatorQuietZone;
        px = Math.Clamp(CreatorPlateMax / modules, CreatorModuleMin, CreatorModuleMax);
        int plate = modules * px;
        using var pixels = new PixelBuffer(plate, plate);
        Surface surface = pixels.AsSurface();
        surface.FillRect(0, 0, plate, plate, Theme.White);
        for (int y = 0; y < code.Size; y++)
            for (int x = 0; x < code.Size; x++)
                if (code.Get(x, y)) surface.FillRect((x + CreatorQuietZone) * px, (y + CreatorQuietZone) * px, px, px, CreatorModuleInk);
        return ImageCache.Prepare(pixels, plate, plate, Theme.RadiusCard);
    }

    /// <summary>The QR plate on the screen (its top-left, its size and the pixels of one module), without the slide-in; null when not shown. For the tests.</summary>
    internal (int X, int Y, int Size, int ModulePx)? CreatorPlate()
    {
        if (_creatorPlate is null) return null;
        var (x, y, _, height) = CreatorCard();
        return (x + ImportCardPad, y + height - CreatorBottom - _creatorPlate.Height, _creatorPlate.Height, _creatorModulePx);
    }

    // ---- Drawing ------------------------------------------------------------------------------------------------------

    // The card: heading and a few lines like the USB Import card's, the plate under them on the card's bottom padding, and
    // beside the plate where to scan or what to type.
    private void DrawCreatorCard(Surface s, int enter)
    {
        var (x, cardY, width, height) = CreatorCard();
        int y = cardY + enter;
        SysGlass(s, x, y, width, height);

        int left = x + ImportCardPad, inner = width - 2 * ImportCardPad;
        int top = y + 48;
        ClipLeft(_screenHeading, s, CreatorHeading, left - SysHeadingOptical, top, White, inner);
        top += _screenHeading.LineHeight + 8;
        for (int i = 0; i < _creatorIntroText.Count; i++)   // indexed: a foreach over the interface would allocate every frame
        {
            DrawLeft(_body, s, _creatorIntroText[i], left, top, Muted);
            top += _body.LineHeight;
        }

        CachedImage plate = _creatorPlate!;
        int plateY = Math.Max(top + 24, y + height - CreatorBottom - plate.Height);
        plate.Draw(s, left, plateY);

        int textX = left + plate.Width + CreatorGap, textWidth = left + inner - textX;
        int block = _small.LineHeight + 8 + _creatorAddressLines.Count * _body.LineHeight;
        int textY = plateY + (plate.Height - block) / 2;
        ClipLeft(_small, s, CreatorScan, textX, textY, Accent, textWidth);
        textY += _small.LineHeight + 8;
        for (int i = 0; i < _creatorAddressLines.Count; i++)
        {
            ClipLeft(_body, s, _creatorAddressLines[i], textX, textY, White, textWidth);
            textY += _body.LineHeight;
        }
    }

    // The Library's empty state names the creator too, when there is one. Wrapped once per state.
    private bool? _emptyForCreator;

    private IReadOnlyList<string> EmptyLibraryText()
    {
        bool creator = _creatorUrl is not null;
        if (_emptyText is null || _emptyForCreator != creator)
        {
            _emptyForCreator = creator;
            _emptyText = creator
                ? _body.Wrap(CreatorEmptyLibrary, 760, 3)
                : _body.Wrap("Copy theme packs from a USB drive on the USB Import tab, or pick some on the Community tab.", 760, 2);
        }
        return _emptyText;
    }

    // The muted line after the Community eyebrow, as far as it fits in front of the count at the right edge (count 0: none).
    private void DrawCommunityCreator(Surface s, int eyebrowRight, int y, int rightLimit)
    {
        if (_creatorUrl is null) return;
        int x = eyebrowRight + 24, room = rightLimit - 24 - x;
        if (room > 0) ClipLeft(_small, s, _creatorCaption, x, y, Muted, Math.Min(room, _creatorCaptionWidth + GlyphPad));
    }
}
