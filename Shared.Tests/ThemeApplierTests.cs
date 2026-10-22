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

    /// <summary>A Zstandard frame header recording <paramref name="decodedSize"/>, then a tag that tells copies apart.</summary>
    private static byte[] Frame(uint decodedSize, string tag)
    {
        var data = new byte[64];
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0xA0 }.CopyTo(data, 0);   // single segment, 4-byte content size
        BitConverter.GetBytes(decodedSize).CopyTo(data, 5);
        Encoding.ASCII.GetBytes(tag).CopyTo(data, 9);
        return data;
    }

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
    public void The_state_file_names_the_applied_pack_until_a_restore()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        var applier = Applier(fake);
        Assert.Null(AppliedTheme.Read(fake.Files.TryGetValue(StatePath, out byte[]? none) ? Encoding.UTF8.GetString(none) : null));

        Assert.True(applier.Apply(SingleTargetPack()).Success);
        Assert.Equal("t-apply", AppliedTheme.Read(Encoding.UTF8.GetString(fake.Files[StatePath])));

        Assert.True(applier.Restore(SingleTargetPack()).Success);
        Assert.Null(AppliedTheme.Read(Encoding.UTF8.GetString(fake.Files[StatePath])));

        Assert.True(applier.Apply(SingleTargetPack()).Success);
        Assert.Equal("t-apply", AppliedTheme.Read(Encoding.UTF8.GetString(fake.Files[StatePath])));
        Assert.True(applier.RestoreOriginals().Success);
        Assert.Null(AppliedTheme.Read(Encoding.UTF8.GetString(fake.Files[StatePath])));
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
    public void Kept_earlier_themes_combine_with_the_next_one_and_restore_undoes_both()
    {
        byte[] live1 = Live("one"), live2 = Live("two");
        var fake = DeviceWithLive((Dev1, live1), (Dev2, live2));
        Assert.True(Applier(fake).Apply(MakePack(null, (Path2, Zst("a2")))).Success);

        var keep = new ThemeApplier(fake, () => FixedNow) { KeepEarlierThemes = true };
        ApplyResult result = keep.Apply(MakePack(null, (Path1, Zst("b1"))));

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("back to default", result.Message);
        Assert.Equal(Zst("b1"), fake.Files[Dev1]);
        Assert.Equal(Zst("a2"), fake.Files[Dev2]);   // the first theme's change is still there

        Assert.True(keep.RestoreOriginals().Success);
        Assert.Equal(live1, fake.Files[Dev1]);
        Assert.Equal(live2, fake.Files[Dev2]);
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
    public void A_file_the_system_replaced_becomes_the_new_default_instead_of_being_overwritten()
    {
        var fake = DeviceWithLive((Dev1, Live("stock-13")));
        Assert.True(Applier(fake).Apply(SingleTargetPack()).Success);
        fake.Files[Dev1] = Live("stock-14");   // a system update replaces the themed file

        ApplyResult result = Applier(fake).RestoreOriginals();

        Assert.True(result.Success, result.Message);
        Assert.Contains("replaced by a system update", result.Message);
        Assert.Equal(Live("stock-14"), fake.Files[Dev1]);
        Assert.Equal(Live("stock-14"), fake.Files[Backup1]);
        Assert.Contains(fake.Files, f => f.Key.StartsWith("/data/prospero-themes/backup/replaced/", StringComparison.Ordinal)
            && f.Value.SequenceEqual(Live("stock-13")));
    }

    [Fact]
    public void Applying_after_a_system_update_backs_up_the_new_file_first()
    {
        var fake = DeviceWithLive((Dev1, Live("stock-13")));
        Assert.True(Applier(fake).Apply(SingleTargetPack()).Success);
        fake.Files[Dev1] = Live("stock-14");

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Zst("two"))));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Zst("two"), fake.Files[Dev1]);
        Assert.Equal(Live("stock-14"), fake.Files[Backup1]);
    }

    [Fact]
    public void The_clean_apply_leaves_a_file_the_system_replaced_alone()
    {
        var fake = DeviceWithLive((Dev1, Live("one")), (Dev2, Live("two")));
        Assert.True(Applier(fake).Apply(MakePack(null, (Path1, Zst("a")), (Path2, Zst("b")))).Success);
        fake.Files[Dev2] = Live("two-new");
        fake.Writes.Clear();

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Zst("c"))));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Live("two-new"), fake.Files[Dev2]);
        Assert.DoesNotContain(Dev2 + ".tmp", fake.Writes);
        Assert.Equal(Live("two-new"), fake.Files[Backup2]);
    }

    [Fact]
    public void Files_written_by_an_older_build_still_count_as_the_apps_own()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        Assert.True(Applier(fake).Apply(SingleTargetPack()).Success);
        Assert.True(fake.Files.Remove("/data/prospero-themes/" + ThemeApplier.WrittenRecord));

        Assert.True(Applier(fake).RestoreOriginals().Success);
        Assert.Equal(Live("one"), fake.Files[Dev1]);
    }

    [Fact]
    public void A_target_this_console_does_not_have_is_skipped_before_anything_is_written()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Zst("a")), (Path2, Zst("b"))));

        Assert.True(result.Success, result.Message);
        Assert.Contains("bg_hub_second.dds.zst: this console has no such file", result.Message);
        Assert.False(fake.Files.ContainsKey(Dev2));
        Assert.Equal(Zst("a"), fake.Files[Dev1]);
    }

    [Fact]
    public void A_payload_in_another_format_than_the_consoles_file_is_skipped()
    {
        var fake = DeviceWithLive((Dev1, Frame(8_294_548, "stock")), (Dev2, Frame(4_147_328, "stock")));

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Frame(8_294_548, "a")), (Path2, Frame(8_294_548, "b"))));

        Assert.True(result.Success, result.Message);
        Assert.Contains("zst 4147328 bytes decoded", result.Message);
        Assert.Equal(Frame(4_147_328, "stock"), fake.Files[Dev2]);
        Assert.Equal(Frame(8_294_548, "a"), fake.Files[Dev1]);
    }

    [Fact]
    public void Nothing_is_written_when_no_target_fits()
    {
        var fake = DeviceWithLive((Dev1, Frame(4_147_328, "stock")));

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Frame(8_294_548, "a"))));

        Assert.False(result.Success);
        Assert.Contains("nothing in this theme fits", result.Message);
        Assert.Equal(0, fake.Remounts);
        Assert.Equal(Frame(4_147_328, "stock"), fake.Files[Dev1]);
    }

    /// <summary>A small DX10 DDS: header for <paramref name="width"/>x<paramref name="height"/> BC7, then a tag.</summary>
    private static byte[] Dds(int width, int height, string tag)
    {
        var data = new byte[148 + 64];
        "DDS "u8.CopyTo(data);
        BitConverter.GetBytes(height).CopyTo(data, 12);
        BitConverter.GetBytes(width).CopyTo(data, 16);
        "DX10"u8.CopyTo(data.AsSpan(84));
        BitConverter.GetBytes(98u).CopyTo(data, 128);
        Encoding.ASCII.GetBytes(tag).CopyTo(data, 148);
        return data;
    }

    /// <summary>A real Zstandard frame holding <paramref name="content"/> in one raw block.</summary>
    private static byte[] RawFrame(byte[] content)
    {
        var frame = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD, 0xA0 };   // single segment, 4-byte content size
        frame.AddRange(BitConverter.GetBytes((uint)content.Length));
        int header = 1 | content.Length << 3;                           // last block, raw
        frame.AddRange([(byte)header, (byte)(header >> 8), (byte)(header >> 16)]);
        frame.AddRange(content);
        return frame.ToArray();
    }

    [Fact]
    public void A_console_with_plain_dds_backgrounds_gets_the_payload_unpacked()
    {
        // Firmware before 13.00: bg_hub_default.dds and the tiles' bg_*.dds, no .dds.zst at all.
        const string hub = "/system_ex/vsh_asset/bg_hub_default.dds", gallery = "/system_ex/vsh_asset/bg_NPXS40056.dds";
        var fake = DeviceWithLive((hub, Dds(64, 32, "stock")), (gallery, Dds(64, 32, "gallery")));
        ThemeApplier applier = Applier(fake);
        ThemePack pack = MakePack(null, (Path1, RawFrame(Dds(64, 32, "theme"))));

        ApplyResult result = applier.Apply(pack);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Dds(64, 32, "theme"), fake.Files[hub]);
        Assert.Equal(Dds(64, 32, "theme"), fake.Files[gallery]);
        Assert.Equal(Dds(64, 32, "stock"), fake.Files["/data/prospero-themes/backup/original/vsh_asset/bg_hub_default.dds"]);
        Assert.False(fake.Files.ContainsKey(Dev1));
        Assert.Equal(1, fake.Remounts);

        Assert.True(applier.Restore(pack).Success);
        Assert.Equal(Dds(64, 32, "stock"), fake.Files[hub]);
        Assert.Equal(Dds(64, 32, "gallery"), fake.Files[gallery]);
    }

    [Fact]
    public void A_plain_dds_in_another_size_is_skipped()
    {
        const string hub = "/system_ex/vsh_asset/bg_hub_default.dds";
        var fake = DeviceWithLive((hub, Dds(128, 64, "stock")));

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, RawFrame(Dds(64, 32, "theme")))));

        Assert.False(result.Success);
        Assert.Contains("vsh_asset/bg_hub_default.dds: the console's file is dds 128x64", result.Message);
        Assert.Equal(Dds(128, 64, "stock"), fake.Files[hub]);
        Assert.Equal(0, fake.Remounts);
    }

    [Fact]
    public void State_records_the_firmware()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        Assert.True(new ThemeApplier(fake, () => FixedNow) { Firmware = "13.000.000" }.Apply(SingleTargetPack()).Success);
        Assert.Contains("\"firmware\": \"13.000.000\"", Encoding.UTF8.GetString(fake.Files[StatePath]));
    }

    [Fact]
    public void Remount_failure_writes_nothing_on_the_system_side()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        fake.RemountThrows = true;

        ApplyResult result = Applier(fake).Apply(SingleTargetPack());

        Assert.False(result.Success);
        Assert.Contains("remount denied", result.Message);
        // Only the backup under /data was made, before the remount.
        Assert.All(fake.Writes, w => Assert.StartsWith("/data/prospero-themes/", w));
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
    public void A_write_refused_before_the_rename_is_not_rolled_back()
    {
        byte[] live1 = Live("one"), live2 = Live("two");
        var fake = DeviceWithLive((Dev1, live1), (Dev2, live2));
        fake.RefusedWrites.Add(Dev2 + ".tmp");

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Zst("one")), (Path2, Zst("two"))));

        Assert.False(result.Success);
        Assert.Contains("Rolled back 1 target(s)", result.Message);
        Assert.DoesNotContain("FAILED", result.Message);
        Assert.Empty(result.Written);
        Assert.Equal(live1, fake.Files[Dev1]);
        Assert.Equal(live2, fake.Files[Dev2]);
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

        /// <summary>Paths whose reported size is this instead of their real one (a file too large for the helper).</summary>
        public Dictionary<string, long> Sizes { get; } = new(StringComparer.Ordinal);

        /// <summary>Paths read with ReadAllBytes, in order.</summary>
        public List<string> Reads { get; } = new();

        public long Length(string path) =>
            Sizes.TryGetValue(path, out long size) ? size : Files.TryGetValue(path, out byte[]? bytes) ? bytes.Length : -1;

        public byte[] ReadAllBytes(string path)
        {
            Reads.Add(path);
            return Files.TryGetValue(path, out byte[]? bytes) ? (byte[])bytes.Clone() : throw new FileNotFoundException(path);
        }

        /// <summary>Paths hashed with Hash (streamed on the console, never held whole), in order.</summary>
        public List<string> Hashes { get; } = new();

        public string Hash(string path)
        {
            Hashes.Add(path);
            return Files.TryGetValue(path, out byte[]? bytes) ? Sha(bytes) : throw new FileNotFoundException(path);
        }

        /// <summary>Writes to these paths throw, as the helper's refusal of a file over its write limit does.</summary>
        public HashSet<string> RefusedWrites { get; } = new(StringComparer.Ordinal);

        public void WriteAllBytes(string path, byte[] data)
        {
            if (RefusedWrites.Contains(path))
                throw new InvalidOperationException($"write {path} failed: refused");
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
