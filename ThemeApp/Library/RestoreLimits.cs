using System;
using System.Collections.Generic;
using System.Text;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The kinds of console file whose original is gone (<see cref="ThemeApplier.NoOriginal"/>), as the app tells the user
/// about them: never by path, only by kind. Most come from a console another tool had already themed before this app
/// made its backups (an icon container without the theme icons' titles, system tiles that share one picture, the home
/// music swapped); a backup deleted from <c>/data</c> gives the same.
/// </summary>
[Flags]
public enum NoOriginalKind
{
    None = 0,
    /// <summary>The home background and the system tiles' backgrounds (<c>vsh_asset/bg_*</c>).</summary>
    Backgrounds = 1,
    /// <summary>A title's own background (<c>pic0.dds</c>).</summary>
    AppBackgrounds = 2,
    /// <summary>The tile icon of a system app (<c>rnps</c>). It comes back from the firmware's own copy, so it is rarely here.</summary>
    TileIcons = 4,
    /// <summary>The icon container: the system icons and the UI sounds, one file.</summary>
    IconsAndSounds = 8,
    /// <summary>A whole sound file (<c>.at9</c>): the home music.</summary>
    Music = 16,
    /// <summary>Anything else (another texture, a video).</summary>
    Other = 32,
}

/// <summary>
/// What Restore defaults can and cannot do on this console, from the files without an original: which kinds they are,
/// which of them still come back without a backup, the texts the app shows, and when it shows its one-time notice.
/// The record of such files (<see cref="FileName"/>, one logical path per line) is kept by the app, not the applier,
/// so the System tab can say it at the next start; the applier's own files stay as they are.
/// </summary>
public static class RestoreLimits
{
    /// <summary>The record under the app's folder: the logical paths known to have no original on this console.</summary>
    public const string FileName = "no-original.txt";

    /// <summary>
    /// The kinds that stay as they are after Restore defaults: all of them. A tile icon comes back from the firmware's copy in
    /// <c>/system_ex/rnps/apps</c>, so it is only here when even that is gone. The icon container's icons and sounds could
    /// come back from a pack with the firmware's own bytes (<see cref="ThemePack.StockLayer"/>), but no such pack is
    /// published (the store refuses them), so the texts do not point users at one.
    /// </summary>
    public static NoOriginalKind Lost(NoOriginalKind kinds) => kinds;

    /// <summary>The kind of one logical path (as <see cref="ThemeApplier.NoOriginal"/> and the backup index name them).</summary>
    public static NoOriginalKind Of(string logical)
    {
        if (logical.EndsWith(".rco", StringComparison.OrdinalIgnoreCase)) return NoOriginalKind.IconsAndSounds;
        if (logical.EndsWith(".at9", StringComparison.OrdinalIgnoreCase)) return NoOriginalKind.Music;
        if (logical.StartsWith(AssetRoots.Rnps + "/", StringComparison.Ordinal)) return NoOriginalKind.TileIcons;
        ThemePart part = ThemeParts.OfLogical(logical);
        if (part == ThemePart.AppBackgrounds) return NoOriginalKind.AppBackgrounds;
        if (part == ThemePart.Background) return NoOriginalKind.Backgrounds;
        return NoOriginalKind.Other;
    }

    /// <summary>The kinds of <paramref name="logicals"/> together.</summary>
    public static NoOriginalKind Of(IEnumerable<string> logicals)
    {
        NoOriginalKind kinds = NoOriginalKind.None;
        foreach (string logical in logicals) kinds |= Of(logical);
        return kinds;
    }

    // ---- the record ---------------------------------------------------------------------------------------------------

    /// <summary>The logical paths of the record, tolerating a missing or broken file (blank lines and duplicates go).</summary>
    public static List<string> Parse(string? text)
    {
        var paths = new List<string>();
        if (string.IsNullOrEmpty(text)) return paths;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            string path = line.Trim();
            if (path.Length > 0 && seen.Add(path)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>The record's text: one path per line, sorted, so the same set always gives the same file.</summary>
    public static string Serialize(IEnumerable<string> paths)
    {
        var sorted = new List<string>(new HashSet<string>(paths, StringComparer.Ordinal));
        sorted.Sort(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (string path in sorted) text.Append(path).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// The record after an apply. The apply looked at each file it wrote (<paramref name="written"/>, logical paths): one
    /// it found without an original is in <paramref name="noOriginal"/>, every other one has its original now (a backup, or
    /// the firmware's own bytes from the theme). Files it did not touch keep what the record said.
    /// </summary>
    public static List<string> AfterApply(IEnumerable<string> known, IEnumerable<string> written, IEnumerable<string> noOriginal)
    {
        var drop = new HashSet<string>(written, StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string path in known)
            if (!drop.Contains(path)) result.Add(path);
        result.AddRange(noOriginal);
        return Parse(Serialize(result));
    }

    /// <summary>
    /// The record after Restore defaults of <paramref name="parts"/>. A restore of every part looks at every file the app
    /// changed, so what it found replaces the record; a restore of some parts only replaces the record's files of those
    /// parts (<see cref="ThemeParts.OfLogical"/>; icons and sounds go together, as the restore takes them).
    /// </summary>
    public static List<string> AfterRestore(IEnumerable<string> known, ThemePart parts, IEnumerable<string> noOriginal)
    {
        if ((parts & ThemePart.All) == ThemePart.All) return Parse(Serialize(noOriginal));
        if ((parts & (ThemePart.Icons | ThemePart.Sounds)) != 0) parts |= ThemePart.Icons | ThemePart.Sounds;
        var result = new List<string>();
        foreach (string path in known)
            if ((ThemeParts.OfLogical(path) & parts) == 0) result.Add(path);
        result.AddRange(noOriginal);
        return Parse(Serialize(result));
    }

    // ---- when the app tells -------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the one-time notice shows after a job that found <paramref name="found"/>: only for a kind the user was not
    /// told about yet (<paramref name="told"/>, <see cref="AppSettings.NoOriginalTold"/>). So it comes on the first apply or
    /// restore that meets such a file, and not again on every apply; a kind met later for the first time tells once more.
    /// </summary>
    public static bool ShouldTell(NoOriginalKind found, NoOriginalKind told) => (found & ~told) != NoOriginalKind.None;

    // ---- texts ----------------------------------------------------------------------------------------------------------

    /// <summary>The title of the one-time notice.</summary>
    public const string NoticeTitle = "This console had another theme";

    /// <summary>
    /// The one-time notice: what happened, what Restore defaults cannot bring back (by kind) and what still comes back.
    /// Empty for no kinds.
    /// </summary>
    public static string NoticeText(NoOriginalKind kinds)
    {
        if (kinds == NoOriginalKind.None) return "";
        return "Another tool changed this console before Prospero Themes, so some originals are gone. Restore defaults can’t bring back the old "
            + Names(kinds) + ": " + (kinds == NoOriginalKind.Music ? "it stays" : "they stay") + " as that theme left them.";
    }

    /// <summary>
    /// The sentence that leads the result of Restore defaults when files stayed as they are: by kind, never by path. Empty
    /// for none. It ends with a space, as the applier's other notes do.
    /// </summary>
    public static string ResultLine(NoOriginalKind kinds)
    {
        if (kinds == NoOriginalKind.None) return "";
        return Capitalized(ShortNames(kinds)) + (kinds == NoOriginalKind.Music ? " stays" : " stay")
            + " as another tool’s theme left " + (kinds == NoOriginalKind.Music ? "it" : "them") + ". ";
    }

    /// <summary>The description of the System tab's Restore defaults row on this console (the default one without kinds).</summary>
    public static string RowText(NoOriginalKind kinds) =>
        kinds == NoOriginalKind.None ? "Put every background, icon and sound back to the original"
            : "On this console, the old " + ShortNames(kinds) + " can’t come back";

    /// <summary>The text of the Restore defaults question (without parts) on this console.</summary>
    public static string DialogText(NoOriginalKind kinds)
    {
        const string plain = "Puts the original backgrounds, icons and sounds back on this console. Your theme library stays as it is.";
        return kinds == NoOriginalKind.None ? plain : "Puts back what Prospero Themes changed; your theme library stays as it is. " + Limit(kinds);
    }

    /// <summary>The text of the Restore defaults question with parts on this console: the parts first, then the limit in one sentence.</summary>
    public static string PartsDialogText(NoOriginalKind kinds)
    {
        const string plain = "Tick the parts to put back. System icons and sounds share one file, so they go back together. Your library stays as it is.";
        return kinds == NoOriginalKind.None ? plain : "Tick the parts to put back. System icons and sounds go back together. " + Limit(kinds);
    }

    // What Restore defaults cannot do on this console, in one short sentence (the questions show five lines at most).
    private static string Limit(NoOriginalKind kinds) =>
        "The old " + ShortNames(kinds) + " can’t come back: another tool’s theme left " + (kinds == NoOriginalKind.Music ? "it." : "them.");

    /// <summary>The kinds in words, in a fixed order ("backgrounds, tile icons and home music").</summary>
    public static string Names(NoOriginalKind kinds)
    {
        var names = new List<string>(6);
        if ((kinds & NoOriginalKind.Backgrounds) != 0) names.Add("backgrounds");
        if ((kinds & NoOriginalKind.AppBackgrounds) != 0) names.Add("app backgrounds");
        if ((kinds & NoOriginalKind.TileIcons) != 0) names.Add("tile icons");
        if ((kinds & NoOriginalKind.IconsAndSounds) != 0) { names.Add("icons"); names.Add("sounds"); }
        if ((kinds & NoOriginalKind.Music) != 0) names.Add("home music");
        if ((kinds & NoOriginalKind.Other) != 0) names.Add("other files");
        if (names.Count <= 1) return names.Count == 0 ? "" : names[0];
        return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[^1];
    }

    /// <summary>
    /// <see cref="Names"/> for one line: up to two kinds by name, more as the first one "and other files"
    /// ("backgrounds and other files"), so a toast or a row never runs long.
    /// </summary>
    public static string ShortNames(NoOriginalKind kinds)
    {
        int count = 0;
        for (int bits = (int)kinds; bits != 0; bits &= bits - 1) count++;
        if (count <= 2) return Names(kinds);
        NoOriginalKind first = (NoOriginalKind)((int)kinds & -(int)kinds);
        return Names(first) + " and other files";
    }

    // ---- the told kinds in settings.json ---------------------------------------------------------------------------------

    private static readonly (NoOriginalKind Kind, string Key)[] Keys =
    [
        (NoOriginalKind.Backgrounds, "backgrounds"), (NoOriginalKind.AppBackgrounds, "app_backgrounds"),
        (NoOriginalKind.TileIcons, "tile_icons"), (NoOriginalKind.IconsAndSounds, "icons_and_sounds"),
        (NoOriginalKind.Music, "music"), (NoOriginalKind.Other, "other"),
    ];

    /// <summary>Kinds as settings.json keeps them: "backgrounds,music". Empty for none.</summary>
    public static string KindsText(NoOriginalKind kinds)
    {
        var keys = new List<string>(Keys.Length);
        foreach (var (kind, key) in Keys)
            if ((kinds & kind) != 0) keys.Add(key);
        return string.Join(",", keys);
    }

    /// <summary>The kinds of <see cref="KindsText"/>; unknown words are ignored.</summary>
    public static NoOriginalKind ParseKinds(string? text)
    {
        NoOriginalKind kinds = NoOriginalKind.None;
        if (string.IsNullOrEmpty(text)) return kinds;
        foreach (string word in text.Split(','))
            foreach (var (kind, key) in Keys)
                if (word.Trim() == key) kinds |= kind;
        return kinds;
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
