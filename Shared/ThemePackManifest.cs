using System;
using System.Collections.Generic;
using System.Text;
using SharpProspero.Storage;

namespace ProsperoThemes.Shared;

/// <summary>Raised when a pack archive cannot be read at all, as opposed to failing validation.</summary>
public sealed class ThemePackException : Exception
{
    public ThemePackException(string message) : base(message)
    {
    }

    public ThemePackException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>One file a pack wants to replace.</summary>
/// <param name="Path">Logical <c>&lt;root&gt;/&lt;relative&gt;</c> path, e.g. <c>vsh_asset/bg_hub_default.dds.zst</c>.</param>
/// <param name="Layer">Advisory grouping: background, icon, glyph, sound, video, model or ui; svg and vag are named icons and sounds inside a container.</param>
/// <param name="File">Payload path inside the archive. The bytes are written verbatim.</param>
/// <param name="Bytes">Exact payload length. <c>-1</c> means the manifest did not say.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the payload.</param>
/// <param name="Screen">Optional human label for the screen this target dresses.</param>
/// <param name="StockSha256">Optional hash of the stock asset, so an applier can tell "still stock" from "already changed".</param>
public sealed record ThemePackTarget(
    string Path,
    string Layer,
    string File,
    long Bytes,
    string Sha256,
    string? Screen = null,
    string? StockSha256 = null,
    string? Resource = null);

/// <summary>
/// A theme pack's <c>theme.json</c>.
/// </summary>
/// <remarks>
/// The manifest is <b>target-driven, not feature-driven</b>: it lists files to replace and tags each with
/// a layer for the UI, and it never encodes how a background is rendered or what an icon means. That is
/// what lets backgrounds, icons, glyphs, sounds, video and eventually GNF and RCO resources all ride the
/// same format without an engine change — and why a target nobody has seen before still works, provided
/// its root is allowlisted.
/// </remarks>
public sealed record ThemePackManifest(
    int Format,
    string Id,
    string Name,
    string Author,
    string Version,
    string Description,
    string Unmapped,
    IReadOnlyList<ThemePackTarget> Targets,
    string? Preview = null)
{
    /// <summary>The manifest version this build understands.</summary>
    public const int CurrentFormat = 2;

    /// <summary>
    /// Largest payload accepted, to keep a mistake off a system partition. A 4K BC7 background frame is
    /// ~50 KB to ~3.7 MB in practice; 32 MiB leaves room for a future uncompressed layer and still refuses
    /// anything that would obviously fill the volume.
    /// </summary>
    public const long DefaultSizeCap = 32L * 1024 * 1024;

    /// <summary>Layers this build knows. An unknown layer is skipped, not refused.</summary>
    public static readonly IReadOnlySet<string> KnownLayers = new HashSet<string>(StringComparer.Ordinal)
    {
        "background", "icon", "glyph", "sound", "video", "model", "ui", "svg", "vag",
    };

    /// <summary>Parses a manifest, raising <see cref="ThemePackException"/> when it is structurally unusable.</summary>
    public static ThemePackManifest Parse(string json)
    {
        JsonValue root;
        try
        {
            root = JsonValue.Parse(json);
        }
        catch (Exception ex)
        {
            throw new ThemePackException($"theme.json is not valid JSON: {ex.Message}", ex);
        }

        if (root.Type != JsonType.Object)
            throw new ThemePackException("theme.json must be a JSON object");

        JsonValue targetsValue = root["targets"];
        if (targetsValue.Type != JsonType.Array)
            throw new ThemePackException("theme.json: 'targets' must be an array");

        var targets = new List<ThemePackTarget>(targetsValue.Count);
        for (int i = 0; i < targetsValue.Count; i++)
        {
            JsonValue t = targetsValue[i];
            if (t.Type != JsonType.Object)
                throw new ThemePackException($"theme.json: targets[{i}] must be an object");

            targets.Add(new ThemePackTarget(
                Path: t.GetString("path"),
                Layer: t.GetString("layer"),
                File: t.GetString("file"),
                Bytes: t.ContainsKey("bytes") ? t["bytes"].AsLong(-1) : -1,
                Sha256: NormaliseHash(t.GetString("sha256")),
                Screen: t.ContainsKey("screen") ? t.GetString("screen") : null,
                StockSha256: t.ContainsKey("stock_sha256") ? NormaliseHash(t.GetString("stock_sha256")) : null,
                Resource: t.ContainsKey("resource") ? t.GetString("resource") : null));
        }

        return new ThemePackManifest(
            Format: root.GetInt("format"),
            Id: root.GetString("id"),
            Name: root.GetString("name"),
            Author: root.GetString("author"),
            Version: root.GetString("version"),
            Description: root.GetString("description"),
            Unmapped: root.ContainsKey("unmapped") ? root.GetString("unmapped") : "keep",
            Targets: targets,
            Preview: root.ContainsKey("preview") ? root.GetString("preview") : null);
    }

    /// <summary>Serialises the manifest back to JSON.</summary>
    public string Write(bool indented = true)
    {
        var o = JsonValue.NewObject();
        o["format"] = Format;
        o["id"] = Id;
        o["name"] = Name;
        o["author"] = Author;
        o["version"] = Version;
        o["description"] = Description;
        if (Preview is not null)
            o["preview"] = Preview;
        o["unmapped"] = Unmapped;

        JsonValue array = JsonValue.NewArray();
        foreach (ThemePackTarget t in Targets)
        {
            var tv = JsonValue.NewObject();
            tv["path"] = t.Path;
            tv["layer"] = t.Layer;
            tv["file"] = t.File;
            tv["bytes"] = t.Bytes;
            tv["sha256"] = t.Sha256;
            if (t.Screen is not null)
                tv["screen"] = t.Screen;
            if (t.StockSha256 is not null)
                tv["stock_sha256"] = t.StockSha256;
            if (t.Resource is not null)
                tv["resource"] = t.Resource;
            array.Add(tv);
        }

        o["targets"] = array;
        return o.Write(indented);
    }

    private static string NormaliseHash(string value) => value.Trim().ToLowerInvariant();
}
