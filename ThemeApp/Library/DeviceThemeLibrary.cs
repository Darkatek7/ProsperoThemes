using System;
using System.Collections.Generic;
using System.Linq;
using SharpProspero.Storage;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>The packs, the scan's message, and when each library pack came in (see <see cref="LibraryOrder"/>).</summary>
public sealed record LibrarySnapshot(IReadOnlyList<ThemeLibraryEntry> Entries, string Message, IReadOnlyDictionary<string, long>? Added = null);

/// <summary>
/// Device file access, called on a worker rather than the frame thread. The library goes through
/// <see cref="AppData.Files"/>, so it works where only ThemeHelper reaches /data; USB drives are read directly.
/// </summary>
public static class DeviceThemeLibrary
{
    public const string Root = "/data/prospero-themes/library";
    public const string AssetRoot = "/app0/sce_sys/prospero-themes";
    public const string Bundle = AssetRoot + "/blue-hour.p5theme";
    private const int MaxPacks = 128;
    private const int MaxCoverBytes = 16 * 1024 * 1024;
    private static DeviceFiles Files => AppData.Files;

    public static LibrarySnapshot Scan()
    {
        var entries = new List<ThemeLibraryEntry>();
        var errors = new List<string>();
        int covers = 0;
        void Add(string path, bool bundled)
        {
            if (entries.Count >= MaxPacks) return;
            try
            {
                long size = Files.Length(path);
                if (size <= 0 || size > ThemeArchive.MaxArchiveBytes)
                    throw new ThemePackException("Pack exceeds the file size limit.");
                var entry = ThemeLibraryEntry.Inspect(path, Files.ReadAllBytes(path)) with { Bundled = bundled };
                if (covers + (entry.Preview?.Length ?? 0) > MaxCoverBytes) entry = entry with { Preview = null };
                covers += entry.Preview?.Length ?? 0;
                entries.Add(entry);
            }
            catch (Exception ex) { if (errors.Count < 8) errors.Add(path.Split('/').Last() + ": " + ex.Message); }
        }
        if (FileSystem.Exists(Bundle)) Add(Bundle, true);
        IReadOnlyList<string> names;
        try { names = Files.ListFiles(Root); }
        catch (Exception ex) { names = []; errors.Add("Can’t read the library: " + ex.Message); }
        foreach (string name in names.Where(IsPack).OrderBy(n => n, StringComparer.Ordinal))
            Add(Root + "/" + name, false);
        entries.Sort((a, b) => string.Compare(a.Manifest.Name, b.Manifest.Name, StringComparison.OrdinalIgnoreCase));
        string message = errors.Count > 0 ? string.Join(" | ", errors) : entries.Count >= MaxPacks ?
            "Library shows the first 128 valid packs." : "Packs checked. Ready to preview.";
        return new LibrarySnapshot(entries, message, RecordAdded(entries));
    }

    /// <summary>
    /// Where a USB drive holds original system files: by logical path, the same tree as the app's own backups
    /// (<see cref="ThemeApplier.BackupFolder"/>) on the same firmware.
    /// </summary>
    public const string OriginalsFolder = "prospero-themes/originals";

    /// <summary>
    /// Copies the theme packs on a connected USB drive into the library, its avatar packs (<c>.p5avatar</c>) into the app's
    /// avatars (<see cref="AvatarLibrary"/>), and the original system files in <see cref="OriginalsFolder"/> that
    /// <paramref name="stock"/> knows into the backups (<see cref="ImportOriginals"/>).
    /// </summary>
    public static LibrarySnapshot ImportUsb(StockCatalog stock)
    {
        int imported = 0, existing = 0, rejected = 0, found = 0, avatars = 0;
        string? lastError = null;
        Files.CreateDirectoryRecursive(Root);
        for (int usb = 0; usb < 8; usb++)
        {
            foreach (string directory in new[] { $"/mnt/usb{usb}/prospero-themes", $"/mnt/usb{usb}" })
            {
                if (!FileSystem.TryEnumerateDirectory(directory, out var files, out _)) continue;
                foreach (var file in files)
                {
                    bool avatar = AvatarLibrary.IsPack(file.Name);
                    if (!file.IsFile || !avatar && !IsPack(file.Name)) continue;
                    if (++found > MaxPacks) { lastError = "Maximum of 128 packs per import."; break; }
                    try
                    {
                        string path = directory.TrimEnd('/') + "/" + file.Name;
                        long size = FileSystem.GetFileSize(path);
                        if (avatar)
                        {
                            if (size <= 0 || size > AvatarPack.ArchiveCap) throw new ThemePackException("Avatar too large (1 MiB at most).");
                            if (AvatarLibrary.Add(Files, FileSystem.ReadAllBytes(path))) avatars++;
                            else existing++;
                            continue;
                        }
                        if (size <= 0 || size > ThemeArchive.MaxArchiveBytes)
                            throw new ThemePackException("Pack too large (48 MiB at most).");
                        if (AddToLibrary(FileSystem.ReadAllBytes(path), path)) imported++;
                        else existing++;
                    }
                    catch (Exception ex) { rejected++; lastError = file.Name + ": " + ex.Message; }
                }
                if (found > MaxPacks) break;
            }
            if (found > MaxPacks) break;
        }
        string? originals = ImportOriginals(stock);
        var snapshot = Scan();
        string result = found == 0 ? originals is null ? "No theme packs or avatars found on a connected USB drive." : "" :
            $"{imported} imported · {existing} already in library · {rejected} skipped";
        if (avatars > 0) result += avatars == 1 ? " · 1 avatar imported" : $" · {avatars} avatars imported";
        if (originals is not null) result += (result.Length > 0 ? " · " : "") + originals;
        if (lastError is not null) result += " · " + lastError;
        return snapshot with { Message = result };
    }

    /// <summary>
    /// Takes the files in <see cref="OriginalsFolder"/> on the first USB drive that has one as the backups of the console's
    /// files, through <see cref="ThemeApplier.ImportOriginal"/>: only a file <paramref name="stock"/> lists, with its size and
    /// SHA-256, so nothing but this firmware's own file ever becomes an original. Restore defaults then puts them back.
    /// Files are read one at a time, a large one outside the managed heap. Null when no drive has the folder.
    /// </summary>
    private static string? ImportOriginals(StockCatalog stock)
    {
        string? folder = null;
        for (int usb = 0; usb < 8 && folder is null; usb++)
            if (FileSystem.Exists($"/mnt/usb{usb}/{OriginalsFolder}")) folder = $"/mnt/usb{usb}/{OriginalsFolder}";
        if (folder is null) return null;
        if (stock.Count == 0) return "Originals skipped: the app has no catalog of this firmware to check them against.";
        var applier = new ThemeApplier(Files, HelperDeviceFiles.UnixSeconds) { Stock = stock };
        int imported = 0, there = 0, wrong = 0;
        string? error = null;
        foreach (string logical in stock.Paths)
        {
            string path = folder + "/" + logical;
            if (!FileSystem.Exists(path)) continue;
            try
            {
                long size = FileSystem.GetFileSize(path), expected = stock.StockBytes(logical);
                if (expected > 0 && size != expected) { wrong++; continue; }
                using FileBytes data = LargeFile.Read(path);
                switch (applier.ImportOriginal(logical, data.Span))
                {
                    case ThemeApplier.OriginalImport.Imported: imported++; break;
                    case ThemeApplier.OriginalImport.AlreadyThere: there++; break;
                    default: wrong++; break;
                }
            }
            catch (Exception ex) { error ??= logical.Split('/').Last() + ": " + ex.Message; }
        }
        if (imported + there + wrong == 0 && error is null) return "No originals of this firmware in " + OriginalsFolder + ".";
        string result = $"{imported} original(s) imported";
        if (there > 0) result += $" · {there} already there";
        if (wrong > 0) result += $" · {wrong} not this firmware’s original";
        if (error is not null) result += " · " + error;
        return result;
    }

    /// <summary>
    /// Checks a pack and stores it in the library under its content hash. Returns false when that exact
    /// pack is already there. Shared by the USB import and the community download.
    /// </summary>
    /// <exception cref="ThemePackException">The pack is invalid, or the copy failed its integrity check.</exception>
    public static bool AddToLibrary(byte[] bytes, string label)
    {
        if (bytes.Length <= 0 || bytes.Length > ThemeArchive.MaxArchiveBytes)
            throw new ThemePackException("Pack too large (48 MiB at most).");
        ThemeLibraryEntry.Inspect(label, bytes);
        Files.CreateDirectoryRecursive(Root);
        string digest = ThemePack.Hash(bytes);
        string destination = Root + "/" + digest + ".p5theme";
        if (Files.Exists(destination))
        {
            if (Files.Length(destination) == bytes.Length && Files.Hash(destination) == digest)
                return false;
            throw new ThemePackException("Existing copy is damaged; import skipped.");
        }
        string temporary = destination + ".tmp";
        Files.WriteAllBytes(temporary, bytes);
        if (Files.Length(temporary) != bytes.Length || Files.Hash(temporary) != digest)
            throw new ThemePackException("The copy failed the integrity check.");
        Files.Move(temporary, destination);
        return true;
    }

    /// <summary>Where an older version of a community theme goes when its update is downloaded.</summary>
    public const string Attic = "/data/prospero-themes/attic";

    /// <summary>
    /// Moves the pack named after <paramref name="digest"/> out of the library into <see cref="Attic"/>; it is
    /// kept there, not deleted. Returns false when the library does not hold it.
    /// </summary>
    public static bool Retire(string digest)
    {
        string source = Root + "/" + digest + ".p5theme";
        if (!Files.Exists(source)) return false;
        Files.CreateDirectoryRecursive(Attic);
        string target = Attic + "/" + digest + ".p5theme";
        for (int n = 2; Files.Exists(target); n++) target = Attic + "/" + digest + "-" + n + ".p5theme";
        Files.Move(source, target);
        return true;
    }

    /// <summary>
    /// Takes a pack out of the library: it is moved into <see cref="Attic"/> under its own file name, not deleted.
    /// Only files directly in the library qualify, never the theme that ships with the app.
    /// </summary>
    public static LibrarySnapshot Remove(string path, string name)
    {
        string file = path[(path.LastIndexOf('/') + 1)..];
        if (path != Root + "/" + file || !IsPack(file))
            throw new InvalidOperationException("Only themes in the library can be removed.");
        Files.CreateDirectoryRecursive(Attic);
        string stem = file[..file.LastIndexOf('.')], extension = file[file.LastIndexOf('.')..];
        string target = Attic + "/" + file;
        for (int n = 2; Files.Exists(target); n++) target = Attic + "/" + stem + "-" + n + extension;
        Files.Move(path, target);
        return Scan() with { Message = "Removed “" + name + "” from your library." };
    }

    /// <summary>
    /// When each library pack came in: read from <see cref="LibraryOrder.AddedPath"/>, with the time of this scan for the
    /// packs it does not know yet (so packs from before the file existed start in the order of their first scan).
    /// </summary>
    private static Dictionary<string, long> RecordAdded(List<ThemeLibraryEntry> entries)
    {
        Dictionary<string, long> added;
        try { added = LibraryOrder.ParseAdded(Files.Exists(LibraryOrder.AddedPath) ? Files.ReadAllText(LibraryOrder.AddedPath) : null); }
        catch { added = new Dictionary<string, long>(StringComparer.Ordinal); }
        if (LibraryOrder.Record(added, entries, HelperDeviceFiles.UnixSeconds()))
        {
            try { Files.WriteAllText(LibraryOrder.AddedPath, LibraryOrder.SerializeAdded(added)); }
            catch { /* the order by date lasts until the app quits */ }
        }
        return added;
    }

    private static bool IsPack(string name) => name.EndsWith(".p5theme", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
}
