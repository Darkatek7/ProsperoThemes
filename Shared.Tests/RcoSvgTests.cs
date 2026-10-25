using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;
using SharpProspero.Compression;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class RcoSvgTests
{
    private const string RcoPath = "vsh_asset/Sce.PlayStation.PUI_UI3.rco";
    private const string RcoDevice = "/system_ex/vsh_asset/Sce.PlayStation.PUI_UI3.rco";
    private const string RcoBackup = "/data/prospero-themes/backup/original/vsh_asset/Sce.PlayStation.PUI_UI3.rco";

    private static string Icon(string name, string fill) =>
        $"<svg viewBox=\"0 0 64 64\" xmlns=\"http://www.w3.org/2000/svg\">\r\n  <title>utility/{name}</title>\r\n" +
        $"  <path d=\"M10 10h44v44H10z\" fill=\"{fill}\"/>\r\n  <!-- padding comment to give the slot room -->\r\n</svg>";

    /// <summary>A stand-in container: binary header, two named icons, binary tail.</summary>
    private static byte[] Container() =>
        new byte[] { 0x52, 0x43, 0x4F, 0x46, 0, 1, 2, 3 }
            .Concat(Encoding.UTF8.GetBytes(Icon("iconid_settings", "#FFFFFF")))
            .Concat(new byte[] { 0, 0, 7, 7 })
            .Concat(Encoding.UTF8.GetBytes(Icon("iconid_notification", "#FFFFFF")))
            .Concat(new byte[] { 9, 9, 9 }).ToArray();

    private static readonly byte[] Star =
        Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 64 64\"><path fill=\"#f00\" d=\"M32 6l8 16 18 2-13 12 3 18-16-9-16 9 3-18L6 24l18-2z\"/></svg>");

    [Fact]
    public void Replace_keeps_the_container_size_and_everything_around_the_icon()
    {
        byte[] stock = Container();
        byte[] patched = (byte[])stock.Clone();

        RcoSvg.Replace(patched, "iconid_settings", Star);

        Assert.Equal(stock.Length, patched.Length);
        (int o, int n) = RcoSvg.Find(stock, "iconid_settings");
        Assert.Equal(stock.AsSpan(0, o).ToArray(), patched.AsSpan(0, o).ToArray());
        Assert.Equal(stock.AsSpan(o + n).ToArray(), patched.AsSpan(o + n).ToArray());
        string icon = Encoding.UTF8.GetString(patched, o, n);
        Assert.StartsWith("<svg", icon);
        Assert.EndsWith("</svg>", icon);
        Assert.Contains("#f00", icon);
    }

    [Fact]
    public void Too_large_or_unnamed_replacements_are_refused()
    {
        byte[] c = Container();
        Assert.Throws<ThemePackException>(() => RcoSvg.Replace(c, "iconid_settings", Encoding.UTF8.GetBytes("<svg>" + new string('x', 5000) + "</svg>")));
        Assert.Throws<ThemePackException>(() => RcoSvg.Replace(c, "iconid_missing", Star));
        Assert.Throws<ThemePackException>(() => RcoSvg.Replace(c, "iconid_settings", "<png/>"u8));
    }

    private static ThemePack SvgPack(string id, params (string Resource, byte[] Svg)[] icons)
    {
        var zip = new ZipBuilder();
        var entries = new List<string>();
        foreach (var (resource, svg) in icons)
        {
            string file = $"icons/{resource}.svg";
            zip.Add(file, svg);
            entries.Add($$"""
                {"path":"{{RcoPath}}","layer":"svg","resource":"{{resource}}","file":"{{file}}",
                 "bytes":{{svg.Length}},"sha256":"{{ThemePack.Hash(svg)}}"}
                """);
        }
        zip.AddText("theme.json", $$"""
            {"format":2,"id":"{{id}}","name":"Icons","author":"tests","version":"1.0",
             "description":"svg","unmapped":"keep","targets":[{{string.Join(",", entries)}}]}
            """);
        return ThemePack.Open(zip.ToArray());
    }

    [Fact]
    public void Svg_pack_validates_and_patches_several_icons_in_one_write()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        var pack = SvgPack("a", ("iconid_settings", Star), ("iconid_notification", Star));
        Assert.False(pack.Validate().HasErrors, string.Join("; ", pack.Validate().Errors.Select(e => e.Message)));

        ApplyResult result = new ThemeApplier(files, () => 0).Apply(pack);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Container(), files.Files[RcoBackup]);
        Assert.Equal(1, files.Writes.Count(w => w == RcoDevice + ".tmp"));
        byte[] live = files.Files[RcoDevice];
        Assert.Equal(Container().Length, live.Length);
        // The replacement carries no <title>, so it is located through the stock layout, which never moves.
        (int o, int n) = RcoSvg.Find(Container(), "iconid_settings");
        Assert.Contains("#f00", Encoding.UTF8.GetString(live, o, n));
    }

    [Fact]
    public void An_icon_this_container_does_not_have_is_skipped_and_the_rest_is_applied()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();

        ApplyResult result = new ThemeApplier(files, () => 0).Apply(
            SvgPack("a", ("iconid_settings", Star), ("iconid_from_another_firmware", Star)));

        Assert.True(result.Success, result.Message);
        Assert.Contains("iconid_from_another_firmware", result.Message);
        (int o, int n) = RcoSvg.Find(Container(), "iconid_settings");
        Assert.Contains("#f00", Encoding.UTF8.GetString(files.Files[RcoDevice], o, n));
    }

    [Fact]
    public void The_untitled_search_icon_is_only_taken_by_position_when_it_is_the_13_00_one()
    {
        // 900 untitled icons: #875 exists, but it is not the 13.00 magnifier (another order, another firmware).
        byte[] container = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 900).Select(i => $"<svg><path d=\"M{i}\"/></svg>")));
        var error = Assert.Throws<ThemePackException>(() => RcoSvg.Find(container, "iconid_search"));
        Assert.Contains("not at #875", error.Message);
    }

    // The power menu's icons (FW 13.00): untitled, right after iconid_power_options.
    private static readonly (string Slot, int Index, int Length)[] PowerMenu =
    [
        ("iconid_power_restart", 705, 598),
        ("iconid_power_off", 708, 452),
    ];

    /// <summary>A container of 720 untitled icons; the one at <paramref name="index"/> is <paramref name="icon"/> when given.</summary>
    private static byte[] Untitled720(int index, string? icon) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 720).Select(i => i == index && icon is not null ? icon : $"<svg><path d=\"M{i}\"/></svg>")));

    private static string UntitledOfLength(int length)
    {
        const string head = "<svg><path d=\"M0 0", tail = "\"/></svg>";
        return head + new string('1', length - head.Length - tail.Length) + tail;
    }

    [Fact]
    public void The_power_menus_icons_are_known_by_the_position_length_and_hash_measured_on_13_00()
    {
        foreach (var (slot, index, length) in PowerMenu)
        {
            Assert.True(RcoSvg.Aliases.TryGetValue(slot, out RcoSvg.Untitled alias), slot);
            Assert.Equal(index, alias.Index);
            Assert.Equal(length, alias.Length);
            Assert.Matches("^[0-9a-f]{64}$", alias.Sha256);
        }
        Assert.Equal(3, RcoSvg.Aliases.Count);   // the search icon and the two above
    }

    [Fact]
    public void An_untitled_icon_is_found_where_the_alias_says_and_refused_when_anything_differs()
    {
        // The real icons cannot be in the repository, so a stand-in of the same length gets its own hash.
        foreach (var (slot, index, length) in PowerMenu)
        {
            string icon = UntitledOfLength(length);
            var alias = new RcoSvg.Untitled(index, length, ThemePack.Hash(Encoding.UTF8.GetBytes(icon)));
            byte[] container = Untitled720(index, icon);

            (int offset, int found) = RcoSvg.FindUntitled(container, slot, alias);
            Assert.Equal(length, found);
            Assert.Equal(icon, Encoding.UTF8.GetString(container, offset, found));

            // Another icon at that position (same length, other content), a shifted order, a titled icon and a short one: all refused.
            string other = UntitledOfLength(length).Replace("1", "2");
            Assert.Throws<ThemePackException>(() => RcoSvg.FindUntitled(Untitled720(index, other), slot, alias));
            Assert.Throws<ThemePackException>(() => RcoSvg.FindUntitled(Untitled720(index + 1, icon), slot, alias));
            Assert.Throws<ThemePackException>(() => RcoSvg.FindUntitled(Untitled720(index, icon.Replace("<svg>", "<svg><title>x</title>")), slot, alias));
            var error = Assert.Throws<ThemePackException>(() => RcoSvg.FindUntitled(Untitled720(index, UntitledOfLength(length - 1)), slot, alias));
            Assert.Contains($"not at #{index}", error.Message);
        }
    }

    [Fact]
    public void The_power_menus_icons_are_refused_in_a_container_that_is_not_the_13_00_one()
    {
        byte[] container = Untitled720(-1, null);
        foreach (var (slot, index, _) in PowerMenu)
        {
            var error = Assert.Throws<ThemePackException>(() => RcoSvg.Find(container, slot));
            Assert.Contains($"not at #{index}", error.Message);
        }
    }

    [Fact]
    public void A_second_icon_theme_starts_from_stock_and_restore_brings_stock_back()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        var applier = new ThemeApplier(files, () => 0);
        Assert.True(applier.Apply(SvgPack("a", ("iconid_settings", Star))).Success);

        Assert.True(applier.Apply(SvgPack("b", ("iconid_notification", Star))).Success);
        string live = Encoding.UTF8.GetString(files.Files[RcoDevice]);
        (int o, int n) = RcoSvg.Find(files.Files[RcoDevice], "iconid_settings");
        Assert.Contains("#FFFFFF", live.Substring(o, n));   // theme a's gear is gone

        Assert.True(applier.RestoreOriginals().Success);
        Assert.Equal(Container(), files.Files[RcoDevice]);
    }

    [Fact]
    public void Kept_icon_themes_combine_in_one_container_and_a_theme_can_change_its_own_icon_again()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        Assert.True(new ThemeApplier(files, () => 0).Apply(SvgPack("a", ("iconid_settings", Star))).Success);

        var keep = new ThemeApplier(files, () => 0) { KeepEarlierThemes = true };
        Assert.True(keep.Apply(SvgPack("b", ("iconid_notification", Star))).Success);
        // Patched slots carry no title any more: they are found by where the stock copy has them.
        string live = Encoding.UTF8.GetString(files.Files[RcoDevice]);
        Assert.Equal(Container().Length, files.Files[RcoDevice].Length);
        Assert.Equal(2, Count(live, "M32 6l8 16"));      // both themes' stars
        Assert.DoesNotContain("#FFFFFF", live);

        // A slot an earlier theme already patched can be patched again.
        byte[] green = Encoding.UTF8.GetBytes(Icon("iconid_settings", "#00FF00"));
        Assert.True(keep.Apply(SvgPack("c", ("iconid_settings", green))).Success);
        live = Encoding.UTF8.GetString(files.Files[RcoDevice]);
        Assert.Equal(1, Count(live, "M32 6l8 16"));
        Assert.Contains("#00FF00", live);

        Assert.True(keep.RestoreOriginals().Success);
        Assert.Equal(Container(), files.Files[RcoDevice]);
    }

    [Fact]
    public void Whole_files_are_held_one_at_a_time_and_always_let_go()
    {
        // Issue #13: on 12.40 the container is about 100 MB, and two in a row ran the app out of memory. The console
        // keeps FileBytes outside the managed heap; here they are counted.
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        var counted = new CountedFiles(files);
        ThemePack a = SvgPack("a", ("iconid_settings", Star));

        Assert.True(new ThemeApplier(counted, () => 0).Apply(a).Success);   // the backup made from the live file
        Assert.True(new ThemeApplier(counted, () => 0).Apply(SvgPack("b", ("iconid_notification", Star))).Success);
        // Kept themes: the stock copy is read, let go, and the live one read instead.
        Assert.True(new ThemeApplier(counted, () => 0) { KeepEarlierThemes = true }.Apply(a).Success);
        files.CorruptMoveTo = RcoDevice;   // a failed verification rolls back from the backup
        Assert.False(new ThemeApplier(counted, () => 0).Apply(SvgPack("c", ("iconid_settings", Star))).Success);
        Assert.True(new ThemeApplier(counted, () => 0).RestoreOriginals().Success);

        Assert.Equal(Container(), files.Files[RcoDevice]);
        Assert.True(counted.Reads >= 5);
        Assert.Equal(1, counted.MostLive);
        Assert.Equal(0, counted.Live);
    }

    /// <summary>Device files whose <see cref="IDeviceFiles.ReadFile"/> counts the buffers it hands out until they are disposed.</summary>
    private sealed class CountedFiles(MemoryDeviceFiles inner) : IDeviceFiles
    {
        public int Reads, Live, MostLive;

        public FileBytes ReadFile(string path)
        {
            byte[] data = inner.ReadAllBytes(path);
            Reads++;
            MostLive = Math.Max(MostLive, ++Live);
            return new Counted(this, data);
        }

        private sealed class Counted(CountedFiles owner, byte[] data) : FileBytes
        {
            private bool _disposed;
            public override Span<byte> Span => _disposed ? throw new ObjectDisposedException(nameof(Counted)) : data;
            public override void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                owner.Live--;
            }
        }

        public bool Exists(string path) => inner.Exists(path);
        public long Length(string path) => inner.Length(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public string Hash(string path) => inner.Hash(path);
        public void WriteAllBytes(string path, byte[] data) => inner.WriteAllBytes(path, data);
        public void Move(string from, string to) => inner.Move(from, to);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void CreateDirectoryRecursive(string path) => inner.CreateDirectoryRecursive(path);
        public void RemountSystemExRw() => inner.RemountSystemExRw();
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public void Icon_changes_say_they_show_after_a_restart_and_backgrounds_do_not()
    {
        // The home screen reads icons at start (app.db caches tile icons), so a restore that "did nothing" was only
        // waiting for a restart; backgrounds change right away.
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        files.Files[BgDevice] = LiveBackground;
        var applier = new ThemeApplier(files, () => 0);

        ApplyResult icons = applier.Apply(SvgPack("a", ("iconid_settings", Star)));
        ApplyResult restore = applier.RestoreOriginals();
        ApplyResult background = applier.Apply(ThemePack.Open(BackgroundOnly(Background("c"))));

        Assert.Contains("after the next restart", icons.Message);
        Assert.Contains("after the next restart", restore.Message);
        Assert.True(background.Success, background.Message);
        Assert.DoesNotContain("restart", background.Message);
    }

    private static byte[] BackgroundOnly(byte[] background)
    {
        var zip = new ZipBuilder();
        zip.Add("bg/hub.dds.zst", background);
        zip.AddText("theme.json", $$"""
            {"format":2,"id":"bg","name":"Bg","author":"tests","version":"1.0","description":"bg","unmapped":"keep",
             "targets":[{"path":"{{BgPath}}","layer":"background","file":"bg/hub.dds.zst",
               "bytes":{{background.Length}},"sha256":"{{ThemePack.Hash(background)}}"}]}
            """);
        return zip.ToArray();
    }

    private const string BgPath = "vsh_asset/bg_hub_default.dds.zst";
    private const string BgDevice = "/system_ex/vsh_asset/bg_hub_default.dds.zst";
    private static readonly byte[] LiveBackground = Encoding.ASCII.GetBytes("live-bg");

    /// <summary>One icon plus the home background.</summary>
    private static ThemePack IconAndBackgroundPack(byte[] background)
    {
        var zip = new ZipBuilder();
        zip.Add("icons/gear.svg", Star);
        zip.Add("bg/hub.dds.zst", background);
        zip.AddText("theme.json", $$"""
            {"format":2,"id":"mixed","name":"Mixed","author":"tests","version":"1.0","description":"svg and bg","unmapped":"keep",
             "targets":[
              {"path":"{{RcoPath}}","layer":"svg","resource":"iconid_settings","file":"icons/gear.svg",
               "bytes":{{Star.Length}},"sha256":"{{ThemePack.Hash(Star)}}"},
              {"path":"{{BgPath}}","layer":"background","file":"bg/hub.dds.zst",
               "bytes":{{background.Length}},"sha256":"{{ThemePack.Hash(background)}}"}]}
            """);
        return ThemePack.Open(zip.ToArray());
    }

    private static byte[] Background(string tag)
    {
        byte[] data = Encoding.ASCII.GetBytes("....payload-" + tag + new string('.', 256));
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD }.CopyTo(data, 0);
        return data;
    }

    [Fact]
    public void An_icon_container_too_large_for_the_helper_is_left_alone_and_the_rest_is_applied()
    {
        // Firmware before 13.00 has a container of about 100 MB: holding copies of it ran the app out of memory,
        // and the helper refuses to write it. It is skipped without being read or backed up.
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        files.Files[BgDevice] = LiveBackground;
        byte[] background = Background("a");

        ApplyResult result = new ThemeApplier(files, () => 0) { MaxFileBytes = Container().Length - 1 }
            .Apply(IconAndBackgroundPack(background));

        Assert.True(result.Success, result.Message);
        Assert.Contains("too large", result.Message);
        Assert.Equal(background, files.Files[BgDevice]);
        Assert.Equal(Container(), files.Files[RcoDevice]);
        Assert.DoesNotContain(RcoDevice, files.Reads);
        Assert.False(files.Files.ContainsKey(RcoBackup));
        Assert.DoesNotContain(files.Writes, w => w.StartsWith(RcoDevice, StringComparison.Ordinal));
    }

    [Fact]
    public void A_backed_up_file_too_large_for_the_helper_is_never_read_by_the_clean_start_or_restore()
    {
        // A backup of the large container from an earlier build: reading it and the live file (two copies of
        // 100 MB) would run out of memory, and the app cannot have changed the live file.
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        files.Files[BgDevice] = LiveBackground;
        Assert.True(new ThemeApplier(files, () => 0).Apply(SvgPack("a", ("iconid_settings", Star))).Success);
        files.Files[RcoDevice] = Container();   // that write never landed
        files.Reads.Clear();

        var applier = new ThemeApplier(files, () => 0) { MaxFileBytes = Container().Length - 1 };
        ApplyResult apply = applier.Apply(IconAndBackgroundPack(Background("b")));
        ApplyResult restore = applier.RestoreOriginals();

        Assert.True(apply.Success, apply.Message);
        Assert.True(restore.Success, restore.Message);
        Assert.DoesNotContain(RcoDevice, files.Reads);
        Assert.DoesNotContain(RcoBackup, files.Reads);
        Assert.Equal(Container(), files.Files[RcoDevice]);
        Assert.Equal(LiveBackground, files.Files[BgDevice]);
    }

    // The container is about 100 MB before firmware 13.00: the applier holds one whole copy of it at a time, and
    // compares and verifies it by streamed hashes (Hashes), never by a second whole read (Reads).

    [Fact]
    public void A_first_icon_apply_reads_the_container_whole_once_and_verifies_by_streamed_hashes()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();

        Assert.True(new ThemeApplier(files, () => 0).Apply(SvgPack("a", ("iconid_settings", Star))).Success);

        Assert.Equal(1, files.Reads.Count(r => r == RcoDevice));
        Assert.DoesNotContain(RcoBackup, files.Reads);
        Assert.Contains(RcoBackup, files.Hashes);   // the backup's read-back
        Assert.Contains(RcoDevice, files.Hashes);   // the written container's read-back
        Assert.Equal(Container(), files.Files[RcoBackup]);
    }

    [Fact]
    public void A_later_icon_apply_reads_only_the_stock_copy_whole()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        var applier = new ThemeApplier(files, () => 0);
        Assert.True(applier.Apply(SvgPack("a", ("iconid_settings", Star))).Success);
        files.Reads.Clear();

        Assert.True(applier.Apply(SvgPack("b", ("iconid_notification", Star))).Success);

        Assert.DoesNotContain(RcoDevice, files.Reads);
        Assert.Equal(1, files.Reads.Count(r => r == RcoBackup));
        (int o, int n) = RcoSvg.Find(Container(), "iconid_notification");
        Assert.Contains("#f00", Encoding.UTF8.GetString(files.Files[RcoDevice], o, n));
    }

    [Fact]
    public void The_clean_start_reads_an_unchanged_container_not_at_all_and_a_themed_one_only_from_its_backup()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        files.Files[BgDevice] = LiveBackground;
        var applier = new ThemeApplier(files, () => 0);
        Assert.True(applier.Apply(SvgPack("a", ("iconid_settings", Star))).Success);
        files.Reads.Clear();

        // A theme without icons puts the themed container back to stock first.
        Assert.True(applier.Apply(BackgroundPack(Background("a"))).Success);
        Assert.Equal(Container(), files.Files[RcoDevice]);
        Assert.DoesNotContain(RcoDevice, files.Reads);
        Assert.Equal(1, files.Reads.Count(r => r == RcoBackup));

        files.Reads.Clear();
        Assert.True(applier.Apply(BackgroundPack(Background("b"))).Success);
        Assert.DoesNotContain(RcoDevice, files.Reads);
        Assert.DoesNotContain(RcoBackup, files.Reads);
    }

    [Fact]
    public void Restore_reads_the_container_backup_whole_once_and_the_live_one_not_at_all()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        var applier = new ThemeApplier(files, () => 0);
        Assert.True(applier.Apply(SvgPack("a", ("iconid_settings", Star))).Success);
        files.Reads.Clear();

        ApplyResult restore = applier.RestoreOriginals();

        Assert.True(restore.Success, restore.Message);
        Assert.Equal(Container(), files.Files[RcoDevice]);
        Assert.DoesNotContain(RcoDevice, files.Reads);
        Assert.Equal(1, files.Reads.Count(r => r == RcoBackup));
    }

    [Fact]
    public void A_failed_write_after_the_container_rolls_the_container_back_to_stock()
    {
        var files = new MemoryDeviceFiles { CorruptMoveTo = BgDevice };
        files.Files[RcoDevice] = Container();
        files.Files[BgDevice] = LiveBackground;

        ApplyResult result = new ThemeApplier(files, () => 0).Apply(IconAndBackgroundPack(Background("a")));

        Assert.False(result.Success);
        Assert.Contains("Rolled back 2 target(s)", result.Message);
        Assert.Equal(Container(), files.Files[RcoDevice]);
        Assert.Equal(LiveBackground, files.Files[BgDevice]);
    }

    private static ThemePack BackgroundPack(byte[] background)
    {
        var zip = new ZipBuilder();
        zip.Add("bg/hub.dds.zst", background);
        zip.AddText("theme.json", $$"""
            {"format":2,"id":"bg","name":"Bg","author":"tests","version":"1.0","description":"bg","unmapped":"keep",
             "targets":[{"path":"{{BgPath}}","layer":"background","file":"bg/hub.dds.zst",
                         "bytes":{{background.Length}},"sha256":"{{ThemePack.Hash(background)}}"}]}
            """);
        return ThemePack.Open(zip.ToArray());
    }

    [Fact]
    public void Svg_target_without_resource_is_an_error()
    {
        var zip = new ZipBuilder();
        zip.Add("icons/x.svg", Star);
        zip.AddText("theme.json", $$"""
            {"format":2,"id":"x","name":"Icons","author":"tests","version":"1.0","description":"svg","unmapped":"keep",
             "targets":[{"path":"{{RcoPath}}","layer":"svg","file":"icons/x.svg","bytes":{{Star.Length}},"sha256":"{{ThemePack.Hash(Star)}}"}]}
            """);
        Assert.Contains(ThemePack.Open(zip.ToArray()).Validate().Errors, e => e.Message.Contains("resource"));
    }

    [Fact]
    public void Rnps_root_resolves_to_the_live_tile_metadata()
    {
        Assert.True(AssetRoots.TryResolve("rnps/NPXS40047/appdb/default/icon0.png", out string device, out _));
        Assert.Equal("/mnt/rnps/apps/NPXS40047/appdb/default/icon0.png", device);
    }

    /// <summary>In-memory device files with a write log.</summary>
    internal sealed class MemoryDeviceFiles : IDeviceFiles
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Writes { get; } = new();
        public List<string> Reads { get; } = new();
        public bool Exists(string path) => Files.ContainsKey(path);
        public long Length(string path) => Files.TryGetValue(path, out byte[]? bytes) ? bytes.Length : -1;
        /// <summary>Paths that exist but fail to read, as a file the app may not read whole.</summary>
        public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);
        public byte[] ReadAllBytes(string path)
        {
            Reads.Add(path);
            if (Unreadable.Contains(path)) throw new IOException($"read {path} failed");
            return (byte[])Files[path].Clone();
        }

        /// <summary>Paths hashed with Hash (streamed on the console, never held whole), in order.</summary>
        public List<string> Hashes { get; } = new();
        public string Hash(string path) { Hashes.Add(path); return ThemePack.Hash(Files[path]); }
        public void WriteAllBytes(string path, byte[] data) { Writes.Add(path); Files[path] = (byte[])data.Clone(); }
        /// <summary>The next Move onto this path stores corrupted bytes, so read-back verification fails once.</summary>
        public string? CorruptMoveTo { get; set; }

        public void Move(string from, string to)
        {
            byte[] bytes = Files[from];
            Files.Remove(from);
            if (CorruptMoveTo == to)
            {
                CorruptMoveTo = null;
                bytes = bytes.Select(b => (byte)(b ^ 0xFF)).ToArray();
            }
            Files[to] = bytes;
        }
        public void DeleteFile(string path) => Files.Remove(path);
        public void CreateDirectoryRecursive(string path) { }
        public void RemountSystemExRw() { }

        public IReadOnlyList<string> ListDirectories(string path) =>
            Files.Keys.Where(k => k.StartsWith(path, StringComparison.Ordinal) && k.IndexOf('/', path.Length) > path.Length)
                .Select(k => k.Substring(path.Length, k.IndexOf('/', path.Length) - path.Length)).Distinct().ToList();
    }
}

public class AllAppsBackgroundTests
{
    private static byte[] Dds(byte fill)
    {
        // A 3840x2160 BC7 header (DX10, DXGI 98) like every pic0.dds on 13.00; the pixels tell copies apart.
        var d = new byte[256];
        Array.Fill(d, fill, 148, d.Length - 148);
        "DDS "u8.CopyTo(d);
        BitConverter.GetBytes(2160).CopyTo(d, 12);
        BitConverter.GetBytes(3840).CopyTo(d, 16);
        "DX10"u8.CopyTo(d.AsSpan(84));
        BitConverter.GetBytes(98).CopyTo(d, 128);
        return d;
    }

    private static ThemePack Pack(byte[] pic0)
    {
        var zip = new ZipBuilder();
        zip.Add("bg/pic0.dds", pic0);
        zip.AddText("theme.json", $$"""
            {"format":2,"id":"all","name":"All","author":"tests","version":"1.0","description":"bg","unmapped":"keep",
             "targets":[{"path":"{{AssetRoots.AllAppsBackground}}","layer":"background","file":"bg/pic0.dds",
                         "bytes":{{pic0.Length}},"sha256":"{{ThemePack.Hash(pic0)}}"}]}
            """);
        return ThemePack.Open(zip.ToArray());
    }

    private static ThemePack PackWith(params (string Path, byte[] Data)[] targets)
    {
        var zip = new ZipBuilder();
        var entries = new List<string>();
        int n = 0;
        foreach (var (path, data) in targets)
        {
            string file = $"bg/{n++}.dds";
            zip.Add(file, data);
            entries.Add($$"""{"path":"{{path}}","layer":"background","file":"{{file}}","bytes":{{data.Length}},"sha256":"{{ThemePack.Hash(data)}}"}""");
        }
        zip.AddText("theme.json", $$"""
            {"format":2,"id":"per","name":"Per","author":"tests","version":"1.0","description":"bg","unmapped":"keep",
             "targets":[{{string.Join(",", entries)}}]}
            """);
        return ThemePack.Open(zip.ToArray());
    }

    [Fact]
    public void Per_app_background_wins_over_all_apps_and_reports_titles_without_one()
    {
        var files = new RcoSvgTests.MemoryDeviceFiles();
        files.Files["/user/app/PPSA00001/sce_sys/pic0.dds"] = Dds(1);
        files.Files["/user/app/PPSA00002/sce_sys/pic0.dds"] = Dds(2);
        byte[] all = Dds(8), one = Dds(9);
        var pack = PackWith(("allapps/pic0.dds", all), ("apps/PPSA00002/pic0.dds", one), ("apps/PPSA00003/pic0.dds", one));
        Assert.False(pack.Validate().HasErrors, string.Join("; ", pack.Validate().Errors.Select(e => e.Message)));

        ApplyResult result = new ThemeApplier(files, () => 0) { IncludeAllApps = true }.Apply(pack);

        Assert.True(result.Success, result.Message);
        Assert.Equal(all, files.Files["/user/app/PPSA00001/sce_sys/pic0.dds"]);
        Assert.Equal(one, files.Files["/user/app/PPSA00002/sce_sys/pic0.dds"]);
        Assert.Contains("PPSA00003", result.Message);
        Assert.False(files.Files.ContainsKey("/user/app/PPSA00003/sce_sys/pic0.dds"));
    }

    [Fact]
    public void Per_app_background_applies_even_without_the_all_apps_setting()
    {
        var files = new RcoSvgTests.MemoryDeviceFiles();
        files.Files["/user/appmeta/PPSA01650/pic0.dds"] = Dds(1);
        byte[] one = Dds(9);
        ApplyResult result = new ThemeApplier(files, () => 0).Apply(PackWith(("apps/PPSA01650/pic0.dds", one)));
        Assert.True(result.Success, result.Message);
        Assert.Equal(one, files.Files["/user/appmeta/PPSA01650/pic0.dds"]);
    }

    [Fact]
    public void By_default_titles_keep_their_own_background()
    {
        var files = new RcoSvgTests.MemoryDeviceFiles();
        byte[] stock = Dds(1);
        files.Files["/user/app/PPSA00001/sce_sys/pic0.dds"] = stock;
        files.Files["/system_ex/vsh_asset/bg_hub_default.dds.zst"] = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3 };
        ApplyResult result = new ThemeApplier(files, () => 0).Apply(Pack(Dds(9)));
        Assert.False(result.Success);                      // nothing applicable: only the all-apps target
        Assert.Equal(stock, files.Files["/user/app/PPSA00001/sce_sys/pic0.dds"]);
    }

    [Fact]
    public void All_apps_background_reaches_every_title_with_its_own_pic0_and_restores()
    {
        var files = new RcoSvgTests.MemoryDeviceFiles();
        byte[] stockA = Dds(1), stockB = Dds(2), theme = Dds(9);
        files.Files["/user/app/PPSA00001/sce_sys/pic0.dds"] = stockA;
        files.Files["/user/app/PPSA00002/sce_sys/param.json"] = new byte[] { 1 };      // no pic0: skipped
        files.Files["/user/appmeta/PPSA01650/pic0.dds"] = stockB;
        var pack = Pack(theme);
        Assert.False(pack.Validate().HasErrors, string.Join("; ", pack.Validate().Errors.Select(e => e.Message)));

        var applier = new ThemeApplier(files, () => 0) { IncludeAllApps = true };
        ApplyResult result = applier.Apply(pack);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Written.Count);
        Assert.Equal(theme, files.Files["/user/app/PPSA00001/sce_sys/pic0.dds"]);
        Assert.Equal(theme, files.Files["/user/appmeta/PPSA01650/pic0.dds"]);
        Assert.False(files.Files.ContainsKey("/user/app/PPSA00002/sce_sys/pic0.dds"));

        Assert.True(applier.RestoreOriginals().Success);
        Assert.Equal(stockA, files.Files["/user/app/PPSA00001/sce_sys/pic0.dds"]);
        Assert.Equal(stockB, files.Files["/user/appmeta/PPSA01650/pic0.dds"]);
    }
}
