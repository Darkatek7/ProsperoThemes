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

    private sealed class RecordedProgress : IApplyProgress
    {
        public List<string> Steps { get; } = new();
        public void Report(string step, int index, int total) => Steps.Add($"{step} {index}/{total}");
    }

    [Fact]
    public void Apply_and_restore_report_each_file_of_each_step()
    {
        var fake = DeviceWithLive((Dev1, Live("one")), (Dev2, Live("two")));
        var progress = new RecordedProgress();
        var applier = new ThemeApplier(fake, () => FixedNow) { Progress = progress };

        Assert.True(applier.Apply(MakePack(null, (Path1, Zst("a1")), (Path2, Zst("a2")))).Success);
        Assert.Equal(new[] { "Backing up 1/2", "Backing up 2/2", "Writing 1/2", "Writing 2/2" }, progress.Steps);

        // The second apply starts from the defaults: each backed-up file it does not write is looked at once.
        progress.Steps.Clear();
        Assert.True(applier.Apply(MakePack(null, (Path1, Zst("b1")))).Success);
        Assert.Equal(new[] { "Backing up 1/1", "Resetting 1/1", "Writing 1/1" }, progress.Steps);

        progress.Steps.Clear();
        Assert.True(applier.RestoreOriginals().Success);
        Assert.Equal(new[] { "Restoring 1/2", "Restoring 2/2" }, progress.Steps);
    }

    [Fact]
    public void CheckFit_names_the_targets_the_console_has_no_file_for_and_writes_nothing()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        ThemePack pack = MakePack(null, (Path1, Zst("a1")), (Path2, Zst("a2")));
        int files = fake.Files.Count;

        PackFit fit = Applier(fake).CheckFit(pack.Manifest);

        Assert.Equal(new[] { Path2 }, fit.Missing.Select(t => t.Path));
        Assert.Same(pack.Manifest.Targets[1], fit.Missing[0]);
        Assert.Empty(fake.Writes);
        Assert.Equal(files, fake.Files.Count);
        Assert.Equal(0, fake.Remounts);
    }

    [Fact]
    public void CheckFit_counts_the_all_apps_background_as_fitting_unless_it_reaches_every_title()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        ThemePack pack = MakePack(null, (Path1, Zst("a1")), (AssetRoots.AllAppsBackground, Zst("all")), ("apps/PPSA01650/pic0.dds", Zst("app")));

        // Without the all-apps scope it reaches titles through bg_hub_default; the title is not installed.
        PackFit fit = Applier(fake).CheckFit(pack.Manifest);
        Assert.Equal(new[] { "apps/PPSA01650/pic0.dds" }, fit.Missing.Select(t => t.Path));

        // With it, it needs titles with their own background, and this console has none.
        fit = new ThemeApplier(fake, () => FixedNow) { IncludeAllApps = true }.CheckFit(pack.Manifest);
        Assert.Equal(new[] { AssetRoots.AllAppsBackground, "apps/PPSA01650/pic0.dds" }, fit.Missing.Select(t => t.Path));
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

    private const string StoreTile = "rnps/NPXS40047/appdb/default/icon0.png";
    private const string LiveStoreTile = "/mnt/rnps/apps/NPXS40047/appdb/default/icon0.png";
    private const string ShippedStoreTile = "/system_ex/rnps/apps/NPXS40047/appdb/default/icon0.png";

    [Fact]
    public void A_console_without_the_live_rnps_copy_gets_the_tile_icon_in_the_shipped_app()
    {
        // 13.60 and 10.01: /mnt/rnps/apps is empty, app.db points the tiles at /system_ex/rnps/apps (issue 8).
        byte[] stock = PreviewKit.Png(4, 4, 0, 0, 255), theme = PreviewKit.Png(4, 4, 255, 0, 0);
        var fake = DeviceWithLive((ShippedStoreTile, stock));
        ThemeApplier applier = Applier(fake);

        ApplyResult result = applier.Apply(MakePack(null, (StoreTile, theme)));

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Icons change after the next restart", result.Message);
        Assert.Equal(theme, fake.Files[ShippedStoreTile]);
        Assert.False(fake.Files.ContainsKey(LiveStoreTile));
        Assert.Equal(stock, fake.Files["/data/prospero-themes/backup/original/" + StoreTile]);

        Assert.True(applier.RestoreOriginals().Success);
        Assert.Equal(stock, fake.Files[ShippedStoreTile]);
    }

    [Fact]
    public void A_home_screen_restart_is_offered_only_for_tile_icons_and_the_icon_container_with_their_backups()
    {
        byte[] stock = PreviewKit.Png(4, 4, 0, 0, 255), theme = PreviewKit.Png(4, 4, 255, 0, 0);
        var fake = DeviceWithLive((ShippedStoreTile, stock), (Dev1, Live("one")));
        ThemeApplier applier = Applier(fake);

        ApplyResult result = applier.Apply(MakePack(null, (StoreTile, theme), (Path1, Zst("one"))));

        Assert.True(result.Success, result.Message);
        Assert.True(ThemeApplier.LoadsWithShell(ShippedStoreTile));
        Assert.True(ThemeApplier.LoadsWithShell(LiveStoreTile));
        Assert.True(ThemeApplier.LoadsWithShell("/system_ex/vsh_asset/Sce.PlayStation.PUI_UI3.rco"));
        Assert.False(ThemeApplier.LoadsWithShell(Dev1));
        Assert.False(ThemeApplier.LoadsWithShell("/system_ex/vsh_asset/bgm_home.at9"));
        // The background shows without a restart, so only the tile icon's backup goes with it.
        Assert.Equal(new[] { ("/data/prospero-themes/backup/original/" + StoreTile, ShippedStoreTile) }, applier.ShellBackups(result.Written));
        Assert.Empty(applier.ShellBackups([Dev1]));
    }

    [Fact]
    public void A_console_with_the_live_rnps_copy_keeps_the_shipped_app_unchanged()
    {
        // 13.00: app.db points at /mnt/rnps/apps, a file written to /system_ex/rnps changes nothing.
        byte[] stock = PreviewKit.Png(4, 4, 0, 0, 255), theme = PreviewKit.Png(4, 4, 255, 0, 0);
        var fake = DeviceWithLive((LiveStoreTile, stock), (ShippedStoreTile, stock));
        fake.Directories.Add("/mnt/rnps/apps/NPXS40047");

        ApplyResult result = Applier(fake).Apply(MakePack(null, (StoreTile, theme)));

        Assert.True(result.Success, result.Message);
        Assert.Equal(theme, fake.Files[LiveStoreTile]);
        Assert.Equal(stock, fake.Files[ShippedStoreTile]);
    }

    [Theory]
    [InlineData("rnps/NPXS40047/appdb/default/icon0.png", "/mnt/rnps/apps/NPXS40047", "/system_ex/rnps/apps/NPXS40047/appdb/default/icon0.png")]
    [InlineData("rnps/NPXS40047", null, null)]
    [InlineData("rnps/NPXS40047/../x.png", null, null)]
    [InlineData("vsh_asset/bg_hub_default.dds.zst", null, null)]
    public void Shipped_maps_only_rnps_files(string logical, string? liveApp, string? shipped)
    {
        bool mapped = AssetRoots.Shipped(logical, out string app, out string file);
        Assert.Equal(liveApp is not null, mapped);
        if (mapped)
        {
            Assert.Equal(liveApp, app);
            Assert.Equal(shipped, file);
        }
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

    // ---- free space (issue #13) ---------------------------------------------

    private static IEnumerable<string> Temporaries(FakeDeviceFiles fake) =>
        fake.Files.Keys.Where(k => k.EndsWith(".tmp", StringComparison.Ordinal));

    private static Dictionary<string, byte[]> SystemSide(FakeDeviceFiles fake) =>
        fake.Files.Where(f => f.Key.StartsWith("/system_ex/", StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => (byte[])f.Value.Clone(), StringComparer.Ordinal);

    [Fact]
    public void A_write_that_fails_half_way_leaves_no_temporary_file_and_is_rolled_back()
    {
        byte[] live1 = Live("one"), live2 = Live("two");
        var fake = DeviceWithLive((Dev1, live1), (Dev2, live2));
        fake.FailWriteTo = Dev2 + ".tmp";

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Zst("one")), (Path2, Zst("two"))));

        Assert.False(result.Success);
        Assert.Contains("no space left on device", result.Message);
        Assert.Contains("Rolled back 1 target(s)", result.Message);
        Assert.Contains(Dev2 + ".tmp", fake.Writes);   // the half file was written, then removed
        Assert.Empty(Temporaries(fake));
        Assert.Equal(live1, fake.Files[Dev1]);
        Assert.Equal(live2, fake.Files[Dev2]);
        Assert.False(fake.Files.ContainsKey(StatePath));
    }

    [Fact]
    public void A_single_write_that_fails_half_way_keeps_the_old_file_and_leaves_no_temporary_file()
    {
        byte[] live = Live("one");
        var fake = DeviceWithLive((Dev1, live));
        fake.FailWriteTo = Dev1 + ".tmp";

        ApplyResult result = Applier(fake).Apply(SingleTargetPack());

        Assert.False(result.Success);
        Assert.Contains("no space left on device", result.Message);
        Assert.Contains("Nothing had been written yet", result.Message);
        Assert.Empty(Temporaries(fake));
        Assert.Equal(live, fake.Files[Dev1]);
    }

    [Fact]
    public void A_temporary_file_an_earlier_failed_write_left_is_gone_after_a_successful_apply()
    {
        var fake = DeviceWithLive((Dev1, Live("one")));
        fake.Files[Dev1 + ".tmp"] = new byte[1000];   // half a file from the earlier attempt

        ApplyResult result = Applier(fake).Apply(SingleTargetPack());

        Assert.True(result.Success, result.Message);
        Assert.Contains(Dev1 + ".tmp", fake.Deleted);   // removed before the new write, not only replaced by it
        Assert.Empty(Temporaries(fake));
        Assert.Equal(Zst("one"), fake.Files[Dev1]);
    }

    [Fact]
    public void A_target_that_cannot_fit_stops_the_apply_before_anything_is_written()
    {
        byte[] live = Live("one");
        byte[] payload = Zst("one");
        var fake = DeviceWithLive((Dev1, live));
        fake.Free = payload.Length + ThemeApplier.SpaceMargin - 1;
        Dictionary<string, byte[]> before = SystemSide(fake);

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, payload)));

        Assert.False(result.Success);
        Assert.Contains("not enough space", result.Message);
        Assert.Contains(Dev1, result.Message);
        Assert.Contains("Nothing was written", result.Message);
        Assert.Equal(0, fake.Remounts);
        Assert.DoesNotContain(fake.Writes, w => w.StartsWith("/system_ex/", StringComparison.Ordinal));
        Assert.Empty(Temporaries(fake));
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), SystemSide(fake).Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(live, fake.Files[Dev1]);
        // The backup, made before the room is checked, is a copy of the file that is still live.
        if (fake.Files.TryGetValue(Backup1, out byte[]? backup)) Assert.Equal(live, backup);
        Assert.False(fake.Files.ContainsKey(StatePath));
    }

    [Fact]
    public void A_second_target_that_cannot_fit_stops_the_apply_before_the_first_is_written()
    {
        byte[] live1 = Live("one"), live2 = Live("two");
        byte[] big = new byte[3 * 1024 * 1024];
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD }.CopyTo(big, 0);
        var fake = DeviceWithLive((Dev1, live1), (Dev2, live2));
        fake.Free = 2 * 1024 * 1024;   // room for the small payload, not for the big one

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, Zst("one")), (Path2, big)));

        Assert.False(result.Success);
        Assert.Contains("not enough space for " + Dev2, result.Message);
        Assert.Contains("Nothing was written", result.Message);
        Assert.Equal(live1, fake.Files[Dev1]);
        Assert.Equal(live2, fake.Files[Dev2]);
        Assert.DoesNotContain(fake.Writes, w => w.StartsWith("/system_ex/", StringComparison.Ordinal));
        Assert.Equal(0, fake.Remounts);
    }

    [Fact]
    public void With_enough_free_space_the_apply_succeeds()
    {
        byte[] payload = Zst("one");
        var fake = DeviceWithLive((Dev1, Live("one")));
        fake.Free = payload.Length + ThemeApplier.SpaceMargin;   // exactly the room a write needs

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, payload)));

        Assert.True(result.Success, result.Message);
        Assert.Equal(payload, fake.Files[Dev1]);
        Assert.Empty(Temporaries(fake));
    }

    [Fact]
    public void With_unknown_free_space_nothing_is_checked_and_the_apply_succeeds()
    {
        byte[] payload = Zst("one");
        var fake = DeviceWithLive((Dev1, Live("one")));
        Assert.Equal(-1, fake.Free);

        ApplyResult result = Applier(fake).Apply(MakePack(null, (Path1, payload)));

        Assert.True(result.Success, result.Message);
        Assert.Equal(payload, fake.Files[Dev1]);
    }

    [Fact]
    public void RestoreOriginals_on_a_console_with_too_little_space_reports_the_file_and_leaves_no_temporary_file()
    {
        byte[] live = Live("one");
        var fake = DeviceWithLive((Dev1, live));
        ThemeApplier applier = Applier(fake);
        Assert.True(applier.Apply(SingleTargetPack()).Success);
        fake.Free = 1000;   // less than the margin alone

        ApplyResult result = applier.RestoreOriginals();

        Assert.False(result.Success);
        Assert.Contains("not enough space", result.Message);
        Assert.Contains(Dev1, result.Message);
        Assert.Empty(Temporaries(fake));
        Assert.Equal(Zst("one"), fake.Files[Dev1]);   // still the theme's file, whole

        fake.Free = -1;
        Assert.True(applier.RestoreOriginals().Success);
        Assert.Equal(live, fake.Files[Dev1]);
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

    // ---- in place when there is no room for a .tmp (issue #13, helper v11) ----------

    private const int PieceBytes = 64;

    /// <summary>
    /// A console file and a payload of one size that differ in pieces 0, 5, 6 and 12 only (of <see cref="PieceBytes"/>): the
    /// first four bytes are the Zstandard magic every payload needs.
    /// </summary>
    private static (byte[] Live, byte[] Payload) SameSizePair()
    {
        var live = new byte[1000];
        Array.Fill(live, (byte)'L');
        byte[] payload = (byte[])live.Clone();
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD }.CopyTo(payload, 0);
        payload[330] = (byte)'P';
        payload[400] = (byte)'P';
        payload[800] = (byte)'P';
        return (live, payload);
    }

    private static ThemeApplier PatchingApplier(FakeDeviceFiles fake) =>
        new(fake, () => FixedNow) { PatchChunkBytes = PieceBytes };

    private static bool OnSystemSide(string path) => path.StartsWith("/system_ex/", StringComparison.Ordinal);

    [Fact]
    public void RestoreOriginals_with_no_room_for_a_temporary_file_patches_the_file_in_place()
    {
        var (live, payload) = SameSizePair();
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.CanPatch = true;
        ThemeApplier applier = PatchingApplier(fake);
        Assert.True(applier.Apply(MakePack(null, (Path1, payload))).Success);
        Assert.Empty(fake.Patches);   // room enough then: the normal path
        Assert.Equal(payload, fake.Files[Dev1]);
        fake.Free = 1000;   // less than the margin alone
        fake.Writes.Clear();

        ApplyResult result = applier.RestoreOriginals();

        Assert.True(result.Success, result.Message);
        Assert.Equal(live, fake.Files[Dev1]);
        Assert.Equal(live, fake.Files[Backup1]);
        Assert.DoesNotContain(fake.Writes, OnSystemSide);   // no .tmp, no whole-file write
        Assert.Empty(Temporaries(fake));
        Assert.NotEmpty(fake.Patches);
    }

    [Fact]
    public void An_in_place_write_sends_only_the_pieces_that_differ_and_neighbours_together()
    {
        var (live, payload) = SameSizePair();
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.CanPatch = true;
        fake.Free = 1000;

        ApplyResult result = PatchingApplier(fake).Apply(MakePack(null, (Path1, payload)));

        Assert.True(result.Success, result.Message);
        Assert.Equal(payload, fake.Files[Dev1]);
        Assert.Equal(live, fake.Files[Backup1]);
        Assert.Equal(new[] { (Dev1, 0L, 64), (Dev1, 320L, 128), (Dev1, 768L, 64) }, fake.Patches);
        Assert.DoesNotContain(fake.Writes, OnSystemSide);
        Assert.Empty(Temporaries(fake));
    }

    [Fact]
    public void An_in_place_patch_is_cut_at_the_most_one_patch_carries()
    {
        var (live, payload) = SameSizePair();
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.CanPatch = true;
        fake.Free = 1000;

        var applier = new ThemeApplier(fake, () => FixedNow) { PatchChunkBytes = PieceBytes, PatchRunBytes = PieceBytes };
        Assert.True(applier.Apply(MakePack(null, (Path1, payload))).Success);

        Assert.Equal(new[] { (Dev1, 0L, 64), (Dev1, 320L, 64), (Dev1, 384L, 64), (Dev1, 768L, 64) }, fake.Patches);
        Assert.Equal(payload, fake.Files[Dev1]);
    }

    [Fact]
    public void A_file_that_already_holds_the_data_is_not_patched_at_all()
    {
        var (live, payload) = SameSizePair();
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.CanPatch = true;
        ThemeApplier applier = PatchingApplier(fake);
        Assert.True(applier.Apply(MakePack(null, (Path1, payload))).Success);
        fake.Free = 1000;

        Assert.True(applier.Apply(MakePack(null, (Path1, payload))).Success);

        Assert.Empty(fake.Patches);
        Assert.Equal(payload, fake.Files[Dev1]);
    }

    [Fact]
    public void With_no_room_and_a_file_of_another_size_the_message_is_not_enough_space()
    {
        byte[] live = Live("one");   // 8 bytes; the theme's file is longer
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.CanPatch = true;
        ThemeApplier applier = PatchingApplier(fake);
        Assert.True(applier.Apply(SingleTargetPack()).Success);
        fake.Free = 1000;

        ApplyResult result = applier.RestoreOriginals();

        Assert.False(result.Success);
        Assert.Contains("not enough space for " + Dev1, result.Message);
        Assert.Empty(fake.Patches);
        Assert.Empty(Temporaries(fake));
        Assert.Equal(Zst("one"), fake.Files[Dev1]);

        ApplyResult apply = applier.Apply(MakePack(null, (Path1, Zst("three"))));
        Assert.False(apply.Success);
        Assert.Contains("not enough space for " + Dev1, apply.Message);
        Assert.Empty(fake.Patches);
    }

    [Fact]
    public void With_no_room_and_a_device_that_cannot_patch_the_message_is_not_enough_space()
    {
        var (live, payload) = SameSizePair();
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.Free = 1000;
        Assert.False(fake.CanPatch);

        ApplyResult result = PatchingApplier(fake).Apply(MakePack(null, (Path1, payload)));

        Assert.False(result.Success);
        Assert.Contains("not enough space for " + Dev1, result.Message);
        Assert.Empty(fake.Patches);
        Assert.Equal(live, fake.Files[Dev1]);
    }

    [Fact]
    public void With_room_the_normal_path_is_used_even_on_a_device_that_can_patch()
    {
        var (live, payload) = SameSizePair();
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.CanPatch = true;
        fake.Free = 10 * 1024 * 1024;
        ThemeApplier applier = PatchingApplier(fake);

        Assert.True(applier.Apply(MakePack(null, (Path1, payload))).Success);
        Assert.Contains(Dev1 + ".tmp", fake.Writes);
        Assert.True(applier.RestoreOriginals().Success);

        Assert.Empty(fake.Patches);
        Assert.Equal(live, fake.Files[Dev1]);
    }

    [Fact]
    public void A_patch_that_fails_half_way_is_finished_by_running_the_restore_again()
    {
        var (live, payload) = SameSizePair();
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.CanPatch = true;
        ThemeApplier applier = PatchingApplier(fake);
        Assert.True(applier.Apply(MakePack(null, (Path1, payload))).Success);
        fake.Free = 1000;
        fake.FailPatchNumber = 2;   // the first range is back, the second is not

        ApplyResult result = applier.RestoreOriginals();

        Assert.False(result.Success);
        Assert.Contains("patch", result.Message);
        Assert.NotEqual(live, fake.Files[Dev1]);
        Assert.NotEqual(payload, fake.Files[Dev1]);   // old and new ranges mixed
        Assert.Equal(live, fake.Files[Backup1]);

        ApplyResult again = PatchingApplier(fake).RestoreOriginals();

        Assert.True(again.Success, again.Message);
        Assert.Equal(live, fake.Files[Dev1]);
        // The mix was not taken for a file the system replaced, so it never became the backup.
        Assert.Equal(live, fake.Files[Backup1]);
        Assert.DoesNotContain(fake.Files.Keys, k => k.Contains("/backup/replaced/", StringComparison.Ordinal));
        Assert.Empty(Temporaries(fake));
    }

    [Fact]
    public void A_patch_that_fails_during_an_apply_is_rolled_back_in_place()
    {
        var (live, payload) = SameSizePair();
        var fake = DeviceWithLive((Dev1, (byte[])live.Clone()));   // the fake patches its own copy
        fake.CanPatch = true;
        fake.Free = 1000;
        fake.FailPatchNumber = 2;

        ApplyResult result = PatchingApplier(fake).Apply(MakePack(null, (Path1, payload)));

        Assert.False(result.Success);
        Assert.Contains("Rolled back 1 target(s)", result.Message);
        Assert.Equal(live, fake.Files[Dev1]);
        Assert.DoesNotContain(fake.Writes, OnSystemSide);
        Assert.Empty(Temporaries(fake));
    }

    [Fact]
    public void A_patch_never_goes_past_the_end_of_the_file()
    {
        var fake = DeviceWithLive((Dev1, new byte[100]));
        fake.CanPatch = true;

        Assert.Throws<InvalidOperationException>(() => ((IDeviceFiles)fake).PatchFile(Dev1, 90, new byte[11]));
        ((IDeviceFiles)fake).PatchFile(Dev1, 90, new byte[10]);

        Assert.Equal(100, fake.Files[Dev1].Length);
    }

    // ---- music volume -------------------------------------------------------

    [Fact]
    public void The_home_music_is_written_at_the_music_volume_and_other_files_as_they_are()
    {
        const string bgm = "/system_ex/vsh_asset/bgm_home.at9";
        byte[] music = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "atrac9", "synth-6ch.at9"));
        var fake = DeviceWithLive((bgm, music), (Dev1, Live("one")));

        var applier = new ThemeApplier(fake, () => FixedNow) { MusicVolume = 0.5 };
        ApplyResult result = applier.Apply(MakePack(null, ("vsh_asset/bgm_home.at9", music), (Path1, Zst("one"))));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Zst("one"), fake.Files[Dev1]);
        byte[] written = fake.Files[bgm];
        Assert.Equal(music.Length, written.Length);
        Assert.NotEqual(music, written);
        Assert.Equal(Atrac9Codec.ScaleVolume(music, 0.5), written);

        var full = DeviceWithLive((bgm, Live("stock")));
        Assert.True(Applier(full).Apply(MakePack(null, ("vsh_asset/bgm_home.at9", music))).Success);
        Assert.Equal(music, full.Files[bgm]);
    }

    [Fact]
    public void Music_the_codec_cannot_read_is_written_as_it_is_with_a_note()
    {
        const string bgm = "/system_ex/vsh_asset/bgm_home.at9";
        byte[] broken = Encoding.ASCII.GetBytes("RIFF not really an at9 file");
        var fake = DeviceWithLive((bgm, Live("stock")));

        ApplyResult result = new ThemeApplier(fake, () => FixedNow) { MusicVolume = 0.1 }
            .Apply(MakePack(null, ("vsh_asset/bgm_home.at9", broken)));

        Assert.True(result.Success, result.Message);
        Assert.Equal(broken, fake.Files[bgm]);
        Assert.Contains("the music is at its own volume", result.Message);
    }

    // ---- originals without a backup ----------------------------------------

    private static StockCatalog CatalogWith(string logical, byte[] stock) => StockCatalog.Parse(
        $$"""{"firmware":"13.00","assets":[{"path":"{{logical}}","stock_sha256":"{{Sha(stock)}}"}]}""");

    [Fact]
    public void A_deleted_backup_never_makes_the_themes_file_the_original()
    {
        byte[] original = Live("original");
        var fake = DeviceWithLive((Dev1, original));
        Assert.True(Applier(fake).Apply(MakePack(null, (Path1, Frame(1000, "one")))).Success);
        fake.Files.Remove(Backup1);

        ThemeApplier again = Applier(fake);
        ApplyResult apply = again.Apply(MakePack(null, (Path1, Frame(1000, "two"))));

        Assert.True(apply.Success, apply.Message);
        Assert.Equal(Frame(1000, "two"), fake.Files[Dev1]);
        Assert.False(fake.Files.ContainsKey(Backup1));
        Assert.Equal(new[] { Path1 }, again.NoOriginal);
        Assert.Contains("no original on this console", apply.Message);

        ApplyResult restore = Applier(fake).RestoreOriginals();
        Assert.Equal(Frame(1000, "two"), fake.Files[Dev1]);
        Assert.Contains("1 changed file(s) have no original", restore.Message);
    }

    [Fact]
    public void A_file_the_app_put_back_still_counts_as_the_original_after_its_backup_is_gone()
    {
        byte[] original = Live("original");
        var fake = DeviceWithLive((Dev1, original));
        Assert.True(Applier(fake).Apply(SingleTargetPack()).Success);
        Assert.True(Applier(fake).RestoreOriginals().Success);
        fake.Files.Remove(Backup1);

        ThemeApplier again = Applier(fake);
        Assert.True(again.Apply(SingleTargetPack()).Success);

        Assert.Empty(again.NoOriginal);
        Assert.Equal(original, fake.Files[Backup1]);
    }

    [Fact]
    public void With_every_record_gone_the_catalog_tells_the_themes_file_from_the_original()
    {
        byte[] original = Live("original");
        var fake = DeviceWithLive((Dev1, original));
        Assert.True(Applier(fake).Apply(MakePack(null, (Path1, Frame(1000, "one")))).Success);
        foreach (string path in fake.Files.Keys.Where(k => k.StartsWith("/data/prospero-themes/backup/", StringComparison.Ordinal)).ToList())
            fake.Files.Remove(path);

        var again = new ThemeApplier(fake, () => FixedNow) { Stock = CatalogWith(Path1, original) };
        Assert.True(again.Apply(MakePack(null, (Path1, Frame(1000, "two")))).Success);

        Assert.Equal(new[] { Path1 }, again.NoOriginal);
        Assert.False(fake.Files.ContainsKey(Backup1));
    }

    [Fact]
    public void A_backup_that_is_not_the_stock_file_is_never_put_back()
    {
        byte[] stock = Live("stock");
        var fake = DeviceWithLive((Dev1, Zst("theme")));
        fake.Files[Backup1] = Zst("older-theme");
        fake.Files["/data/prospero-themes/backup/index.txt"] = Encoding.UTF8.GetBytes(Path1 + "\n");

        var applier = new ThemeApplier(fake, () => FixedNow) { Stock = CatalogWith(Path1, stock) };
        ApplyResult result = applier.RestoreOriginals();

        Assert.Equal(Zst("theme"), fake.Files[Dev1]);
        Assert.Equal(new[] { Path1 }, applier.NoOriginal);
        Assert.Contains("no original", result.Message);

        // Once the console has the stock file again, it becomes the backup.
        fake.Files[Dev1] = stock;
        Assert.True(new ThemeApplier(fake, () => FixedNow) { Stock = CatalogWith(Path1, stock) }.RestoreOriginals().Success);
        Assert.Equal(stock, fake.Files[Backup1]);
    }

    // ---- originals imported from USB ----------------------------------------

    private static StockCatalog CatalogWithSize(string logical, byte[] stock) => StockCatalog.Parse(
        $$"""{"firmware":"12.40","assets":[{"path":"{{logical}}","stock_bytes":{{stock.Length}},"stock_sha256":"{{Sha(stock)}}"},{"path":"unknown/thing.bin","stock_bytes":{{stock.Length}},"stock_sha256":"{{Sha(stock)}}"}]}""");

    // The issue #13 console: a theme's file is live, and the app's backup was made from it (after a factory reset).
    private static FakeDeviceFiles ThemedWithThemedBackup()
    {
        var fake = DeviceWithLive((Dev1, Zst("theme")));
        fake.Files[Backup1] = Zst("theme");
        fake.Files["/data/prospero-themes/backup/index.txt"] = Encoding.UTF8.GetBytes(Path1 + "\n");
        return fake;
    }

    [Fact]
    public void An_imported_original_makes_restore_put_back_the_stock_file()
    {
        byte[] stock = Live("stock");
        var fake = ThemedWithThemedBackup();
        StockCatalog catalog = CatalogWithSize(Path1, stock);

        ThemeApplier before = new(fake, () => FixedNow) { Stock = catalog };
        before.RestoreOriginals();
        Assert.Equal(new[] { Path1 }, before.NoOriginal);
        Assert.Equal(Zst("theme"), fake.Files[Dev1]);

        Assert.Equal(ThemeApplier.OriginalImport.Imported, new ThemeApplier(fake, () => FixedNow) { Stock = catalog }.ImportOriginal(Path1, stock));
        Assert.Equal(stock, fake.Files[Backup1]);
        Assert.Contains(fake.Files, f => f.Key.StartsWith("/data/prospero-themes/backup/replaced/", StringComparison.Ordinal)
            && f.Key.EndsWith(Path1, StringComparison.Ordinal) && f.Value.SequenceEqual(Zst("theme")));

        ThemeApplier after = new(fake, () => FixedNow) { Stock = catalog };
        ApplyResult restore = after.RestoreOriginals();
        Assert.True(restore.Success, restore.Message);
        Assert.Empty(after.NoOriginal);
        Assert.Equal(stock, fake.Files[Dev1]);
    }

    [Fact]
    public void Only_this_firmwares_stock_file_is_imported()
    {
        byte[] stock = Live("stock");
        var fake = ThemedWithThemedBackup();
        var applier = new ThemeApplier(fake, () => FixedNow) { Stock = CatalogWithSize(Path1, stock) };

        Assert.Equal(ThemeApplier.OriginalImport.NotStock, applier.ImportOriginal(Path1, Live("stocK")));
        Assert.Equal(ThemeApplier.OriginalImport.NotStock, applier.ImportOriginal(Path1, Live("stock-longer")));
        Assert.Equal(ThemeApplier.OriginalImport.NotListed, applier.ImportOriginal("vsh_asset/bg_image_load_error.dds.zst", stock));
        // Listed, but no console file it could restore: the import would only promise a restore that never comes.
        Assert.Equal(ThemeApplier.OriginalImport.NotListed, applier.ImportOriginal("unknown/thing.bin", stock));
        Assert.Equal(Zst("theme"), fake.Files[Backup1]);
        Assert.DoesNotContain(fake.Files, f => f.Key.Contains("/backup/replaced/", StringComparison.Ordinal));

        Assert.Equal(ThemeApplier.OriginalImport.Imported, applier.ImportOriginal(Path1, stock));
        Assert.Equal(ThemeApplier.OriginalImport.AlreadyThere, applier.ImportOriginal(Path1, stock));
        Assert.Equal(stock, fake.Files[Backup1]);
    }

    [Fact]
    public void A_stock_backup_stays_and_is_put_back_whatever_the_written_record_says()
    {
        byte[] stock = Live("stock");
        var fake = ThemedWithThemedBackup();
        StockCatalog catalog = CatalogWithSize(Path1, stock);
        // A record older than the theme now on the console: on its own it would make the live file a system update's.
        fake.Files["/data/prospero-themes/backup/written.txt"] = Encoding.UTF8.GetBytes($"{Sha(Zst("earlier"))} {Dev1}\n");
        Assert.Equal(ThemeApplier.OriginalImport.Imported, new ThemeApplier(fake, () => FixedNow) { Stock = catalog }.ImportOriginal(Path1, stock));

        ApplyResult apply = new ThemeApplier(fake, () => FixedNow) { Stock = catalog }.Apply(MakePack(null, (Path1, Frame(1000, "two"))));
        Assert.True(apply.Success, apply.Message);
        Assert.Equal(stock, fake.Files[Backup1]);

        fake.Files["/data/prospero-themes/backup/written.txt"] = Encoding.UTF8.GetBytes($"{Sha(Zst("earlier"))} {Dev1}\n");
        ApplyResult restore = new ThemeApplier(fake, () => FixedNow) { Stock = catalog }.RestoreOriginals();
        Assert.True(restore.Success, restore.Message);
        Assert.Equal(stock, fake.Files[Dev1]);
        Assert.Equal(stock, fake.Files[Backup1]);
    }

    [Fact]
    public void The_stock_catalog_knows_each_files_size()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "catalog", "12.40.json"));
        StockCatalog catalog = StockCatalog.Parse(json);
        Assert.Equal(105_968_720, catalog.StockBytes("vsh_asset/Sce.PlayStation.PUI_UI3.rco"));
        Assert.Contains("vsh_asset/bg_hub_default.dds", catalog.Paths);
        Assert.Equal(0, CatalogWith(Path1, Live("x")).StockBytes(Path1));
    }

    // ---- backups that look like a theme's (no catalog) ------------------------

    private const string Rco = "vsh_asset/Sce.PlayStation.PUI_UI3.rco";
    private const string RcoDevice = "/system_ex/" + Rco;
    private const string RcoBackup = "/data/prospero-themes/backup/original/" + Rco;
    private const string TileA = "vsh_asset/bg_NPXS40037.dds.zst", TileB = "vsh_asset/bg_NPXS40047.dds.zst";

    private static void Index(FakeDeviceFiles fake, params string[] logicals) =>
        fake.Files["/data/prospero-themes/backup/index.txt"] = Encoding.UTF8.GetBytes(string.Join("\n", logicals) + "\n");

    [Fact]
    public void An_icon_container_backup_without_the_theme_icons_titles_is_never_put_back()
    {
        byte[] themed = StockLookTests.Container([]);
        var fake = DeviceWithLive((RcoDevice, StockLookTests.Container(["utility/iconid_extra"])));
        fake.Files[RcoBackup] = themed;
        Index(fake, Rco);

        ThemeApplier applier = Applier(fake);
        ApplyResult result = applier.RestoreOriginals();

        Assert.Equal(StockLookTests.Container(["utility/iconid_extra"]), fake.Files[RcoDevice]);
        Assert.Equal(new[] { Rco }, applier.NoOriginal);
        Assert.Contains("no original", result.Message);
        Assert.False(fake.Files.ContainsKey("/data/prospero-themes/backup/originals.txt")
            && Encoding.UTF8.GetString(fake.Files["/data/prospero-themes/backup/originals.txt"]).Contains(Sha(themed)));
    }

    [Fact]
    public void A_stock_looking_icon_container_backup_is_put_back()
    {
        byte[] stock = StockLookTests.StockContainer();
        var fake = DeviceWithLive((RcoDevice, StockLookTests.Container([])));
        fake.Files[RcoBackup] = stock;
        Index(fake, Rco);

        ThemeApplier applier = Applier(fake);
        Assert.True(applier.RestoreOriginals().Success);

        Assert.Equal(stock, fake.Files[RcoDevice]);
        Assert.Empty(applier.NoOriginal);
    }

    [Fact]
    public void Tile_background_backups_with_one_picture_are_never_put_back()
    {
        byte[] theme = Frame(1000, "other-tool");
        var fake = DeviceWithLive(("/system_ex/" + TileA, Frame(1000, "live-a")), ("/system_ex/" + TileB, Frame(1000, "live-b")));
        fake.Files["/data/prospero-themes/backup/original/" + TileA] = theme;
        fake.Files["/data/prospero-themes/backup/original/" + TileB] = theme;
        Index(fake, TileA, TileB);

        ThemeApplier applier = Applier(fake);
        applier.RestoreOriginals();

        Assert.Equal(Frame(1000, "live-a"), fake.Files["/system_ex/" + TileA]);
        Assert.Equal(new[] { TileA, TileB }, applier.NoOriginal.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void A_first_apply_keeps_no_backup_of_tile_backgrounds_another_tool_gave_one_picture()
    {
        byte[] theme = Frame(1000, "other-tool");
        var fake = DeviceWithLive(("/system_ex/" + TileA, theme), ("/system_ex/" + TileB, theme));

        ThemeApplier applier = Applier(fake);
        ApplyResult result = applier.Apply(MakePack(null, (TileA, Frame(1000, "ours"))));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Frame(1000, "ours"), fake.Files["/system_ex/" + TileA]);
        Assert.False(fake.Files.ContainsKey("/data/prospero-themes/backup/original/" + TileA));
        Assert.Equal(new[] { TileA }, applier.NoOriginal);
    }

    [Fact]
    public void A_catalog_hash_wins_over_the_look()
    {
        byte[] themed = StockLookTests.Container([]);
        var fake = DeviceWithLive((RcoDevice, StockLookTests.StockContainer()));
        fake.Files[RcoBackup] = themed;
        Index(fake, Rco);

        // A catalog that (wrongly or not) lists this container as the stock one: it decides, not the titles.
        var applier = new ThemeApplier(fake, () => FixedNow) { Stock = CatalogWith(Rco, themed) };
        Assert.True(applier.RestoreOriginals().Success);

        Assert.Equal(themed, fake.Files[RcoDevice]);
        Assert.Empty(applier.NoOriginal);
    }

    [Fact]
    public void A_tile_icon_without_a_backup_comes_back_from_the_shipped_app()
    {
        // 13.00: the tile icons live in /mnt/rnps/apps, the firmware keeps its own copy in /system_ex/rnps/apps.
        byte[] stock = PreviewKit.Png(4, 4, 0, 0, 255), theme = PreviewKit.Png(4, 4, 255, 0, 0);
        var fake = DeviceWithLive((LiveStoreTile, stock), (ShippedStoreTile, stock));
        fake.Directories.Add("/mnt/rnps/apps/NPXS40047");
        Assert.True(Applier(fake).Apply(MakePack(null, (StoreTile, theme))).Success);
        foreach (string path in fake.Files.Keys.Where(k => k.StartsWith("/data/prospero-themes/backup/original/", StringComparison.Ordinal)).ToList())
            fake.Files.Remove(path);

        ApplyResult result = Applier(fake).RestoreOriginals();

        Assert.True(result.Success, result.Message);
        Assert.Equal(stock, fake.Files[LiveStoreTile]);
        Assert.Equal(stock, fake.Files["/data/prospero-themes/backup/original/" + StoreTile]);
        Assert.DoesNotContain("no original", result.Message);
    }

    [Fact]
    public void A_tile_icon_applied_again_without_its_backup_backs_up_the_shipped_copy()
    {
        byte[] stock = PreviewKit.Png(4, 4, 0, 0, 255), theme = PreviewKit.Png(4, 4, 255, 0, 0), other = PreviewKit.Png(4, 4, 0, 255, 0);
        var fake = DeviceWithLive((LiveStoreTile, stock), (ShippedStoreTile, stock));
        fake.Directories.Add("/mnt/rnps/apps/NPXS40047");
        Assert.True(Applier(fake).Apply(MakePack(null, (StoreTile, theme))).Success);
        fake.Files.Remove("/data/prospero-themes/backup/original/" + StoreTile);

        ThemeApplier again = Applier(fake);
        Assert.True(again.Apply(MakePack(null, (StoreTile, other))).Success);

        Assert.Empty(again.NoOriginal);
        Assert.Equal(stock, fake.Files["/data/prospero-themes/backup/original/" + StoreTile]);
    }

    [Theory]
    [InlineData("13.000.012", "13.00")]
    [InlineData("9.600.004", "9.60")]
    [InlineData("13.600.007", "13.60")]
    [InlineData("13.00", null)]
    [InlineData(null, null)]
    public void The_catalog_of_a_firmware_is_named_like_theme_pack_names_it(string? version, string? name) =>
        Assert.Equal(name, StockCatalog.FirmwareName(version));

    [Fact]
    public void The_stock_catalog_knows_the_assets_and_the_icon_container()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "catalog", "13.00.json"));
        StockCatalog catalog = StockCatalog.Parse(json);
        Assert.True(catalog.TryStock("vsh_asset/bgm_home.at9", out _));
        Assert.True(catalog.TryStock("vsh_asset/Sce.PlayStation.PUI_UI3.rco", out string rco));
        Assert.StartsWith("6050861c", rco);
        Assert.Equal(0, StockCatalog.Parse("not json").Count);
    }

    [Theory]
    [InlineData("/system_ex/vsh_asset/bgm_home.at9", "vsh_asset/bgm_home.at9")]
    [InlineData("/mnt/rnps/apps/NPXS40047/appdb/default/icon0.png", "rnps/NPXS40047/appdb/default/icon0.png")]
    [InlineData("/system_ex/rnps/apps/NPXS40047/appdb/default/icon0.png", "rnps/NPXS40047/appdb/default/icon0.png")]
    [InlineData("/user/app/PPSA01650/sce_sys/pic0.dds", "userapp/PPSA01650/sce_sys/pic0.dds")]
    [InlineData("/system_ex/vsh_asset/../x", null)]
    [InlineData("/data/prospero-themes/state.json", null)]
    public void TryLogical_is_the_inverse_of_TryResolve(string device, string? logical)
    {
        Assert.Equal(logical is not null, AssetRoots.TryLogical(device, out string found));
        if (logical is not null) Assert.Equal(logical, found);
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

        /// <summary>Bytes free that <see cref="FreeBytes"/> reports for every path; -1 is unknown, then nothing is checked.</summary>
        public long Free { get; set; } = -1;

        /// <summary>A write to this path stores the first half of the data, then throws as a full disk does.</summary>
        public string? FailWriteTo { get; set; }

        /// <summary>Paths deleted with DeleteFile that existed, in order.</summary>
        public List<string> Deleted { get; } = new();

        public long FreeBytes(string path) => Free;

        /// <summary>Whether the device patches files in place (ThemeHelper v11); off by default, as for a device that cannot.</summary>
        public bool CanPatch { get; set; }

        /// <summary>Every in-place patch as (path, offset, length), in order.</summary>
        public List<(string Path, long Offset, int Length)> Patches { get; } = new();

        /// <summary>The nth patch (from 1) throws before it writes anything, once, as a helper that stops half-way would.</summary>
        public int FailPatchNumber { get; set; }

        private int _patchCalls;

        void IDeviceFiles.PatchFile(string path, long offset, ReadOnlySpan<byte> data)
        {
            if (!Files.TryGetValue(path, out byte[]? bytes) || offset < 0 || offset + data.Length > bytes.Length)
                throw new InvalidOperationException($"patch {path} at {offset} failed: PATCH rc=-22");
            if (FailPatchNumber > 0 && ++_patchCalls == FailPatchNumber)
            {
                FailPatchNumber = 0;
                _patchCalls = 0;
                throw new InvalidOperationException($"patch {path} at {offset} failed: connection lost");
            }
            data.CopyTo(bytes.AsSpan((int)offset));
            Patches.Add((path, offset, data.Length));
        }

        bool IDeviceFiles.CanPatch => CanPatch;

        public void WriteAllBytes(string path, byte[] data)
        {
            if (RefusedWrites.Contains(path))
                throw new InvalidOperationException($"write {path} failed: refused");
            Writes.Add(path);
            if (FailWriteTo == path)
            {
                Files[path] = data[..(data.Length / 2)];
                throw new IOException("no space left on device");
            }
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

        public void DeleteFile(string path)
        {
            if (Files.Remove(path)) Deleted.Add(path);
        }

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
