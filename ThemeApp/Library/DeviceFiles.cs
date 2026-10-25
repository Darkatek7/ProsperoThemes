using System;
using System.Collections.Generic;
using System.IO;
using SharpProspero.Interop;
using SharpProspero.Memory;
using SharpProspero.Security;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// Production <see cref="IDeviceFiles"/> over SharpProspero: the app's own file access, with the app's folder
/// (<see cref="AppData.Root"/>) going through ThemeHelper where the app cannot reach /data (see <see cref="AppData"/>).
/// Kept out of ThemeApplier.cs so the PC tests never reference the device SDK.
/// </summary>
public sealed class DeviceFiles : IDeviceFiles
{
    public bool Exists(string path) =>
        AppData.Route(path, () => FileSystem.Exists(path), () => ThemeHelperClient.Exists(path));

    public long Length(string path) => AppData.Route(path,
        () => FileSystem.Exists(path) ? Folders.Named(path, () => FileSystem.GetFileSize(path)) : -1,
        () => ThemeHelperClient.Size(path));

    public byte[] ReadAllBytes(string path) => AppData.Route(path,
        () => Folders.Named(path, () => FileSystem.ReadAllBytes(path)), () => ThemeHelperClient.Read(path));

    public FileBytes ReadFile(string path) => AppData.Route(path,
        () => Folders.Named(path, () => LargeFile.Read(path)), () => ThemeHelperClient.ReadFile(path));

    public string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path));

    public string Hash(string path) => AppData.Route(path,
        () => Folders.Named(path, () => Sha256.HashFileHex(path)), () => ThemeHelperClient.Hash(path));

    public void WriteAllBytes(string path, byte[] data) => AppData.Route(path,
        () => Folders.Named(path, () => FileSystem.WriteAllBytes(path, data)), () => ThemeHelperClient.Write(path, data));

    public unsafe void WriteFile(string path, ReadOnlySpan<byte> data)
    {
        // A lambda cannot hold a span, so the routes get its address: pinned here, or memory outside the heap anyway.
        fixed (byte* start = data)
        {
            nint address = (nint)start;
            int length = data.Length;
            AppData.Route(path,
                () => Folders.Named(path, () => FileSystem.WriteAllBytes(path, new ReadOnlySpan<byte>((void*)address, length))),
                () => ThemeHelperClient.Write(path, new ReadOnlySpan<byte>((void*)address, length)));
        }
    }

    public void WriteAllText(string path, string text) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    public void Move(string from, string to) => AppData.Route(from,
        () => Folders.Named(from, () => FileSystem.Move(from, to)), () => ThemeHelperClient.Rename(from, to));

    public void DeleteFile(string path) => AppData.Route(path,
        () => Folders.Named(path, () => FileSystem.DeleteFile(path)), () => ThemeHelperClient.Unlink(path));

    public void CreateDirectoryRecursive(string path) => AppData.Route(path,
        () => Folders.Ensure(path), () => ThemeHelperClient.MakeDirectories(path));

    public void RemountSystemExRw() => SharpProspero.Platform.MountService.RemountSystemExRw();

    public IReadOnlyList<string> ListDirectories(string path) => List(path).Folders;

    /// <summary>The names of the files in a folder; empty when it does not exist.</summary>
    public IReadOnlyList<string> ListFiles(string path) => List(path).Files;

    private static (IReadOnlyList<string> Folders, IReadOnlyList<string> Files) List(string path) => AppData.Route(path, () =>
    {
        var folders = new List<string>();
        var files = new List<string>();
        if (FileSystem.TryEnumerateDirectory(path, out var entries, out _))
            foreach (var e in entries)
            {
                if (e.IsDirectory) folders.Add(e.Name);
                else if (e.IsFile) files.Add(e.Name);
            }
        return ((IReadOnlyList<string>)folders, (IReadOnlyList<string>)files);
    }, () => ThemeHelperClient.ListEntries(path));
}

/// <summary>
/// The app's own folder. The app reads and writes it itself, but some jailbreaks start it without /data in its
/// sandbox (10.01 with OnionHEN: creating the folder answers "no such entry", and /data itself "permission
/// denied", 0x8002000D; 13.00 did the same once after the app was suspended). Then ThemeHelper, which may reach
/// /data, keeps the folder for it, from that moment on. <see cref="Probe"/> decides at start; a later failure of
/// the app's own access switches over too, when the folder has become unreachable.
/// </summary>
public static class AppData
{
    public const string Root = "/data/prospero-themes";

    /// <summary>When this file exists, the app uses the helper for its folder anyway: the way to test that path on a
    /// console where the app reaches /data.</summary>
    public const string ForceHelperMarker = Root + "/use-helper";

    /// <summary>File access for the app's folder (and any other path, which always uses the app's own access).</summary>
    public static readonly DeviceFiles Files = new();

    private static readonly object Gate = new();
    private static volatile bool _viaHelper;

    /// <summary>True once the app's folder goes through ThemeHelper.</summary>
    public static bool ViaHelper => _viaHelper;

    public static bool Owns(string path) =>
        path == Root || path.StartsWith(Root + "/", StringComparison.Ordinal);

    /// <summary>
    /// Makes sure the app's folder exists, through the helper when the app cannot create it. Returns null when the
    /// folder is usable, otherwise why not. Blocks while the helper starts; the app calls it once at start.
    /// </summary>
    public static string? Probe()
    {
        try
        {
            if (!_viaHelper)
            {
                Folders.Ensure(Root);
                if (!FileSystem.Exists(ForceHelperMarker)) return null;
                string? problem = ThemeHelperClient.EnsureRunning();
                if (problem is not null) return problem;
                _viaHelper = true;
            }
        }
        catch (IOException local)
        {
            return SwitchToHelper(out string? problem) ? null : $"{local.Message}. {problem}";
        }
        try { ThemeHelperClient.MakeDirectories(Root); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Runs <paramref name="local"/> for a path, or <paramref name="helper"/> when it is in the app's folder and that
    /// goes through the helper. A failure of the app's own access to its folder switches to the helper when the
    /// folder itself has become unreachable; any other failure is the caller's.
    /// </summary>
    internal static T Route<T>(string path, Func<T> local, Func<T> helper)
    {
        if (!Owns(path)) return local();
        if (!_viaHelper)
        {
            try { return local(); }
            catch (Exception ex) when (ex is IOException or ProsperoException)
            {
                if (!SwitchToHelper(out _)) throw;
            }
        }
        return helper();
    }

    internal static void Route(string path, Action local, Action helper) =>
        Route(path, () => { local(); return 0; }, () => { helper(); return 0; });

    /// <summary>
    /// Switches to the helper when the app cannot create its folder (any more) and the helper runs and can. False
    /// when the app's own access still works, or the helper cannot take over; <paramref name="problem"/> says why.
    /// </summary>
    private static bool SwitchToHelper(out string? problem)
    {
        lock (Gate)
        {
            problem = null;
            if (_viaHelper) return true;
            try { Folders.Ensure(Root); return false; }
            catch (IOException) { /* the folder is out of the app's reach */ }
            problem = ThemeHelperClient.EnsureRunning();
            if (problem is not null) return false;
            try { ThemeHelperClient.MakeDirectories(Root); }
            catch (Exception ex) { problem = ex.Message; return false; }
            _viaHelper = true;
            return true;
        }
    }
}

/// <summary>
/// <see cref="FileBytes"/> on the console: a file of <see cref="Threshold"/> or more is held in flexible memory, outside
/// the managed heap. Measured on 13.00 (2026-10-25): the app has about 1.6 GiB of flexible memory free and a 256 MiB heap,
/// of which two ~100 MB arrays and an apply left 233 MiB committed; the icon container of firmware before 13.00 (about
/// 100 MB) did not fit in the heap twice in a row, and with other memory in use not even once (issue #13).
/// </summary>
public static class LargeFile
{
    public const int Threshold = 16 * 1024 * 1024;

    /// <summary>Memory for <paramref name="length"/> bytes: a managed array below <see cref="Threshold"/>.</summary>
    public static FileBytes Allocate(int length) => length < Threshold ? FileBytes.Of(new byte[length]) : new Region(length);

    /// <summary>A whole file the app reaches itself.</summary>
    public static FileBytes Read(string path)
    {
        long size = FileSystem.GetFileSize(path);
        if (size < Threshold) return FileBytes.Of(FileSystem.ReadAllBytes(path));
        if (size > int.MaxValue) throw new IOException($"{path} is too large to read whole ({size} bytes)");
        using DeviceFileStream stream = FileSystem.OpenRead(path);
        FileBytes data = Allocate((int)size);
        try
        {
            stream.ReadExactlyAt(0, data.Span);
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private sealed unsafe class Region(int length) : FileBytes
    {
        private readonly FlexibleMemoryRegion _region = FlexibleMemoryRegion.Allocate((nuint)length);

        public override Span<byte> Span => new(_region.Pointer, length);

        public override void Dispose() => _region.Dispose();
    }
}

/// <summary>Folder creation and error messages that name the file.</summary>
public static class Folders
{
    private const int AlreadyExists = unchecked((int)0x80020011), NoSuchEntry = unchecked((int)0x80020002);

    /// <summary>
    /// Creates <paramref name="path"/> and the folders above it that are missing. It asks mkdir itself instead of
    /// checking first: FileSystem.Exists (sceKernelCheckReachability) does not reliably report a folder as there,
    /// and the SDK's walk from the top then calls mkdir on /data, which the app's sandbox refuses with "permission
    /// denied" (0x8002000D, seen on 12.20 and on 13.00 after the app was suspended). "Already exists" counts as done,
    /// and only "no such entry" goes one folder up, so a folder that exists is never asked for its parents.
    /// </summary>
    public static void Ensure(string path)
    {
        string folder = path.TrimEnd('/');
        if (folder.Length == 0) return;
        int code = Mkdir(folder);
        if (code == NoSuchEntry && Parent(folder) is { Length: > 0 } parent)
        {
            Ensure(parent);
            code = Mkdir(folder);
        }
        if (code < 0 && code != AlreadyExists)
            throw new IOException($"sceKernelMkdir failed (0x{unchecked((uint)code):X8}) for {folder}");
    }

    private static unsafe int Mkdir(string folder)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(folder + "\0");
        fixed (byte* p = name)
            return SharpProspero.Interop.Kernel.KernelFile.sceKernelMkdir(p, 0x1FF);
    }

    /// <summary>Runs <paramref name="action"/>; an SDK error names <paramref name="path"/>.</summary>
    public static void Named(string path, Action action) => Named(path, () => { action(); return 0; });

    /// <inheritdoc cref="Named(string, Action)"/>
    public static T Named<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ProsperoException ex)
        {
            throw new IOException($"{Short(ex)} for {path}", ex);
        }
    }

    private static string Short(Exception ex) => ex.Message.TrimEnd('.');

    private static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "" : path[..slash];
    }
}
