using System.Text;
using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class LookTests
{
    private static LookLayer Layer(string pack, ThemePart parts) => new(pack, "id-" + pack, "Name " + pack, parts);

    [Fact]
    public void A_look_survives_writing_and_reading_and_a_bad_file_reads_as_unknown()
    {
        var look = new List<LookLayer> { Layer("/data/a.p5theme", ThemePart.Background | ThemePart.Sounds), Layer("", ThemePart.Icons) };

        List<LookLayer>? read = Look.Parse(Look.Serialize(look));

        Assert.Equal(look, read);
        Assert.Contains("\"parts\"", Look.Serialize(look));
        Assert.Null(Look.Parse(null));
        Assert.Null(Look.Parse("not json"));
        Assert.Null(Look.Parse("{\"layers\": 3}"));
        Assert.Empty(Look.Parse("{\"layers\": []}")!);
    }

    [Fact]
    public void Replacing_keeps_one_layer_and_combining_puts_the_pack_on_top_once()
    {
        var a = Layer("a", ThemePart.Background);
        var b = Layer("b", ThemePart.Icons);

        Assert.Equal(new[] { b }, Look.Applied(new[] { a }, b, combine: false));
        Assert.Equal(new[] { a, b }, Look.Applied(new[] { a }, b, combine: true));
        // The same pack again moves up instead of showing twice.
        var a2 = Layer("a", ThemePart.Background | ThemePart.Tiles);
        Assert.Equal(new[] { b, a2 }, Look.Applied(new[] { a, b }, a2, combine: true));
        // Without a look file (01.000.020 and before) the new layer is all that is known.
        Assert.Equal(new[] { b }, Look.Applied(null, b, combine: true));
    }

    [Fact]
    public void Restoring_parts_takes_them_out_of_every_layer()
    {
        var look = new[] { Layer("a", ThemePart.Background | ThemePart.Icons), Layer("b", ThemePart.Icons) };

        List<LookLayer> left = Look.Without(look, ThemePart.Icons);

        Assert.Equal(new[] { Layer("a", ThemePart.Background) }, left);
        Assert.Equal(ThemePart.Background | ThemePart.Icons, Look.Parts(look));
        Assert.Empty(Look.Without(look, ThemePart.All));
        Assert.Equal(new[] { look[1] }, Look.WithoutPack(look, "a"));
    }

    [Fact]
    public void A_backup_belongs_to_every_part_whose_targets_write_that_file()
    {
        // The targets of every fixture and of the shapes the stores make, with the logical paths an apply backs up for
        // them: a restore of a part must reach each file a target of that part wrote.
        var cases = new List<(ThemePackTarget Target, string Logical)>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.p5theme"))
            foreach (ThemePackTarget t in ThemeLibraryEntry.Inspect(file, File.ReadAllBytes(file)).Manifest.Targets)
                cases.Add((t, t.Path));

        ThemePackTarget T(string path, string layer) => new(path, layer, "f", 1, new string('0', 64));
        ThemePackTarget hub = T(AssetRoots.HubBackground, "background");
        cases.Add((hub, AssetRoots.HubBackground));
        cases.Add((hub, AssetRoots.Uncompressed(AssetRoots.HubBackground)!));
        foreach (string tile in AssetRoots.SystemTileBackgrounds) { cases.Add((hub, tile)); cases.Add((hub, AssetRoots.Uncompressed(tile)!)); }
        foreach (ThemePackTarget app in new[] { T(AssetRoots.AllAppsBackground, "background"), T("apps/PPSA01650/pic0.dds", "background") })
        {
            cases.Add((app, AssetRoots.UserApp + "/PPSA01650/sce_sys/pic0.dds"));
            cases.Add((app, AssetRoots.AppMeta + "/PPSA01650/pic0.dds"));
        }
        cases.Add((T("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "svg"), "vsh_asset/Sce.PlayStation.PUI_UI3.rco"));
        cases.Add((T("vsh_asset/Sce.PlayStation.PUI_UI3.rco", "vag"), "vsh_asset/Sce.PlayStation.PUI_UI3.rco"));
        cases.Add((T("rnps/NPXS40047/appdb/default/icon0.png", "icon"), "rnps/NPXS40047/appdb/default/icon0.png"));
        cases.Add((T("vsh_asset/bgm_home.at9", "background"), "vsh_asset/bgm_home.at9"));
        cases.Add((T("vsh_asset/bg_image_load_error.dds.zst", "background"), "vsh_asset/bg_image_load_error.dds.zst"));

        foreach ((ThemePackTarget target, string logical) in cases)
        {
            ThemePart part = ThemeParts.Of(target);
            Assert.True((ThemeParts.OfLogical(logical) & part) == part, $"{target.Path} ({target.Layer}) is {part}, its file {logical} is {ThemeParts.OfLogical(logical)}");
        }
    }

    [Fact]
    public void A_title_image_named_directly_is_an_app_background()
    {
        ThemePackTarget T(string path) => new(path, "background", "f", 1, new string('0', 64));
        Assert.Equal(ThemePart.AppBackgrounds, ThemeParts.Of(T("appmeta/PPSA99169/pic0.dds")));
        Assert.Equal(ThemePart.AppBackgrounds, ThemeParts.Of(T("userapp/PPSA99169/sce_sys/pic0.dds")));
        Assert.Equal(ThemePart.Background, ThemeParts.Of(T("vsh_asset/bg_hub_default.dds.zst")));
    }
}
