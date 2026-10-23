using System;
using System.Collections.Generic;
using System.Text;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>
/// What a theme pack changes, grouped for display: the Library line, the list on Detail, and (through
/// <see cref="Format"/>) the community store's cards, so a local pack and its store card read the same.
/// </summary>
/// <remarks>
/// The counting rules are the store's (<c>summarize</c> in web/server/src/pack-info.ts): the home background is
/// <c>vsh_asset/bg_hub_default.dds.zst</c>, the all-apps background is <c>allapps/pic0.dds</c>, per-app backgrounds
/// match <c>apps/&lt;TID&gt;/pic0.dds</c>, system icons are the targets of the <c>svg</c> layer and tile icons the
/// targets under <c>rnps/</c>. Sounds count the targets of the sound and vag layers (the store lists the music apart and
/// a card adds it back), video those of its layer, which the store does not list, so a store card never shows it.
/// Every target counts in each group it matches; a target that matches none is <see cref="Other"/>.
/// </remarks>
/// <summary>
/// Which targets of a pack have no file on this console (ThemeApplier.CheckFit): an apply would skip them.
/// <paramref name="Missing"/> holds the manifest's own target objects.
/// </summary>
public sealed record PackFit(IReadOnlyList<ThemePackTarget> Missing);

/// <summary>The groups of <see cref="PackSummary.Groups"/>, in display order.</summary>
public enum PackGroup { Background, AppBackground, AppImages, Icons, Tiles, Sounds, Video }

public sealed class PackSummary
{
    /// <summary>Layer names of the sound and video groups (<see cref="ThemePackManifest.KnownLayers"/>).</summary>
    private const string SoundLayer = "sound", VideoLayer = "video";

    // One group as the text sees it: a flag group ("Background") is written without its number.
    private readonly record struct Item(PackGroup Kind, string Singular, string Plural, int Count, bool Counted);

    public bool HomeBackground { get; }
    public bool AllApps { get; }
    public int PerApp { get; }
    public int Icons { get; }
    public int Tiles { get; }
    public int Sounds { get; }
    public int Video { get; }
    /// <summary>Targets that belong to none of the groups (glyphs, other backgrounds, plain icons, ...).</summary>
    public int Other { get; }

    /// <summary>
    /// The groups with something in them, in display order: home background, app background, app images, icons,
    /// tile icons, sounds, video. A group that is a yes or no (the two backgrounds) has a count of 1. The label is
    /// singular for a count of 1 and has no number in it ("Icons", with the count next to it).
    /// </summary>
    public IReadOnlyList<(string Label, int Count)> Groups { get; }

    /// <summary>Which group each entry of <see cref="Groups"/> is.</summary>
    public IReadOnlyList<PackGroup> Kinds { get; }

    /// <summary>How many targets are in <paramref name="group"/> (a background counts 1 when the pack has it).</summary>
    public int Count(PackGroup group) => group switch
    {
        PackGroup.Background => HomeBackground ? 1 : 0,
        PackGroup.AppBackground => AllApps ? 1 : 0,
        PackGroup.AppImages => PerApp,
        PackGroup.Icons => Icons,
        PackGroup.Tiles => Tiles,
        PackGroup.Sounds => Sounds,
        _ => Video,
    };

    /// <summary>The groups in one line: "Background · App background · 16 icons · 9 tile icons".</summary>
    public string Text { get; }

    private PackSummary(bool homeBackground, bool allApps, int perApp, int icons, int tiles, int sounds, int video, int other)
    {
        HomeBackground = homeBackground;
        AllApps = allApps;
        PerApp = Math.Max(0, perApp);
        Icons = Math.Max(0, icons);
        Tiles = Math.Max(0, tiles);
        Sounds = Math.Max(0, sounds);
        Video = Math.Max(0, video);
        Other = Math.Max(0, other);

        var items = new List<Item>(7);
        if (HomeBackground) items.Add(new Item(PackGroup.Background, "Background", "Background", 1, false));
        if (AllApps) items.Add(new Item(PackGroup.AppBackground, "App background", "App background", 1, false));
        if (PerApp > 0) items.Add(new Item(PackGroup.AppImages, "App image", "App images", PerApp, true));
        if (Icons > 0) items.Add(new Item(PackGroup.Icons, "Icon", "Icons", Icons, true));
        if (Tiles > 0) items.Add(new Item(PackGroup.Tiles, "Tile icon", "Tile icons", Tiles, true));
        if (Sounds > 0) items.Add(new Item(PackGroup.Sounds, "Sound", "Sounds", Sounds, true));
        if (Video > 0) items.Add(new Item(PackGroup.Video, "Video", "Videos", Video, true));

        var groups = new List<(string, int)>(items.Count);
        var kinds = new List<PackGroup>(items.Count);
        var text = new StringBuilder();
        foreach (Item item in items)
        {
            string label = item.Count == 1 ? item.Singular : item.Plural;
            groups.Add((label, item.Count));
            kinds.Add(item.Kind);
            if (text.Length > 0) text.Append(" · ");
            if (item.Counted) text.Append(item.Count).Append(' ').Append(label.ToLowerInvariant());
            else text.Append(label);
        }
        Groups = groups;
        Kinds = kinds;
        // A pack the store would call just "Theme" still says how many files it brings, when it brings any.
        Text = text.Length > 0 ? text.ToString() : Other > 0 ? (Other == 1 ? "1 file" : Other + " files") : "Theme";
    }

    /// <summary>The summary of a pack, counted with the store's rules (see the class remarks).</summary>
    public static PackSummary Of(ThemePackManifest manifest) => Of(manifest.Targets);

    /// <summary>The summary of some targets (the ones of a pack that do not fit a console, see <see cref="PackFit"/>).</summary>
    public static PackSummary Of(IEnumerable<ThemePackTarget> targets)
    {
        bool home = false, allApps = false;
        int perApp = 0, icons = 0, tiles = 0, sounds = 0, video = 0, other = 0;
        foreach (ThemePackTarget t in targets)
        {
            bool counted = false;
            if (t.Path == AssetRoots.HubBackground) { home = true; counted = true; }
            if (t.Path == AssetRoots.AllAppsBackground) { allApps = true; counted = true; }
            if (AssetRoots.IsAppBackground(t.Path, out _)) { perApp++; counted = true; }
            if (t.Layer == ThemePack.SvgLayer) { icons++; counted = true; }
            if (t.Path.StartsWith(AssetRoots.Rnps + "/", StringComparison.Ordinal)) { tiles++; counted = true; }
            if (t.Layer == SoundLayer || t.Layer == ThemePack.VagLayer) { sounds++; counted = true; }
            if (t.Layer == VideoLayer) { video++; counted = true; }
            if (!counted) other++;
        }
        return new PackSummary(home, allApps, perApp, icons, tiles, sounds, video, other);
    }

    /// <summary>A summary from counts, the way the store lists them (it has no video count).</summary>
    public static PackSummary FromCounts(bool homeBackground, bool allApps, int perApp, int icons, int tiles, int sounds = 0, int video = 0)
        => new(homeBackground, allApps, perApp, icons, tiles, sounds, video, other: 0);

    /// <summary>The one-line text for counts, shared with the community store's cards (see <see cref="CommunityTheme.Contents"/>).</summary>
    public static string Format(bool homeBackground, bool allApps, int perApp, int icons, int tiles, int sounds = 0, int video = 0)
        => FromCounts(homeBackground, allApps, perApp, icons, tiles, sounds, video).Text;
}
