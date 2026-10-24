using System;
using System.Collections.Generic;
using System.Text;
using ProsperoThemes.Shared;
using SharpProspero.Compression;
using SharpProspero.Security;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>An update that cannot go on; the message says why in words for the user.</summary>
public sealed class AppInstallException(string message) : Exception(message);

/// <summary>
/// The console side of <see cref="AppInstall"/>: ShadowMountPlus, and file access to the install folder through ThemeHelper.
/// A fake in the tests, <c>ConsoleInstallHost</c> on the console.
/// </summary>
/// <remarks>
/// Every write goes through ThemeHelper, never through the app's own file access: the console refuses to start an app whose
/// eboot.bin or modules are not executable (a self-updater of another app wrote mode 0644 and got "Can't start the game or
/// app", errno 13), the helper sets mode 0777 on what it writes into the install folder (<see cref="HelperPaths.InstallFileMode"/>),
/// and the app's own file API creates files with mode 0666 and sets nothing else. One tested path also beats two: the app's
/// sandbox may not reach the folder at all (OnionHEN) or write slowly, and the helper is the one that always can.
/// </remarks>
public interface IInstallHost
{
    /// <summary>The body of ShadowMount's answer to the install question, or null when it does not answer.</summary>
    string? AskShadowMount();

    /// <summary>File access through ThemeHelper (v9 or later); throws <see cref="AppInstallException"/> when the helper cannot be used.</summary>
    IDeviceFiles HelperFiles();

    /// <summary>Asks ShadowMount to scan its folders again so the home screen shows the new version. Best effort: failures are ignored.</summary>
    void RequestScan();
}

/// <summary>Where the app is installed and the file access that writes there (ThemeHelper's).</summary>
public sealed record InstallTarget(string Folder, IDeviceFiles Files);

/// <summary>
/// Updating the app in place: find its install folder (ShadowMountPlus first, then ShadowMount's scan roots), check the
/// downloaded release ZIP, and write its files with the files that decide whether the old version still starts written
/// last. Plain logic over <see cref="IDeviceFiles"/>, so the PC tests cover it.
/// </summary>
public static class AppInstall
{
    public const string TitleId = HelperPaths.InstallTitleId;
    public const string TopFolder = TitleId + "/";
    public const string EbootEntry = "eboot.bin", ParamEntry = "sce_sys/param.json";
    /// <summary>The helper the app starts; when this file changes, the console needs a restart for the new one.</summary>
    public const string HelperEntry = "sce_sys/prospero-themes/ThemeHelper.elf";

    public const int MaxEntries = 256;
    public const long MaxFileBytes = 64L * 1024 * 1024, MaxExpandedBytes = 160L * 1024 * 1024;

    public const string RestartOnce = "Restart the console once, then try the update again.";
    public const string DiskImage = "Installed as a disk image: update it by hand.";

    public const int ShadowMountPort = 10101;
    public const string ShadowMountInfoPath = "/api/v1/games/info";
    public static string ShadowMountInfoBody => "{\"title_id\":\"" + TitleId + "\"}";
    public const string ShadowMountScanPath = "/api/v1/scan", ShadowMountScanBody = "{}";

    // ---- where ShadowMount looks ----------------------------------------------------------------------------------

    /// <summary>
    /// The folders ShadowMountPlus scans for games on its own, which are the places a folder install can be: the app is at
    /// <c>&lt;root&gt;/PPSA90210</c>. In ShadowMount's order of use. ThemeHelper (v9) accepts exactly these (<see cref="HelperPaths"/>).
    /// </summary>
    public static IReadOnlyList<string> ScanRoots { get; } = BuildScanRoots();

    private static string[] BuildScanRoots()
    {
        var roots = new List<string> { "/data/homebrew", "/data/etaHEN/games" };
        foreach (string drive in new[] { "/mnt/ext0", "/mnt/ext1" })
        {
            roots.Add(drive + "/homebrew");
            roots.Add(drive + "/etaHEN/games");
        }
        for (int i = 0; i < 8; i++) roots.Add($"/mnt/usb{i}/homebrew");
        for (int i = 0; i < 8; i++) roots.Add($"/mnt/usb{i}/etaHEN/games");
        for (int i = 0; i < 8; i++) roots.Add($"/mnt/usb{i}");
        roots.Add("/mnt/ext0");
        roots.Add("/mnt/ext1");
        return [.. roots];
    }

    /// <summary>The place the app would be in each scan root.</summary>
    public static IEnumerable<string> Candidates()
    {
        foreach (string root in ScanRoots) yield return root + "/" + TitleId;
    }

    /// <summary>
    /// Whether <paramref name="path"/> can be the app's install folder: absolute, normalised (no "//", no "." or ".." segment,
    /// no trailing slash, no backslash, no space or control character) and ending in "/PPSA90210".
    /// </summary>
    public static bool IsValidFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 200 || path[0] != '/' || !path.EndsWith("/" + TitleId, StringComparison.Ordinal))
            return false;
        foreach (char c in path)
            if (c < 33 || c > 126 || c == '\\') return false;
        foreach (string segment in path.Split('/', StringSplitOptions.None)[1..])
            if (segment.Length == 0 || segment == "." || segment.Contains("..", StringComparison.Ordinal)) return false;
        return true;
    }

    // ---- ShadowMountPlus ------------------------------------------------------------------------------------------

    public enum ShadowKind { Unavailable, Folder, Image }

    /// <summary>What ShadowMount's answer says about the install: a folder (with its path), a disk image, or nothing usable.</summary>
    public readonly record struct ShadowAnswer(ShadowKind Kind, string Path = "");

    /// <summary>
    /// Reads the answer of <c>POST /api/v1/games/info</c>. The folder is used only for <c>source_type</c> "folder", not image
    /// backed, with the source available, and a valid path. Its <c>version</c> is the registration's and can be stale, so it
    /// is not read at all.
    /// </summary>
    public static ShadowAnswer ParseShadowMount(string? json)
    {
        if (json is null || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Object) return default;
        if (root.ContainsKey("status") && root["status"].AsInt(-1) != 0) return default;
        if (root.ContainsKey("title_id") && root.GetString("title_id") != TitleId) return default;
        string type = root.GetString("source_type");
        if (type == "image" || root.GetBool("image_backed")) return new ShadowAnswer(ShadowKind.Image);
        if (type != "folder" || !root.GetBool("source_available")) return default;
        string path = root.GetString("path");
        return IsValidFolder(path) ? new ShadowAnswer(ShadowKind.Folder, path) : default;
    }

    /// <summary>A plain HTTP/1.1 POST with a JSON body, as ShadowMount's local API takes it.</summary>
    public static byte[] HttpPost(string host, int port, string path, string body)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        string head = $"POST {path} HTTP/1.1\r\nHost: {host}:{port}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
        byte[] header = Encoding.ASCII.GetBytes(head);
        var request = new byte[header.Length + content.Length];
        header.CopyTo(request, 0);
        content.CopyTo(request, header.Length);
        return request;
    }

    /// <summary>
    /// The body of a plain HTTP response read to the end of the connection, or null when it is no complete 200 answer
    /// (status line, headers, and a body as long as Content-Length or its chunks say).
    /// </summary>
    public static string? HttpBody(ReadOnlySpan<byte> response)
    {
        int split = response.IndexOf("\r\n\r\n"u8);
        if (split < 0) return null;
        string head = Encoding.ASCII.GetString(response[..split]);
        string[] lines = head.Split("\r\n");
        string[] status = lines[0].Split(' ');
        if (status.Length < 2 || !status[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || status[1] != "200") return null;
        long length = -1;
        bool chunked = false;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string name = lines[i][..colon].Trim().ToLowerInvariant(), value = lines[i][(colon + 1)..].Trim();
            if (name == "content-length" && long.TryParse(value, out long n) && n >= 0) length = n;
            else if (name == "transfer-encoding" && value.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
        }
        ReadOnlySpan<byte> body = response[(split + 4)..];
        if (chunked) return Unchunk(body);
        if (length >= 0)
            return body.Length >= length ? Encoding.UTF8.GetString(body[..(int)length]) : null;
        return Encoding.UTF8.GetString(body);
    }

    private static string? Unchunk(ReadOnlySpan<byte> body)
    {
        var output = new List<byte>();
        int at = 0;
        while (true)
        {
            int eol = body[at..].IndexOf("\r\n"u8);
            if (eol < 0) return null;
            string size = Encoding.ASCII.GetString(body.Slice(at, eol));
            int semicolon = size.IndexOf(';');
            if (semicolon >= 0) size = size[..semicolon];
            if (!int.TryParse(size.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int chunk) || chunk < 0) return null;
            at += eol + 2;
            if (chunk == 0) return Encoding.UTF8.GetString([.. output]);
            if (at + chunk > body.Length) return null;
            foreach (byte b in body.Slice(at, chunk)) output.Add(b);
            at += chunk + 2;
            if (at > body.Length) return null;
        }
    }

    // ---- finding the install folder -------------------------------------------------------------------------------

    /// <summary>
    /// Finds where the app is installed. ShadowMount's answer comes first; without a usable one the scan roots are looked at,
    /// and a candidate counts only when its param.json says PPSA90210 and the version that is running. A disk image, no match or
    /// more than one match is an <see cref="AppInstallException"/> that says so. The files are ThemeHelper's: an older helper,
    /// or none, ends the update with what to do.
    /// </summary>
    /// <param name="runningVersion">The <c>contentVersion</c> the app started with.</param>
    public static InstallTarget Locate(IInstallHost host, string runningVersion)
    {
        string? answerBody = null;
        try { answerBody = host.AskShadowMount(); }
        catch { /* ShadowMount is not there or does not answer: the scan roots decide */ }
        ShadowAnswer answer = ParseShadowMount(answerBody);
        if (answer.Kind == ShadowKind.Image) throw new AppInstallException(DiskImage);
        if (answer.Kind == ShadowKind.Folder && !HelperPaths.IsInstallFolder(answer.Path))
            throw new AppInstallException("This app can’t write to " + answer.Path + ". Update it by hand.");

        IDeviceFiles files = Guard(host.HelperFiles());
        if (answer.Kind == ShadowKind.Folder)
        {
            // ShadowMount says where; the folder is still checked to be this app's.
            ParamInfo? shadow = null;
            try { shadow = ReadParam(files, answer.Path); }
            catch (AppInstallException) { throw; }
            catch { /* unreadable: the scan roots decide */ }
            if (shadow is { } info && info.TitleId == TitleId) return new InstallTarget(answer.Path, files);
        }

        var found = new List<string>();
        Scan(files, runningVersion, found);
        if (found.Count == 1) return new InstallTarget(found[0], files);
        if (found.Count > 1)
            throw new AppInstallException("Prospero Themes is installed in more than one place (" + string.Join(", ", found) + "). Update it by hand.");
        throw new AppInstallException("Can’t find the folder this app is installed in. Update it where you installed it from.");
    }

    // Candidates whose param.json is this app at this version.
    private static void Scan(IDeviceFiles files, string runningVersion, List<string> found)
    {
        foreach (string folder in Candidates())
        {
            ParamInfo? info = null;
            try { info = ReadParam(files, folder); }
            catch (AppInstallException) { throw; }
            catch { /* not readable: not a candidate */ }
            if (info is { } p && p.TitleId == TitleId && p.Version == runningVersion) found.Add(folder);
        }
    }

    private readonly record struct ParamInfo(string TitleId, string Version);

    private static ParamInfo? ReadParam(IDeviceFiles files, string folder)
    {
        string path = folder + "/" + ParamEntry;
        if (!files.Exists(path)) return null;
        if (!JsonValue.TryParse(Encoding.UTF8.GetString(files.ReadAllBytes(path)), out JsonValue root)) return null;
        return new ParamInfo(root.GetString("titleId"), root.GetString("contentVersion"));
    }

    // A helper from before v9 refuses the install paths (ERR badpath); say what to do instead of failing obscurely.
    private static IDeviceFiles Guard(IDeviceFiles files) => new GuardedFiles(files);

    private sealed class GuardedFiles(IDeviceFiles inner) : IDeviceFiles
    {
        private static T Run<T>(Func<T> action)
        {
            try { return action(); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("badpath", StringComparison.Ordinal))
            {
                throw new AppInstallException(RestartOnce);
            }
        }

        public bool Exists(string path) => Run(() => inner.Exists(path));
        public long Length(string path) => Run(() => inner.Length(path));
        public byte[] ReadAllBytes(string path) => Run(() => inner.ReadAllBytes(path));
        public string Hash(string path) => Run(() => inner.Hash(path));
        public void WriteAllBytes(string path, byte[] data) => Run(() => { inner.WriteAllBytes(path, data); return 0; });
        public void Move(string from, string to) => Run(() => { inner.Move(from, to); return 0; });
        public void DeleteFile(string path) => Run(() => { inner.DeleteFile(path); return 0; });
        public void CreateDirectoryRecursive(string path) => Run(() => { inner.CreateDirectoryRecursive(path); return 0; });
        public void RemountSystemExRw() => throw new InvalidOperationException("an update does not remount");
        public IReadOnlyList<string> ListDirectories(string path) => Run(() => inner.ListDirectories(path));
    }

    // ---- the release ZIP ------------------------------------------------------------------------------------------

    /// <summary>A file of the release in the order it is written.</summary>
    public sealed record Item(string Relative, ZipEntry Entry);

    /// <summary>A checked release ZIP: its files, in write order, and the archive they are read from.</summary>
    public sealed class Package
    {
        internal Package(string version, ZipArchive zip, IReadOnlyList<Item> items)
        {
            Version = version;
            Zip = zip;
            Items = items;
        }

        /// <summary>The version the ZIP's param.json names (the one the store announced).</summary>
        public string Version { get; }
        internal ZipArchive Zip { get; }
        public IReadOnlyList<Item> Items { get; }
    }

    /// <summary>
    /// Checks a downloaded release ZIP and opens it: the size and SHA-256 the store announced, every entry name (nothing
    /// absolute, no "..", no backslash, nothing outside PPSA90210/), the limits, eboot.bin and sce_sys/param.json present, and the
    /// param.json naming PPSA90210 at <paramref name="version"/>. Nothing is written.
    /// </summary>
    public static Package Open(byte[] zipBytes, AppUpdate.ZipInfo expected, string version)
    {
        if (zipBytes.Length != expected.Bytes)
            throw new AppInstallException($"The download is {zipBytes.Length} bytes, not the {expected.Bytes} the store announced. Nothing was changed.");
        if (zipBytes.Length > AppUpdate.MaxZipBytes) throw new AppInstallException("The download is larger than 64 MiB. Nothing was changed.");
        if (!string.Equals(Sha256.HashHex(zipBytes), expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new AppInstallException("The download does not match its SHA-256. Nothing was changed.");

        ZipArchive zip;
        try { zip = ZipArchive.Open(zipBytes); }
        catch (Exception ex) { throw new AppInstallException("The download is not a readable ZIP: " + ex.Message + " Nothing was changed."); }
        if (zip.Entries.Count > MaxEntries) throw new AppInstallException("The ZIP has too many files. Nothing was changed.");

        var files = new List<Item>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (ZipEntry entry in zip.Entries)
        {
            string? relative = RelativePath(entry);
            if (relative is null) continue;   // a folder entry
            if (!seen.Add(relative)) throw new AppInstallException($"The ZIP names {relative} twice. Nothing was changed.");
            if (entry.Method is not (0 or 8)) throw new AppInstallException($"{relative} is compressed in a way this app can’t read. Nothing was changed.");
            if (entry.UncompressedSize < 0 || entry.UncompressedSize > MaxFileBytes)
                throw new AppInstallException($"{relative} is too large. Nothing was changed.");
            total += entry.UncompressedSize;
            if (total > MaxExpandedBytes) throw new AppInstallException("The ZIP expands to more than the update allows. Nothing was changed.");
            files.Add(new Item(relative, entry));
        }
        if (!seen.Contains(EbootEntry) || !seen.Contains(ParamEntry))
            throw new AppInstallException($"The ZIP has no {TopFolder}{EbootEntry} or no {TopFolder}{ParamEntry}. Nothing was changed.");

        Item param = files.Find(i => i.Relative == ParamEntry)!;
        string text;
        try { text = Encoding.UTF8.GetString(zip.Extract(param.Entry)); }
        catch (Exception ex) { throw new AppInstallException("param.json in the ZIP can’t be read: " + ex.Message + " Nothing was changed."); }
        if (!JsonValue.TryParse(text, out JsonValue root) || root.GetString("titleId") != TitleId)
            throw new AppInstallException("The ZIP is not Prospero Themes (" + ParamEntry + " does not name " + TitleId + "). Nothing was changed.");
        if (root.GetString("contentVersion") != version)
            throw new AppInstallException($"The ZIP holds version {root.GetString("contentVersion")}, not {version} as announced. Nothing was changed.");

        return new Package(version, zip, Order(files));
    }

    /// <summary>
    /// The path of an entry below PPSA90210/, or null for a folder entry. Throws for a name that is absolute, holds "..", a
    /// backslash, a space, a control or non-ASCII character, or lies outside PPSA90210/ (a zip-slip), or that ThemeHelper would refuse.
    /// </summary>
    public static string? RelativePath(ZipEntry entry)
    {
        string name = entry.Name;
        string shown = name.Length > 60 ? name[..60] + "…" : name;
        string bad = $"The ZIP holds a file this app won’t write (“{shown}”). Nothing was changed.";
        if (name.Length == 0 || name[0] == '/' || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)) throw new AppInstallException(bad);
        foreach (char c in name)
            if (c < 33 || c > 126) throw new AppInstallException(bad);
        if (!name.StartsWith(TopFolder, StringComparison.Ordinal)) throw new AppInstallException(bad);
        string relative = name[TopFolder.Length..];
        if (entry.IsDirectory || relative.EndsWith('/'))
        {
            relative = relative.TrimEnd('/');
            if (relative.Length > 0 && !HelperPaths.IsInstallRelative(relative)) throw new AppInstallException(bad);
            return null;
        }
        if (!HelperPaths.IsInstallRelative(relative)) throw new AppInstallException(bad);
        return relative;
    }

    /// <summary>
    /// Write order: everything but eboot.bin and sce_sys/param.json first (by name), then eboot.bin, then param.json. A
    /// half-done update then still starts the old version, whose param.json still says it is old, and is tried again.
    /// </summary>
    public static IReadOnlyList<Item> Order(IEnumerable<Item> items)
    {
        var rest = new List<Item>();
        Item? eboot = null, param = null;
        foreach (Item item in items)
        {
            if (item.Relative == EbootEntry) eboot = item;
            else if (item.Relative == ParamEntry) param = item;
            else rest.Add(item);
        }
        rest.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        if (eboot is not null) rest.Add(eboot);
        if (param is not null) rest.Add(param);
        return rest;
    }

    // ---- writing --------------------------------------------------------------------------------------------------

    /// <summary>What an install did: files written, files that were identical already, and whether ThemeHelper.elf is a new one.</summary>
    public sealed record InstallResult(int Written, int Unchanged, bool HelperChanged);

    /// <summary>
    /// Writes the package into the install folder: each file to a new "&lt;file&gt;.tmp" (the helper gives it mode 0777), renamed
    /// onto the file and read back (SHA-256); a file is never written in place, so the running program is not touched. A file
    /// that is identical to what is there is left alone. Files in the folder that the ZIP does not name stay. Stops at the
    /// first failure; because eboot.bin and param.json come last, the old version still starts then.
    /// </summary>
    public static InstallResult Write(Package package, InstallTarget target, IApplyProgress? progress = null)
    {
        IDeviceFiles files = target.Files;
        int written = 0, unchanged = 0;
        bool helperChanged = false;
        var folders = new HashSet<string>(StringComparer.Ordinal) { target.Folder };
        for (int i = 0; i < package.Items.Count; i++)
        {
            Item item = package.Items[i];
            progress?.Report("Installing", i + 1, package.Items.Count);
            string path = target.Folder + "/" + item.Relative, tmp = path + ".tmp";
            if (!HelperPaths.IsInstallPath(tmp))
                throw new AppInstallException($"ThemeHelper won’t write {item.Relative}. Update it by hand.");
            try
            {
                byte[] data = package.Zip.Extract(item.Entry);
                string hash = Sha256.HashHex(data);
                if (SameAlready(files, path, data.Length, hash))
                {
                    unchanged++;
                    continue;
                }
                int slash = path.LastIndexOf('/');
                string parent = path[..slash];
                if (folders.Add(parent)) files.CreateDirectoryRecursive(parent);
                files.WriteAllBytes(tmp, data);
                files.Move(tmp, path);
                if (files.Hash(path) != hash) throw new AppInstallException($"{item.Relative} did not read back as written.");
                written++;
                if (item.Relative == HelperEntry) helperChanged = true;
            }
            catch (Exception ex) { throw Stopped(item.Relative, ex.Message); }
        }
        return new InstallResult(written, unchanged, helperChanged);
    }

    private static AppInstallException Stopped(string relative, string reason) =>
        reason == RestartOnce
            ? new AppInstallException(RestartOnce)
            : new AppInstallException($"The update stopped at {relative}: {reason} Run it again; the old version still starts until the last file is in.");

    private static bool SameAlready(IDeviceFiles files, string path, int length, string hash)
    {
        try { return files.Exists(path) && files.Length(path) == length && files.Hash(path) == hash; }
        catch { return false; }
    }

    /// <summary>What to tell the user when the files are in.</summary>
    public static string DoneText(string version, InstallResult result) =>
        $"Prospero Themes {version} is installed. Close the app and start it again." +
        (result.HelperChanged ? " This version has a new theme helper: restart the console once as well." : "");
}
