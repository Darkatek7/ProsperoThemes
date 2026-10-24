using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ProsperoThemes.Shared;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The firmware report (System tab, Firmware report): what this console has where a theme writes, so a firmware other than
/// 13.00 can be measured without a developer at the console. For every file a theme can name it records whether it
/// exists, its size, hash and format, and for the icon container the list of icons. The stock copy is described
/// (the backup, when the app made one), so an applied theme does not change the report. Only reads.
/// <c>ThemePackCreator/theme_pack.py catalog-from-report</c> turns a report into <c>catalog/&lt;firmware&gt;.json</c>.
/// </summary>
public static class FirmwareReport
{
    public const string Folder = "/data/prospero-themes/reports";

    /// <summary>The measured catalog the app carries; its paths are what the report looks at first.</summary>
    public const string BundledCatalog = "/app0/sce_sys/prospero-themes/catalog/13.00.json";

    public const string IconContainer = AssetRoots.VshAsset + "/Sce.PlayStation.PUI_UI3.rco";

    /// <summary>The system tile icons a theme can replace (web/shared/tile-icons.json, measured on 13.00).</summary>
    public static readonly IReadOnlyList<string> TileIcons =
    [
        "rnps/NPXS40047/appdb/default/icon0.png",
        "rnps/NPXS40037/appdb/default/icon0.png",
        "rnps/NPXS40071/appdb/default/icon0.png",
        "rnps/NPXS40071/appdb/NPXS40139/icon0.png",
        "rnps/NPXS40016/appdb/NPXS40053/icon0.png",
        "rnps/NPXS40016/appdb/NPXS40054/icon0.png",
        "rnps/NPXS40016/appdb/NPXS40056/icon0.png",
        "rnps/NPXS40016/appdb/NPXS40305/icon0.png",
        "rnps/NPXS40063/appdb/default/icon0.png",
    ];

    /// <summary>The asset paths of a catalog (<c>{"assets":[{"path":...}]}</c>); none for a missing or broken one.</summary>
    public static List<string> CatalogPaths(string? json)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(json) || !JsonValue.TryParse(json, out JsonValue root)) return paths;
        JsonValue assets = root["assets"];
        if (assets.Type != JsonType.Array) return paths;
        for (int i = 0; i < assets.Count; i++)
            if (assets[i].GetString("path") is { Length: > 0 } path) paths.Add(path);
        return paths;
    }

    /// <summary>
    /// Everything the report looks at: the catalog's files, the backgrounds the applier adds, tile icons, icons, and
    /// for each compressed texture its plain-DDS name (what firmware before 13.00 has, see <see cref="AssetRoots.Uncompressed"/>).
    /// </summary>
    public static List<string> Paths(IEnumerable<string> catalogPaths)
    {
        List<string> paths = catalogPaths.Concat([AssetRoots.HubBackground, .. AssetRoots.SystemTileBackgrounds, .. TileIcons, IconContainer])
            .Distinct(StringComparer.Ordinal).ToList();
        return paths.Concat(paths.Select(AssetRoots.Uncompressed).OfType<string>()).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>"firmware-13.000.000.json"; anything but digits, letters and dots becomes "_".</summary>
    /// <summary>Where the store takes a report (POST); it answers with a short code to post in a GitHub issue.</summary>
    public static string UploadUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/api/firmware-reports";

    /// <summary>The code in the store's answer (<c>{"code":"K7Q2MX"}</c>), or null when it holds none.</summary>
    public static string? ParseCode(string? json)
    {
        if (json is null || !JsonValue.TryParse(json, out JsonValue root)) return null;
        string code = root.GetString("code");
        if (code.Length is < 4 or > 12) return null;
        foreach (char c in code)
            if (c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9'))) return null;
        return code;
    }

    /// <summary>Why the store did not take a report, from its status and its <c>{"error": ...}</c> answer.</summary>
    public static string SendProblem(int status, string? json)
    {
        if (status == 429) return "the store takes only a few reports an hour from one network; try again later";
        string error = json is not null && JsonValue.TryParse(json, out JsonValue root) ? root.GetString("error") : "";
        return error.Length > 0 ? $"the store answered {status}: {error}" : $"the store answered {status}";
    }

    public static string FileName(string? firmware)
    {
        string name = string.IsNullOrEmpty(firmware) ? "unknown" : firmware;
        return "firmware-" + new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '.' ? c : '_').ToArray()) + ".json";
    }

    /// <param name="after">Extra top-level fields written after the files (e.g. which side read them).</param>
    public static byte[] Build(IDeviceFiles files, string? firmware, string appVersion, string whenUtc,
        IEnumerable<string> logicalPaths, string dataRoot = "/data/prospero-themes",
        Func<IEnumerable<(string Key, string Value)>>? after = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteNumber("report", 1);
            if (firmware is null) w.WriteNull("firmware"); else w.WriteString("firmware", firmware);
            w.WriteString("app_version", appVersion);
            w.WriteString("when_utc", whenUtc);
            w.WriteStartArray("files");
            foreach (string logical in logicalPaths)
            {
                w.WriteStartObject();
                w.WriteString("path", logical);
                try
                {
                    Describe(w, files, logical, $"{dataRoot}/{ThemeApplier.BackupFolder}/{logical}");
                }
                catch (Exception ex)
                {
                    w.WriteString("error", ex.Message);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (after is not null)
                foreach (var (key, value) in after())
                    w.WriteString(key, value);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void Describe(Utf8JsonWriter w, IDeviceFiles files, string logical, string backup)
    {
        if (!AssetRoots.TryResolve(logical, out string device, out string reason))
        {
            w.WriteString("error", reason);
            return;
        }
        // A tile icon on a console without the live copy of its app (13.60, 10.01): the file the applier writes.
        string resolved = ThemeApplier.OnDevice(files, logical, device);
        if (resolved != device)
        {
            device = resolved;
            w.WriteString("device", device);
        }
        bool exists = files.Exists(device);
        w.WriteBoolean("exists", exists);
        if (!exists) return;

        // One copy of a file in memory at a time: before 13.00 the icon container is about 100 MB, and the live file
        // and its backup together did not fit (11.60 report). The live file is compared by its hash.
        string liveHash = files.Hash(device);
        byte[]? stock = null;
        if (files.Exists(backup))
        {
            // A backup the app cannot read is reported with its error; the live file is described instead.
            try { stock = files.ReadAllBytes(backup); }
            catch (Exception ex) { w.WriteString("backup_error", ex.Message); }
        }
        string hash;
        if (stock is not null)
        {
            hash = ThemePack.Hash(stock);
            w.WriteString("source", "backup");
            // The backup is the stock file unless the system replaced the file since; then the live one is.
            w.WriteBoolean("themed", hash != liveHash);
        }
        else
        {
            stock = files.ReadAllBytes(device);
            hash = liveHash;
            w.WriteString("source", "console");
        }
        w.WriteNumber("bytes", stock.Length);
        w.WriteString("sha256", hash);
        if (hash != liveHash)
        {
            w.WriteNumber("live_bytes", files.Length(device));
            w.WriteString("live_sha256", liveHash);
        }

        PayloadFacts facts = PayloadCompat.Read(stock);
        w.WriteString("facts", facts.ToString());
        if (facts.Width > 0) { w.WriteNumber("width", facts.Width); w.WriteNumber("height", facts.Height); }
        if (facts.Format.Length > 0) w.WriteString("format", facts.Format);
        if (facts.ContentSize >= 0) w.WriteNumber("content_bytes", facts.ContentSize);

        if (logical == IconContainer)
        {
            w.WriteStartArray("icons");
            foreach (var (length, title) in RcoSvg.List(stock))
            {
                w.WriteStartObject();
                w.WriteNumber("bytes", length);
                if (title is null) w.WriteNull("title"); else w.WriteString("title", title);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
    }
}
