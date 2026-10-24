using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class FirmwareReportTests
{
    private const string Hub = "/system_ex/vsh_asset/bg_hub_default.dds.zst";
    private const string HubBackup = "/data/prospero-themes/backup/original/vsh_asset/bg_hub_default.dds.zst";
    private const string Rco = "/system_ex/vsh_asset/Sce.PlayStation.PUI_UI3.rco";

    private static byte[] Frame(uint decodedSize, string tag)
    {
        var data = new byte[64];
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0xA0 }.CopyTo(data, 0);
        BitConverter.GetBytes(decodedSize).CopyTo(data, 5);
        Encoding.ASCII.GetBytes(tag).CopyTo(data, 9);
        return data;
    }

    private static JsonElement File(JsonDocument report, string path) =>
        report.RootElement.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == path);

    [Fact]
    public void The_store_answers_a_sent_report_with_a_code()
    {
        Assert.Equal("https://themes.example.org/api/firmware-reports", FirmwareReport.UploadUrl("https://themes.example.org/"));
        Assert.Equal("K7Q2MX", FirmwareReport.ParseCode("""{"code":"K7Q2MX"}"""));
        Assert.Null(FirmwareReport.ParseCode("""{"error":"send the app's firmware report (report 1 with a files list)"}"""));
        Assert.Null(FirmwareReport.ParseCode("""{"code":"k7<b>"}"""));
        Assert.Null(FirmwareReport.ParseCode("<html>"));
        Assert.Null(FirmwareReport.ParseCode(null));
    }

    [Fact]
    public void A_refused_report_says_why()
    {
        Assert.StartsWith("the store takes only a few reports an hour", FirmwareReport.SendProblem(429, """{"error":"rate limit reached, try again later"}"""));
        Assert.Equal("the store answered 413: Request body is too large", FirmwareReport.SendProblem(413, """{"error":"Request body is too large"}"""));
        Assert.Equal("the store answered 404", FirmwareReport.SendProblem(404, "<html>"));
    }

    [Fact]
    public void Describes_the_stock_copy_of_every_file_and_lists_the_icons()
    {
        var files = new RcoSvgTests.MemoryDeviceFiles();
        files.Files[Hub] = Frame(8_294_548, "theme");
        files.Files[HubBackup] = Frame(8_294_548, "stock");
        files.Files[Rco] = Encoding.UTF8.GetBytes("RCOF<svg><title>utility/iconid_settings</title></svg>..<svg><path/></svg>");
        string[] paths = [AssetRoots.HubBackground, FirmwareReport.IconContainer, FirmwareReport.TileIcons[0]];

        using JsonDocument report = JsonDocument.Parse(
            FirmwareReport.Build(files, "13.000.000", "01.000.002", "2026-10-17T18:00:00Z", paths));

        Assert.Equal("13.000.000", report.RootElement.GetProperty("firmware").GetString());
        JsonElement hub = File(report, AssetRoots.HubBackground);
        Assert.Equal("backup", hub.GetProperty("source").GetString());
        Assert.True(hub.GetProperty("themed").GetBoolean());
        Assert.Equal(ThemePack.Hash(Frame(8_294_548, "stock")), hub.GetProperty("sha256").GetString());
        Assert.Equal(8_294_548, hub.GetProperty("content_bytes").GetInt64());
        Assert.Equal(ThemePack.Hash(Frame(8_294_548, "theme")), hub.GetProperty("live_sha256").GetString());

        JsonElement icons = File(report, FirmwareReport.IconContainer).GetProperty("icons");
        Assert.Equal("utility/iconid_settings", icons[0].GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, icons[1].GetProperty("title").ValueKind);

        Assert.False(File(report, FirmwareReport.TileIcons[0]).GetProperty("exists").GetBoolean());
        Assert.Empty(files.Writes);   // a report only reads
        // With a backup, the live file is only hashed: an icon container of about 100 MB and its backup did not both
        // fit in memory (11.60 report).
        Assert.DoesNotContain(Hub, files.Reads);
        Assert.Contains(Hub, files.Hashes);
    }

    [Fact]
    public void A_backup_that_cannot_be_read_is_reported_with_its_error_and_the_live_file_is_described()
    {
        const string RcoBackup = "/data/prospero-themes/backup/original/vsh_asset/Sce.PlayStation.PUI_UI3.rco";
        var files = new RcoSvgTests.MemoryDeviceFiles();
        files.Files[Rco] = Encoding.UTF8.GetBytes("RCOF<svg><title>utility/iconid_settings</title></svg>");
        files.Files[RcoBackup] = Encoding.UTF8.GetBytes("RCOF stock");
        files.Unreadable.Add(RcoBackup);

        using JsonDocument report = JsonDocument.Parse(
            FirmwareReport.Build(files, "13.600.007", "01.000.017", "2026-10-22T18:00:00Z", [FirmwareReport.IconContainer]));

        JsonElement rco = File(report, FirmwareReport.IconContainer);
        Assert.Equal($"read {RcoBackup} failed", rco.GetProperty("backup_error").GetString());
        Assert.Equal("console", rco.GetProperty("source").GetString());
        Assert.Equal(files.Files[Rco].Length, rco.GetProperty("bytes").GetInt64());
        Assert.Equal("utility/iconid_settings", rco.GetProperty("icons")[0].GetProperty("title").GetString());
    }

    [Fact]
    public void Looks_at_the_catalog_and_everything_the_applier_adds_once()
    {
        string catalog = """{"firmware":"13.00","assets":[{"path":"vsh_asset/bg_hub_default.dds.zst"},{"path":"vsh_asset/bgm_home.at9"}]}""";
        var paths = FirmwareReport.Paths(FirmwareReport.CatalogPaths(catalog));
        Assert.Equal(paths.Count, paths.Distinct().Count());
        Assert.Contains("vsh_asset/bgm_home.at9", paths);
        Assert.Contains(AssetRoots.SystemTileBackgrounds[0], paths);
        Assert.Contains(FirmwareReport.IconContainer, paths);
        Assert.Empty(FirmwareReport.CatalogPaths("not json"));
        Assert.Equal("firmware-13.000.000.json", FirmwareReport.FileName("13.000.000"));
        Assert.Equal("firmware-unknown.json", FirmwareReport.FileName(null));
        Assert.Equal("firmware-a_b.json", FirmwareReport.FileName("a/b"));
    }

    [Fact]
    public void The_bundled_13_00_catalog_lists_assets()
    {
        string? root = FindRepo();
        if (root is null) return;   // a copy of the tests without the catalog
        var paths = FirmwareReport.CatalogPaths(System.IO.File.ReadAllText(Path.Combine(root, "catalog", "13.00.json")));
        Assert.Contains(AssetRoots.HubBackground, paths);
        Assert.True(paths.Count >= 40);
    }

    [Fact]
    public void Tile_icons_match_the_web_creators_list()
    {
        string? root = FindRepo();
        string file = root is null ? "" : Path.Combine(root, "web", "shared", "tile-icons.json");
        if (!System.IO.File.Exists(file)) return;   // the public repository has no web folder
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        string[] web = doc.RootElement.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(web, FirmwareReport.TileIcons);
    }

    private static string? FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "catalog")) && Directory.Exists(Path.Combine(dir.FullName, "Shared")))
                return dir.FullName;
        return null;
    }
}
