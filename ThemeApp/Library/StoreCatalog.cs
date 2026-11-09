using System;
using System.Collections.Generic;
using System.Text;
using ProsperoThemes.Shared;
using SharpProspero.Platform;
using SharpProspero.Security;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// Stock catalogs from the store, for a firmware the app carries no catalog for (<see cref="StockCatalog"/>), so a newly
/// measured firmware does not wait for an app release. The store lists what it has (<c>GET /api/catalogs</c>:
/// <c>{"items":[{"firmware":"13.60","sha256":"…","updatedAt":"…"}]}</c>, the hash of the exact body of the next request) and
/// serves each one (<c>GET /api/catalogs/13.60</c>, the format of <c>catalog/*.json</c>; 404 when it has none). The app
/// keeps a checked copy in <see cref="Folder"/> and asks again in the background at every start; a catalog the app
/// carries always wins. Anything that does not pass <see cref="Validate"/> is never used or kept, and the copy kept
/// before stays. Plain functions over <see cref="IDeviceFiles"/> and a GET, so the PC tests cover them.
/// </summary>
public static class StoreCatalog
{
    /// <summary>Where the checked copies live, one <c>&lt;firmware&gt;.json</c> each (the app's data folder, see AppData).</summary>
    public const string Folder = "/data/prospero-themes/catalogs";

    /// <summary>A catalog is about 20 KB; the store's answer is never used when it is larger than this.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>What a <see cref="Refresh"/> came to. Only <see cref="RefreshOutcome.Updated"/> changes the kept copy.</summary>
    public enum RefreshOutcome
    {
        /// <summary>The store's catalog passed the checks and is the kept copy now.</summary>
        Updated,
        /// <summary>The kept copy is the store's catalog already; nothing was downloaded.</summary>
        Unchanged,
        /// <summary>The store has no catalog for this firmware (not listed, or 404); a kept copy stays.</summary>
        NotOffered,
        /// <summary>The store could not be reached or answered with an error; a kept copy stays.</summary>
        Failed,
        /// <summary>The store's catalog failed a check (<see cref="Validate"/>); a kept copy stays.</summary>
        Rejected,
    }

    /// <summary>
    /// The result of a refresh: what happened, the catalog to use from now on (null: keep the one in use), and a short
    /// English reason for the log.
    /// </summary>
    public sealed record RefreshResult(RefreshOutcome Outcome, StockCatalog? Catalog, string Message);

    public static string ListUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/api/catalogs";

    public static string CatalogUrl(string baseUrl, string firmwareName) => ListUrl(baseUrl) + "/" + firmwareName;

    /// <summary>The kept copy of a firmware's catalog.</summary>
    public static string CachePath(string firmwareName) => Folder + "/" + firmwareName + ".json";

    /// <summary>
    /// The store's list as firmware name to the SHA-256 of its catalog (lower case). Items without a firmware name of the
    /// <see cref="StockCatalog.FirmwareName"/> form or without a 64-digit hex hash are left out; null when the answer is no list.
    /// </summary>
    public static Dictionary<string, string>? ParseList(string? json)
    {
        if (json is null || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Object) return null;
        JsonValue items = root["items"];
        if (items.Type != JsonType.Array) return null;
        var list = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            string firmware = items[i].GetString("firmware"), sha = items[i].GetString("sha256").ToLowerInvariant();
            if (IsFirmwareName(firmware) && IsSha256(sha)) list[firmware] = sha;
        }
        return list;
    }

    /// <summary>
    /// Why <paramref name="body"/> is not a usable catalog of <paramref name="firmwareName"/>, or null when it is: at most
    /// <see cref="MaxBytes"/>, a JSON object whose <c>firmware</c> is the name asked for, SHA-256
    /// <paramref name="sha256"/> (when given: the hash the store's list named), every <c>path</c> (of <c>assets</c>,
    /// <c>icon_container</c> and <c>tile_icons</c>) a logical path under a known asset root (<see cref="AssetRoots.TryResolve"/>),
    /// every <c>stock_sha256</c> 64 lower-case hex digits, and at least one stock hash in all.
    /// </summary>
    public static string? Validate(byte[]? body, string firmwareName, string? sha256)
    {
        if (body is null || body.Length == 0) return "empty";
        if (body.Length > MaxBytes) return $"larger than {MaxBytes} bytes";
        if (sha256 is not null && !string.Equals(Sha256.HashHex(body), sha256, StringComparison.OrdinalIgnoreCase))
            return "its SHA-256 is not the one the store listed";
        string text;
        try { text = new UTF8Encoding(false, true).GetString(body); }
        catch (ArgumentException) { return "not UTF-8"; }
        if (!JsonValue.TryParse(text, out JsonValue root) || root.Type != JsonType.Object) return "not a JSON object";
        if (root["firmware"].Type != JsonType.String || root.GetString("firmware") != firmwareName)
            return "it is not the catalog of " + firmwareName;
        JsonValue assets = root["assets"];
        if (assets.Type != JsonType.Array || assets.Count == 0) return "no assets";
        for (int i = 0; i < assets.Count; i++)
            if (Entry(assets[i], "asset " + i) is { } problem) return problem;
        if (root.TryGet("icon_container", out JsonValue container) && !container.IsNull
            && Entry(container, "icon_container") is { } containerProblem) return containerProblem;
        if (root.TryGet("tile_icons", out JsonValue tiles) && !tiles.IsNull)
        {
            if (tiles.Type != JsonType.Array) return "tile_icons is not a list";
            for (int i = 0; i < tiles.Count; i++)
                if (Entry(tiles[i], "tile icon " + i) is { } tileProblem) return tileProblem;
        }
        return StockCatalog.Parse(text).Count == 0 ? "no stock hashes" : null;
    }

    // One entry with a path: an object, the path a known logical path, a stock hash (when it has one) 64 lower-case hex digits.
    private static string? Entry(JsonValue entry, string what)
    {
        if (entry.Type != JsonType.Object) return what + " is not an object";
        if (entry["path"].Type != JsonType.String) return what + ": no path";
        if (!AssetRoots.TryResolve(entry.GetString("path"), out _, out string reason)) return what + ": " + reason;
        if (entry.TryGet("stock_sha256", out JsonValue sha) && !sha.IsNull && !(sha.Type == JsonType.String && IsSha256(sha.AsString())))
            return what + ": stock_sha256 is not 64 lower-case hex digits";
        return null;
    }

    /// <summary>
    /// The catalog to use at start and where it is from: the bundled one (<paramref name="bundledJson"/>, the text of
    /// <see cref="StockCatalog.PathFor"/>, null when there is none) when it has stock hashes, otherwise the kept copy of the
    /// store's when it still passes <see cref="Validate"/>, otherwise none (<see cref="StockCatalog.Empty"/>).
    /// </summary>
    public static (StockCatalog Catalog, CatalogSource Source) Load(string? firmwareName, string? bundledJson, IDeviceFiles data)
    {
        StockCatalog bundled = StockCatalog.Parse(bundledJson);
        if (bundled.Count > 0) return (bundled, CatalogSource.Bundled);
        return ReadCache(firmwareName, data) is { } cached ? (cached, CatalogSource.Store) : (StockCatalog.Empty, CatalogSource.None);
    }

    // The kept copy when it is there and still passes the checks (a copy damaged on disk is not used); null otherwise.
    private static StockCatalog? ReadCache(string? firmwareName, IDeviceFiles data)
    {
        if (!IsFirmwareName(firmwareName)) return null;
        try
        {
            string path = CachePath(firmwareName!);
            if (!data.Exists(path)) return null;
            byte[] body = data.ReadAllBytes(path);
            return Validate(body, firmwareName!, null) is null ? StockCatalog.Parse(Encoding.UTF8.GetString(body)) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Asks the store for the catalog of <paramref name="firmwareName"/> (<paramref name="get"/> sends a GET; it may throw)
    /// and keeps it in <see cref="CachePath"/> when it passes <see cref="Validate"/> against the list's hash. The kept copy
    /// only changes then: a store without one, an error, a 404 or a catalog that fails a check leave it as it was. A kept
    /// copy whose hash is the listed one is not downloaded again.
    /// </summary>
    public static RefreshResult Refresh(string baseUrl, string firmwareName, Func<string, HttpResponse> get, IDeviceFiles data)
    {
        if (!IsFirmwareName(firmwareName)) return new(RefreshOutcome.NotOffered, null, "no firmware name");
        string? sha;
        try
        {
            HttpResponse answer = get(ListUrl(baseUrl));
            if (!answer.IsSuccess) return new(RefreshOutcome.Failed, null, "the catalog list answered " + answer.StatusCode);
            if (ParseList(Encoding.UTF8.GetString(answer.Body)) is not { } list) return new(RefreshOutcome.Failed, null, "the catalog list is not one");
            if (!list.TryGetValue(firmwareName, out sha)) return new(RefreshOutcome.NotOffered, null, "the store lists no catalog for " + firmwareName);
        }
        catch (Exception ex) { return new(RefreshOutcome.Failed, null, "can’t reach the store: " + ex.Message); }

        string path = CachePath(firmwareName);
        try
        {
            if (data.Exists(path) && string.Equals(data.Hash(path), sha, StringComparison.OrdinalIgnoreCase)
                && ReadCache(firmwareName, data) is { } kept)
                return new(RefreshOutcome.Unchanged, kept, "the kept catalog is the store’s");
        }
        catch (Exception) { /* downloaded again */ }

        byte[] body;
        try
        {
            HttpResponse answer = get(CatalogUrl(baseUrl, firmwareName));
            if (answer.StatusCode == 404) return new(RefreshOutcome.NotOffered, null, "the store has no catalog for " + firmwareName);
            if (!answer.IsSuccess) return new(RefreshOutcome.Failed, null, "the catalog answered " + answer.StatusCode);
            body = answer.Body;
        }
        catch (Exception ex) { return new(RefreshOutcome.Failed, null, "can’t reach the store: " + ex.Message); }
        if (Validate(body, firmwareName, sha) is { } problem)
            return new(RefreshOutcome.Rejected, null, "the store’s catalog for " + firmwareName + " was not used: " + problem);

        StockCatalog catalog = StockCatalog.Parse(Encoding.UTF8.GetString(body));
        try
        {
            // A new file next to the old one, then over it: an interrupted write never leaves half a catalog.
            data.CreateDirectoryRecursive(Folder);
            data.WriteAllBytes(path + ".tmp", body);
            data.Move(path + ".tmp", path);
        }
        catch (Exception ex)
        {
            // Still used until the app closes; the next start asks again.
            return new(RefreshOutcome.Updated, catalog, "catalog for " + firmwareName + " from the store, not kept: " + ex.Message);
        }
        return new(RefreshOutcome.Updated, catalog, "catalog for " + firmwareName + " from the store");
    }

    // "13.60", as StockCatalog.FirmwareName makes it: digits, one dot, two digits. Also all that goes into a URL or a path.
    private static bool IsFirmwareName(string? name)
    {
        if (name is null) return false;
        int dot = name.IndexOf('.');
        if (dot is < 1 or > 3 || name.Length != dot + 3) return false;
        for (int i = 0; i < name.Length; i++)
            if (i != dot && name[i] is not (>= '0' and <= '9')) return false;
        return true;
    }

    private static bool IsSha256(string text)
    {
        if (text.Length != 64) return false;
        foreach (char c in text)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
