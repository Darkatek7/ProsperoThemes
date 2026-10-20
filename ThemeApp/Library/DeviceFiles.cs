using System;
using System.Collections.Generic;
using System.IO;
using SharpProspero.Interop;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>Production <see cref="IDeviceFiles"/> over SharpProspero. Kept out of ThemeApplier.cs so the PC tests never reference the device SDK.</summary>
public sealed class DeviceFiles : IDeviceFiles
{
    public bool Exists(string path) => FileSystem.Exists(path);

    public byte[] ReadAllBytes(string path) => Folders.Named(path, () => FileSystem.ReadAllBytes(path));

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
    /// <summary>
    /// Creates <paramref name="path"/> and the folders above it that are missing. Starts below the deepest folder
    /// that exists, so /data itself is never touched: some firmware answers mkdir on /data with "permission denied"
    /// (0x8002000D) inside the app's sandbox, where the SDK's walk from the top would stop.
    /// </summary>
    public static void Ensure(string path)
    {
        string trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0 || FileSystem.Exists(trimmed)) return;
        var missing = new Stack<string>();
        for (string current = trimmed; current.Length > 0 && !FileSystem.Exists(current); current = Parent(current))
            missing.Push(current);
        while (missing.Count > 0)
        {
            string folder = missing.Pop();
            try
            {
                FileSystem.CreateDirectory(folder);
            }
            catch (ProsperoException ex) when (!FileSystem.Exists(folder))
            {
                throw new IOException($"{Short(ex)} for {folder}", ex);
            }
            catch (ProsperoException)
            {
                // Made meanwhile, or the answer was not "created" although the folder is there: fine either way.
            }
        }
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
