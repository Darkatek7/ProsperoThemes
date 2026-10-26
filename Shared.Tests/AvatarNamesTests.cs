using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>The store's names for the app's avatars (<see cref="AvatarLibrary.NamesPath"/>): the pack keeps its own name.</summary>
public class AvatarNamesTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "avatar-house.p5avatar"));

    private static StoreAvatar Listed(string name, string? sha256) =>
        new("id-" + name, name, "someone", "1.0.0", "", 1000, 0, 0, null, "https://store.example/a.p5avatar", sha256);

    private static List<AvatarEntry> Entries(MemoryFiles files) =>
        AvatarLibrary.Read(files, Library(files)).Entries;

    private static List<string> Library(MemoryFiles files) =>
        files.Files.Keys.Where(k => k.StartsWith(AvatarLibrary.Root + "/")).Select(k => k[(AvatarLibrary.Root.Length + 1)..]).ToList();

    [Fact]
    public void A_pack_without_a_store_name_shows_its_own()
    {
        var files = new MemoryFiles();
        AvatarLibrary.Add(files, Fixture());
        Assert.Equal("House", Assert.Single(Entries(files)).Tile.Name);
        Assert.False(files.Exists(AvatarLibrary.NamesPath));
    }

    [Fact]
    public void The_store_name_replaces_the_pack_name_and_the_pack_stays_as_it_was()
    {
        var files = new MemoryFiles();
        byte[] pack = Fixture();
        AvatarLibrary.Add(files, pack);
        string digest = ThemePack.Hash(pack);

        Assert.True(AvatarLibrary.RememberNames(files, [Listed("Hollow Knight", digest.ToUpperInvariant())], Library(files)));
        Assert.Equal("Hollow Knight", Assert.Single(Entries(files)).Tile.Name);
        Assert.Equal(pack, files.Files[AvatarLibrary.Root + "/" + digest + ".p5avatar"]);
        Assert.DoesNotContain(files.Files.Keys, k => k.EndsWith(".tmp"));

        // The same name again writes nothing; a new name from the store replaces the old one.
        Assert.False(AvatarLibrary.RememberNames(files, [Listed("Hollow Knight", digest)], Library(files)));
        Assert.True(AvatarLibrary.RememberNames(files, [Listed("The Knight", digest)], Library(files)));
        Assert.Equal("The Knight", Assert.Single(Entries(files)).Tile.Name);
    }

    [Fact]
    public void Only_avatars_in_the_library_get_a_line()
    {
        var files = new MemoryFiles();
        AvatarLibrary.Add(files, Fixture());
        string other = new('a', 64);
        Assert.False(AvatarLibrary.RememberNames(files, [Listed("Not here", other), Listed("No digest", null)], Library(files)));
        Assert.False(files.Exists(AvatarLibrary.NamesPath));
    }

    [Fact]
    public void Names_are_one_line_each_and_bad_lines_are_skipped()
    {
        var files = new MemoryFiles();
        byte[] pack = Fixture();
        AvatarLibrary.Add(files, pack);
        string digest = ThemePack.Hash(pack);
        Assert.True(AvatarLibrary.RememberNames(files, [Listed("Two\nlines\tand " + new string('x', 200), digest)], Library(files)));
        string name = AvatarLibrary.ReadNames(files)[digest];
        Assert.StartsWith("Two lines and x", name);
        Assert.Equal(AvatarLibrary.MaxNameLength, name.Length);

        files.Files[AvatarLibrary.NamesPath] = Encoding.UTF8.GetBytes("garbage\nnot-a-digest Name\n" + digest + " \n" + digest + " Kept\n");
        Assert.Equal("Kept", Assert.Single(AvatarLibrary.ReadNames(files)).Value);
    }

    [Fact]
    public void A_broken_names_file_falls_back_to_the_pack_names()
    {
        var files = new MemoryFiles { Broken = AvatarLibrary.NamesPath };
        AvatarLibrary.Add(files, Fixture());
        files.Files[AvatarLibrary.NamesPath] = [1];
        var (entries, problem) = AvatarLibrary.Read(files, Library(files));
        Assert.Equal("House", Assert.Single(entries).Tile.Name);
        Assert.Null(problem);
    }

    private sealed class MemoryFiles : IDeviceFiles
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public string? Broken { get; init; }
        public bool Exists(string path) => Files.ContainsKey(path);
        public long Length(string path) => Files.TryGetValue(path, out byte[]? d) ? d.Length : -1;
        public byte[] ReadAllBytes(string path) => path == Broken ? throw new IOException("read failed") : Files[path];
        public string Hash(string path) => ThemePack.Hash(Files[path]);
        public void WriteAllBytes(string path, byte[] data) => Files[path] = data;
        public void Move(string from, string to) { Files[to] = Files[from]; Files.Remove(from); }
        public void DeleteFile(string path) => Files.Remove(path);
        public void CreateDirectoryRecursive(string path) { }
        public void RemountSystemExRw() { }
        public IReadOnlyList<string> ListDirectories(string path) => [];
    }
}
