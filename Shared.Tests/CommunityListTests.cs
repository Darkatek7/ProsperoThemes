using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class CommunityListTests
{
    // The answer shape web/server returns for GET /api/themes.
    private const string Json = """
        {"items":[
          {"id":"abc123def456","name":"Night Drive","author":"Mia","version":"1.0","description":"Neon",
           "size":14009587,"likes":12,"downloads":340,"createdAt":"2026-10-13T12:00:00Z","status":"approved",
           "summary":{"fallback":true,"allApps":true,"perApp":2,"icons":3,"tiles":0},
           "previewUrl":"/api/themes/abc123def456/preview.png","thumbUrl":"/api/themes/abc123def456/thumb.jpg?v=0",
           "downloadUrl":"/api/themes/abc123def456/download"},
          {"id":"","name":"broken"},
          {"id":"zz","name":"No preview","author":"A","version":"2","description":"","size":1,"likes":0,"downloads":0,
           "summary":{},"previewUrl":null,"downloadUrl":"https://cdn.example/zz.p5theme"}
        ],"page":1,"pageSize":24,"total":2}
        """;

    [Fact]
    public void Parses_items_makes_urls_absolute_and_skips_broken_entries()
    {
        var list = CommunityList.Parse(Json, "https://themes.example/");
        Assert.Equal(2, list.Count);
        CommunityTheme a = list[0];
        Assert.Equal("Night Drive", a.Name);
        Assert.Equal(12, a.Likes);
        Assert.Equal(340, a.Downloads);
        Assert.Equal("https://themes.example/api/themes/abc123def456/preview.png", a.PreviewUrl);
        Assert.Equal("https://themes.example/api/themes/abc123def456/thumb.jpg?v=0", a.PictureUrl);   // the small JPEG
        Assert.Equal("https://themes.example/api/themes/abc123def456/download", a.DownloadUrl);
        Assert.Equal("Background · App background · 2 app images · 3 icons", a.Contents);
        Assert.Null(list[1].PreviewUrl);
        Assert.Null(list[1].PictureUrl);
        Assert.Equal("https://cdn.example/zz.p5theme", list[1].DownloadUrl);
    }

    [Fact]
    public void Parses_a_real_server_answer()
    {
        // Written by web/server `npm run dump-fixture` from an actual upload of blue-hour.p5theme, so a
        // renamed field on either side fails here.
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "store-list.json"));
        var list = CommunityList.Parse(json, "http://10.0.0.22:8080");
        CommunityTheme t = Assert.Single(list);
        Assert.Equal("Blue Hour", t.Name);
        Assert.Equal("Prospero Studio", t.Author);
        Assert.True(t.Size > 0);
        Assert.Equal("http://10.0.0.22:8080/api/themes/" + t.Id + "/download", t.DownloadUrl);
        Assert.Equal("http://10.0.0.22:8080/api/themes/" + t.Id + "/preview.png", t.PreviewUrl);
        Assert.Equal("http://10.0.0.22:8080/api/themes/" + t.Id + "/thumb.jpg?v=0", t.PictureUrl);
        Assert.Equal("Background", t.Contents);
    }

    [Fact]
    public void Sort_orders_have_english_labels()
    {
        Assert.Equal("Top rated", CommunityList.Label(CommunitySort.Top));
        Assert.Equal("Most downloaded", CommunityList.Label(CommunitySort.Downloads));
        Assert.Equal("New", CommunityList.Label(CommunitySort.New));
    }

    [Fact]
    public void Contents_uses_singular_and_plural_nouns()
    {
        var one = new CommunityTheme("a", "A", "", "1", "", 1, 0, 0, null, "u", false, false, 1, 1, 1);
        Assert.Equal("1 app image · 1 icon · 1 tile icon", one.Contents);
        var many = one with { PerApp = 2, Icons = 5, Tiles = 3 };
        Assert.Equal("2 app images · 5 icons · 3 tile icons", many.Contents);
        Assert.Equal("Theme", (one with { PerApp = 0, Icons = 0, Tiles = 0 }).Contents);
    }

    [Fact]
    public void Builds_list_and_like_urls_and_reads_like_counts()
    {
        Assert.Equal("https://t.example/api/themes?sort=downloads&page=1&pageSize=24", CommunityList.ListUrl("https://t.example/", CommunitySort.Downloads));
        Assert.Equal("https://t.example/api/themes/a%20b/like", CommunityList.LikeUrl("https://t.example", "a b"));
        Assert.Equal(13, CommunityList.ParseLikes("""{"likes":13,"liked":true}"""));
        Assert.Equal(-1, CommunityList.ParseLikes("nope"));
    }

    // ---- Categories -------------------------------------------------------------------------------------------------

    private static string ItemWith(string id, string categoriesField) =>
        "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"author\":\"A\",\"version\":\"1\",\"description\":\"\",\"size\":1,\"likes\":0,\"downloads\":0," +
        "\"summary\":{},\"previewUrl\":null,\"downloadUrl\":\"/api/themes/" + id + "/download\"" + categoriesField + "}";

    [Fact]
    public void Parses_the_categories_of_each_theme_keeping_known_ids_in_the_stores_order()
    {
        string json = "{\"items\":[" + string.Join(",",
            ItemWith("a", ",\"categories\":[\"anime\",\"games\"]"),
            ItemWith("b", ",\"categories\":[\"sci-fi\",\"made-up\",7,null,\"sci-fi\",\"space\",\"pixel-art\"]"),
            ItemWith("c", ""),
            ItemWith("d", ",\"categories\":[]"),
            ItemWith("e", ",\"categories\":\"anime\""),
            ItemWith("f", ",\"categories\":null")) + "]}";
        var list = CommunityList.Parse(json, "https://t.example");
        Assert.Equal(6, list.Count);
        Assert.Equal(["anime", "games"], list[0].Categories);
        Assert.Equal(["sci-fi", "space", "pixel-art"], list[1].Categories);   // unknown, non-string and repeated ids dropped
        Assert.Empty(list[2].Categories);                                      // a store that predates categories
        Assert.Empty(list[3].Categories);
        Assert.Empty(list[4].Categories);                                      // not an array
        Assert.Empty(list[5].Categories);
    }

    [Fact]
    public void A_theme_made_by_the_positional_constructor_has_no_categories_and_a_copy_keeps_them()
    {
        var plain = new CommunityTheme("a", "A", "", "1", "", 1, 0, 0, null, "u", false, false, 0, 0, 0);
        Assert.Empty(plain.Categories);
        var tagged = plain with { Categories = ["games"] };
        Assert.Equal(["games"], (tagged with { Likes = 5 }).Categories);   // a like coming in replaces the theme, not its categories
    }

    [Fact]
    public void The_list_url_names_the_category_only_when_one_is_chosen_and_escapes_it()
    {
        const string all = "https://t.example/api/themes?sort=top&page=1&pageSize=24";
        Assert.Equal(all, CommunityList.ListUrl("https://t.example", CommunitySort.Top));
        Assert.Equal(all, CommunityList.ListUrl("https://t.example", CommunitySort.Top, null));
        Assert.Equal(all, CommunityList.ListUrl("https://t.example", CommunitySort.Top, ""));
        Assert.Equal(all + "&category=anime", CommunityList.ListUrl("https://t.example/", CommunitySort.Top, "anime"));
        Assert.Equal("https://t.example/api/themes?sort=new&page=2&pageSize=12&category=movies-tv",
            CommunityList.ListUrl("https://t.example", CommunitySort.New, "movies-tv", page: 2, pageSize: 12));
        Assert.EndsWith("&category=a%20b%26c%3Dd", CommunityList.ListUrl("https://t.example", CommunitySort.Top, "a b&c=d"));
    }

    [Fact]
    public void The_category_list_is_the_stores_twelve_in_its_order_with_their_labels()
    {
        // Mirrors web/shared/src/categories.ts: the store answers 400 for an id that is not in its list.
        string[] ids = ["anime", "games", "movies-tv", "cartoon", "nature", "space", "fantasy", "sci-fi", "pixel-art", "minimal", "abstract", "seasonal"];
        string[] labels = ["Anime", "Games", "Movies & TV", "Cartoon", "Nature", "Space", "Fantasy", "Sci-Fi", "Pixel art", "Minimal", "Abstract", "Seasonal"];
        Assert.Equal(ids, CommunityCategories.List.Select(c => c.Id));
        Assert.Equal(labels, CommunityCategories.List.Select(c => c.Label));
        Assert.Equal(12, CommunityCategories.Count);
        Assert.Equal(2, CommunityCategories.IndexOf("movies-tv"));
        Assert.Equal(-1, CommunityCategories.IndexOf("Movies-TV"));   // ids are exact
        Assert.Equal(-1, CommunityCategories.IndexOf(""));
        Assert.Equal("sci-fi", CommunityCategories.IdAt(7));
        Assert.Null(CommunityCategories.IdAt(-1));
        Assert.Null(CommunityCategories.IdAt(12));
        Assert.Equal("Pixel art", CommunityCategories.LabelAt(8));
        Assert.Equal("All", CommunityCategories.LabelAt(-1));
    }

    [Fact]
    public void Settings_round_trip_tolerate_bad_files_and_create_a_device_id()
    {
        var s = AppSettings.Parse("""{"background_scope":"all","store_url":"https://t.example","device_id":"abcdef0123456789"}""", () => "unused");
        Assert.True(s.AllAppsBackgrounds);
        Assert.Equal("https://t.example", s.StoreUrl);
        Assert.Equal("abcdef0123456789", s.DeviceId);
        Assert.Equal(s, AppSettings.Parse(s.Serialize(), () => "unused"));

        var fresh = AppSettings.Parse("not json", () => "0123456789abcdef0123456789abcdef");
        Assert.False(fresh.AllAppsBackgrounds);
        Assert.Equal(AppSettings.DefaultStoreUrl, fresh.StoreUrl);
        Assert.Equal("0123456789abcdef0123456789abcdef", fresh.DeviceId);
        Assert.True(AppSettings.IsDeviceId(AppSettings.NewDeviceId()));
        Assert.False(AppSettings.IsDeviceId("bad id!"));
    }

    [Fact]
    public void A_pack_from_the_web_creator_passes_the_library_check()
    {
        // Built in the browser (BC7 + zstd in the worker), published to a local store and downloaded again:
        // the bytes a community download hands to DeviceThemeLibrary.AddToLibrary.
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "web-creator-export.p5theme"));
        ThemeLibraryEntry entry = ThemeLibraryEntry.Inspect("web-creator-export.p5theme", bytes);
        Assert.Equal("smoke-test-sunset", entry.Manifest.Id);
        Assert.Contains(entry.Manifest.Targets, t => t.Path == "vsh_asset/bg_hub_default.dds.zst");
        Assert.False(entry.Report.HasErrors);
    }

    private static readonly string HashA = new('a', 64), HashB = new('b', 64);

    [Fact]
    public void Downloaded_themes_are_badged_while_their_pack_is_in_the_library()
    {
        var list = CommunityList.Parse(Json, "https://themes.example");
        Assert.Null(list[0].Sha256);   // the public list carries no hash today
        var downloads = CommunityList.ParseDownloads(CommunityList.SerializeDownloads(
            new Dictionary<string, string> { ["abc123def456"] = HashA, ["zz"] = HashB }));
        Assert.Equal(HashA, downloads["abc123def456"]);

        var library = new HashSet<string> { CommunityList.LibraryDigest($"/data/prospero-themes/library/{HashA}.p5theme")! };
        Assert.Equal(["abc123def456"], CommunityList.InLibrary(list, downloads, library));
        // Removed from the library: no badge, although it was downloaded once.
        Assert.Empty(CommunityList.InLibrary(list, downloads, new HashSet<string>()));
    }

    [Fact]
    public void A_listed_hash_wins_over_the_recorded_one()
    {
        string json = Json.Replace("\"status\":\"approved\",", $"\"status\":\"approved\",\"sha256\":\"{HashB.ToUpperInvariant()}\",");
        var list = CommunityList.Parse(json, "https://themes.example");
        Assert.Equal(HashB, list[0].Sha256);
        var library = new HashSet<string> { HashB };
        Assert.Equal(["abc123def456"], CommunityList.InLibrary(list, new Dictionary<string, string>(), library));
    }

    [Fact]
    public void A_newer_listed_hash_marks_an_update_while_the_old_pack_is_in_the_library()
    {
        string json = Json.Replace("\"status\":\"approved\",", $"\"status\":\"approved\",\"sha256\":\"{HashB}\",");
        var list = CommunityList.Parse(json, "https://themes.example");
        var downloads = new Dictionary<string, string> { ["abc123def456"] = HashA };

        var oldOnly = new HashSet<string> { HashA };
        Assert.Equal(["abc123def456"], CommunityList.Updates(list, downloads, oldOnly));
        Assert.Empty(CommunityList.InLibrary(list, downloads, oldOnly));
        // Updated: the new pack is in the library, so it is badged as in the library and no update is left.
        var both = new HashSet<string> { HashA, HashB };
        Assert.Empty(CommunityList.Updates(list, downloads, both));
        Assert.Equal(["abc123def456"], CommunityList.InLibrary(list, downloads, both));
        // Never downloaded, or the old pack removed by hand: nothing to update.
        Assert.Empty(CommunityList.Updates(list, new Dictionary<string, string>(), oldOnly));
        Assert.Empty(CommunityList.Updates(list, downloads, new HashSet<string>()));
    }

    [Fact]
    public void Without_a_listed_hash_there_is_no_update() =>
        Assert.Empty(CommunityList.Updates(CommunityList.Parse(Json, "https://themes.example"),
            new Dictionary<string, string> { ["abc123def456"] = HashA }, new HashSet<string> { HashA }));

    [Theory]
    [InlineData("/data/prospero-themes/library/layer-test.p5theme")]
    [InlineData("/data/prospero-themes/library/notes.txt")]
    [InlineData("")]
    public void Only_hash_named_packs_have_a_library_digest(string path) =>
        Assert.Null(CommunityList.LibraryDigest(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"abc\":\"short\",\"\":\"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\"}")]
    public void A_missing_or_broken_downloads_file_records_nothing(string? json) =>
        Assert.Empty(CommunityList.ParseDownloads(json));
}
