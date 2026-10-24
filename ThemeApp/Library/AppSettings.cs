using System;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The app's few user settings, kept in <c>/data/prospero-themes/settings.json</c>. Parsing and writing are
/// plain functions so the PC tests cover them; the app only reads and writes the file.
/// </summary>
public sealed record AppSettings(bool AllAppsBackgrounds, string StoreUrl, string DeviceId)
{
    public const string Path = "/data/prospero-themes/settings.json";

    /// <summary>
    /// Whether an apply first puts back what earlier themes changed (see ThemeApplier.KeepEarlierThemes). Off by
    /// default, so themes combine: "earlier_themes": "replace" in settings.json turns it on.
    /// </summary>
    public bool ReplaceEarlierThemes { get; init; }

    /// <summary>
    /// Whether an apply takes a theme's music and sounds. On by default; "sounds": "off" in settings.json turns it off,
    /// and then the apply question starts with them left out.
    /// </summary>
    public bool Sounds { get; init; } = true;

    /// <summary>The volumes the UI sound and music settings offer, in percent; 100 writes a theme's sounds as they were made.</summary>
    public static readonly int[] SoundVolumes = [10, 25, 50, 75, 100];

    /// <summary>
    /// How loud a theme's UI sounds are written, in percent (one of <see cref="SoundVolumes"/>); "sound_volume" in
    /// settings.json, 25 when missing or not one of them: theme sounds are usually much louder than the system's own.
    /// </summary>
    public int SoundVolume { get; init; } = DefaultSoundVolume;

    /// <summary>The UI sound volume of a new settings file.</summary>
    public const int DefaultSoundVolume = 25;

    /// <summary>The volume one step (<paramref name="step"/> -1 or +1) from <paramref name="volume"/>, kept within <see cref="SoundVolumes"/>.</summary>
    public static int StepSoundVolume(int volume, int step) => StepVolume(volume, step, DefaultSoundVolume);

    /// <summary>
    /// How loud a theme's home music is written, in percent (one of <see cref="SoundVolumes"/>); "music_volume" in
    /// settings.json, 10 when missing or not one of them: a theme's music plays far louder than the system's own.
    /// </summary>
    public int MusicVolume { get; init; } = DefaultMusicVolume;

    /// <summary>The home music volume of a new settings file.</summary>
    public const int DefaultMusicVolume = 10;

    /// <summary>Like <see cref="StepSoundVolume"/>, for <see cref="MusicVolume"/>.</summary>
    public static int StepMusicVolume(int volume, int step) => StepVolume(volume, step, DefaultMusicVolume);

    private static int StepVolume(int volume, int step, int fallback)
    {
        int index = Array.IndexOf(SoundVolumes, volume);
        if (index < 0) index = Array.IndexOf(SoundVolumes, fallback);
        return SoundVolumes[Math.Clamp(index + step, 0, SoundVolumes.Length - 1)];
    }

    private static int VolumeOf(string text, int fallback) =>
        int.TryParse(text, out int percent) && Array.IndexOf(SoundVolumes, percent) >= 0 ? percent : fallback;

    /// <summary>The order of the library row after the applied theme; "library_sort": "newest" in settings.json.</summary>
    public LibrarySort LibrarySort { get; init; }

    /// <summary>The Community tab leaves out themes with AI-generated pictures; "hide_ai": "on" in settings.json.</summary>
    public bool HideAi { get; init; }

    /// <summary>
    /// The system software for which the user answered "Not now" when asked to apply the theme again after a system
    /// update ("reapply_skipped"), so the question does not come back until the next update. Null when never answered.
    /// </summary>
    public string? ReapplySkipped { get; init; }

    /// <summary>
    /// The firmware ("12.40") for which the app asked once to send a firmware report ("report_asked"), answered or not,
    /// so it does not ask again for that firmware. Null when never asked.
    /// </summary>
    public string? ReportAsked { get; init; }

    /// <summary>
    /// The firmware to ask a report for on the System tab, or null: only a named firmware without a bundled catalog
    /// (<paramref name="hasCatalog"/>) that was not asked about yet.
    /// </summary>
    public string? ReportToAsk(string? firmware, bool hasCatalog) =>
        firmware is { Length: > 0 } && !hasCatalog && ReportAsked != firmware ? firmware : null;

    /// <summary>
    /// The public community store, used when settings.json has no "store_url" (a first start writes it there).
    /// An empty "store_url" means the user removed the store on purpose.
    /// </summary>
    public const string DefaultStoreUrl = "https://themes.darkatek7.com";

    /// <summary>Reads settings, tolerating a missing or broken file; a missing device id is created.</summary>
    public static AppSettings Parse(string? json, Func<string> newDeviceId)
    {
        bool all = false, replace = false, sounds = true, newest = false, hideAi = false;
        string url = DefaultStoreUrl, device = "", skipped = "", asked = "", volume = "", music = "";
        // The SDK's parser, not System.Text.Json: JsonDocument seeds its hashing from the BCL's crypto
        // random source, which pulls OpenSSL imports the console cannot link (see ThemePack.Hash).
        if (!string.IsNullOrWhiteSpace(json) && JsonValue.TryParse(json, out JsonValue root) && root.Type == JsonType.Object)
        {
            all = root.GetString("background_scope") == "all";
            replace = root.GetString("earlier_themes") == "replace";
            sounds = root.GetString("sounds") != "off";
            newest = root.GetString("library_sort") == "newest";
            hideAi = root.GetString("hide_ai") == "on";
            url = root.GetString("store_url", url).Trim();
            device = root.GetString("device_id");
            skipped = root.GetString("reapply_skipped");
            asked = root.GetString("report_asked");
            volume = root.GetString("sound_volume");
            music = root.GetString("music_volume");
        }
        if (!IsDeviceId(device)) device = newDeviceId();
        return new AppSettings(all, url, device)
        {
            ReplaceEarlierThemes = replace, Sounds = sounds, LibrarySort = newest ? LibrarySort.Newest : LibrarySort.Name, HideAi = hideAi,
            ReapplySkipped = skipped.Length > 0 ? skipped : null, ReportAsked = asked.Length > 0 ? asked : null,
            SoundVolume = VolumeOf(volume, DefaultSoundVolume), MusicVolume = VolumeOf(music, DefaultMusicVolume),
        };
    }

    public string Serialize()
    {
        var o = JsonValue.NewObject();
        o["background_scope"] = AllAppsBackgrounds ? "all" : "default";
        o["earlier_themes"] = ReplaceEarlierThemes ? "replace" : "keep";
        o["sounds"] = Sounds ? "on" : "off";
        o["sound_volume"] = SoundVolume.ToString(System.Globalization.CultureInfo.InvariantCulture);
        o["music_volume"] = MusicVolume.ToString(System.Globalization.CultureInfo.InvariantCulture);
        o["library_sort"] = LibrarySort == LibrarySort.Newest ? "newest" : "name";
        o["hide_ai"] = HideAi ? "on" : "off";
        o["store_url"] = StoreUrl;
        o["device_id"] = DeviceId;
        if (ReapplySkipped is { Length: > 0 }) o["reapply_skipped"] = ReapplySkipped;
        if (ReportAsked is { Length: > 0 }) o["report_asked"] = ReportAsked;
        return o.Write(indented: true) + "\n";
    }

    /// <summary>The longest store address the app accepts (what the on-screen keyboard can type).</summary>
    public const int MaxStoreUrlLength = 200;

    /// <summary>Trims the address and drops trailing slashes, but keeps the "://" of a bare scheme.</summary>
    public static string NormalizeStoreUrl(string url)
    {
        string result = url.Trim();
        while (result.EndsWith('/') && !result.EndsWith("://", StringComparison.Ordinal)) result = result[..^1];
        return result;
    }

    /// <summary>
    /// Why an address cannot be used as the store, as a short sentence, or null when it can. The empty
    /// string is fine: it means no store. Only the shape is checked; whether a server answers is not.
    /// </summary>
    public static string? StoreUrlProblem(string url)
    {
        if (url.Length == 0) return null;
        if (url.Length > MaxStoreUrlLength) return $"The address is too long ({MaxStoreUrlLength} characters at most).";
        foreach (char c in url)
            if (char.IsWhiteSpace(c)) return "The address can’t contain spaces.";
        string rest;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) rest = url[7..];
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) rest = url[8..];
        else return "The address must start with http:// or https://.";
        int end = rest.IndexOfAny(['/', '?', '#']);
        string authority = end < 0 ? rest : rest[..end];
        int colon = authority.EndsWith(']') ? -1 : authority.LastIndexOf(':');
        string host = colon < 0 ? authority : authority[..colon];
        if (host.Length == 0) return "The server name is missing after http:// or https://.";
        if (colon >= 0 && !IsPort(authority[(colon + 1)..])) return "The port after the colon is not valid.";
        return null;
    }

    private static bool IsPort(string text)
    {
        if (text.Length is 0 or > 5) return false;
        foreach (char c in text)
            if (!char.IsAsciiDigit(c)) return false;
        return int.Parse(text) is >= 1 and <= 65535;
    }

    /// <summary>A random 32-character id: anonymous, only used to count one like per console.</summary>
    public static string NewDeviceId() => Guid.NewGuid().ToString("N");

    /// <summary>8 to 64 characters of [A-Za-z0-9_-], what the store accepts.</summary>
    public static bool IsDeviceId(string value)
    {
        if (value.Length is < 8 or > 64) return false;
        foreach (char c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) return false;
        return true;
    }
}
