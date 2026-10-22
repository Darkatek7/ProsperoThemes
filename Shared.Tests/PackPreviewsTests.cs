using System.IO;
using System.Linq;
using System.Text;
using SharpProspero.Compression;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The manifest's optional <c>previews</c> (JPEGs of backgrounds, an MP3 of the home music, PNG renders of system icons).
/// The store and the console app's preview show them because neither can show BC7, play ATRAC9 or draw SVG. They are
/// never targets, so the pack reader has to leave them alone: no target, no error, no warning, whatever they hold. The
/// manifest reads them into <see cref="ThemePackPreviews"/>, keeping only entries that are usable on their face, and
/// writes them back.
/// </summary>
public class PackPreviewsTests
{
    private static byte[] DdsPayload() => Encoding.ASCII.GetBytes("DDS ").Concat(new byte[60]).ToArray();

    private static string ManifestJson(string extra) => $$"""
        {"format":2,"id":"previews","name":"Previews","author":"tests","version":"1.0","description":"d","unmapped":"keep",
         {{extra}}
         "targets":[{"path":"allapps/pic0.dds","layer":"background","file":"assets/allapps.dds","bytes":64,"sha256":"{{ThemePack.Hash(DdsPayload())}}"}]}
        """;

    private static ThemePack Pack(string manifestJson, params (string Name, byte[] Data)[] files)
    {
        var zip = new ZipBuilder().AddText("theme.json", manifestJson).Add("assets/allapps.dds", DdsPayload());
        foreach ((string name, byte[] data) in files)
            zip.Add(name, data);
        return ThemePack.Open(zip.ToArray());
    }

    private static string Describe(PackReport report) =>
        string.Join("; ", report.Issues.Select(i => $"{i.Severity} {i.Code}: {i.Message}"));

    [Fact]
    public void A_pack_from_the_creator_with_previews_validates_without_a_word_about_them()
    {
        // fixtures/creator-previews.p5theme was written by theme_pack.py create: a JPEG of a per-app background and an
        // MP3 excerpt of the home music, next to two targets.
        string path = Path.Combine(System.AppContext.BaseDirectory, "fixtures", "creator-previews.p5theme");
        Assert.True(File.Exists(path), $"fixture is missing from the test output: {path}");

        ThemePack pack = ThemePack.OpenFile(path);
        PackReport report = pack.Validate();

        Assert.Empty(report.Issues);
        Assert.Equal(2, pack.Manifest.Targets.Count);
        Assert.Equal(2, pack.ApplicableTargets().Count());
        Assert.All(pack.Manifest.Targets, t => Assert.DoesNotContain("previews/", t.File));

        // They are in the archive and in theme.json; validation simply never asks for them.
        Assert.True(pack.HasPayload("previews/apps/PPSA99169.jpg"));
        Assert.True(pack.HasPayload("previews/music.mp3"));
        string json = Encoding.UTF8.GetString(pack.Find("theme.json")!);
        Assert.Contains("\"previews\"", json);
    }

    [Fact]
    public void Previews_next_to_the_targets_are_not_reported()
    {
        ThemePack pack = Pack(
            ManifestJson("""
                "previews":{"apps":{"PPSA99169":"previews/apps/PPSA99169.jpg"},"allApps":"previews/allapps.jpg","music":"previews/music.mp3"},
                """),
            ("previews/apps/PPSA99169.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }),
            ("previews/allapps.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6 }),
            ("previews/music.mp3", Encoding.ASCII.GetBytes("ID3").Concat(new byte[40]).ToArray()));

        PackReport report = pack.Validate();

        Assert.Empty(report.Issues);
        Assert.Single(pack.ApplicableTargets());
    }

    [Theory]
    [InlineData("\"previews\":\"nope\",")]
    [InlineData("\"previews\":[],")]
    [InlineData("\"previews\":{\"apps\":[1,2],\"allApps\":7,\"music\":null},")]
    [InlineData("\"previews\":{\"apps\":{\"../../x\":\"/etc/passwd\"},\"music\":\"../music.mp3\"},")]
    [InlineData("\"previews\":{\"allApps\":\"previews/missing.jpg\"},")]
    public void Odd_or_dangling_previews_do_not_matter_to_the_app(string previews)
    {
        // The store's validator warns about these; the app's validation has nothing to say, and its preview ignores them.
        ThemePack pack = Pack(ManifestJson(previews));

        Assert.Empty(pack.Validate().Issues);
        Assert.Single(pack.ApplicableTargets());
    }

    [Fact]
    public void The_manifest_reads_the_previews_of_the_creator_fixture()
    {
        string path = Path.Combine(System.AppContext.BaseDirectory, "fixtures", "creator-previews.p5theme");
        ThemePackPreviews previews = ThemePack.OpenFile(path).Manifest.Previews;

        Assert.Equal("previews/apps/PPSA99169.jpg", Assert.Single(previews.Apps).Value);
        Assert.Equal("PPSA99169", Assert.Single(previews.Apps).Key);
        Assert.Equal("previews/music.mp3", previews.Music);
        Assert.Null(previews.AllApps);
        Assert.Empty(previews.Icons);
        Assert.False(previews.IsEmpty);
    }

    [Fact]
    public void A_manifest_without_previews_has_none()
    {
        ThemePackPreviews previews = Pack(ManifestJson("")).Manifest.Previews;

        Assert.True(previews.IsEmpty);
        Assert.Same(ThemePackPreviews.None, previews);
        Assert.DoesNotContain("\"previews\":", Pack(ManifestJson("")).Manifest.Write());   // the pack's id is "previews", the key is not there
    }

    [Fact]
    public void Previews_with_icons_are_read_and_written_back()
    {
        ThemePack pack = Pack(ManifestJson("""
            "previews":{"apps":{"PPSA99169":"previews/apps/PPSA99169.jpg","NPXS40071":"previews/apps/NPXS40071.jpg"},
                        "allApps":"previews/allapps.jpg","music":"previews/music.mp3",
                        "icons":{"iconid_home":"previews/icons/iconid_home.png","iconid_game_base":"previews/icons/iconid_game_base.png"}},
            """));
        ThemePackPreviews previews = pack.Manifest.Previews;

        Assert.Equal(2, previews.Apps.Count);
        Assert.Equal("previews/apps/NPXS40071.jpg", previews.Apps["NPXS40071"]);
        Assert.Equal("previews/allapps.jpg", previews.AllApps);
        Assert.Equal("previews/icons/iconid_game_base.png", previews.Icons["iconid_game_base"]);

        // Write keeps them, in an object a second read gives back the same previews from.
        string json = pack.Manifest.Write();
        ThemePackPreviews again = ThemePackManifest.Parse(json).Previews;
        Assert.Equal(previews, again);
        Assert.Equal(previews.GetHashCode(), again.GetHashCode());
        Assert.NotEqual(previews, again with { Music = "previews/other.mp3" });
        // The order of the apps, which is the order of their tiles, survives.
        Assert.Equal(["PPSA99169", "NPXS40071"], again.Apps.Keys.ToArray());
    }

    [Fact]
    public void Entries_that_are_not_usable_on_their_face_are_dropped_and_the_rest_stays()
    {
        ThemePackPreviews previews = Pack(ManifestJson("""
            "previews":{"apps":{"PPSA99169":"previews/ok.jpg","ppsa99169":"previews/lower.jpg","PPSA1":"previews/short.jpg","PPSA99170":"../escape.jpg",
                                "PPSA99171":"/absolute.jpg","PPSA99172":"back\\slash.jpg","PPSA99173":"a//b.jpg","PPSA99174":"a/./b.jpg","PPSA99175":"dir/",
                                "PPSA99176":7,"PPSA99177":null,"PPSA99178":["x"],"PPSA99179":""},
                        "allApps":"previews/../x.jpg","music":3,
                        "icons":{"iconid_home":"ok.png","ICON":"upper.png","iconid home":"space.png","":"empty.png","iconid_mic":"/abs.png"}},
            """)).Manifest.Previews;

        Assert.Equal(["PPSA99169"], previews.Apps.Keys.ToArray());
        Assert.Null(previews.AllApps);
        Assert.Null(previews.Music);
        Assert.Equal(["iconid_home"], previews.Icons.Keys.ToArray());
    }

    [Fact]
    public void A_path_of_two_hundred_characters_is_kept_and_one_character_more_is_not()
    {
        string ok = new string('a', 196) + ".png", tooLong = new string('a', 197) + ".png";
        ThemePackPreviews previews = Pack(ManifestJson("\"previews\":{\"icons\":{\"iconid_home\":\"" + ok + "\",\"iconid_mic\":\"" + tooLong + "\"}},")).Manifest.Previews;

        Assert.Equal(["iconid_home"], previews.Icons.Keys.ToArray());
    }

    [Fact]
    public void No_more_than_sixty_four_apps_and_icons_are_kept()
    {
        string apps = string.Join(",", Enumerable.Range(0, 80).Select(i => $"\"PPSA{10000 + i}\":\"previews/{i}.jpg\""));
        string icons = string.Join(",", Enumerable.Range(0, 80).Select(i => $"\"iconid_{i}\":\"previews/{i}.png\""));
        ThemePackPreviews previews = Pack(ManifestJson("\"previews\":{\"apps\":{" + apps + "},\"icons\":{" + icons + "}},")).Manifest.Previews;

        Assert.Equal(ThemePackPreviews.MaxEntries, previews.Apps.Count);
        Assert.Equal(ThemePackPreviews.MaxEntries, previews.Icons.Count);
        Assert.Equal("PPSA10000", previews.Apps.Keys.First());
    }

    [Fact]
    public void Opening_a_pack_for_some_files_inflates_only_those()
    {
        // The preview wants a few pictures out of a pack that also holds a payload it must not pay for.
        byte[] big = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 % 251)).ToArray();
        byte[] archive = new ZipBuilder().AddText("theme.json", ManifestJson(""))
            .Add("assets/allapps.dds", DdsPayload()).Add("assets/big.bin", big).Add("previews/music.mp3", new byte[] { 1, 2, 3 }).ToArray();

        ThemePack all = ThemePack.Open(archive);
        ThemePack some = ThemePack.OpenSelected(archive, name => name == "previews/music.mp3");

        Assert.NotNull(all.Find("assets/big.bin"));
        Assert.Null(some.Find("assets/big.bin"));
        Assert.Null(some.Find("assets/allapps.dds"));
        Assert.Equal(new byte[] { 1, 2, 3 }, some.Find("previews/music.mp3"));
        Assert.Equal(all.Manifest.Id, some.Manifest.Id);   // the manifest always comes with it
        Assert.Equal(all.Manifest.Targets, some.Manifest.Targets);
    }

    [Fact]
    public void A_pack_with_previews_reads_the_same_as_one_without()
    {
        ThemePackManifest with = Pack(ManifestJson("\"previews\":{\"music\":\"previews/music.mp3\"},")).Manifest;
        ThemePackManifest without = Pack(ManifestJson("")).Manifest;

        Assert.Equal(without.Id, with.Id);
        Assert.Equal(without.Unmapped, with.Unmapped);
        Assert.Equal(without.Preview, with.Preview);
        Assert.Equal(without.Targets, with.Targets);
    }
}
