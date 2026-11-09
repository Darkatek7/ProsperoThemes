using System;
using System.Collections.Generic;
using System.Linq;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The SHA-256 of each stock file a firmware's measured catalog (<c>catalog/&lt;firmware&gt;.json</c>, bundled with the
/// app) lists, by logical path: its <c>assets</c> and its <c>icon_container</c>. The applier asks it whether a file
/// is the console's original, so it never takes a theme's file for one when the backup is gone, and never puts back
/// a backup that is not one. A firmware without a catalog has an empty one: then only the app's own records count.
/// </summary>
public sealed class StockCatalog
{
    /// <summary>Where the app carries the catalogs (ThemeApp/build.ps1 copies every <c>catalog/*.json</c>).</summary>
    public const string Folder = "/app0/sce_sys/prospero-themes/catalog";

    /// <summary>A catalog that knows no file.</summary>
    public static readonly StockCatalog Empty = new(new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, long>(StringComparer.Ordinal), null, 0);

    private readonly Dictionary<string, string> _stock;
    private readonly Dictionary<string, long> _bytes;

    private StockCatalog(Dictionary<string, string> stock, Dictionary<string, long> bytes, HashSet<int>? untitled, int icons) =>
        (_stock, _bytes, UntitledIcons, IconCount) = (stock, bytes, untitled, icons);

    /// <summary>Which icons of this firmware's stock icon container have no title, by position; null when the catalog does not say.</summary>
    public IReadOnlySet<int>? UntitledIcons { get; }

    /// <summary>How many icons this firmware's stock icon container has; 0 when the catalog does not say.</summary>
    public int IconCount { get; }

    /// <summary>How many files the catalog knows.</summary>
    public int Count => _stock.Count;

    /// <summary>The stock SHA-256 of a logical path, when the catalog lists it.</summary>
    public bool TryStock(string logical, out string sha256) => _stock.TryGetValue(logical, out sha256!);

    /// <summary>The size of the stock file in bytes, or 0 when the catalog does not say.</summary>
    public long StockBytes(string logical) => _bytes.TryGetValue(logical, out long bytes) ? bytes : 0;

    /// <summary>Every logical path the catalog has a stock hash for, in order.</summary>
    public IEnumerable<string> Paths => _stock.Keys.OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>
    /// The catalog name of a system software version as the console reports it: "13.000.012" is "13.00", the way
    /// <c>theme_pack.py catalog-from-report</c> names it. Null for anything else.
    /// </summary>
    public static string? FirmwareName(string? version)
    {
        if (version is null) return null;
        string[] parts = version.Split('.');
        if (parts.Length != 3 || parts[1].Length != 3 || !int.TryParse(parts[0], out int major)) return null;
        return $"{major}.{parts[1][..2]}";
    }

    /// <summary>The bundled catalog file of <paramref name="version"/>, or null when it cannot be named.</summary>
    public static string? PathFor(string? version) => FirmwareName(version) is { } name ? $"{Folder}/{name}.json" : null;

    /// <summary>The stock hashes of a catalog file; <see cref="Empty"/> for a missing or broken one.</summary>
    public static StockCatalog Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Object)
            return Empty;
        var stock = new Dictionary<string, string>(StringComparer.Ordinal);
        var bytes = new Dictionary<string, long>(StringComparer.Ordinal);
        JsonValue assets = root["assets"];
        if (assets.Type == JsonType.Array)
            for (int i = 0; i < assets.Count; i++)
                Add(stock, bytes, assets[i]);
        JsonValue container = root["icon_container"];
        if (container.Type == JsonType.Object) Add(stock, bytes, container);
        HashSet<int>? untitled = null;
        if (container["untitled"] is { Type: JsonType.Array } list)
        {
            untitled = new HashSet<int>(list.Count);
            for (int i = 0; i < list.Count; i++) untitled.Add(list[i].AsInt(-1));
        }
        return stock.Count == 0 ? Empty : new StockCatalog(stock, bytes, untitled, container.GetInt("icons"));
    }

    /// <summary>
    /// The icon containers every catalog among <paramref name="catalogs"/> knows, by SHA-256: their path and the firmware
    /// whose original they are. The icon container is the one file whose original differs between firmware; every other
    /// file a catalog lists is the same on all of them. Broken catalogs are skipped.
    /// </summary>
    public static Dictionary<string, (string Path, string Firmware)> Containers(IEnumerable<string> catalogs)
    {
        var found = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (string json in catalogs)
        {
            if (string.IsNullOrWhiteSpace(json) || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Object)
                continue;
            JsonValue container = root["icon_container"];
            if (container.Type != JsonType.Object || container.GetString("path") is not { Length: > 0 } path
                || container.GetString("stock_sha256") is not { Length: 64 } sha256)
                continue;
            found.TryAdd(sha256, (path, root.GetString("firmware") is { Length: > 0 } firmware ? firmware : "another firmware"));
        }
        return found;
    }

    private static void Add(Dictionary<string, string> stock, Dictionary<string, long> bytes, JsonValue entry)
    {
        if (entry.GetString("path") is not { Length: > 0 } path || entry.GetString("stock_sha256") is not { Length: 64 } sha256)
            return;
        stock[path] = sha256;
        double size = entry.GetNumber("stock_bytes");
        if (size > 0 && size == Math.Floor(size)) bytes[path] = (long)size;
    }
}
