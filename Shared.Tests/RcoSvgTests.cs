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
        public bool Exists(string path) => Files.ContainsKey(path);
        public byte[] ReadAllBytes(string path) => (byte[])Files[path].Clone();
        public void WriteAllBytes(string path, byte[] data) { Writes.Add(path); Files[path] = (byte[])data.Clone(); }
        public void Move(string from, string to) { Files[to] = Files[from]; Files.Remove(from); }
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
