using System;
using System.Collections.Generic;
using System.Linq;
using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class AvatarStoreTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // The answer shape web/server returns for GET /api/avatars.
    private const string Json = """
        {"items":[
          {"id":"av1","name":"Blue Hour","author":"Mia","version":"1.0","description":"Calm",
           "size":300000,"likes":4,"downloads":9,"createdAt":"2026-10-13T12:00:00Z","status":"approved",
           "previewUrl":"/api/avatars/av1/preview.jpg","downloadUrl":"/api/avatars/av1/download",
           "sha256":"0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef"},
          {"id":"","name":"no id","downloadUrl":"/api/avatars//download"},
          {"id":"av3","name":"no download","previewUrl":"/api/avatars/av3/preview.jpg"},
          {"id":"av4","name":"Absolute","author":"A","version":"2","description":"","size":1,"likes":0,"downloads":0,
           "previewUrl":"https://cdn.example/av4.jpg","downloadUrl":"https://cdn.example/av4.p5avatar",
           "sha256":"not-a-digest"},
          {"id":"av5","name":"Short digest","downloadUrl":"/api/avatars/av5/download","sha256":"abc"}
        ],"page":1,"pageSize":36,"total":3}
        """;

    [Fact]
    public void ListUrl_uses_the_sort_key_page_and_page_size()
    {
        Assert.Equal("https://store.example/api/avatars?sort=top&page=1&pageSize=36",
            AvatarStore.ListUrl("https://store.example", CommunitySort.Top));
        Assert.Equal("https://store.example/api/avatars?sort=downloads&page=3&pageSize=12",
            AvatarStore.ListUrl("https://store.example", CommunitySort.Downloads, page: 3, pageSize: 12));
        Assert.Equal("https://store.example/api/avatars?sort=new&page=1&pageSize=36",
            AvatarStore.ListUrl("https://store.example", CommunitySort.New));
    }

    [Fact]
    public void ListUrl_escapes_the_query_and_leaves_it_out_when_empty()
    {
        Assert.Equal("https://store.example/api/avatars?sort=top&page=1&pageSize=36&q=blue%20hour%26co",
            AvatarStore.ListUrl("https://store.example", CommunitySort.Top, query: "blue hour&co"));
        Assert.Equal("https://store.example/api/avatars?sort=top&page=1&pageSize=36",
            AvatarStore.ListUrl("https://store.example", CommunitySort.Top, query: ""));
        Assert.Equal("https://store.example/api/avatars?sort=top&page=1&pageSize=36",
            AvatarStore.ListUrl("https://store.example", CommunitySort.Top, query: null));
    }

    [Fact]
    public void ListUrl_drops_a_trailing_slash_from_the_base()
    {
        Assert.Equal("https://store.example/api/avatars?sort=new&page=1&pageSize=36",
            AvatarStore.ListUrl("https://store.example/", CommunitySort.New));
        Assert.Equal("https://store.example/api/avatars?sort=new&page=1&pageSize=36",
            AvatarStore.ListUrl("https://store.example///", CommunitySort.New));
    }

    [Fact]
    public void LikeUrl_escapes_the_id_and_drops_a_trailing_slash()
    {
        Assert.Equal("https://store.example/api/avatars/av1/like", AvatarStore.LikeUrl("https://store.example/", "av1"));
        Assert.Equal("https://store.example/api/avatars/a%2Fb%3Fc/like", AvatarStore.LikeUrl("https://store.example", "a/b?c"));
    }

    [Fact]
    public void Parse_makes_preview_and_download_urls_absolute()
    {
        List<StoreAvatar> list = AvatarStore.Parse(Json, "https://store.example/");
        StoreAvatar a = list[0];
        Assert.Equal("av1", a.Id);
        Assert.Equal("Blue Hour", a.Name);
        Assert.Equal("Mia", a.Author);
        Assert.Equal(300000L, a.Size);
        Assert.Equal(4, a.Likes);
        Assert.Equal(9, a.Downloads);
        Assert.Equal("https://store.example/api/avatars/av1/preview.jpg", a.PreviewUrl);
        Assert.Equal("https://store.example/api/avatars/av1/download", a.DownloadUrl);
    }

    [Fact]
    public void Parse_keeps_absolute_urls_as_they_are()
    {
        StoreAvatar a = AvatarStore.Parse(Json, "https://store.example").Single(x => x.Id == "av4");
        Assert.Equal("https://cdn.example/av4.jpg", a.PreviewUrl);
        Assert.Equal("https://cdn.example/av4.p5avatar", a.DownloadUrl);
    }

    [Fact]
    public void Parse_skips_entries_without_an_id_or_a_download_address()
    {
        List<StoreAvatar> list = AvatarStore.Parse(Json, "https://store.example");
        Assert.Equal(new[] { "av1", "av4", "av5" }, list.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void Parse_keeps_a_valid_sha256_lowercased_and_drops_an_invalid_one()
    {
        List<StoreAvatar> list = AvatarStore.Parse(Json, "https://store.example");
        Assert.Equal(Digest, list[0].Sha256);
        Assert.Null(list[1].Sha256);   // "not-a-digest"
        Assert.Null(list[2].Sha256);   // too short
    }

    [Fact]
    public void Parse_gives_null_for_a_missing_preview_and_defaults_the_name_to_the_id()
    {
        const string json = """{"items":[{"id":"x1","downloadUrl":"/api/avatars/x1/download"}]}""";
        StoreAvatar a = Assert.Single(AvatarStore.Parse(json, "https://store.example"));
        Assert.Equal("x1", a.Name);
        Assert.Null(a.PreviewUrl);
        Assert.Null(a.Sha256);
        Assert.Equal(0, a.Likes);
    }

    [Fact]
    public void Parse_gives_an_empty_list_for_empty_or_missing_items()
    {
        Assert.Empty(AvatarStore.Parse("""{"items":[],"page":1,"pageSize":36,"total":0}""", "https://store.example"));
        Assert.Empty(AvatarStore.Parse("""{"page":1}""", "https://store.example"));
    }

    [Fact]
    public void Parse_throws_on_malformed_json()
    {
        Assert.ThrowsAny<Exception>(() => AvatarStore.Parse("{\"items\":[", "https://store.example"));
    }

    [Fact]
    public void InLibrary_matches_a_store_digest_to_a_library_file_named_by_it()
    {
        var avatars = new[]
        {
            new StoreAvatar("av1", "A", "", "", "", 1, 0, 0, null, "/d", Digest),
            new StoreAvatar("av2", "B", "", "", "", 1, 0, 0, null, "/d", new string('f', 64)),
            new StoreAvatar("av3", "C", "", "", "", 1, 0, 0, null, "/d", null),
        };
        HashSet<string> ids = AvatarStore.InLibrary(avatars, new[] { Digest + ".p5avatar" });
        Assert.Equal(new[] { "av1" }, ids.ToArray());
    }

    [Fact]
    public void InLibrary_accepts_an_uppercase_extension_and_a_path_and_compares_the_digest_in_lowercase()
    {
        var avatars = new[] { new StoreAvatar("av1", "A", "", "", "", 1, 0, 0, null, "/d", Digest) };
        Assert.Contains("av1", AvatarStore.InLibrary(avatars, new[] { "/data/prospero-themes/avatars/" + Digest + ".P5AVATAR" }));
        Assert.Contains("av1", AvatarStore.InLibrary(avatars, new[] { Digest.ToUpperInvariant() + ".p5avatar" }));
        var upperListed = new[] { new StoreAvatar("av1", "A", "", "", "", 1, 0, 0, null, "/d", Digest.ToUpperInvariant()) };
        Assert.Contains("av1", AvatarStore.InLibrary(upperListed, new[] { Digest + ".p5avatar" }));
    }

    [Fact]
    public void InLibrary_ignores_files_that_are_not_avatar_packs_named_by_digest()
    {
        var avatars = new[] { new StoreAvatar("av1", "A", "", "", "", 1, 0, 0, null, "/d", Digest) };
        Assert.Empty(AvatarStore.InLibrary(avatars, new[]
        {
            Digest + ".p5theme",         // a theme pack, not an avatar
            Digest + ".p5avatar.tmp",    // a half-written copy
            "not-a-digest.p5avatar",
            Digest[..63] + ".p5avatar",  // one character short
            "",
        }));
        Assert.Empty(AvatarStore.InLibrary(Array.Empty<StoreAvatar>(), new[] { Digest + ".p5avatar" }));
    }

    [Fact]
    public void Like_answers_and_liked_ids_round_trip_with_the_theme_helpers()
    {
        const string answer = """{"likes":5,"liked":true}""";
        Assert.Equal(5, CommunityList.ParseLikes(answer));
        Assert.True(CommunityList.ParseLiked(answer));
        Assert.Equal(new[] { "av1", "av2" },
            CommunityList.ParseLikedIds(CommunityList.FormatLikedIds(new[] { "av2", "av1", "av2" })).OrderBy(x => x).ToArray());
        Assert.Equal("/data/prospero-themes/avatar-likes.json", AvatarStore.LikesPath);
    }
}
