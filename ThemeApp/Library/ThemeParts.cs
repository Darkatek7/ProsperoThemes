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
        if (path == AssetRoots.AllAppsBackground || AssetRoots.IsAppBackground(path, out _)) return ThemePart.AppBackgrounds;
        if (target.Layer == ThemePack.VagLayer || target.Layer == "sound" || path.EndsWith(".at9", StringComparison.OrdinalIgnoreCase))
            return ThemePart.Sounds;
        if (target.Layer == ThemePack.SvgLayer) return ThemePart.Icons;
        if (path.StartsWith(AssetRoots.Rnps + "/", StringComparison.Ordinal)) return ThemePart.Tiles;
        if (path == AssetRoots.HubBackground || target.Layer == "background") return ThemePart.Background;
        return ThemePart.Other;
    }

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
