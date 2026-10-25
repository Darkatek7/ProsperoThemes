using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class AvatarApplierTests
{
    private const int User = 0x1EA2F4D9;
    private const string Folder = "/system_data/priv/cache/profile/0x1EA2F4D9";

    private static AvatarPack Pack() =>
        AvatarPack.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "avatar-house.p5avatar")));

    [Fact]
    public void Folder_name_is_upper_case_hex()
    {
        Assert.Equal("0x1EA2F4D9", AvatarApplier.FolderName(User));
        Assert.Equal("0x0000000A", AvatarApplier.FolderName(10));
    }

    [Fact]
    public void Apply_on_an_offline_user_writes_the_four_files_and_an_empty_online_json()
    {
        var profile = new FakeProfile(("version.json", "{}"u8.ToArray()));
        var data = new MemoryFiles();
        AvatarResult result = AvatarApplier.Apply(Pack(), User, profile, data);

        Assert.True(result.Success, result.Message);
        foreach (int size in AvatarPack.Sizes) Assert.Equal(Pack().Image(size), profile.File(AvatarPack.FileName(size)));
        Assert.Equal("{}"u8.ToArray(), profile.File("online.json"));
        Assert.All(profile.Writes, w => Assert.EndsWith(".tmp", w));   // the helper only writes .tmp files
        AvatarApplier.State state = AvatarApplier.Current(User, data)!;
        Assert.Equal("fixture-house", state.AvatarId);
        Assert.True(state.CreatedOnline);
        Assert.Empty(state.BackedUp);
    }

    [Fact]
    public void An_existing_online_json_is_never_touched()
    {
        byte[] psn = "{\"firstName\":\"Ann\",\"lastName\":\"Lee\"}"u8.ToArray();
        var profile = new FakeProfile(("online.json", psn));
        var data = new MemoryFiles();
        Assert.True(AvatarApplier.Apply(Pack(), User, profile, data).Success);
        Assert.Equal(psn, profile.File("online.json"));
        Assert.True(AvatarApplier.Reset(User, profile, data).Success);
        Assert.Equal(psn, profile.File("online.json"));
    }

    [Fact]
    public void Reset_puts_back_the_console_picture_even_after_two_applies()
    {
        byte[] own = Enumerable.Repeat((byte)7, 4224).ToArray();
        var profile = new FakeProfile(("avatar64.dds", own), ("picture64.dds", own), ("version.json", "{}"u8.ToArray()));
        var data = new MemoryFiles();
        Assert.True(AvatarApplier.Apply(Pack(), User, profile, data).Success);
        Assert.True(AvatarApplier.Apply(Pack(), User, profile, data).Success);   // keeps the first backup

        AvatarResult reset = AvatarApplier.Reset(User, profile, data);
        Assert.True(reset.Success, reset.Message);
        Assert.Equal(own, profile.File("avatar64.dds"));
        Assert.Null(profile.File("avatar128.dds"));
        Assert.Null(profile.File("avatar440.dds"));
        Assert.Null(profile.File("online.json"));            // the apply created it, so the reset removes it
        Assert.Equal(own, profile.File("picture64.dds"));    // never the app's
        Assert.NotNull(profile.File("version.json"));
        Assert.Null(AvatarApplier.Current(User, data));
        Assert.Empty(data.Files);
    }

    [Fact]
    public void A_user_without_a_profile_folder_is_refused_before_anything_is_written()
    {
        var profile = new FakeProfile { FolderName = "0x11111111" };
        var data = new MemoryFiles();
        AvatarResult result = AvatarApplier.Apply(Pack(), User, profile, data);
        Assert.False(result.Success);
        Assert.Contains("0x1EA2F4D9", result.Message);
        Assert.Empty(profile.Writes);
        Assert.Empty(data.Files);
    }

    [Fact]
    public void A_failed_write_puts_the_old_picture_back()
    {
        byte[] own = Enumerable.Repeat((byte)7, 67728).ToArray();
        var profile = new FakeProfile(("avatar260.dds", own), ("online.json", "{}"u8.ToArray())) { FailWriteOf = "avatar440.dds.tmp" };
        var data = new MemoryFiles();
        AvatarResult result = AvatarApplier.Apply(Pack(), User, profile, data);
        Assert.False(result.Success);
        Assert.Contains("old picture is back", result.Message);
        Assert.Equal(own, profile.File("avatar260.dds"));
        Assert.Null(profile.File("avatar64.dds"));
        Assert.Null(AvatarApplier.Current(User, data));
    }

    [Fact]
    public void A_changed_state_file_can_only_name_the_avatar_files()
    {
        AvatarApplier.State state = AvatarApplier.ParseState(
            "{\"avatar\":\"x\",\"backed_up\":[\"avatar64.dds\",\"../../evil\",\"version.json\",\"online.json\"],\"created_online\":true}")!;
        Assert.Equal(new[] { "avatar64.dds" }, state.BackedUp);
    }

    /// <summary>One user's profile folder as ThemeHelper shows it.</summary>
    private sealed class FakeProfile : IAvatarFiles
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public FakeProfile(params (string Name, byte[] Data)[] files)
        {
            foreach (var (name, data) in files) _files[Folder + "/" + name] = data;
        }

        public string FolderName { get; init; } = "0x1EA2F4D9";
        public string? FailWriteOf { get; init; }
        public List<string> Writes { get; } = new();

        public byte[]? File(string name) => _files.TryGetValue(Folder + "/" + name, out byte[]? d) ? d : null;

        public IReadOnlyList<string> ProfileFolders() => [FolderName];

        public IReadOnlyList<string> FilesIn(string folder) =>
            _files.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).Select(k => k[(folder.Length + 1)..]).ToList();

        public byte[] Read(string path) => _files[path];

        public string Hash(string path) => ThemePack.Hash(_files[path]);

        public void Write(string path, byte[] data)
        {
            if (FailWriteOf is not null && path.EndsWith("/" + FailWriteOf, StringComparison.Ordinal)) throw new IOException("write failed");
            Writes.Add(path);
            _files[path] = data;
        }

        public void Rename(string from, string to)
        {
            _files[to] = _files[from];
            _files.Remove(from);
        }

        public void Delete(string path) => _files.Remove(path);
    }

    /// <summary>The app's own /data folder.</summary>
    private sealed class MemoryFiles : IDeviceFiles
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public bool Exists(string path) => Files.ContainsKey(path);
        public long Length(string path) => Files.TryGetValue(path, out byte[]? d) ? d.Length : -1;
        public byte[] ReadAllBytes(string path) => Files[path];
        public string Hash(string path) => ThemePack.Hash(Files[path]);
        public void WriteAllBytes(string path, byte[] data) => Files[path] = data;
        public void Move(string from, string to) { Files[to] = Files[from]; Files.Remove(from); }
        public void DeleteFile(string path) => Files.Remove(path);
        public void CreateDirectoryRecursive(string path) { }
        public void RemountSystemExRw() { }
        public IReadOnlyList<string> ListDirectories(string path) => [];
    }
}
