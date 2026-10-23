using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The pack the console makes of the current look. <c>fixtures/console-mix.p5theme</c> is what <see cref="LookPack"/>
/// makes of <see cref="FixtureLook"/>; the TypeScript reader and <c>theme_pack.py verify</c> check the same file, so the
/// console's writer is held to the format like the other writers. Set PROSPERO_WRITE_FIXTURES=1 to write it again.
/// </summary>
public class LookPackTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static ThemePack Open(LookLayer layer) => ThemeArchive.Open(File.ReadAllBytes(Fixture(layer.Pack)));

    // Two backgrounds, then one of them replaced; an app background and the home music; the same UI sound from two packs.
    private static readonly LookLayer[] FixtureLook =
    [
        new("two-screen.p5theme", "test-pack", "Test Pack", ThemePart.Background),
        new("web-creator-export.p5theme", "smoke-test-sunset", "Sunset", ThemePart.Background),
        new("creator-previews.p5theme", "creator-previews", "Creator previews", ThemePart.AppBackgrounds | ThemePart.Sounds),
        new("ui-sound.p5theme", "ui-sound-test", "UI Sound Test", ThemePart.Sounds),
        new("web-ui-sound.p5theme", "web-ui-sound", "Web UI Sound", ThemePart.Sounds),
    ];

    private static ThemePackTarget TargetOf(string fixture, string path) =>
        Open(new LookLayer(fixture, "", "", ThemePart.All)).Manifest.Targets.Single(t => t.Path == path);

    [Fact]
    public void Later_layers_replace_earlier_ones_by_path_and_by_sound_name()
    {
        LookPack.Mix mix = LookPack.Build(FixtureLook, Open, "My mix 1");
        ThemePack pack = ThemePack.Open(mix.Archive);
        Assert.Empty(pack.Validate().Issues);

        var targets = pack.Manifest.Targets;
        Assert.Equal(5, targets.Count);
        Assert.Equal(TargetOf("web-creator-export.p5theme", "vsh_asset/bg_hub_default.dds.zst").Sha256,
            targets.Single(t => t.Path == "vsh_asset/bg_hub_default.dds.zst").Sha256);
        Assert.Equal(TargetOf("two-screen.p5theme", "vsh_asset/bg_image_load_error.dds.zst").Sha256,
            targets.Single(t => t.Path == "vsh_asset/bg_image_load_error.dds.zst").Sha256);
        ThemePackTarget sound = targets.Single(t => t.Layer == ThemePack.VagLayer);
        Assert.Equal("psfx_focus_move", sound.Resource);
        Assert.Equal(TargetOf("web-ui-sound.p5theme", "vsh_asset/Sce.PlayStation.PUI_UI3.rco").Sha256, sound.Sha256);
        Assert.Contains(targets, t => t.Path == "appmeta/PPSA99169/pic0.dds");
        Assert.Contains(targets, t => t.Path == "vsh_asset/bgm_home.at9");

        // Each payload is stored under its hash, and nothing else is in the archive but the previews.
        foreach (ThemePackTarget t in targets) Assert.Equal("payloads/" + t.Sha256 + Path.GetFileName(t.File)[64..], t.File);
        Assert.Equal(targets.Select(t => t.File).Distinct().Count() + 3 + 1, pack.Files.Count);   // previews: cover, app, music

        Assert.Equal("My mix 1", pack.Manifest.Name);
        Assert.Equal(LookPack.Author, pack.Manifest.Author);
        Assert.StartsWith("mix-", pack.Manifest.Id);
        Assert.Equal("keep", pack.Manifest.Unmapped);
    }

    [Fact]
    public void The_previews_come_with_their_part_and_the_cover_with_the_last_background()
    {
        ThemePack pack = ThemePack.Open(LookPack.Build(FixtureLook, Open, "My mix 1").Archive);
        ThemePackPreviews previews = pack.Manifest.Previews;
        Assert.Equal(["PPSA99169"], previews.Apps.Keys);
        Assert.NotNull(previews.Music);
        Assert.Equal(ThemePack.Hash(Open(FixtureLook[1]).Find("preview.png")!), ThemePack.Hash(pack.Find(pack.Manifest.Preview!)!));

        // Only the sounds of the pack with an app picture and music: no app background, no app picture, the music.
        LookLayer[] sounds = [FixtureLook[0], FixtureLook[2] with { Parts = ThemePart.Sounds }];
        ThemePack soundsOnly = ThemePack.Open(LookPack.Build(sounds, Open, "My mix 2").Archive);
        Assert.DoesNotContain(soundsOnly.Manifest.Targets, t => t.Path.StartsWith("appmeta/"));
        Assert.Empty(soundsOnly.Manifest.Previews.Apps);
        Assert.NotNull(soundsOnly.Manifest.Previews.Music);
        Assert.Null(soundsOnly.Manifest.Preview);   // no layer has a cover
    }

    [Fact]
    public void The_same_look_gives_the_same_pack_and_the_committed_fixture_is_it()
    {
        byte[] first = LookPack.Build(FixtureLook, Open, "My mix 1").Archive;
        Assert.Equal(first, LookPack.Build(FixtureLook, Open, "My mix 1").Archive);
        // Another name is another pack file, but the id says the look is the same.
        Assert.Equal(LookPack.Build(FixtureLook, Open, "My mix 1").Manifest.Id, LookPack.Build(FixtureLook, Open, "My mix 7").Manifest.Id);
        Assert.NotEqual(LookPack.Build(FixtureLook, Open, "My mix 1").Manifest.Id, LookPack.Build(FixtureLook[..2], Open, "My mix 1").Manifest.Id);

        string fixture = Fixture("console-mix.p5theme");
        if (Environment.GetEnvironmentVariable("PROSPERO_WRITE_FIXTURES") == "1")
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "console-mix.p5theme"), first);
        else Assert.Equal(first, File.ReadAllBytes(fixture));
    }

    [Fact]
    public void A_look_without_targets_or_with_a_pack_that_cannot_be_read_is_refused()
    {
        LookLayer[] video = [new("video-layer.p5theme", "video-test", "Video", ThemePart.Background)];   // the pack has no background
        Assert.Throws<ThemePackException>(() => LookPack.Build(video, Open, "My mix 1"));
        LookLayer[] gone = [FixtureLook[0], new("missing.p5theme", "x", "Gone", ThemePart.All)];
        Assert.ThrowsAny<Exception>(() => LookPack.Build(gone, Open, "My mix 1"));
    }

    [Fact]
    public void The_description_names_each_theme_with_its_parts()
    {
        Assert.Equal("Made on the console from Test Pack (Background) and Creator previews (App backgrounds, Music and sounds).",
            LookPack.Description([FixtureLook[0], FixtureLook[2]]));
    }

    [Fact]
    public void The_name_is_the_first_free_number_and_a_look_of_library_packs_can_be_saved()
    {
        Assert.Equal("My mix 1", LookPack.NextName(["Blue Hour"]));
        Assert.Equal("My mix 3", LookPack.NextName(["My mix 1", "my mix 2", "My mix 4"]));

        string[] library = ["/lib/a.p5theme", "/lib/b.p5theme"];
        LookLayer a = new("/lib/a.p5theme", "a", "A", ThemePart.Background), b = new("/lib/b.p5theme", "b", "B", ThemePart.Icons);
        Assert.True(LookPack.Savable([a, b], library));
        Assert.False(LookPack.Savable([a], library));                                   // one theme is that theme
        Assert.False(LookPack.Savable(null, library));
        Assert.False(LookPack.Savable([a, b with { Pack = "/lib/gone.p5theme" }], library));   // no longer in the library
        Assert.False(LookPack.Savable([a, b with { Pack = "" }], library));
    }
}
