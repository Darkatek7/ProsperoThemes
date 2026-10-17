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
    /// <summary>What the pack changes, in one short line for the card.</summary>
    public string Contents
    {
        get
        {
            var parts = new List<string>();
            if (Fallback) parts.Add("Background");
            if (AllApps) parts.Add("all apps");
            if (PerApp > 0) parts.Add($"{PerApp} app image{(PerApp == 1 ? "" : "s")}");
            if (Icons > 0) parts.Add($"{Icons} icon{(Icons == 1 ? "" : "s")}");
            if (Tiles > 0) parts.Add($"{Tiles} tile icon{(Tiles == 1 ? "" : "s")}");
            return parts.Count == 0 ? "Theme" : string.Join(" · ", parts);
        }
    }
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

    public static string ListUrl(string baseUrl, CommunitySort sort, int page = 1, int pageSize = 24) =>
        $"{baseUrl.TrimEnd('/')}/api/themes?sort={SortKey(sort)}&page={page}&pageSize={pageSize}";

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
                IsDigest(e.GetString("sha256")) ? e.GetString("sha256").ToLowerInvariant() : null));
        }
        return list;
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

    private static string? Absolute(string baseUrl, string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        return baseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
    }
}
