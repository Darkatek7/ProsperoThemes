using System;
using System.Collections.Generic;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>The two sections of the home screen.</summary>
public enum PreviewTab { Games, Media }

/// <summary>Where a tile of the preview comes from: a system tile (its icon is a target of the pack), an app the pack gives its own background, or a drawn stand-in.</summary>
public enum PreviewTileKind { System, App, Demo }

/// <summary>The UI sounds the preview plays, one per kind of move (the pack's own clips when it has them).</summary>
public enum PreviewSound { None, Move, Confirm, Back, End }

/// <summary>One tile of the preview's home row.</summary>
/// <param name="Id">Title id of a system or app tile, <c>demo-N</c> for a stand-in.</param>
/// <param name="Name">What the console shows under the focused tile.</param>
public sealed record PreviewTile(string Id, string Name, PreviewTab Tab, PreviewTileKind Kind)
{
    /// <summary>Archive path of the tile's 512x512 PNG icon, when the pack replaces it.</summary>
    public string? IconFile { get; init; }

    /// <summary>Archive path of the JPEG of this app's own background (<c>previews.apps</c>).</summary>
    public string? PictureFile { get; init; }

    /// <summary>The drawn symbol of a tile without an icon (see <c>ThemeRenderer</c>), and the hue of a stand-in.</summary>
    public int Symbol { get; init; }

    public int Hue { get; init; }

    /// <summary>The picture keys that may go behind this tile, best first, ending with the cover (see <see cref="PreviewModel.PictureKeys"/>).</summary>
    public IReadOnlyList<string> PictureKeys { get; init; } = [PreviewModel.CoverKey];
}

/// <summary>One button of the control centre's bottom row.</summary>
/// <param name="Slot">The pack's icon slot, e.g. <c>iconid_game_base</c>.</param>
/// <param name="IconFile">Archive path of the PNG render the pack brings for the slot (<c>previews.icons</c>).</param>
public sealed record PreviewControl(string Slot, string Name, string? IconFile);

/// <summary>
/// What a theme's preview shows and where it gets each piece, worked out from the manifest alone: the home row of each
/// section (system tiles, then apps with their own background, then stand-ins), the control centre's twelve buttons, the
/// pictures that go behind the tiles, the music and the UI sounds. No bytes are read, so the layout is known at once and
/// does not move when the pictures and icons arrive later.
/// </summary>
/// <remarks>
/// The system tiles are the console's (FW 13.00), in the order the website's preview uses
/// (<c>web/shared/src/data/tile-icons.ts</c>); a tile the pack has no icon for is drawn as a stand-in. A target under
/// <c>rnps/</c> the list does not know becomes a tile named after its title id. The stand-ins are neutral and drawn.
/// </remarks>
public sealed class PreviewModel
{
    public const string CoverKey = "cover", AllAppsKey = "allapps";
    /// <summary>Most tiles for apps with their own background; the rest are left out.</summary>
    public const int MaxAppTiles = 12;

    /// <summary>The system tiles with an icon the theme engine can replace (<c>rnps</c> is <c>/mnt/rnps/apps</c>, or <c>/system_ex/rnps/apps</c> without the live copy).</summary>
    private static readonly (string Id, string Name, string Path, PreviewTab Tab, int Symbol)[] SystemTiles =
    [
        ("NPXS40047", "PlayStation Store", "rnps/NPXS40047/appdb/default/icon0.png", PreviewTab.Games, 4),
        ("NPXS40037", "PlayStation Plus", "rnps/NPXS40037/appdb/default/icon0.png", PreviewTab.Games, 2),
        ("NPXS40071", "Game Library", "rnps/NPXS40071/appdb/default/icon0.png", PreviewTab.Games, 1),
        ("NPXS40139", "App Library", "rnps/NPXS40071/appdb/NPXS40139/icon0.png", PreviewTab.Games, 4),
        ("NPXS40063", "Welcome", "rnps/NPXS40063/appdb/default/icon0.png", PreviewTab.Games, 0),
        ("NPXS40053", "Discover", "rnps/NPXS40016/appdb/NPXS40053/icon0.png", PreviewTab.Media, 2),
        ("NPXS40054", "Music Home", "rnps/NPXS40016/appdb/NPXS40054/icon0.png", PreviewTab.Media, 5),
        ("NPXS40056", "All Apps", "rnps/NPXS40016/appdb/NPXS40056/icon0.png", PreviewTab.Media, 4),
        ("NPXS40305", "Live TV on PS5", "rnps/NPXS40016/appdb/NPXS40305/icon0.png", PreviewTab.Media, 3),
    ];

    /// <summary>The media hub: its tiles sit in the Media section.</summary>
    private const string MediaRoot = "rnps/NPXS40016/";

    // Neutral apps for the rest of the row: name, symbol (see ThemeRenderer's drawn symbols), hue in degrees.
    private static readonly (string Name, PreviewTab Tab, int Symbol, int Hue)[] Demos =
    [
        ("Demo game", PreviewTab.Games, 1, 215),
        ("Demo racing game", PreviewTab.Games, 4, 12),
        ("Demo adventure", PreviewTab.Games, 1, 150),
        ("Prospero Themes", PreviewTab.Media, 0, 205),
        ("Demo video app", PreviewTab.Media, 3, 280),
        ("Demo music app", PreviewTab.Media, 5, 335),
    ];

    /// <summary>The control centre's row in the console's order, with the slot each button takes its icon from.</summary>
    public static readonly IReadOnlyList<(string Slot, string Name)> ControlSlots =
    [
        ("iconid_home", "Home"),
        ("iconid_switcher", "Switcher"),
        ("iconid_notification", "Notifications"),
        ("iconid_game_base", "Game Base"),
        ("iconid_music", "Music"),
        ("iconid_download", "Downloads"),
        ("iconid_share_screen", "Share Screen"),
        ("iconid_sound", "Sound"),
        ("iconid_mic", "Mic"),
        ("iconid_ps5_wireless_controller", "Accessories"),
        ("iconid_ps_user", "Profile"),
        ("iconid_power", "Power"),
    ];

    /// <summary>The clip names of the UI sounds the preview plays (<c>UI_SOUNDS</c> in web/shared/src/sound.ts).</summary>
    public static readonly IReadOnlyDictionary<PreviewSound, string> Clips = new Dictionary<PreviewSound, string>
    {
        [PreviewSound.Move] = "psfx_focus_move",
        [PreviewSound.Confirm] = "psfx_enter",
        [PreviewSound.Back] = "psfx_cancel",
        [PreviewSound.End] = "psfx_cannot_move",
    };

    private readonly Dictionary<string, string> _pictureFiles = new(StringComparer.Ordinal);

    private PreviewModel(IReadOnlyList<PreviewTile> games, IReadOnlyList<PreviewTile> media, IReadOnlyList<PreviewControl> controls,
        string? allApps, string? music, IReadOnlyDictionary<string, string> sounds)
    {
        Games = games;
        Media = media;
        Controls = controls;
        AllAppsFile = allApps;
        MusicFile = music;
        SoundFiles = sounds;
    }

    public IReadOnlyList<PreviewTile> Games { get; }
    public IReadOnlyList<PreviewTile> Media { get; }
    public IReadOnlyList<PreviewControl> Controls { get; }
    /// <summary>Archive path of the all-apps picture, or null.</summary>
    public string? AllAppsFile { get; }
    /// <summary>Archive path of the MP3 excerpt of the home music, or null.</summary>
    public string? MusicFile { get; }
    /// <summary>Clip name (<c>psfx_focus_move</c>, ...) to the archive path of the pack's VAG for it.</summary>
    public IReadOnlyDictionary<string, string> SoundFiles { get; }

    public IReadOnlyList<PreviewTile> Tiles(PreviewTab tab) => tab == PreviewTab.Games ? Games : Media;

    /// <summary>The key of the picture of an app with its own background.</summary>
    public static string AppKey(string titleId) => "app:" + titleId;

    /// <summary>
    /// The pictures that may go behind <paramref name="tile"/> when it has the focus, best first, ending with the cover:
    /// its own background; then, for anything but a system tile, the all-apps background (the console gives an app
    /// without one of its own that, and a system tile its home background, which is the cover).
    /// </summary>
    public IReadOnlyList<string> PictureKeys(PreviewTile tile) => tile.PictureKeys;

    // Worked out once per tile, so a frame that asks for them allocates nothing.
    private static string[] KeysFor(string id, PreviewTileKind kind, string? pictureFile, string? allApps)
    {
        var keys = new List<string>(3);
        if (pictureFile is not null) keys.Add(AppKey(id));
        if (kind != PreviewTileKind.System && allApps is not null) keys.Add(AllAppsKey);
        keys.Add(CoverKey);
        return [.. keys];
    }

    /// <summary>The archive path of the picture behind a key from <see cref="PictureKeys"/>; null for the cover.</summary>
    public string? PictureFile(string key) => key == AllAppsKey ? AllAppsFile : _pictureFiles.GetValueOrDefault(key);

    /// <summary>Every archive path the preview reads, so a pack can be opened for just those.</summary>
    public HashSet<string> Files()
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (PreviewTile tile in Games) AddFiles(files, tile);
        foreach (PreviewTile tile in Media) AddFiles(files, tile);
        foreach (PreviewControl control in Controls) if (control.IconFile is not null) files.Add(control.IconFile);
        foreach (string file in SoundFiles.Values) files.Add(file);
        if (AllAppsFile is not null) files.Add(AllAppsFile);
        if (MusicFile is not null) files.Add(MusicFile);
        return files;
    }

    private static void AddFiles(HashSet<string> files, PreviewTile tile)
    {
        if (tile.IconFile is not null) files.Add(tile.IconFile);
        if (tile.PictureFile is not null) files.Add(tile.PictureFile);
    }

    /// <summary>Works out the preview of a pack from its manifest.</summary>
    public static PreviewModel Of(ThemePackManifest manifest)
    {
        ThemePackPreviews previews = manifest.Previews;

        // The icon each system tile gets from the pack, by its target path; a target of an unknown tile comes after.
        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknown = new List<(string Id, PreviewTab Tab, string Path)>();
        foreach (ThemePackTarget target in manifest.Targets)
        {
            if (!target.Path.StartsWith(AssetRoots.Rnps + "/", StringComparison.Ordinal) || !target.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;
            icons[target.Path] = target.File;
            if (Array.FindIndex(SystemTiles, t => t.Path == target.Path) < 0 && TileIdOf(target.Path) is { } id && unknown.FindIndex(u => u.Id == id) < 0)
                unknown.Add((id, target.Path.StartsWith(MediaRoot, StringComparison.Ordinal) ? PreviewTab.Media : PreviewTab.Games, target.Path));
        }

        var games = new List<PreviewTile>();
        var media = new List<PreviewTile>();
        var pictures = new Dictionary<string, string>(StringComparer.Ordinal);
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var system in SystemTiles)
        {
            knownIds.Add(system.Id);
            previews.Apps.TryGetValue(system.Id, out string? own);
            var tile = new PreviewTile(system.Id, system.Name, system.Tab, PreviewTileKind.System)
            {
                IconFile = icons.GetValueOrDefault(system.Path),
                PictureFile = own,
                Symbol = system.Symbol,
                PictureKeys = KeysFor(system.Id, PreviewTileKind.System, own, previews.AllApps),
            };
            (system.Tab == PreviewTab.Games ? games : media).Add(tile);
            if (own is not null) pictures[AppKey(system.Id)] = own;
        }
        foreach (var (id, tab, path) in unknown)
        {
            if (!knownIds.Add(id)) continue;
            previews.Apps.TryGetValue(id, out string? own);
            (tab == PreviewTab.Games ? games : media).Add(new PreviewTile(id, id, tab, PreviewTileKind.System)
            {
                IconFile = icons[path],
                PictureFile = own,
                PictureKeys = KeysFor(id, PreviewTileKind.System, own, previews.AllApps),
            });
            if (own is not null) pictures[AppKey(id)] = own;
        }

        // Apps the pack gives a background of their own: the ones with a picture first, then those with only the DDS target.
        var apps = new List<string>();
        foreach (string tid in previews.Apps.Keys) if (!knownIds.Contains(tid)) apps.Add(tid);
        foreach (ThemePackTarget target in manifest.Targets)
            if (AppBackgroundOf(target.Path) is { } tid && !knownIds.Contains(tid) && !apps.Contains(tid)) apps.Add(tid);
        for (int i = 0; i < Math.Min(apps.Count, MaxAppTiles); i++)
        {
            string tid = apps[i];
            previews.Apps.TryGetValue(tid, out string? own);
            games.Add(new PreviewTile(tid, tid, PreviewTab.Games, PreviewTileKind.App)
            {
                PictureFile = own,
                Symbol = 1,
                Hue = 215,
                PictureKeys = KeysFor(tid, PreviewTileKind.App, own, previews.AllApps),
            });
            if (own is not null) pictures[AppKey(tid)] = own;
        }

        for (int i = 0; i < Demos.Length; i++)
        {
            var demo = Demos[i];
            (demo.Tab == PreviewTab.Games ? games : media).Add(new PreviewTile("demo-" + i, demo.Name, demo.Tab, PreviewTileKind.Demo)
            {
                Symbol = demo.Symbol,
                Hue = demo.Hue,
                PictureKeys = KeysFor("demo-" + i, PreviewTileKind.Demo, null, previews.AllApps),
            });
        }

        var controls = new List<PreviewControl>(ControlSlots.Count);
        foreach (var (slot, name) in ControlSlots)
            controls.Add(new PreviewControl(slot, name, previews.Icons.GetValueOrDefault(slot)));

        // The pack's own UI sounds: vag targets name the clip (their resource) and carry the VAG as their payload.
        var sounds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ThemePackTarget target in manifest.Targets)
            if (target.Layer == ThemePack.VagLayer && target.Resource is { Length: > 0 } clip)
                foreach (string wanted in Clips.Values)
                    if (clip == wanted) sounds[clip] = target.File;

        var model = new PreviewModel(games, media, controls, previews.AllApps, previews.Music, sounds);
        foreach (var (key, file) in pictures) model._pictureFiles[key] = file;
        return model;
    }

    // rnps/<hub>/appdb/<entry>/icon0.png: the tile is the entry, or the hub itself for "default".
    private static string? TileIdOf(string path)
    {
        string[] parts = path.Split('/');
        if (parts.Length != 5 || parts[0] != AssetRoots.Rnps || parts[2] != "appdb" || parts[4] != "icon0.png") return null;
        string id = parts[3] == "default" ? parts[1] : parts[3];
        return id.Length is > 0 and <= 32 ? id : null;
    }

    // apps/<TID>/pic0.dds and appmeta/<TID>/pic0.dds: the titles the pack gives an entry background.
    private static string? AppBackgroundOf(string path)
    {
        if (AssetRoots.IsAppBackground(path, out string tid)) return tid;
        string[] parts = path.Split('/');
        return parts.Length == 3 && parts[0] == AssetRoots.AppMeta && parts[2] == "pic0.dds" && AssetRoots.IsTitleId(parts[1]) ? parts[1] : null;
    }
}
