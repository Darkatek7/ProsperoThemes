using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;

/// <summary>
/// The packs the PC preview shows beyond the bundled theme, and a store list built from them, so the screens can be
/// reviewed with real data. Nothing here touches the network or a console.
/// </summary>
internal static class PreviewData
{
    /// <summary>
    /// Every <c>*.p5theme</c> in <paramref name="libraryFolder"/> (PREVIEW_LIBRARY, when set), then every one in
    /// <paramref name="fixturesFolder"/>, each folder in name order. A file that does not inspect is skipped and
    /// reported on stderr; each loaded pack is reported on stdout, so a run shows what it actually has.
    /// </summary>
    public static List<ThemeLibraryEntry> LoadPacks(string? libraryFolder, string fixturesFolder)
    {
        var entries = new List<ThemeLibraryEntry>();
        if (!string.IsNullOrWhiteSpace(libraryFolder))
        {
            if (!Directory.Exists(libraryFolder)) throw new DirectoryNotFoundException("PREVIEW_LIBRARY names no folder: " + libraryFolder);
            LoadFolder(libraryFolder, entries);
        }
        if (Directory.Exists(fixturesFolder)) LoadFolder(fixturesFolder, entries);
        else Console.Error.WriteLine("skipped  fixtures: no folder at " + fixturesFolder);
        return entries;
    }

    private static void LoadFolder(string folder, List<ThemeLibraryEntry> entries)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "*.p5theme").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ThemeLibraryEntry entry = ThemeLibraryEntry.Inspect(path, File.ReadAllBytes(path));
                entries.Add(entry);
                Console.WriteLine($"loaded   {entry.Manifest.Name}  ({Path.GetFileName(path)})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"skipped  {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A store list for the Community tab, one card per entry (card i is <c>entries[i]</c>, up to six). Names, authors,
    /// versions, descriptions and the summary flags come from each pack; the like and download counts are invented.
    /// Card 2 is meant to read as already in the library, card 3 as having an update (see <see cref="Card"/>).
    /// </summary>
    public static List<CommunityTheme> Store(IReadOnlyList<ThemeLibraryEntry> entries)
    {
        int[] likes = [482, 311, 227, 164, 96, 58];
        int[] downloads = [6120, 3875, 2410, 1204, 644, 213];
        // Category ids of each card: none to three. Card 5 has three long labels, which do not all fit on a card ("+1").
        string[][] categories =
        [
            ["anime", "games"], ["nature"], ["sci-fi", "space", "pixel-art"], ["games"], ["movies-tv", "fantasy"],
            ["movies-tv", "pixel-art", "seasonal"],
        ];
        var store = new List<CommunityTheme>();
        for (int i = 0; i < Math.Min(entries.Count, likes.Length); i++)
        {
            ThemeLibraryEntry entry = entries[i];
            ThemePackManifest manifest = entry.Manifest;
            // The same counts the store keeps (web/server pack-info.ts), so a card reads like the pack's library line.
            PackSummary summary = PackSummary.Of(manifest);
            bool update = i == 2;
            store.Add(new CommunityTheme(
                Id: $"store-{i}",
                Name: manifest.Name,
                Author: manifest.Author,
                Version: update ? NextVersion(manifest.Version) : manifest.Version,
                Description: manifest.Description,
                Size: new FileInfo(entry.Path).Length,
                Likes: likes[i],
                Downloads: downloads[i],
                PreviewUrl: null,
                DownloadUrl: $"/api/themes/store-{i}/download",
                Fallback: summary.HomeBackground,
                AllApps: summary.AllApps,
                PerApp: summary.PerApp,
                Icons: summary.Icons,
                Tiles: summary.Tiles) { Categories = categories[i], Ai = i == 1 });
        }
        return store;
    }

    /// <summary>
    /// What the store answers for a list request: the cards of <paramref name="store"/> (oldest first, as built above) in
    /// category <paramref name="categoryId"/> (all of them when null), in the order of <paramref name="sort"/>.
    /// </summary>
    public static List<CommunityTheme> Listing(IReadOnlyList<CommunityTheme> store, CommunitySort sort, string? categoryId, string query = "")
    {
        IEnumerable<CommunityTheme> cards = categoryId is null ? store : store.Where(t => t.Categories.Contains(categoryId));
        // The store's search: the term anywhere in the name, the author or the description.
        if (query.Length > 0)
            cards = cards.Where(t => (t.Name + "\n" + t.Author + "\n" + t.Description).Contains(query, StringComparison.OrdinalIgnoreCase));
        return (sort switch
        {
            CommunitySort.Downloads => cards.OrderByDescending(t => t.Downloads),
            CommunitySort.New => cards.Reverse(),
            _ => cards.OrderByDescending(t => t.Likes),
        }).ToList();
    }

    /// <summary>The id of the card at <paramref name="index"/> as a set, or an empty set when the store has no such card.</summary>
    public static HashSet<string> Card(IReadOnlyList<CommunityTheme> store, int index)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (index < store.Count) ids.Add(store[index].Id);
        return ids;
    }

    /// <summary>A newer version than <paramref name="version"/>: the last number counts up ("1.2.0" becomes "1.2.1").</summary>
    private static string NextVersion(string version)
    {
        if (version.Length == 0) return "1.0.1";
        string[] parts = version.Split('.');
        if (int.TryParse(parts[^1], out int last))
        {
            parts[^1] = (last + 1).ToString();
            return string.Join('.', parts);
        }
        return version + ".1";
    }
}
