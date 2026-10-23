using System;
using System.Collections.Generic;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>How the library row is ordered after the applied theme: by name, or the newest additions first.</summary>
public enum LibrarySort { Name, Newest }

/// <summary>
/// The order of the library row: the theme the console shows first, then the rest by <see cref="LibrarySort"/>. When a
/// pack came into the library is kept in <see cref="AddedPath"/> (pack hash to Unix seconds), because the helper that
/// serves /data on some jailbreaks reports no file dates. Plain functions, so the PC tests cover them.
/// </summary>
public static class LibraryOrder
{
    public const string AddedPath = "/data/prospero-themes/library/added.json";

    /// <summary>How the library names the order, next to its count.</summary>
    public static string Label(LibrarySort sort) => sort == LibrarySort.Newest ? "Newest first" : "A to Z";

    /// <summary>
    /// The entries in display order. The first entry whose manifest id is <paramref name="appliedId"/> leads; the rest
    /// follow by name, or by the time they were added (newest first, then by name). A pack without a recorded time
    /// (the bundled theme) counts as the oldest.
    /// </summary>
    public static List<ThemeLibraryEntry> Sort(IReadOnlyList<ThemeLibraryEntry> entries, string? appliedId, LibrarySort sort,
        IReadOnlyDictionary<string, long> added)
    {
        var list = new List<ThemeLibraryEntry>(entries);
        long When(ThemeLibraryEntry e) => DigestOf(e.Path) is { } d && added.TryGetValue(d, out long t) ? t : 0;
        int ByName(ThemeLibraryEntry a, ThemeLibraryEntry b)
        {
            int c = string.Compare(a.Manifest.Name, b.Manifest.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
        }
        list.Sort(sort == LibrarySort.Newest ? (a, b) => When(b).CompareTo(When(a)) is var c && c != 0 ? c : ByName(a, b) : ByName);
        int applied = appliedId is { Length: > 0 } id ? list.FindIndex(e => e.Manifest.Id == id) : -1;
        if (applied > 0)
        {
            ThemeLibraryEntry lead = list[applied];
            list.RemoveAt(applied);
            list.Insert(0, lead);
        }
        return list;
    }

    /// <summary>The hash a library file is named after (<c>&lt;sha256&gt;.p5theme</c>), or null for any other path.</summary>
    public static string? DigestOf(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        int dot = name.IndexOf('.');
        string stem = dot < 0 ? name : name[..dot];
        if (stem.Length != 64) return null;
        foreach (char c in stem)
            if (!char.IsAsciiHexDigitLower(c)) return null;
        return stem;
    }

    public static Dictionary<string, long> ParseAdded(string? json)
    {
        var added = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Object) return added;
        foreach (string key in root.Keys)
            if (DigestOf(key) == key && root[key].Type == JsonType.Number) added[key] = (long)root.GetNumber(key);
        return added;
    }

    public static string SerializeAdded(IReadOnlyDictionary<string, long> added)
    {
        var o = JsonValue.NewObject();
        var keys = new List<string>(added.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys) o[key] = (double)added[key];
        return o.Write(indented: true) + "\n";
    }

    /// <summary>
    /// Records <paramref name="now"/> for every library pack in <paramref name="entries"/> without a time yet, and drops
    /// the times of packs that are gone. Returns whether anything changed.
    /// </summary>
    public static bool Record(Dictionary<string, long> added, IReadOnlyList<ThemeLibraryEntry> entries, long now)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        foreach (ThemeLibraryEntry entry in entries)
        {
            if (entry.Bundled || DigestOf(entry.Path) is not { } digest) continue;
            present.Add(digest);
            if (added.TryAdd(digest, now)) changed = true;
        }
        foreach (string key in new List<string>(added.Keys))
            if (!present.Contains(key)) { added.Remove(key); changed = true; }
        return changed;
    }
}
