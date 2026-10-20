using System;
using System.Collections.Generic;
using System.IO;
using SharpProspero.Interop;
using SharpProspero.Security;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>Production <see cref="IDeviceFiles"/> over SharpProspero. Kept out of ThemeApplier.cs so the PC tests never reference the device SDK.</summary>
public sealed class DeviceFiles : IDeviceFiles
{
    public bool Exists(string path) => FileSystem.Exists(path);

    public long Length(string path) => FileSystem.Exists(path) ? Folders.Named(path, () => FileSystem.GetFileSize(path)) : -1;

    public byte[] ReadAllBytes(string path) => Folders.Named(path, () => FileSystem.ReadAllBytes(path));

    public string Hash(string path) => Folders.Named(path, () => Sha256.HashFileHex(path));

    public void WriteAllBytes(string path, byte[] data) => Folders.Named(path, () => FileSystem.WriteAllBytes(path, data));

    public void Move(string from, string to) => Folders.Named(from, () => FileSystem.Move(from, to));

    public void DeleteFile(string path) => Folders.Named(path, () => FileSystem.DeleteFile(path));

    public void CreateDirectoryRecursive(string path) => Folders.Ensure(path);

    public void RemountSystemExRw() => SharpProspero.Platform.MountService.RemountSystemExRw();

    public IReadOnlyList<string> ListDirectories(string path)
    {
        var names = new List<string>();
        if (FileSystem.TryEnumerateDirectory(path, out var entries, out _))
            foreach (var e in entries)
                if (e.IsDirectory) names.Add(e.Name);
        return names;
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
