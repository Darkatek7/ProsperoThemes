using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;
using SharpProspero.Compression;
using SharpProspero.Ui;
using Xunit;
using MemoryDeviceFiles = ProsperoThemes.Shared.Tests.RcoSvgTests.MemoryDeviceFiles;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// UI sounds (named VAG clips in the icon container), music as whole .at9 files, the parts a user picks when applying,
/// and the Music and sounds setting.
/// </summary>
public class SoundThemeTests
{
    private const string RcoPath = "vsh_asset/Sce.PlayStation.PUI_UI3.rco";
    private const string RcoDevice = "/system_ex/vsh_asset/Sce.PlayStation.PUI_UI3.rco";
    private const string MusicPath = "vsh_asset/bgm_home.at9";
    private const string MusicDevice = "/system_ex/vsh_asset/bgm_home.at9";
    private const string BgPath = "vsh_asset/bg_hub_default.dds.zst";
    private const string BgDevice = "/system_ex/vsh_asset/bg_hub_default.dds.zst";

    // ---- helpers ----------------------------------------------------------

    /// <summary>A stock-like stereo clip: header, a silent pair, <paramref name="pairs"/> sound pairs, the end pair.</summary>
    private static byte[] StockClip(string name, int pairs, byte fill)
    {
        int size = (pairs + 2) * 32;
        var clip = new byte[0x30 + size];
        "VAGp"u8.CopyTo(clip);
        BinaryPrimitives.WriteInt32BigEndian(clip.AsSpan(4), 0x00020001);
        BinaryPrimitives.WriteInt32BigEndian(clip.AsSpan(0x0C), size);
        BinaryPrimitives.WriteInt32BigEndian(clip.AsSpan(0x10), 48000);
        clip[0x1E] = 2;
        Encoding.ASCII.GetBytes(name.Length > 16 ? name[..16] : name).CopyTo(clip, 0x20);
        for (int f = 2; f < 2 * (pairs + 1); f++)
        {
            clip[0x30 + f * 16] = 0x2C;   // filter 2, shift 12
            clip.AsSpan(0x30 + f * 16 + 2, 14).Fill(fill);
        }
        clip[0x30 + size - 32 + 1] = 7;
        clip[0x30 + size - 16 + 1] = 7;
        clip.AsSpan(0x30 + size - 32 + 2, 14).Fill(0x77);
        clip.AsSpan(0x30 + size - 16 + 2, 14).Fill(0x77);
        return clip;
    }

    /// <summary>A stand-in icon container: header, an icon, three clips (two cut to the same 16-character name), a tail.</summary>
    private static byte[] Container() =>
        new byte[] { 0x52, 0x43, 0x4F, 0x46, 0, 1, 2, 3 }
            .Concat(Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 64 64\"><title>utility/iconid_settings</title><path d=\"M10 10h44v44H10z\" fill=\"#FFFFFF\"/><!-- room for a replacement icon to fit in --></svg>"))
            .Concat(StockClip("psfx_focus_move", 8, 0x11))
            .Concat(StockClip("psfx_focus_move_in_keyboard", 4, 0x22))
            .Concat(StockClip("psfx_focus_move_vibration", 4, 0x33))
            .Concat(StockClip("psfx_enter", 6, 0x44))
            .Concat(new byte[] { 9, 9, 9 }).ToArray();

    /// <summary>A mono VAG file of <paramref name="frames"/> sound frames (and an end-marker frame) at <paramref name="rate"/>.</summary>
    private static byte[] MonoVag(int frames, byte fill, int rate = 48000, byte channels = 1, byte filter = 1)
    {
        var vag = new byte[0x30 + (frames + 1) * 16];
        "VAGp"u8.CopyTo(vag);
        BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(4), 0x20);
        BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(0x0C), (frames + 1) * 16);
        BinaryPrimitives.WriteInt32BigEndian(vag.AsSpan(0x10), rate);
        vag[0x1E] = channels;
        for (int f = 0; f < frames; f++)
        {
            vag[0x30 + f * 16] = (byte)(filter << 4 | 4);
            vag.AsSpan(0x30 + f * 16 + 2, 14).Fill(fill);
        }
        vag[0x30 + frames * 16 + 1] = 7;
        return vag;
    }

    private static readonly byte[] Star =
        Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 64 64\"><path fill=\"#f00\" d=\"M32 6l8 16 18 2-13 12 3 18-16-9-16 9 3-18L6 24l18-2z\"/></svg>");

    private static byte[] Music(string tag) => Encoding.ASCII.GetBytes("RIFF....WAVEfmt " + tag + new string('.', 64));

    private static byte[] Background(string tag)
    {
        byte[] data = Encoding.ASCII.GetBytes("....payload-" + tag + new string('.', 256));
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD }.CopyTo(data, 0);
        return data;
    }

    private record Item(string Path, string Layer, byte[] Payload, string? Resource = null);

    private static ThemePack Pack(string id, params Item[] items)
    {
        var zip = new ZipBuilder();
        var entries = new List<string>();
        for (int i = 0; i < items.Length; i++)
        {
            Item item = items[i];
            string file = $"f/{i}.bin";
            zip.Add(file, item.Payload);
            string resource = item.Resource is null ? "" : $",\"resource\":\"{item.Resource}\"";
            entries.Add($$"""
                {"path":"{{item.Path}}","layer":"{{item.Layer}}"{{resource}},"file":"{{file}}",
                 "bytes":{{item.Payload.Length}},"sha256":"{{ThemePack.Hash(item.Payload)}}"}
                """);
        }
        zip.AddText("theme.json", $$"""
            {"format":2,"id":"{{id}}","name":"Sounds","author":"tests","version":"1.0",
             "description":"sounds","unmapped":"keep","targets":[{{string.Join(",", entries)}}]}
            """);
        return ThemePack.Open(zip.ToArray());
    }

    private static MemoryDeviceFiles Console()
    {
        var files = new MemoryDeviceFiles();
        files.Files[RcoDevice] = Container();
        files.Files[MusicDevice] = Music("stock");
        files.Files[BgDevice] = Encoding.ASCII.GetBytes("live-bg");
        return files;
    }

    private static Span<byte> ClipData(byte[] container, string name)
    {
        (int offset, int length, _) = RcoVag.Find(container, name);
        return container.AsSpan(offset, length);
    }

    // ---- RcoVag -----------------------------------------------------------

    [Fact]
    public void A_sound_goes_to_both_channels_between_the_stock_first_and_last_pair_and_keeps_the_size()
    {
        byte[] stock = Container();
        byte[] patched = (byte[])stock.Clone();

        RcoVag.Replace(patched, "psfx_focus_move", MonoVag(3, 0x5A));

        Assert.Equal(stock.Length, patched.Length);
        (int o, int n, int rate) = RcoVag.Find(stock, "psfx_focus_move");
        Assert.Equal(48000, rate);
        Assert.Equal(stock.AsSpan(0, o).ToArray(), patched.AsSpan(0, o).ToArray());
        Assert.Equal(stock.AsSpan(o + n).ToArray(), patched.AsSpan(o + n).ToArray());
        Span<byte> clip = patched.AsSpan(o, n);
        Assert.Equal(stock.AsSpan(o, 32).ToArray(), clip[..32].ToArray());                     // silent first pair
        Assert.Equal(stock.AsSpan(o + n - 32, 32).ToArray(), clip[^32..].ToArray());           // end pair
        for (int pair = 1; pair < n / 32 - 1; pair++)
        {
            Span<byte> left = clip.Slice(pair * 32, 16), right = clip.Slice(pair * 32 + 16, 16);
            Assert.Equal(left.ToArray(), right.ToArray());                                     // L = R
            bool sound = pair <= 3;
            Assert.Equal(sound ? (byte)0x5A : (byte)0, left[2]);                               // the sound, then silence
            Assert.Equal(pair == n / 32 - 2 ? 1 : 0, left[1]);                                // the last frame ends it
        }
    }

    [Fact]
    public void A_sound_longer_than_the_clip_is_cut_to_it()
    {
        byte[] patched = Container();
        RcoVag.Replace(patched, "psfx_enter", MonoVag(40, 0x5A));
        Span<byte> clip = ClipData(patched, "psfx_enter");
        Assert.Equal(StockClip("psfx_enter", 6, 0).Length - 0x30, clip.Length);
        Assert.Equal(0x5A, clip[clip.Length - 48 + 2]);   // sound up to the last frame before the end pair
        Assert.Equal(1, clip[clip.Length - 48 + 1]);
    }

    [Fact]
    public void Names_match_as_the_container_cuts_them_and_have_to_be_unique()
    {
        byte[] c = Container();
        Assert.Equal(10 * 32, ClipData(c, "psfx_focus_move").Length);   // the 15-character name: the others are cut to 16
        var cut = Assert.Throws<ThemePackException>(() => RcoVag.Find(c, "psfx_focus_move_in_keyboard"));
        Assert.Contains("matches 2 clips", cut.Message);                // both longer names are "psfx_focus_move_"
        Assert.Throws<ThemePackException>(() => RcoVag.Find(c, "psfx_missing"));
        Assert.Equal(6 * 32 + 64, ClipData(c, "psfx_enter").Length);
    }

    [Fact]
    public void Only_a_mono_standard_vag_at_the_clip_rate_is_accepted()
    {
        byte[] c = Container();
        Assert.Throws<ThemePackException>(() => RcoVag.Replace(c, "psfx_enter", MonoVag(3, 1, channels: 2)));
        Assert.Throws<ThemePackException>(() => RcoVag.Replace(c, "psfx_enter", MonoVag(3, 1, rate: 44100)));
        Assert.Throws<ThemePackException>(() => RcoVag.Replace(c, "psfx_enter", MonoVag(3, 1, filter: 9)));
        Assert.Throws<ThemePackException>(() => RcoVag.Replace(c, "psfx_enter", Encoding.ASCII.GetBytes("RIFF not a vag file at all.......................")));
        Assert.Equal(Container(), c);
    }

    [Fact]
    public void A_vag_target_needs_a_name_and_a_vag_payload()
    {
        var good = Pack("a", new Item(RcoPath, "vag", MonoVag(3, 1), "psfx_enter"));
        Assert.False(good.Validate().HasErrors, string.Join("; ", good.Validate().Errors.Select(e => e.Message)));
        Assert.True(Pack("b", new Item(RcoPath, "vag", MonoVag(3, 1))).Validate().HasErrors);
        Assert.True(Pack("c", new Item(RcoPath, "vag", Star, "psfx_enter")).Validate().HasErrors);
        // An icon and a sound share the container's path.
        Assert.False(Pack("d", new Item(RcoPath, "vag", MonoVag(3, 1), "psfx_enter"), new Item(RcoPath, "svg", Star, "iconid_settings"))
            .Validate().HasErrors);
    }

    [Theory]
    [InlineData("ui-sound.p5theme")]       // Python writer (theme_pack.py)
    [InlineData("web-ui-sound.p5theme")]   // TypeScript writer (web creator)
    public void A_ui_sound_pack_from_the_other_writers_validates_and_fits_a_clip(string fixture)
    {
        ThemePack pack = ThemePack.OpenFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", fixture));
        Assert.False(pack.Validate().HasErrors, string.Join("; ", pack.Validate().Errors.Select(e => e.Message)));
        ThemePackTarget target = Assert.Single(pack.Manifest.Targets);
        Assert.Equal((RcoPath, "vag", "psfx_focus_move"), (target.Path, target.Layer, target.Resource));
        (int frames, int rate) = RcoVag.Check(pack.Payload(target));
        Assert.Equal(48000, rate);
        Assert.Equal((12000 + 27) / 28, frames);                        // 0.25 s of tone, 28 samples a frame

        byte[] container = Container();
        RcoVag.Replace(container, "psfx_focus_move", pack.Payload(target));
        Assert.Equal(Container().Length, container.Length);
        Assert.NotEqual(ClipData(Container(), "psfx_focus_move").ToArray(), ClipData(container, "psfx_focus_move").ToArray());
    }

    // ---- applying ---------------------------------------------------------

    [Fact]
    public void Icons_and_sounds_of_one_container_go_in_one_write()
    {
        var files = Console();
        var pack = Pack("mixed", new Item(RcoPath, "svg", Star, "iconid_settings"), new Item(RcoPath, "vag", MonoVag(3, 0x5A), "psfx_enter"));

        ApplyResult result = new ThemeApplier(files, () => 0).Apply(pack);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, files.Writes.Count(w => w == RcoDevice + ".tmp"));
        Assert.Contains("#f00", Encoding.UTF8.GetString(files.Files[RcoDevice]));
        Assert.Equal(0x5A, ClipData(files.Files[RcoDevice], "psfx_enter")[34]);
        Assert.Contains("Icons and sounds change after the next restart", result.Message);
    }

    [Fact]
    public void A_sound_theme_kept_after_an_icon_theme_leaves_its_icons_and_restore_brings_stock_back()
    {
        var files = Console();
        Assert.True(new ThemeApplier(files, () => 0).Apply(Pack("icons", new Item(RcoPath, "svg", Star, "iconid_settings"))).Success);

        var keep = new ThemeApplier(files, () => 0) { KeepEarlierThemes = true };
        ApplyResult sounds = keep.Apply(Pack("sounds", new Item(RcoPath, "vag", MonoVag(2, 0x5A), "psfx_focus_move")));

        Assert.True(sounds.Success, sounds.Message);
        Assert.Contains("#f00", Encoding.UTF8.GetString(files.Files[RcoDevice]));     // the icon theme's star stays
        Assert.Equal(0x5A, ClipData(files.Files[RcoDevice], "psfx_focus_move")[34]);
        Assert.StartsWith("Sounds change after the next restart", sounds.Message);   // a sound-only write does not claim icons

        Assert.True(keep.RestoreOriginals().Success);
        Assert.Equal(Container(), files.Files[RcoDevice]);
    }

    [Fact]
    public void Music_is_a_whole_file_and_says_it_changes_after_a_restart()
    {
        var files = Console();
        ApplyResult result = new ThemeApplier(files, () => 0).Apply(Pack("music", new Item(MusicPath, "sound", Music("fein"))));
        Assert.True(result.Success, result.Message);
        Assert.Equal(Music("fein"), files.Files[MusicDevice]);
        Assert.StartsWith("Sounds change after the next restart", result.Message);
    }

    private static ThemePack Everything() => Pack("all",
        new Item(BgPath, "background", Background("bg")),
        new Item(RcoPath, "svg", Star, "iconid_settings"),
        new Item(RcoPath, "vag", MonoVag(2, 0x5A), "psfx_focus_move"),
        new Item(MusicPath, "sound", Music("fein")));

    [Fact]
    public void Parts_left_out_are_not_written()
    {
        var files = Console();
        var applier = new ThemeApplier(files, () => 0) { Parts = ThemePart.All & ~ThemePart.Sounds };

        ApplyResult result = applier.Apply(Everything());

        Assert.True(result.Success, result.Message);
        Assert.Equal(Music("stock"), files.Files[MusicDevice]);                                    // the music stays
        Assert.DoesNotContain(MusicDevice + ".tmp", files.Writes);
        Assert.Equal(ClipData(Container(), "psfx_focus_move").ToArray(), ClipData(files.Files[RcoDevice], "psfx_focus_move").ToArray());
        Assert.Contains("#f00", Encoding.UTF8.GetString(files.Files[RcoDevice]));                  // the icon is applied
        Assert.Equal(Background("bg"), files.Files[BgDevice]);
        Assert.Contains("Icons change after the next restart", result.Message);
    }

    [Fact]
    public void Only_the_chosen_part_is_written_and_a_choice_with_nothing_in_it_writes_nothing()
    {
        var files = Console();
        ApplyResult sounds = new ThemeApplier(files, () => 0) { Parts = ThemePart.Sounds }.Apply(Everything());
        Assert.True(sounds.Success, sounds.Message);
        Assert.Equal(Encoding.ASCII.GetBytes("live-bg"), files.Files[BgDevice]);
        Assert.Equal(Music("fein"), files.Files[MusicDevice]);
        Assert.DoesNotContain("#f00", Encoding.UTF8.GetString(files.Files[RcoDevice]));

        var none = Console();
        ApplyResult tiles = new ThemeApplier(none, () => 0) { Parts = ThemePart.Tiles }.Apply(Everything());
        Assert.False(tiles.Success);
        Assert.Contains("chosen parts", tiles.Message);
        Assert.Empty(none.Writes);
    }

    private const string StatePath = "/data/prospero-themes/state.json", LookPath = "/data/prospero-themes/look.json";

    private static string Text(MemoryDeviceFiles files, string path) => Encoding.UTF8.GetString(files.Files[path]);

    [Fact]
    public void Restoring_the_background_keeps_the_rest_of_the_theme_applied()
    {
        var files = Console();
        Assert.True(new ThemeApplier(files, () => 0) { SourcePath = "/lib/all.p5theme" }.Apply(Everything()).Success);

        ApplyResult result = new ThemeApplier(files, () => 0).RestoreOriginals(ThemePart.Background);

        Assert.True(result.Success, result.Message);
        Assert.Contains("original(s) of Background", result.Message);
        Assert.Equal(Encoding.ASCII.GetBytes("live-bg"), files.Files[BgDevice]);
        Assert.Contains("#f00", Text(files, RcoDevice));
        Assert.Equal(Music("fein"), files.Files[MusicDevice]);
        // The theme is still applied, and the look keeps what is left of it.
        Assert.Contains("\"action\": \"restore-parts\"", Text(files, StatePath));
        Assert.Equal("all", AppliedTheme.Read(Text(files, StatePath)));
        Assert.Equal(new[] { new LookLayer("/lib/all.p5theme", "all", "Sounds", ThemePart.Icons | ThemePart.Sounds) }, Look.Parse(Text(files, LookPath)));
    }

    [Fact]
    public void Icons_and_sounds_go_back_together_and_the_last_part_ends_the_theme()
    {
        var files = Console();
        Assert.True(new ThemeApplier(files, () => 0) { SourcePath = "/lib/all.p5theme" }.Apply(Everything()).Success);
        Assert.True(new ThemeApplier(files, () => 0).RestoreOriginals(ThemePart.Background).Success);

        // Only the icons were asked for: they share the container with the UI sounds, so the sounds come back too.
        ApplyResult result = new ThemeApplier(files, () => 0).RestoreOriginals(ThemePart.Icons);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("#f00", Text(files, RcoDevice));
        Assert.Equal(Music("stock"), files.Files[MusicDevice]);
        Assert.Empty(Look.Parse(Text(files, LookPath))!);
        Assert.Null(AppliedTheme.Read(Text(files, StatePath)));
    }

    [Fact]
    public void Every_part_is_the_full_restore_and_a_part_without_backups_writes_nothing()
    {
        var files = Console();
        Assert.True(new ThemeApplier(files, () => 0).Apply(Pack("bg", new Item(BgPath, "background", Background("bg")))).Success);

        ApplyResult tiles = new ThemeApplier(files, () => 0).RestoreOriginals(ThemePart.Tiles);
        Assert.False(tiles.Success);
        Assert.Contains("chosen parts", tiles.Message);
        Assert.Equal(Background("bg"), files.Files[BgDevice]);

        ApplyResult all = new ThemeApplier(files, () => 0).RestoreOriginals(ThemePart.All);
        Assert.True(all.Success, all.Message);
        Assert.Contains("\"action\": \"restore-originals\"", Text(files, StatePath));
        Assert.Empty(Look.Parse(Text(files, LookPath))!);
    }

    [Fact]
    public void Without_a_look_file_a_partial_restore_keeps_the_pack_and_writes_no_look()
    {
        var files = Console();
        Assert.True(new ThemeApplier(files, () => 0).Apply(Everything()).Success);
        files.Files.Remove(LookPath);   // applied by 01.000.020

        Assert.True(new ThemeApplier(files, () => 0).RestoreOriginals(ThemePart.Icons).Success);

        Assert.Equal("all", AppliedTheme.Read(Text(files, StatePath)));
        Assert.False(files.Files.ContainsKey(LookPath));
    }

    [Fact]
    public void The_look_records_the_parts_an_apply_wrote_and_combines_or_replaces()
    {
        var files = Console();
        Assert.True(new ThemeApplier(files, () => 0) { SourcePath = "/lib/all.p5theme", Parts = ThemePart.All & ~ThemePart.Sounds }
            .Apply(Everything()).Success);
        Assert.Equal(new[] { new LookLayer("/lib/all.p5theme", "all", "Sounds", ThemePart.Background | ThemePart.Icons) },
            Look.Parse(Text(files, LookPath)));

        ThemePack music = Pack("music", new Item(MusicPath, "sound", Music("fein")));
        Assert.True(new ThemeApplier(files, () => 0) { SourcePath = "/lib/music.p5theme", KeepEarlierThemes = true }.Apply(music).Success);
        Assert.Equal(new[] { "/lib/all.p5theme", "/lib/music.p5theme" }, Look.Parse(Text(files, LookPath))!.Select(l => l.Pack));

        Assert.True(new ThemeApplier(files, () => 0) { SourcePath = "/lib/music.p5theme" }.Apply(music).Success);
        Assert.Equal(new[] { "/lib/music.p5theme" }, Look.Parse(Text(files, LookPath))!.Select(l => l.Pack));
    }

    [Fact]
    public void Each_target_belongs_to_one_part()
    {
        ThemePackTarget T(string path, string layer) => new(path, layer, "f", 1, new string('0', 64));
        Assert.Equal(ThemePart.Background, ThemeParts.Of(T(BgPath, "background")));
        Assert.Equal(ThemePart.AppBackgrounds, ThemeParts.Of(T(AssetRoots.AllAppsBackground, "background")));
        Assert.Equal(ThemePart.Icons, ThemeParts.Of(T(RcoPath, "svg")));
        Assert.Equal(ThemePart.Sounds, ThemeParts.Of(T(RcoPath, "vag")));
        Assert.Equal(ThemePart.Tiles, ThemeParts.Of(T("rnps/NPXS40047/appdb/default/icon0.png", "icon")));
        // A sound is a sound by its file, whatever layer the pack gave it.
        Assert.Equal(ThemePart.Sounds, ThemeParts.Of(T(MusicPath, "background")));
        Assert.Equal(ThemePart.Other, ThemeParts.Of(T("vsh_asset/some.mp4", "video")));
        Assert.Equal(ThemePart.Background | ThemePart.Icons | ThemePart.Sounds, ThemeParts.In(Everything().Manifest));
        Assert.Equal("Music and sounds", ThemeParts.Label(ThemePart.Sounds));
    }

    [Fact]
    public void Sound_targets_count_as_sounds_in_the_summary()
    {
        PackSummary summary = PackSummary.Of(Everything().Manifest);
        Assert.Equal(2, summary.Sounds);
        Assert.Equal(1, summary.Icons);
    }

    // ---- settings ---------------------------------------------------------

    [Fact]
    public void Sounds_are_on_unless_the_settings_turn_them_off()
    {
        Assert.True(AppSettings.Parse(null, AppSettings.NewDeviceId).Sounds);
        Assert.True(AppSettings.Parse("{\"background_scope\":\"all\"}", AppSettings.NewDeviceId).Sounds);
        AppSettings off = AppSettings.Parse("{\"sounds\":\"off\"}", AppSettings.NewDeviceId);
        Assert.False(off.Sounds);
        Assert.Contains("\"sounds\": \"off\"", off.Serialize());
        Assert.False(AppSettings.Parse(off.Serialize(), AppSettings.NewDeviceId).Sounds);
        Assert.Contains("\"sounds\": \"on\"", (off with { Sounds = true }).Serialize());
    }

    // ---- the apply question -----------------------------------------------

    private static UiInput Input(bool right = false, bool left = false, bool confirm = false, bool cancel = false, bool up = false, bool down = false) =>
        UiInput.None with { Right = right, Left = left, Confirm = confirm, Cancel = cancel, Up = up, Down = down };

    private static readonly UiInput X = Input(confirm: true), O = Input(cancel: true), Up = Input(up: true), Down = Input(down: true);
    private const ThemePart Mixed = ThemePart.Background | ThemePart.Icons | ThemePart.Sounds;

    private static ThemeBrowser OnDetail()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(1);
        browser.Update(X);
        Assert.Equal(ThemeView.Detail, browser.View);
        return browser;
    }

    [Fact]
    public void A_pack_with_several_parts_asks_which_with_all_ticked_and_the_focus_on_apply()
    {
        var browser = OnDetail();
        browser.Update(X, parts: Mixed);

        Assert.Equal(DialogKind.ApplyParts, browser.Dialog);
        Assert.Equal(new[] { ThemePart.Background, ThemePart.Icons, ThemePart.Sounds }, browser.DialogParts);
        Assert.Equal(new[] { "Background", "System icons", "Music and sounds", "Apply theme", "Cancel" }, browser.DialogOptions);
        Assert.Equal(3, browser.DialogFocus);
        Assert.Equal(Mixed, browser.ApplyParts);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X, parts: Mixed);
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.Equal(Mixed, browser.ApplyParts);
    }

    [Fact]
    public void X_on_a_part_ticks_it_and_only_apply_applies()
    {
        var browser = OnDetail();
        browser.Update(X, parts: Mixed);
        browser.Update(Up);
        browser.Update(Up);
        browser.Update(X);                                  // System icons off
        Assert.Equal(DialogKind.ApplyParts, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(ThemePart.Background | ThemePart.Sounds, browser.ApplyParts);
        browser.Update(X);                                  // and on again
        Assert.Equal(Mixed, browser.ApplyParts);
        browser.Update(X);
        browser.Update(Down);
        browser.Update(Down);
        browser.Update(X);                                  // Apply theme
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.Equal(ThemePart.Background | ThemePart.Sounds, browser.ApplyParts);
    }

    [Fact]
    public void With_sounds_off_they_start_unticked_and_a_choice_of_nothing_does_not_apply()
    {
        var browser = OnDetail();
        browser.Update(X, parts: ThemePart.Sounds, sounds: false);   // one part, but it is the sounds: still asked
        Assert.Equal(DialogKind.ApplyParts, browser.Dialog);
        Assert.Equal(ThemePart.None, browser.ApplyParts);
        browser.Update(X);                                          // Apply theme with nothing ticked
        Assert.Equal(DialogKind.ApplyParts, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(O);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X, parts: Mixed, sounds: false);
        Assert.Equal(ThemePart.Background | ThemePart.Icons, browser.ApplyParts);
    }

    [Fact]
    public void A_pack_with_one_part_asks_as_before_and_applies_everything()
    {
        var browser = OnDetail();
        browser.Update(X, parts: ThemePart.Icons);
        Assert.Equal(DialogKind.Apply, browser.Dialog);
        browser.Update(X);
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.Equal(ThemePart.All, browser.ApplyParts);
    }

    [Fact]
    public void A_ticked_app_background_asks_where_it_goes_next_and_an_unticked_one_does_not()
    {
        const ThemePart parts = ThemePart.Background | ThemePart.AppBackgrounds;
        var browser = OnDetail();
        browser.Update(X, asksScope: true, allAppsScope: true, parts: parts);
        browser.Update(X);                                          // Apply theme
        Assert.Equal(DialogKind.ApplyScope, browser.Dialog);
        Assert.Equal(1, browser.DialogFocus);                       // the saved scope
        browser.Update(X);
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.True(browser.ApplyAsksScope);
        Assert.True(browser.ApplyAllApps);
        Assert.Equal(parts, browser.ApplyParts);

        var without = OnDetail();
        without.Update(X, asksScope: true, parts: parts);
        without.Update(Up);                                         // App backgrounds
        without.Update(X);
        without.Update(Down);
        without.Update(X);
        Assert.Equal(DialogKind.None, without.Dialog);
        Assert.Equal(ThemeAction.Apply, without.TakeAction());
        Assert.False(without.ApplyAsksScope);
        Assert.Equal(ThemePart.Background, without.ApplyParts);
    }

    [Fact]
    public void A_job_that_started_meanwhile_stops_the_apply()
    {
        var browser = OnDetail();
        browser.Update(X, parts: Mixed);
        browser.Update(X, busy: true);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
    }
}
