namespace ProsperoThemes.App.Library;

/// <summary>Production <see cref="IDeviceFiles"/> over SharpProspero. Kept out of ThemeApplier.cs so the PC tests never reference the device SDK.</summary>
public sealed class DeviceFiles : IDeviceFiles
{
    public bool Exists(string path) => SharpProspero.Storage.FileSystem.Exists(path);

    public byte[] ReadAllBytes(string path) => SharpProspero.Storage.FileSystem.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] data) => SharpProspero.Storage.FileSystem.WriteAllBytes(path, data);

    public void Move(string from, string to) => SharpProspero.Storage.FileSystem.Move(from, to);

    public void DeleteFile(string path) => SharpProspero.Storage.FileSystem.DeleteFile(path);

    public void CreateDirectoryRecursive(string path) => SharpProspero.Storage.FileSystem.CreateDirectoryRecursive(path);

    public void RemountSystemExRw() => SharpProspero.Platform.MountService.RemountSystemExRw();

    public System.Collections.Generic.IReadOnlyList<string> ListDirectories(string path)
    {
        var names = new System.Collections.Generic.List<string>();
        if (SharpProspero.Storage.FileSystem.TryEnumerateDirectory(path, out var entries, out _))
            foreach (var e in entries)
                if (e.IsDirectory) names.Add(e.Name);
        return names;
    }
}
