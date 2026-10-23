using System;
using System.Collections.Generic;
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
    public static readonly StockCatalog Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly Dictionary<string, string> _stock;

    private StockCatalog(Dictionary<string, string> stock) => _stock = stock;

    /// <summary>How many files the catalog knows.</summary>
    public int Count => _stock.Count;

    /// <summary>The stock SHA-256 of a logical path, when the catalog lists it.</summary>
    public bool TryStock(string logical, out string sha256) => _stock.TryGetValue(logical, out sha256!);

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
        JsonValue assets = root["assets"];
        if (assets.Type == JsonType.Array)
            for (int i = 0; i < assets.Count; i++)
                Add(stock, assets[i]);
        JsonValue container = root["icon_container"];
        if (container.Type == JsonType.Object) Add(stock, container);
        return stock.Count == 0 ? Empty : new StockCatalog(stock);
    }

    private static void Add(Dictionary<string, string> stock, JsonValue entry)
    {
        if (entry.GetString("path") is { Length: > 0 } path && entry.GetString("stock_sha256") is { Length: 64 } sha256)
            stock[path] = sha256;
    }
}
