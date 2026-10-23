using System;
using System.Collections.Generic;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The parts of a theme a user can apply on their own: the home background (with the system tiles that take it), the
/// app backgrounds, the system icons, the tile icons, the sounds, and everything else.
/// </summary>
[Flags]
public enum ThemePart
{
    None = 0,
    Background = 1,
    AppBackgrounds = 2,
    Icons = 4,
    Tiles = 8,
    Sounds = 16,
    Other = 32,
    All = Background | AppBackgrounds | Icons | Tiles | Sounds | Other,
}

public static class ThemeParts
{
    /// <summary>Every part, in the order the apply question lists them.</summary>
    public static readonly ThemePart[] Order =
        [ThemePart.Background, ThemePart.AppBackgrounds, ThemePart.Icons, ThemePart.Tiles, ThemePart.Sounds, ThemePart.Other];

    /// <summary>
    /// The part one target belongs to. Sounds are judged by the file (an <c>.at9</c>) and the vag layer, not by the
    /// advisory "sound" tag, so a pack cannot slip a sound past a user who turned sounds off.
    /// </summary>
    public static ThemePart Of(ThemePackTarget target)
    {
        string path = target.Path;
        if (path == AssetRoots.AllAppsBackground || AssetRoots.IsAppBackground(path, out _) || IsTitleImage(path)) return ThemePart.AppBackgrounds;
        if (target.Layer == ThemePack.VagLayer || target.Layer == "sound" || path.EndsWith(".at9", StringComparison.OrdinalIgnoreCase))
            return ThemePart.Sounds;
        if (target.Layer == ThemePack.SvgLayer) return ThemePart.Icons;
        if (path.StartsWith(AssetRoots.Rnps + "/", StringComparison.Ordinal)) return ThemePart.Tiles;
        if (path == AssetRoots.HubBackground || target.Layer == "background") return ThemePart.Background;
        return ThemePart.Other;
    }

    /// <summary>
    /// The parts a backed-up file belongs to, from its logical path (the backup index has no layers). The icon container
    /// holds the system icons and the UI sounds, so it is both. A home or system tile background, also as the plain .dds
    /// of firmware before 13.00, is the background; a title's own image is an app background (what the all-apps and
    /// per-app targets resolve to). Any other texture of the system assets may be a background or another file, so it
    /// is both. Superset of <see cref="Of"/> for the targets that wrote the file, so a restore of a part reaches it.
    /// </summary>
    public static ThemePart OfLogical(string logical)
    {
        if (logical.EndsWith(".rco", StringComparison.OrdinalIgnoreCase)) return ThemePart.Icons | ThemePart.Sounds;
        if (logical.EndsWith(".at9", StringComparison.OrdinalIgnoreCase)) return ThemePart.Sounds;
        if (logical.StartsWith(AssetRoots.Rnps + "/", StringComparison.Ordinal)) return ThemePart.Tiles;
        if (IsTitleImage(logical)) return ThemePart.AppBackgrounds;
        if (logical.StartsWith(AssetRoots.VshAsset + "/", StringComparison.Ordinal)
            && (logical.EndsWith(".dds.zst", StringComparison.Ordinal) || logical.EndsWith(".dds", StringComparison.Ordinal)))
            return logical.StartsWith(AssetRoots.VshAsset + "/bg_", StringComparison.Ordinal) ? ThemePart.Background : ThemePart.Background | ThemePart.Other;
        return ThemePart.Other;
    }

    // A title's own entry image as the console keeps it: userapp/<TID>/sce_sys/pic0.dds or appmeta/<TID>/pic0.dds.
    private static bool IsTitleImage(string path) =>
        (path.StartsWith(AssetRoots.UserApp + "/", StringComparison.Ordinal) || path.StartsWith(AssetRoots.AppMeta + "/", StringComparison.Ordinal))
        && path.EndsWith("/pic0.dds", StringComparison.Ordinal);

    /// <summary>The parts a pack has something for.</summary>
    public static ThemePart In(ThemePackManifest manifest)
    {
        ThemePart parts = ThemePart.None;
        foreach (ThemePackTarget t in manifest.Targets) parts |= Of(t);
        return parts;
    }

    /// <summary>The parts in <paramref name="set"/>, in <see cref="Order"/>.</summary>
    public static List<ThemePart> List(ThemePart set)
    {
        var list = new List<ThemePart>(Order.Length);
        foreach (ThemePart part in Order)
            if ((set & part) != 0) list.Add(part);
        return list;
    }

    /// <summary>How the apply question names a part.</summary>
    public static string Label(ThemePart part) => part switch
    {
        ThemePart.Background => "Background",
        ThemePart.AppBackgrounds => "App backgrounds",
        ThemePart.Icons => "System icons",
        ThemePart.Tiles => "Tile icons",
        ThemePart.Sounds => "Music and sounds",
        _ => "Other files",
    };
}
