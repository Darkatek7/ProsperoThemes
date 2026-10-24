using System;
using System.Collections.Generic;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>Sort orders the community store offers, in the order the app cycles through them.</summary>
public enum CommunitySort { Top, Downloads, New }

/// <summary>One published theme as the store lists it (see web/server, <c>GET /api/themes</c>).</summary>
/// <param name="Sha256">The pack's SHA-256 when the store lists it; the library names packs by it.</param>
public sealed record CommunityTheme(
    string Id, string Name, string Author, string Version, string Description,
    long Size, int Likes, int Downloads, string? PreviewUrl, string DownloadUrl,
    bool Fallback, bool AllApps, int PerApp, int Icons, int Tiles, string? Sha256 = null)
{
    /// <summary>
    /// What the pack changes, in one short line for the card. Written by <see cref="PackSummary"/>, like the
    /// library's own line for the same pack: the music counts as one of its sounds. The store's summary has no video
    /// count, so that is absent.
    /// </summary>
    public string Contents => PackSummary.Format(Fallback, AllApps, PerApp, Icons, Tiles, Sounds + (Music ? 1 : 0));

    /// <summary>Background music (bgm_*.at9) is in the pack; false from a store that predates the music count.</summary>
    public bool Music { get; init; }

    /// <summary>UI sounds and other system sound files besides the music; 0 from a store that predates the count.</summary>
    public int Sounds { get; init; }

    /// <summary>
    /// The store's small JPEG of the preview (about an eighth of the PNG), or null from a store that has none.
    /// </summary>
    public string? ThumbUrl { get; init; }

    /// <summary>The picture a card loads: the small JPEG when the store has one, else the preview PNG.</summary>
    public string? PictureUrl => ThumbUrl ?? PreviewUrl;

    /// <summary>
    /// The category ids the store lists the theme under (0 to 3, from <see cref="CommunityCategories"/>); empty for a
    /// theme without any and for a store that predates categories. An init property, so the positional constructor
    /// stays as it was and a <c>with</c> copy (a like or download count coming in) keeps it.
    /// </summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The pictures count as AI-generated (the author, the store's check or a moderator says so); false from a store
    /// that predates it.
    /// </summary>
    public bool Ai { get; init; }
}

/// <summary>One theme category: the id the store's API uses and the label the app shows.</summary>
public readonly record struct CommunityCategory(string Id, string Label);

/// <summary>
/// The store's fixed category list, in the order the filter chips show it. MUST MATCH <c>web/shared/src/categories.ts</c>
/// (ids and labels, same order): the store validates <c>?category=</c> against its copy and answers 400 for an id it
/// does not know.
/// </summary>
public static class CommunityCategories
{
    public static readonly CommunityCategory[] List =
    [
        new("anime", "Anime"),
        new("games", "Games"),
        new("movies-tv", "Movies & TV"),
        new("cartoon", "Cartoon"),
        new("nature", "Nature"),
        new("space", "Space"),
        new("fantasy", "Fantasy"),
        new("sci-fi", "Sci-Fi"),
        new("pixel-art", "Pixel art"),
        new("minimal", "Minimal"),
        new("abstract", "Abstract"),
        new("seasonal", "Seasonal"),
    ];

    public static int Count => List.Length;

    /// <summary>The index of <paramref name="id"/> in <see cref="List"/>, or -1 when the store does not know it.</summary>
    public static int IndexOf(string id)
    {
        for (int i = 0; i < List.Length; i++)
            if (string.Equals(List[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The id at <paramref name="index"/> (an index into <see cref="List"/>), or null for -1 ("All") and anything out of range.</summary>
    public static string? IdAt(int index) => (uint)index < (uint)List.Length ? List[index].Id : null;

    /// <summary>The label at <paramref name="index"/>, or "All" for -1 and anything out of range.</summary>
    public static string LabelAt(int index) => (uint)index < (uint)List.Length ? List[index].Label : "All";
}

/// <summary>
/// The store's wire format, parsed without the network, so the PC tests cover it. URLs in the answers are
/// server-relative and are made absolute against the configured store address.
/// </summary>
public static class CommunityList
{
    public static string SortKey(CommunitySort sort) => sort switch
    {
        CommunitySort.Downloads => "downloads",
        CommunitySort.New => "new",
        _ => "top",
    };

    public static string Label(CommunitySort sort) => sort switch
    {
        CommunitySort.Downloads => "Most downloaded",
        CommunitySort.New => "New",
        _ => "Top rated",
    };

    /// <summary>
    /// The list request. <paramref name="category"/> is a category id (see <see cref="CommunityCategories"/>); without
    /// one the store lists every theme. The id is escaped, so only what the store would accept reaches it intact.
    /// <paramref name="hideAi"/> leaves out themes with AI-generated pictures (<c>ai=hide</c>).
    /// </summary>
    public static string ListUrl(string baseUrl, CommunitySort sort, string? category = null, int page = 1, int pageSize = 24,
        string? query = null, bool hideAi = false) =>
        $"{baseUrl.TrimEnd('/')}/api/themes?sort={SortKey(sort)}&page={page}&pageSize={pageSize}"
        + (string.IsNullOrEmpty(category) ? "" : "&category=" + Uri.EscapeDataString(category))
        + (string.IsNullOrEmpty(query) ? "" : "&q=" + Uri.EscapeDataString(query))
        + (hideAi ? "&ai=hide" : "");

    /// <summary>
    /// The download address for a preview: the store does not count it as a download (<c>preview=1</c>; an older store ignores
    /// the flag and counts it). <paramref name="downloadUrl"/> is a theme's <see cref="CommunityTheme.DownloadUrl"/>.
    /// </summary>
    public static string PreviewDownloadUrl(string downloadUrl) => downloadUrl + (downloadUrl.Contains('?') ? "&" : "?") + "preview=1";

    public static string LikeUrl(string baseUrl, string id) => $"{baseUrl.TrimEnd('/')}/api/themes/{Uri.EscapeDataString(id)}/like";

    /// <summary>Parses <c>{ items: [...] }</c>; malformed entries are skipped, a malformed document throws.</summary>
    /// <remarks>Uses the SDK's parser: System.Text.Json's JsonDocument pulls OpenSSL imports into the module.</remarks>
    public static List<CommunityTheme> Parse(string json, string baseUrl)
    {
        JsonValue root = JsonValue.Parse(json);
        var list = new List<CommunityTheme>();
        JsonValue items = root["items"];
        if (items.Type != JsonType.Array) return list;
        for (int i = 0; i < items.Count; i++)
        {
            JsonValue e = items[i];
            string id = e.GetString("id"), download = e.GetString("downloadUrl");
            if (id.Length == 0 || download.Length == 0) continue;
            JsonValue s = e["summary"];
            list.Add(new CommunityTheme(
                id, e.GetString("name", id), e.GetString("author"), e.GetString("version"), e.GetString("description"),
                e["size"].AsLong(), e.GetInt("likes"), e.GetInt("downloads"),
                Absolute(baseUrl, e.GetString("previewUrl")), Absolute(baseUrl, download)!,
                s.GetBool("fallback"), s.GetBool("allApps"), s.GetInt("perApp"), s.GetInt("icons"), s.GetInt("tiles"),
                IsDigest(e.GetString("sha256")) ? e.GetString("sha256").ToLowerInvariant() : null)
            {
                Categories = ParseCategories(e["categories"]),
                ThumbUrl = Absolute(baseUrl, e.GetString("thumbUrl")),
                Music = s.GetBool("music"),
                Sounds = s.GetInt("sounds"),
                Ai = e.GetBool("ai"),
            });
        }
        return list;
    }

    /// <summary>
    /// The known category ids of a <c>categories</c> array, in the store's order. Ids this app does not know (a store
    /// newer than the app), repeats and anything that is not a string are dropped; a missing field (a store that
    /// predates categories) gives an empty list.
    /// </summary>
    private static IReadOnlyList<string> ParseCategories(JsonValue array)
    {
        if (array.Type != JsonType.Array || array.Count == 0) return Array.Empty<string>();
        var ids = new List<string>(array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            JsonValue item = array[i];
            if (item.Type != JsonType.String) continue;
            int index = CommunityCategories.IndexOf(item.AsString());
            if (index >= 0 && !ids.Contains(CommunityCategories.List[index].Id)) ids.Add(CommunityCategories.List[index].Id);
        }
        return ids.Count == 0 ? Array.Empty<string>() : ids;
    }

    /// <summary>
    /// Which store theme was downloaded as which pack: store id to the pack's SHA-256. The library stores a
    /// pack as <c>&lt;sha256&gt;.p5theme</c>, so this is what tells the Community tab that a theme is already in
    /// the library (the store's public list does not carry the hash).
    /// </summary>
    public const string DownloadsPath = "/data/prospero-themes/community.json";

    /// <summary>Reads <see cref="DownloadsPath"/>, tolerating a missing or broken file.</summary>
    public static Dictionary<string, string> ParseDownloads(string? json)
    {
        var downloads = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Object)
            return downloads;
        foreach (string id in root.Keys)
        {
            string digest = root.GetString(id);
            if (id.Length > 0 && IsDigest(digest)) downloads[id] = digest.ToLowerInvariant();
        }
        return downloads;
    }

    public static string SerializeDownloads(IReadOnlyDictionary<string, string> downloads)
    {
        var o = JsonValue.NewObject();
        foreach (var (id, digest) in downloads) o[id] = digest;
        return o.Write(indented: true) + "\n";
    }

    /// <summary>The pack hash a library file is named after, or null for a file not named that way.</summary>
    public static string? LibraryDigest(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        if (!name.EndsWith(".p5theme", StringComparison.OrdinalIgnoreCase)) return null;
        string stem = name[..^".p5theme".Length];
        return IsDigest(stem) ? stem.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The store ids whose pack is in the library: by the hash the store lists, else by the hash recorded when
    /// the app downloaded it. A pack removed from the library loses its badge again.
    /// </summary>
    public static HashSet<string> InLibrary(IEnumerable<CommunityTheme> themes, IReadOnlyDictionary<string, string> downloads,
        IReadOnlySet<string> libraryDigests)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (CommunityTheme t in themes)
        {
            string? digest = t.Sha256 ?? (downloads.TryGetValue(t.Id, out string? recorded) ? recorded : null);
            if (digest is not null && libraryDigests.Contains(digest)) ids.Add(t.Id);
        }
        return ids;
    }

    /// <summary>
    /// The store ids with a newer version than the library holds: the store lists a hash that is not in the
    /// library, while the pack recorded at the last download still is.
    /// </summary>
    public static HashSet<string> Updates(IEnumerable<CommunityTheme> themes, IReadOnlyDictionary<string, string> downloads,
        IReadOnlySet<string> libraryDigests)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (CommunityTheme t in themes)
            if (t.Sha256 is { } listed && !libraryDigests.Contains(listed)
                && downloads.TryGetValue(t.Id, out string? recorded) && recorded != listed && libraryDigests.Contains(recorded))
                ids.Add(t.Id);
        return ids;
    }

    private static bool IsDigest(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>The <c>likes</c> count from a like/unlike answer, or -1.</summary>
    public static int ParseLikes(string json) =>
        JsonValue.TryParse(json, out JsonValue root) ? root.GetInt("likes", -1) : -1;

    /// <summary>The <c>liked</c> flag of a like/unlike answer, or null when the answer does not carry one.</summary>
    public static bool? ParseLiked(string json) =>
        JsonValue.TryParse(json, out JsonValue root) && root["liked"].Type == JsonType.Boolean ? root["liked"].AsBool() : null;

    /// <summary>
    /// What the like button sends for a theme: a theme this console already liked is unliked with DELETE, any other
    /// gets POST (which the store treats as idempotent, so a like made before the app kept a record just shows up).
    /// </summary>
    public static SharpProspero.Platform.HttpMethod LikeMethod(bool alreadyLiked) =>
        alreadyLiked ? SharpProspero.Platform.HttpMethod.Delete : SharpProspero.Platform.HttpMethod.Post;

    /// <summary>The store ids this console has liked, kept as a JSON array in <see cref="LikesPath"/>.</summary>
    public const string LikesPath = "/data/prospero-themes/likes.json";

    /// <summary>Reads <see cref="LikesPath"/>, tolerating a missing or broken file; anything that is not a non-empty id is skipped.</summary>
    public static HashSet<string> ParseLikedIds(string? json)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Array)
            return ids;
        for (int i = 0; i < root.Count; i++)
        {
            JsonValue item = root[i];
            if (item.Type == JsonType.String && item.AsString() is { Length: > 0 and <= 128 } id) ids.Add(id);
        }
        return ids;
    }

    /// <summary>The text of <see cref="LikesPath"/>: the ids as a sorted JSON array, so the same set always writes the same file.</summary>
    public static string FormatLikedIds(IEnumerable<string> ids)
    {
        var sorted = new List<string>(new HashSet<string>(ids, StringComparer.Ordinal));
        sorted.Sort(StringComparer.Ordinal);
        JsonValue array = JsonValue.NewArray();
        foreach (string id in sorted) array.Add(id);
        return array.Write(indented: true) + "\n";
    }

    private static string? Absolute(string baseUrl, string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        return baseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
    }
}
