using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;
using SharpProspero.Compression;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class ThemeApplierTests
{
    private const string Dev1 = "/system_ex/vsh_asset/bg_hub_default.dds.zst";
    private const string Dev2 = "/system_ex/vsh_asset/bg_hub_second.dds.zst";
    private const string Backup1 = "/data/prospero-themes/backup/original/vsh_asset/bg_hub_default.dds.zst";
    private const string Backup2 = "/data/prospero-themes/backup/original/vsh_asset/bg_hub_second.dds.zst";
    private const string StatePath = "/data/prospero-themes/state.json";
    private const string Path1 = "vsh_asset/bg_hub_default.dds.zst";
    private const string Path2 = "vsh_asset/bg_hub_second.dds.zst";

    private static readonly long FixedNow = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    // ---- helpers ----------------------------------------------------------

    private static byte[] Zst(string tag)
    {
        var data = Encoding.ASCII.GetBytes("payload-" + tag + new string('.', 256));
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD }.CopyTo(data, 0);
        return data;
    }

    private static byte[] Live(string tag) => Encoding.ASCII.GetBytes("live-" + tag);

    private static string Sha(byte[] data) => ThemePack.Hash(data);

    /// <summary>A valid format-2 pack; <paramref name="badHashFor"/> corrupts one manifest hash.</summary>
    private static ThemePack MakePack(string? badHashFor, params (string Path, byte[] Payload)[] targets)
    {
        var zip = new ZipBuilder();
        var entries = new List<string>();
        foreach (var (path, payload) in targets)
        {
            string file = "payloads/" + path;
            zip.Add(file, payload);
            string hash = path == badHashFor ? new string('0', 64) : Sha(payload);
            entries.Add($$"""
                {"path":"{{path}}","layer":"background","file":"{{file}}",
                 "bytes":{{payload.LongLength}},"sha256":"{{hash}}"}
                """);
        }

        string manifest = $$"""
            {"format":2,"id":"t-apply","name":"Test Pack","author":"tests","version":"1.0",
             "description":"applier tests","unmapped":"keep","targets":[{{string.Join(",", entries)}}]}
            """;
        zip.AddText("theme.json", manifest);
        return ThemePack.Open(zip.ToArray());
    }

    private static ThemePack SingleTargetPack() => MakePack(null, (Path1, Zst("one")));

    private static FakeDeviceFiles DeviceWithLive(params (string Device, byte[] Bytes)[] live)
    {
        var fake = new FakeDeviceFiles();
        foreach (var (device, bytes) in live)
            fake.Files[device] = bytes;
        return fake;
    }

    private static ThemeApplier Applier(FakeDeviceFiles fake) => new(fake, () => FixedNow);

    // ---- tests ------------------------------------------------------------

    [Fact]
    public void Apply_writes_payload_backs_up_live_file_and_records_state()
    {
        byte[] live = Live("one");
        byte[] payload = Zst("one");
        var fake = DeviceWithLive((Dev1, live));

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, payload)));

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { Dev1 }, result.Written);
        Assert.Equal(payload, fake.Files[Dev1]);
        Assert.Equal(live, fake.Files[Backup1]);
        Assert.Equal(1, fake.Remounts);

        string expected = """
            {
              "pack": {
                "id": "t-apply",
                "name": "Test Pack",
                "version": "1.0"
              },
              "action": "apply",
              "when_utc": "2026-10-13T12:00:00Z",
              "targets": [
                {
                  "path": "/system_ex/vsh_asset/bg_hub_default.dds.zst",
                  "backup": "/data/prospero-themes/backup/original/vsh_asset/bg_hub_default.dds.zst"
                }
              ]
            }
            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, Encoding.UTF8.GetString(fake.Files[StatePath]));
    }

    [Fact]
    public void RestoreOriginals_undoes_every_pack_applied_since_the_backup()
    {
        byte[] live1 = Live("one"), live2 = Live("two");
        var fake = DeviceWithLive((Dev1, live1), (Dev2, live2));
        ThemeApplier applier = Applier(fake);

        Assert.True(applier.Apply(MakePack(null, (Path1, Zst("a1")), (Path2, Zst("a2")))).Success);
        Assert.True(applier.Apply(MakePack(null, (Path1, Zst("b1")))).Success);

        ApplyResult result = applier.RestoreOriginals();

        Assert.True(result.Success, result.Message);
        Assert.Equal(live1, fake.Files[Dev1]);
        Assert.Equal(live2, fake.Files[Dev2]);
        Assert.Contains("\"action\": \"restore-originals\"", Encoding.UTF8.GetString(fake.Files[StatePath]));
    }

    [Fact]
    public void Apply_starts_from_the_defaults_so_themes_do_not_stack()
    {
        byte[] live1 = Live("one"), live2 = Live("two");
        var fake = DeviceWithLive((Dev1, live1), (Dev2, live2));
        ThemeApplier applier = Applier(fake);
        Assert.True(applier.Apply(MakePack(null, (Path1, Zst("a1")), (Path2, Zst("a2")))).Success);

        ApplyResult result = applier.Apply(MakePack(null, (Path1, Zst("b1"))));

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Put 1 earlier change(s) back to default.", result.Message);
        Assert.Equal(Zst("b1"), fake.Files[Dev1]);
        Assert.Equal(live2, fake.Files[Dev2]);
        Assert.Equal(2, fake.Remounts);   // one per apply, never a second remount inside one
    }

    [Fact]
    public void System_tiles_get_the_main_background_unless_the_pack_names_them()
    {
        const string gallery = "/system_ex/vsh_asset/bg_NPXS40056.dds.zst", allApps = "/system_ex/vsh_asset/bg_NPXS40139.dds.zst";
        var fake = DeviceWithLive((Dev1, Live("hub")), (gallery, Live("gallery")), (allApps, Live("all")));
        ThemeApplier applier = Applier(fake);

        ApplyResult result = applier.Apply(MakePack(null, (Path1, Zst("main")), ("vsh_asset/bg_NPXS40139.dds.zst", Zst("own"))));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Zst("main"), fake.Files[gallery]);
        Assert.Equal(Zst("own"), fake.Files[allApps]);
        Assert.False(fake.Files.ContainsKey("/system_ex/vsh_asset/bg_NPXS40071.dds.zst"));   // not on this console: skipped
        Assert.True(applier.RestoreOriginals().Success);
        Assert.Equal(Live("gallery"), fake.Files[gallery]);
    }

    [Fact]
    public void Files_already_at_their_default_are_not_rewritten()
    {
        var fake = DeviceWithLive((Dev1, Live("one")), (Dev2, Live("two")));
        ThemeApplier applier = Applier(fake);
        Assert.True(applier.Apply(MakePack(null, (Path1, Zst("a1")), (Path2, Zst("a2")))).Success);
        Assert.True(applier.RestoreOriginals().Success);
        fake.Writes.Clear();

        ApplyResult result = applier.Apply(MakePack(null, (Path1, Zst("b1"))));

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Applied and verified 1", result.Message);
        Assert.DoesNotContain(Dev2 + ".tmp", fake.Writes);
    }

    [Fact]
    public void A_default_that_cannot_be_put_back_stops_the_apply()
    {
        var fake = DeviceWithLive((Dev1, Live("one")), (Dev2, Live("two")));
        ThemeApplier applier = Applier(fake);
        Assert.True(applier.Apply(MakePack(null, (Path1, Zst("a1")), (Path2, Zst("a2")))).Success);
        fake.CorruptNextMoveTo = Dev2;

        ApplyResult result = applier.Apply(MakePack(null, (Path1, Zst("b1"))));

        Assert.False(result.Success);
        Assert.Contains("not applied", result.Message);
        Assert.Equal(Zst("a1"), fake.Files[Dev1]);
    }

    [Fact]
    public void Backup_made_before_the_index_is_restored_once_a_pack_touches_it()
    {
        byte[] original = Live("original");
        var fake = DeviceWithLive((Dev1, Live("modded")));
        fake.Files[Backup1] = original;   // left by an earlier tool, not in the index
        ThemeApplier applier = Applier(fake);

        Assert.True(applier.Apply(SingleTargetPack()).Success);
        ApplyResult result = applier.RestoreOriginals();

        Assert.True(result.Success, result.Message);
        Assert.Equal(original, fake.Files[Dev1]);
    }

    [Fact]
    public void RestoreOriginals_without_backups_writes_nothing()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));

        ApplyResult result = Applier(fake).RestoreOriginals();

        Assert.False(result.Success);
        Assert.Empty(fake.Writes);
        Assert.Equal(0, fake.Remounts);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(951782400L)]      // 2000-02-29, leap day
    [InlineData(1791892800L)]     // 2026-10-13 12:00
    [InlineData(4102444799L)]     // 2099-12-31 23:59:59
    public void FormatUtc_matches_the_runtime_calendar(long seconds)
    {
        string expected = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, ThemeApplier.FormatUtc(seconds));
    }

    [Fact]
    public void Existing_backup_is_never_overwritten()
    {
        byte[] original = Live("original");
        var fake = DeviceWithLive((Dev1, Live("already-modded")));
        fake.Files[Backup1] = original;

        ApplyResult result = Applier(fake).Apply(SingleTargetPack());

        Assert.True(result.Success, result.Message);
        Assert.Equal(original, fake.Files[Backup1]);
        Assert.Equal(Zst("one"), fake.Files[Dev1]);
    }

    [Fact]
    public void Invalid_pack_writes_nothing_and_never_remounts()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        ThemePack bad = MakePack(Path1, (Path1, Zst("one")));

        ApplyResult result = Applier(fake).Apply(bad);

        Assert.False(result.Success);
        Assert.Contains("invalid", result.Message);
        Assert.Empty(fake.Writes);
        Assert.Equal(0, fake.Remounts);
        Assert.Equal(Live("one"), fake.Files[Dev1]);
        Assert.False(fake.Files.ContainsKey(Backup1));
    }

    [Fact]
    public void Remount_failure_writes_nothing()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        fake.RemountThrows = true;

        ApplyResult result = Applier(fake).Apply(SingleTargetPack());

        Assert.False(result.Success);
        Assert.Contains("remount denied", result.Message);
        Assert.Empty(fake.Writes);
        Assert.Equal(Live("one"), fake.Files[Dev1]);
    }

    [Fact]
    public void Second_target_failing_verification_rolls_the_first_back_to_its_backup()
    {
        byte[] live1 = Live("one");
        byte[] live2 = Live("two");
        var fake = DeviceWithLive((Dev1, live1), (Dev2, live2));
        fake.CorruptNextMoveTo = Dev2;

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Zst("one")), (Path2, Zst("two"))));

        Assert.False(result.Success);
        Assert.Contains("verification failed", result.Message);
        Assert.Contains(Path2, result.Message);
        Assert.Contains("Rolled back", result.Message);
        Assert.Empty(result.Written);
        Assert.Equal(live1, fake.Files[Dev1]);
        Assert.Equal(live2, fake.Files[Dev2]);
        Assert.False(fake.Files.ContainsKey(StatePath));
    }

    [Fact]
    public void Restore_puts_the_backup_bytes_back()
    {
        byte[] live = Live("one");
        var fake = DeviceWithLive((Dev1, live));
        var applier = Applier(fake);
        Assert.True(applier.Apply(SingleTargetPack()).Success);

        ApplyResult result = applier.Restore(SingleTargetPack());

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { Dev1 }, result.Written);
        Assert.Equal(live, fake.Files[Dev1]);
        Assert.Contains("\"action\": \"restore\"", Encoding.UTF8.GetString(fake.Files[StatePath]));
    }

    [Fact]
    public void Restore_with_a_missing_backup_writes_nothing()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));

        ApplyResult result = Applier(fake).Restore(SingleTargetPack());

        Assert.False(result.Success);
        Assert.Contains("no on-console backup", result.Message);
        Assert.Empty(fake.Writes);
        Assert.Equal(0, fake.Remounts);
        Assert.Equal(Live("one"), fake.Files[Dev1]);
    }

    // ---- fake -------------------------------------------------------------

    private sealed class FakeDeviceFiles : IDeviceFiles
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public List<string> Writes { get; } = new();
        public int Remounts { get; private set; }
        public bool RemountThrows { get; set; }

        /// <summary>The next Move onto this path stores corrupted bytes, so read-back verification fails once.</summary>
        public string? CorruptNextMoveTo { get; set; }

        public bool Exists(string path) => Files.ContainsKey(path) || Directories.Contains(path);

        public byte[] ReadAllBytes(string path) =>
            Files.TryGetValue(path, out byte[]? bytes) ? (byte[])bytes.Clone() : throw new FileNotFoundException(path);

        public void WriteAllBytes(string path, byte[] data)
        {
            Writes.Add(path);
            Files[path] = (byte[])data.Clone();
        }

        public void Move(string from, string to)
        {
            if (!Files.Remove(from, out byte[]? bytes))
                throw new FileNotFoundException(from);

            if (CorruptNextMoveTo == to)
            {
                CorruptNextMoveTo = null;
                bytes = bytes.Select(b => (byte)(b ^ 0xFF)).ToArray();
            }

            Files[to] = bytes;
        }

        public void DeleteFile(string path) => Files.Remove(path);

        public void CreateDirectoryRecursive(string path)
        {
            string current = string.Empty;
            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + segment;
                Directories.Add(current);
            }
        }

        public IReadOnlyList<string> ListDirectories(string path) =>
            Files.Keys.Where(k => k.StartsWith(path, StringComparison.Ordinal) && k.IndexOf('/', path.Length) > path.Length)
                .Select(k => k.Substring(path.Length, k.IndexOf('/', path.Length) - path.Length)).Distinct().ToList();

        public void RemountSystemExRw()
        {
            Remounts++;
            if (RemountThrows)
                throw new InvalidOperationException("remount denied");
        }
    }
}
