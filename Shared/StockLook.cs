using System;
using System.Collections.Generic;
using System.Linq;

namespace ProsperoThemes.Shared;

/// <summary>
/// Whether a file can still be a console's original by its look, for firmware without a measured catalog
/// (<c>catalog/&lt;firmware&gt;.json</c>). Firmware reports from 9.60, 10.01, 11.20, 11.60, 13.00 and 13.60 agree: a stock
/// icon container titles every icon of <see cref="ThemeIcons"/>, and the system tiles' backgrounds all differ. A
/// replacement icon carries no title, and a theme gives many tiles one picture. Backups that had been made from a
/// theme's files (12.40 and 13.60 reports: none of these titles, 13 tile backgrounds alike) fail both.
/// </summary>
public static class StockLook
{
    /// <summary>
    /// The icons themes replace most (<c>web/shared/icon-slots.json</c> "featured"), without the search magnifier, which
    /// no firmware titles.
    /// </summary>
    public static readonly IReadOnlyList<string> ThemeIcons =
    [
        "iconid_settings", "iconid_notification", "iconid_home", "iconid_power", "iconid_ps_store", "iconid_ps_user",
        "iconid_app_library_game", "iconid_app_library_media", "iconid_music", "iconid_switcher", "iconid_game_base",
        "iconid_download", "iconid_sound", "iconid_mic", "iconid_ps5_wireless_controller",
    ];

    /// <summary>A real container lists a thousand icons or more; a smaller one is not judged.</summary>
    public const int MinIcons = 100;

    /// <summary>
    /// The <see cref="ThemeIcons"/> that <paramref name="container"/> has no title for. Empty for a stock container and for
    /// one with fewer than <see cref="MinIcons"/> icons.
    /// </summary>
    public static List<string> UntitledThemeIcons(ReadOnlySpan<byte> container)
    {
        List<(int Length, string? Title)> icons = RcoSvg.List(container);
        if (icons.Count < MinIcons) return [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, title) in icons)
            if (title is not null) names.Add(title[(title.LastIndexOf('/') + 1)..]);
        return ThemeIcons.Where(name => !names.Contains(name)).ToList();
    }

    /// <summary>Whether a logical path is a system tile's background (<see cref="AssetRoots.SystemTileBackgrounds"/>, also as plain DDS).</summary>
    public static bool IsTileBackground(string logical) =>
        AssetRoots.SystemTileBackgrounds.Contains(logical, StringComparer.Ordinal)
        || AssetRoots.SystemTileBackgrounds.Any(t => AssetRoots.Uncompressed(t) == logical);

    /// <summary>The hashes that two or more of the tile backgrounds in <paramref name="hashes"/> (logical path → SHA-256) share.</summary>
    public static HashSet<string> SharedTileHashes(IEnumerable<KeyValuePair<string, string>> hashes) =>
        hashes.Where(h => IsTileBackground(h.Key))
            .GroupBy(h => h.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
}
