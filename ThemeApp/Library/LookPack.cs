using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>
/// Makes one pack of the current look (<see cref="Look"/>): the targets of each layer's pack, of the parts that layer
/// shows, with later layers replacing earlier ones where both write the same target (the key <see cref="ThemePack.Validate"/>
/// checks: the path, and the icon or sound name for a patch). Payloads and their hashes are taken over as they are; each
/// is stored under its hash, so two packs' files of the same name do not collide and equal files are kept once. The
/// previews come along with their part, the cover from the last layer that shows the background. The result is an
/// ordinary pack (a stored zip, see <see cref="StoredZip"/>) that goes into the library like an imported one.
/// </summary>
public static class LookPack
{
    public const string Author = "You";
    public const string NamePrefix = "My mix ";

    /// <summary>The pack and its manifest.</summary>
    public sealed record Mix(byte[] Archive, ThemePackManifest Manifest);

    /// <summary>
    /// The pack of <paramref name="look"/>; <paramref name="open"/> reads a layer's pack (one at a time, so only the
    /// files that are kept stay in memory).
    /// </summary>
    /// <exception cref="ThemePackException">A layer's pack cannot be read, or the mix would not be a valid pack.</exception>
    public static Mix Build(IReadOnlyList<LookLayer> look, Func<LookLayer, ThemePack> open, string name)
    {
        var targets = new List<ThemePackTarget>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var apps = new Dictionary<string, string>(StringComparer.Ordinal);
        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        string? allApps = null, music = null, cover = null, anyCover = null;

        string Keep(byte[] data, string original, string folder)
        {
            string file = folder + ThemePack.Hash(data) + Extension(original);
            files.TryAdd(file, data);
            return file;
        }
        string? KeepFile(ThemePack pack, string? path, string folder) =>
            path is { Length: > 0 } && pack.Find(path) is { } data ? Keep(data, path, folder) : null;

        foreach (LookLayer layer in look)
        {
            ThemePack pack = open(layer);
            foreach (ThemePackTarget target in pack.ApplicableTargets())
            {
                if ((ThemeParts.Of(target) & layer.Parts) == 0) continue;
                ThemePackTarget kept = target with { File = Keep(pack.Payload(target), target.File, "payloads/") };
                string key = ThemePack.IsPatchLayer(target.Layer) ? target.Path + "#" + target.Resource : target.Path;
                if (index.TryGetValue(key, out int at)) targets[at] = kept;
                else
                {
                    index[key] = targets.Count;
                    targets.Add(kept);
                }
            }

            ThemePackPreviews previews = pack.Manifest.Previews;
            if ((layer.Parts & ThemePart.AppBackgrounds) != 0)
            {
                foreach ((string titleId, string path) in previews.Apps)
                    if (KeepFile(pack, path, "previews/") is { } file) apps[titleId] = file;
                allApps = KeepFile(pack, previews.AllApps, "previews/") ?? allApps;
            }
            if ((layer.Parts & ThemePart.Icons) != 0)
                foreach ((string slot, string path) in previews.Icons)
                    if (KeepFile(pack, path, "previews/") is { } file) icons[slot] = file;
            if ((layer.Parts & ThemePart.Sounds) != 0)
                music = KeepFile(pack, previews.Music, "previews/") ?? music;
            string? picture = KeepFile(pack, pack.Manifest.Preview, "previews/");
            if ((layer.Parts & ThemePart.Background) != 0) cover = picture ?? cover;
            anyCover = picture ?? anyCover;
        }
        if (targets.Count == 0) throw new ThemePackException("The look has nothing to save.");
        cover ??= anyCover;

        // The id says what the mix writes, so saving the same look twice gives the same id.
        var what = new StringBuilder();
        foreach (ThemePackTarget t in targets.OrderBy(t => t.Path + "#" + t.Resource, StringComparer.Ordinal))
            what.Append(t.Path).Append('#').Append(t.Resource).Append(' ').Append(t.Sha256).Append('\n');
        string id = "mix-" + ThemePack.Hash(Encoding.UTF8.GetBytes(what.ToString()))[..16];

        var manifest = new ThemePackManifest(
            Format: ThemePackManifest.CurrentFormat, Id: id, Name: name, Author: Author, Version: "1.0.0",
            Description: Description(look), Unmapped: "keep", Targets: targets, Preview: cover)
        {
            Previews = new ThemePackPreviews(apps, allApps, music, icons),
        };

        // Only the files the manifest names: a payload a later layer replaced is left out.
        var used = new HashSet<string>(targets.Select(t => t.File), StringComparer.Ordinal);
        foreach (string path in apps.Values.Concat(icons.Values)) used.Add(path);
        foreach (string? path in new[] { allApps, music, cover }) if (path is not null) used.Add(path);
        var entries = new List<(string, byte[])> { (ThemePack.ManifestName, Encoding.UTF8.GetBytes(manifest.Write())) };
        foreach (string file in used.OrderBy(f => f, StringComparer.Ordinal)) entries.Add((file, files[file]));
        byte[] archive = StoredZip.Write(entries);

        if (archive.Length > ThemeArchive.MaxArchiveBytes)
            throw new ThemePackException($"The mix would be {archive.Length / (1024 * 1024)} MiB; a pack may be 48 MiB at most.");
        PackReport report = ThemePack.Open(archive).Validate();
        if (report.Errors.FirstOrDefault() is { } error) throw new ThemePackException("The mix is not a valid pack: " + error.Message);
        return new Mix(archive, manifest);
    }

    /// <summary>"Made on the console from Blue Hour (Background) and Kanto (Sounds, Icons)."</summary>
    public static string Description(IReadOnlyList<LookLayer> look)
    {
        var parts = look.Select(layer => layer.Name + " (" + string.Join(", ", ThemeParts.List(layer.Parts).Select(ThemeParts.Label)) + ")").ToList();
        string list = parts.Count > 1 ? string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1] : parts.FirstOrDefault() ?? "";
        return "Made on the console from " + list + ".";
    }

    /// <summary>"My mix N" with the first N no theme in <paramref name="names"/> has.</summary>
    public static string NextName(IEnumerable<string> names)
    {
        var taken = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        int n = 1;
        while (taken.Contains(NamePrefix + n)) n++;
        return NamePrefix + n;
    }

    /// <summary>Whether every layer's pack is one of <paramref name="paths"/> (the library), so the look can be saved.</summary>
    public static bool Savable(IReadOnlyList<LookLayer>? look, IEnumerable<string> paths)
    {
        if (look is not { Count: > 1 }) return false;
        var library = new HashSet<string>(paths, StringComparer.Ordinal);
        return look.All(layer => library.Contains(layer.Pack));
    }

    // ".dds.zst" of "payload/bg_hub_default.dds.zst": what follows the first dot of the file name, when it is short and
    // plain; the payload's kind is told by its target, so a name without one is fine.
    private static string Extension(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        int dot = name.IndexOf('.');
        if (dot < 0) return "";
        string extension = name[dot..].ToLowerInvariant();
        return extension.Length <= 16 && extension.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.') ? extension : "";
    }
}
