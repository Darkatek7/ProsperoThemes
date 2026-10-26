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

/// <summary>
/// The Library's search: a local filter of the collection by name, author and description, with no network. The match
/// ignores case and accents and takes the words of the term in any order (every word has to appear somewhere in the three
/// texts). The folding is a small table of its own, not the framework's normalization: the keyboard only types a to z, and
/// the console's runtime may not carry the culture data the PC has, so the PC tests and the console must agree.
/// </summary>
public static class LibraryFilter
{
    /// <summary>The longest term the library search takes (the keyboard's own limit).</summary>
    public const int MaxLength = 60;

    /// <summary>
    /// The entries whose name, author or description contain every word of <paramref name="query"/>, in their order. An empty
    /// term (or only spaces) returns <paramref name="entries"/> itself.
    /// </summary>
    public static IReadOnlyList<ThemeLibraryEntry> Apply(IReadOnlyList<ThemeLibraryEntry> entries, string? query)
    {
        string[] words = Words(query);
        if (words.Length == 0) return entries;
        var shown = new List<ThemeLibraryEntry>();
        foreach (ThemeLibraryEntry entry in entries)
            if (Matches(entry, words)) shown.Add(entry);
        return shown;
    }

    /// <summary>Whether one entry matches the term.</summary>
    public static bool Matches(ThemeLibraryEntry entry, string? query) => Matches(entry, Words(query));

    private static bool Matches(ThemeLibraryEntry entry, string[] words)
    {
        if (words.Length == 0) return true;
        string text = Fold(entry.Manifest.Name) + "\n" + Fold(entry.Manifest.Author) + "\n" + Fold(entry.Manifest.Description);
        foreach (string word in words)
            if (!text.Contains(word, StringComparison.Ordinal)) return false;
        return true;
    }

    private static string[] Words(string? query) =>
        string.IsNullOrWhiteSpace(query) ? [] : Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lowercase, accents and umlauts removed, ß as ss, typographic quotes and dashes as plain ones; whitespace as one space.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char raw in text)
        {
            char c = char.ToLowerInvariant(raw);
            switch (c)
            {
                case 'à' or 'á' or 'â' or 'ã' or 'ä' or 'å': sb.Append('a'); break;
                case 'ç': sb.Append('c'); break;
                case 'è' or 'é' or 'ê' or 'ë': sb.Append('e'); break;
                case 'ì' or 'í' or 'î' or 'ï': sb.Append('i'); break;
                case 'ñ': sb.Append('n'); break;
                case 'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø': sb.Append('o'); break;
                case 'ù' or 'ú' or 'û' or 'ü': sb.Append('u'); break;
                case 'ý' or 'ÿ': sb.Append('y'); break;
                case 'ß': sb.Append("ss"); break;
                case '’' or '‘' or '‚': sb.Append('\''); break;
                case '–' or '—': sb.Append('-'); break;
                default: sb.Append(char.IsWhiteSpace(c) ? ' ' : c); break;
            }
        }
        return sb.ToString();
    }
}
