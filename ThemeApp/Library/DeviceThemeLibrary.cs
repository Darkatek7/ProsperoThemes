using System;
using System.Collections.Generic;
using System.Linq;
using SharpProspero.Storage;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

public sealed record LibrarySnapshot(IReadOnlyList<ThemeLibraryEntry> Entries, string Message);

/// <summary>Device file access, called on a worker rather than the frame thread.</summary>
public static class DeviceThemeLibrary
{
    public const string Root = "/data/prospero-themes/library";
    public const string AssetRoot = "/app0/sce_sys/prospero-themes";
    public const string Bundle = AssetRoot + "/blue-hour.p5theme";
    private const int MaxPacks = 128;
    private const int MaxCoverBytes = 16 * 1024 * 1024;

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
                long size = FileSystem.GetFileSize(path);
                if (size <= 0 || size > ThemeArchive.MaxArchiveBytes)
                    throw new ThemePackException("Pack exceeds the file size limit.");
                var entry = ThemeLibraryEntry.Inspect(path, FileSystem.ReadAllBytes(path)) with { Bundled = bundled };
                if (covers + (entry.Preview?.Length ?? 0) > MaxCoverBytes) entry = entry with { Preview = null };
                covers += entry.Preview?.Length ?? 0;
                entries.Add(entry);
            }
            catch (Exception ex) { if (errors.Count < 8) errors.Add(path.Split('/').Last() + ": " + ex.Message); }
        }
        if (FileSystem.Exists(Bundle)) Add(Bundle, true);
        if (FileSystem.TryEnumerateDirectory(Root, out var files, out int error))
            foreach (var file in files.Where(f => f.IsFile && IsPack(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
                Add(Root + "/" + file.Name, false);
        else if (error != 0 && FileSystem.Exists(Root)) errors.Add($"Can’t read the library (0x{error:X8}).");
        entries.Sort((a, b) => string.Compare(a.Manifest.Name, b.Manifest.Name, StringComparison.OrdinalIgnoreCase));
        string message = errors.Count > 0 ? string.Join(" | ", errors) : entries.Count >= MaxPacks ?
            "Library shows the first 128 valid packs." : "Packs checked. Ready to preview.";
        return new LibrarySnapshot(entries, message);
    }

    public static LibrarySnapshot ImportUsb()
    {
        int imported = 0, existing = 0, rejected = 0, found = 0;
        string? lastError = null;
        Folders.Ensure(Root);
        for (int usb = 0; usb < 8; usb++)
        {
            foreach (string directory in new[] { $"/mnt/usb{usb}/prospero-themes", $"/mnt/usb{usb}" })
            {
                if (!FileSystem.TryEnumerateDirectory(directory, out var files, out _)) continue;
                foreach (var file in files)
                {
                    if (!file.IsFile || !IsPack(file.Name)) continue;
                    if (++found > MaxPacks) { lastError = "Maximum of 128 packs per import."; break; }
                    try
                    {
                        string path = directory.TrimEnd('/') + "/" + file.Name;
                        long size = FileSystem.GetFileSize(path);
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
        var snapshot = Scan();
        string result = found == 0 ? "No theme packs found on a connected USB drive." :
            $"{imported} imported · {existing} already in library · {rejected} skipped";
        if (lastError is not null) result += " · " + lastError;
        return snapshot with { Message = result };
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
        Folders.Ensure(Root);
        string digest = ThemePack.Hash(bytes);
        string destination = Root + "/" + digest + ".p5theme";
        if (FileSystem.Exists(destination))
        {
            if (FileSystem.GetFileSize(destination) == bytes.Length && ThemePack.Hash(FileSystem.ReadAllBytes(destination)) == digest)
                return false;
            throw new ThemePackException("Existing copy is damaged; import skipped.");
        }
        string temporary = destination + ".tmp";
        Folders.Named(temporary, () => FileSystem.WriteAllBytes(temporary, bytes));
        if (FileSystem.GetFileSize(temporary) != bytes.Length || ThemePack.Hash(FileSystem.ReadAllBytes(temporary)) != digest)
            throw new ThemePackException("The copy failed the integrity check.");
        Folders.Named(destination, () => FileSystem.Move(temporary, destination));
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
        if (!FileSystem.Exists(source)) return false;
        Folders.Ensure(Attic);
        string target = Attic + "/" + digest + ".p5theme";
        for (int n = 2; FileSystem.Exists(target); n++) target = Attic + "/" + digest + "-" + n + ".p5theme";
        Folders.Named(target, () => FileSystem.Move(source, target));
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
        Folders.Ensure(Attic);
        string stem = file[..file.LastIndexOf('.')], extension = file[file.LastIndexOf('.')..];
        string target = Attic + "/" + file;
        for (int n = 2; FileSystem.Exists(target); n++) target = Attic + "/" + stem + "-" + n + extension;
        Folders.Named(target, () => FileSystem.Move(path, target));
        return Scan() with { Message = "Removed “" + name + "” from your library." };
    }

    private static bool IsPack(string name) => name.EndsWith(".p5theme", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
}
