using System;
using System.Collections.Generic;
using ProsperoThemes.Shared;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>One avatar as the community store lists it (see web/server/src/avatars.ts, <c>GET /api/avatars</c>).</summary>
/// <param name="Sha256">The pack's SHA-256 when the store lists it; the app's avatar library names packs by it.</param>
public sealed record StoreAvatar(
    string Id, string Name, string Author, string Version, string Description,
    long Size, int Likes, int Downloads, string? PreviewUrl, string DownloadUrl, string? Sha256);

/// <summary>
/// The store's avatar list, parsed without the network so the PC tests cover it. It mirrors <see cref="CommunityList"/>
/// for themes: the same sort keys, likes answer and absolute-URL rules. URLs in the answers are server-relative and are
/// made absolute against the configured store address.
/// </summary>
public static class AvatarStore
{
    /// <summary>The store ids this console has liked as avatars, kept as a JSON array (the same format as <see cref="CommunityList.LikesPath"/>).</summary>
    public const string LikesPath = "/data/prospero-themes/avatar-likes.json";

    /// <summary>
    /// The list request. <paramref name="query"/> is escaped, so only what the store would accept reaches it intact.
    /// </summary>
    public static string ListUrl(string baseUrl, CommunitySort sort, int page = 1, int pageSize = 36, string? query = null) =>
        $"{baseUrl.TrimEnd('/')}/api/avatars?sort={CommunityList.SortKey(sort)}&page={page}&pageSize={pageSize}"
        + (string.IsNullOrEmpty(query) ? "" : "&q=" + Uri.EscapeDataString(query));

    /// <summary>The like route for an avatar: POST likes it, DELETE on the same address takes the like back.</summary>
    public static string LikeUrl(string baseUrl, string id) => $"{baseUrl.TrimEnd('/')}/api/avatars/{Uri.EscapeDataString(id)}/like";

    /// <summary>
    /// Parses <c>{ items: [...] }</c>; entries without an id or a download address are skipped, a malformed document throws.
    /// A <c>sha256</c> that is not a valid digest is dropped, a valid one is lowercased.
    /// </summary>
    /// <remarks>Uses the SDK's parser: System.Text.Json's JsonDocument pulls OpenSSL imports into the module.</remarks>
    public static List<StoreAvatar> Parse(string json, string baseUrl)
    {
        JsonValue root = JsonValue.Parse(json);
        var list = new List<StoreAvatar>();
        JsonValue items = root["items"];
        if (items.Type != JsonType.Array) return list;
        for (int i = 0; i < items.Count; i++)
        {
            JsonValue e = items[i];
            string id = e.GetString("id"), download = e.GetString("downloadUrl");
            if (id.Length == 0 || download.Length == 0) continue;
            string sha = e.GetString("sha256");
            list.Add(new StoreAvatar(
                id, e.GetString("name", id), e.GetString("author"), e.GetString("version"), e.GetString("description"),
                e["size"].AsLong(), e.GetInt("likes"), e.GetInt("downloads"),
                CommunityList.Absolute(baseUrl, e.GetString("previewUrl")), CommunityList.Absolute(baseUrl, download)!,
                CommunityList.IsDigest(sha) ? sha.ToLowerInvariant() : null));
        }
        return list;
    }

    /// <summary>
    /// The store ids whose pack is already in the avatar library: the store's SHA-256 matches a file named
    /// <c>&lt;sha256&gt;.p5avatar</c> among <paramref name="libraryFileNames"/> (names or paths; the extension is compared
    /// without case, the digest in lowercase). Files not named that way are ignored.
    /// </summary>
    public static HashSet<string> InLibrary(IEnumerable<StoreAvatar> avatars, IEnumerable<string> libraryFileNames)
    {
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in libraryFileNames)
        {
            string name = path[(path.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(AvatarPack.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            string stem = name[..^AvatarPack.Extension.Length];
            if (CommunityList.IsDigest(stem)) digests.Add(stem.ToLowerInvariant());
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (StoreAvatar a in avatars)
            if (a.Sha256 is { } digest && digests.Contains(digest.ToLowerInvariant())) ids.Add(a.Id);
        return ids;
    }
}
