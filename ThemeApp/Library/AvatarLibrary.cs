using System;
using System.Collections.Generic;
using System.Text;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>One avatar pack of the app's avatars: where it is stored and what its tile shows.</summary>
public sealed record AvatarEntry(string Path, AvatarTile Tile);

/// <summary>
/// The app's avatars: <c>.p5avatar</c> packs in <see cref="Root"/>, apart from the theme library (avatars never come with a
/// theme). Each is stored under its content hash, like a library pack, and checked end to end (<see cref="AvatarPack.Validate"/>)
/// when it comes in and when it is read. Plain logic over <see cref="IDeviceFiles"/>, so the PC tests cover it; on the console
/// the files are <see cref="AppData.Files"/>, which go through ThemeHelper where the app cannot reach /data.
/// </summary>
public static class AvatarLibrary
{
    public const string Root = "/data/prospero-themes/avatars";

    /// <summary>Where a removed avatar goes, as a removed theme does: kept, not deleted.</summary>
    public const string Attic = "/data/prospero-themes/attic";

    /// <summary>The most avatars the grid shows; four DXT5 files and a preview are about 300 KB each.</summary>
    public const int MaxAvatars = 128;

    /// <summary>
    /// The store's names for the avatars, one <c>&lt;sha256&gt; &lt;name&gt;</c> line each. The store can rename an avatar after a
    /// check (its pack keeps the name it was made with), and the pack itself is never rewritten, because its file name is its
    /// hash. A pack without a line here shows its own name.
    /// </summary>
    public const string NamesPath = "/data/prospero-themes/avatar-names.txt";

    /// <summary>The most names <see cref="NamesPath"/> keeps, and the longest name.</summary>
    public const int MaxNames = 512, MaxNameLength = 80;

    public static bool IsPack(string name) => name.EndsWith(AvatarPack.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads and checks the packs among <paramref name="names"/> (the files in <see cref="Root"/>), sorted by name. A pack that
    /// cannot be read or fails its check is left out and named in the problem (null when every pack was fine).
    /// </summary>
    public static (List<AvatarEntry> Entries, string? Problem) Read(IDeviceFiles files, IEnumerable<string> names)
    {
        var entries = new List<AvatarEntry>();
        var errors = new List<string>();
        var sorted = new List<string>();
        foreach (string name in names) if (IsPack(name)) sorted.Add(name);
        sorted.Sort(StringComparer.Ordinal);
        Dictionary<string, string> storeNames;
        try { storeNames = ReadNames(files); }
        catch { storeNames = new(StringComparer.Ordinal); }   // the packs' own names then
        foreach (string name in sorted)
        {
            if (entries.Count >= MaxAvatars) break;
            string path = Root + "/" + name;
            try
            {
                long size = files.Length(path);
                if (size <= 0 || size > AvatarPack.ArchiveCap) throw new ThemePackException($"is {size} bytes, over {AvatarPack.ArchiveCap}");
                AvatarPack pack = Check(files.ReadAllBytes(path));
                string shown = DigestOf(name) is { } digest && storeNames.TryGetValue(digest, out string? storeName) ? storeName : pack.Name;
                entries.Add(new AvatarEntry(path, new AvatarTile(pack.Id, shown, pack.Author, pack.Preview())));
            }
            catch (Exception ex) { if (errors.Count < 4) errors.Add(name + ": " + ex.Message); }
        }
        entries.Sort((a, b) => string.Compare(a.Tile.Name, b.Tile.Name, StringComparison.OrdinalIgnoreCase));
        return (entries, errors.Count == 0 ? null : string.Join(" | ", errors));
    }

    /// <summary>
    /// Checks a pack and stores it under its content hash (.tmp, hash check, then rename). Returns false when that exact pack is
    /// already there. Shared by the USB import and the store download.
    /// </summary>
    /// <exception cref="ThemePackException">The pack is invalid, or the copy failed its check.</exception>
    public static bool Add(IDeviceFiles files, byte[] bytes)
    {
        Check(bytes);
        files.CreateDirectoryRecursive(Root);
        string digest = ThemePack.Hash(bytes);
        string destination = Root + "/" + digest + AvatarPack.Extension;
        if (files.Exists(destination))
        {
            if (files.Length(destination) == bytes.Length && files.Hash(destination) == digest) return false;
            throw new ThemePackException("Existing copy is damaged; import skipped.");
        }
        string temporary = destination + ".tmp";
        files.WriteAllBytes(temporary, bytes);
        if (files.Length(temporary) != bytes.Length || files.Hash(temporary) != digest)
            throw new ThemePackException("The copy failed the integrity check.");
        files.Move(temporary, destination);
        return true;
    }

    /// <summary>
    /// The names in <see cref="NamesPath"/> by digest (lowercase); empty when there is no file. Lines that are not a digest and a
    /// name are skipped.
    /// </summary>
    public static Dictionary<string, string> ReadNames(IDeviceFiles files)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!files.Exists(NamesPath)) return names;
        foreach (string line in Encoding.UTF8.GetString(files.ReadAllBytes(NamesPath)).Split('\n'))
        {
            int space = line.IndexOf(' ');
            if (space <= 0 || !CommunityList.IsDigest(line[..space])) continue;
            string name = Clean(line[(space + 1)..]);
            if (name.Length > 0 && names.Count < MaxNames) names[line[..space].ToLowerInvariant()] = name;
        }
        return names;
    }

    /// <summary>
    /// Keeps the store's names for <paramref name="avatars"/>, but only for packs among <paramref name="libraryFileNames"/> (the
    /// files in <see cref="Root"/>), so a list load does not fill the file with avatars this console never had. Returns whether a
    /// name changed; the file is written only then (.tmp, then rename).
    /// </summary>
    public static bool RememberNames(IDeviceFiles files, IEnumerable<StoreAvatar> avatars, IEnumerable<string> libraryFileNames)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in libraryFileNames)
            if (DigestOf(path[(path.LastIndexOf('/') + 1)..]) is { } digest) present.Add(digest);
        Dictionary<string, string> names = ReadNames(files);
        bool changed = false;
        foreach (StoreAvatar avatar in avatars)
        {
            string name = Clean(avatar.Name);
            if (avatar.Sha256?.ToLowerInvariant() is not { } digest || !present.Contains(digest) || name.Length == 0) continue;
            if (names.TryGetValue(digest, out string? known) && known == name) continue;
            if (!names.ContainsKey(digest) && names.Count >= MaxNames) continue;
            names[digest] = name;
            changed = true;
        }
        if (!changed) return false;
        var text = new StringBuilder();
        var digests = new List<string>(names.Keys);
        digests.Sort(StringComparer.Ordinal);
        foreach (string digest in digests) text.Append(digest).Append(' ').Append(names[digest]).Append('\n');
        files.CreateDirectoryRecursive(NamesPath[..NamesPath.LastIndexOf('/')]);
        files.WriteAllBytes(NamesPath + ".tmp", Encoding.UTF8.GetBytes(text.ToString()));
        files.Move(NamesPath + ".tmp", NamesPath);
        return true;
    }

    /// <summary>
    /// Moves an avatar into <see cref="Attic"/> under its own file name. Only packs directly in <see cref="Root"/> qualify. A
    /// picture already set stays on the console; the backup that Console picture puts back is not touched.
    /// </summary>
    public static void Remove(IDeviceFiles files, string path)
    {
        string file = path[(path.LastIndexOf('/') + 1)..];
        if (path != Root + "/" + file || !IsPack(file)) throw new InvalidOperationException("Only avatars in " + Root + " can be removed.");
        files.CreateDirectoryRecursive(Attic);
        string stem = file[..^AvatarPack.Extension.Length];
        string target = Attic + "/" + file;
        for (int n = 2; files.Exists(target); n++) target = Attic + "/" + stem + "-" + n + AvatarPack.Extension;
        files.Move(path, target);
    }

    // The digest a file named <sha256>.p5avatar is stored under, lowercase; null for any other name.
    private static string? DigestOf(string fileName)
    {
        if (!IsPack(fileName)) return null;
        string stem = fileName[..^AvatarPack.Extension.Length];
        return CommunityList.IsDigest(stem) ? stem.ToLowerInvariant() : null;
    }

    // One line of the names file: no line breaks or other control characters, trimmed, at most MaxNameLength characters.
    private static string Clean(string name)
    {
        var clean = new StringBuilder(name.Length);
        foreach (char c in name) clean.Append(char.IsControl(c) ? ' ' : c);
        string text = clean.ToString().Trim();
        return text.Length > MaxNameLength ? text[..MaxNameLength].TrimEnd() : text;
    }

    // Opens and validates; the first error is the message.
    private static AvatarPack Check(byte[] bytes)
    {
        AvatarPack pack = AvatarPack.Open(bytes);
        PackReport report = pack.Validate();
        foreach (var issue in report.Errors) throw new ThemePackException(issue.Message);
        return pack;
    }
}
