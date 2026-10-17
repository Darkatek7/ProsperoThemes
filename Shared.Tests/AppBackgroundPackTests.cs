using System;
using System.Linq;
using System.Text;
using ProsperoThemes.Shared;
using SharpProspero.Compression;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The per-app entry backgrounds under <c>/user/appmeta/&lt;TID&gt;/</c>, which are a different kind of
/// target from everything else: they are plain DDS rather than Zstandard frames, and which titles exist is
/// decided by what is installed rather than by a catalog.
/// </summary>
/// <remarks>
/// These tests also guard the format check, which exists because the engine is deliberately format-blind.
/// Writing a PNG where the shell expects a texture is the kind of mistake that shows up as a broken screen
/// rather than an error, so it is refused before anything is written.
/// </remarks>
public class AppBackgroundPackTests
{
    // ---- helpers ----------------------------------------------------------

    private static string J(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string TargetJson(string path, string layer, string file, byte[] data) => $$"""
        {"path":"{{J(path)}}","layer":"{{J(layer)}}","file":"{{J(file)}}",
         "bytes":{{data.LongLength}},"sha256":"{{ThemePack.Hash(data)}}"}
        """;

    private static string ManifestJson(string targets) => $$"""
        {"format":2,"id":"app-bg","name":"App Background","author":"tests","version":"1.0",
         "description":"per-app entry backgrounds","unmapped":"keep","targets":[{{targets}}]}
        """;

    private static ThemePack Pack(string path, string layer, string file, byte[] payload)
    {
        string manifest = ManifestJson(TargetJson(path, layer, file, payload));
        byte[] archive = new ZipBuilder().AddText("theme.json", manifest).Add(file, payload).ToArray();
        return ThemePack.Open(archive);
    }

    /// <summary>A payload whose first bytes satisfy the suffix, so a test about something else is not tripped.</summary>
    private static byte[] PayloadFor(string path, int size = 4096)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = (byte)(i & 0xFF);

        if (path.EndsWith(".zst", StringComparison.Ordinal))
            new byte[] { 0x28, 0xB5, 0x2F, 0xFD }.CopyTo(data, 0);
        else if (path.EndsWith(".dds", StringComparison.Ordinal))
            "DDS "u8.ToArray().CopyTo(data, 0);
        else if (path.EndsWith(".png", StringComparison.Ordinal))
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        else if (path.EndsWith(".mp4", StringComparison.Ordinal))
            "ftyp"u8.ToArray().CopyTo(data, 4);

        return data;
    }

    // ---- the root ---------------------------------------------------------

    [Fact]
    public void The_appmeta_root_resolves_onto_the_user_volume()
    {
        Assert.True(AssetRoots.TryResolve("appmeta/PPSA01650/pic0.dds", out string path, out string reason),
            reason);
        Assert.Equal("/user/appmeta/PPSA01650/pic0.dds", path);
    }

    [Fact]
    public void The_title_is_part_of_the_target_path()
    {
        // Two titles, two different files, one pack: which is what makes this layer worth having.
        Assert.True(AssetRoots.TryResolve("appmeta/PPSA01650/pic0.dds", out string a, out _));
        Assert.True(AssetRoots.TryResolve("appmeta/MOUU12023/pic0.dds", out string b, out _));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_path_that_climbs_out_of_appmeta_is_refused()
    {
        Assert.False(AssetRoots.TryResolve("appmeta/../../user/app/PPSA01650/eboot.bin", out _, out string reason));
        Assert.Contains("..", reason, StringComparison.Ordinal);

        Assert.False(AssetRoots.TryResolve("appmeta/PPSA01650/../../../system_ex/vsh_asset/x", out _, out _));
        Assert.False(AssetRoots.TryResolve("appmeta//pic0.dds", out _, out _));
    }

    /// <summary>
    /// A pin, not a feature test. <c>DevProbe/apply-pack.ps1</c> keeps its own copy of this table because a
    /// PowerShell applier cannot call into this assembly; when a root is added or renamed, this test fails
    /// first and points whoever changed it at the mirror that has to move with it.
    /// </summary>
    [Fact]
    public void The_root_table_is_exactly_what_the_applier_mirrors()
    {
        string[] roots = AssetRoots.Known.OrderBy(r => r, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "appmeta", "rnps", "userapp", "vsh_asset" }, roots);

        Assert.Equal("/system_ex/vsh_asset", AssetRoots.MountFor(AssetRoots.VshAsset));
        Assert.Equal("/user/appmeta", AssetRoots.MountFor(AssetRoots.AppMeta));
        Assert.Equal("/mnt/rnps/apps", AssetRoots.MountFor(AssetRoots.Rnps));
        Assert.Null(AssetRoots.MountFor("system_data"));
    }

    // ---- the format check -------------------------------------------------

    [Fact]
    public void A_plain_dds_app_background_validates()
    {
        string target = "appmeta/PPSA01650/pic0.dds";
        ThemePack pack = Pack(target, "background", "assets/pic0.dds", PayloadFor(target));

        PackReport report = pack.Validate();

        Assert.False(report.HasErrors, string.Join("; ", report.Errors.Select(e => e.Message)));
        Assert.Single(pack.ApplicableTargets());
    }

    [Fact]
    public void A_dds_target_carrying_png_bytes_is_refused()
    {
        // The exact mistake the check exists for: the right name, the wrong kind of bytes.
        string target = "appmeta/PPSA01650/pic0.dds";
        ThemePack pack = Pack(target, "background", "assets/pic0.dds", PayloadFor("x.png"));

        PackReport report = pack.Validate();

        Assert.True(report.HasErrors);
        Assert.Contains(report.Errors, e => e.Code == "format");
        Assert.Contains("DDS", report.Errors.First(e => e.Code == "format").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zstd_target_carrying_a_raw_dds_is_refused()
    {
        // The wallpaper targets are Zstandard frames; the same texture without the wrapper is not one.
        string target = "vsh_asset/bg_hub_default.dds.zst";
        ThemePack pack = Pack(target, "background", "assets/bg", PayloadFor("x.dds"));

        PackReport report = pack.Validate();

        Assert.Contains(report.Errors, e => e.Code == "format");
    }

    [Fact]
    public void The_mp4_magic_is_looked_for_at_offset_four()
    {
        // "ftyp" is a box type, so it sits after the box length. Checking offset 0 would refuse every real
        // mp4 and accept nothing, which is worth pinning down.
        byte[] good = new byte[64];
        "ftyp"u8.ToArray().CopyTo(good, 4);
        Assert.False(Pack("vsh_asset/x.mp4", "video", "assets/x.mp4", good).Validate().HasErrors);

        byte[] bad = new byte[64];
        "ftyp"u8.ToArray().CopyTo(bad, 0);
        Assert.True(Pack("vsh_asset/x.mp4", "video", "assets/x.mp4", bad).Validate().HasErrors);
    }

    [Fact]
    public void An_unknown_suffix_is_not_refused()
    {
        // This build has no opinion about .pak, .wad or .fbxd, and inventing one would refuse good packs.
        byte[] payload = Encoding.ASCII.GetBytes("not a known container at all");
        foreach (string target in new[] { "vsh_asset/bglayer_haptics.pak", "vsh_asset/cis_ac_model.wad",
                                          "vsh_asset/shutdown_lightbar.fbxd" })
        {
            PackReport report = Pack(target, "ui", "assets/x", payload).Validate();
            Assert.False(report.HasErrors, $"{target}: {string.Join("; ", report.Errors.Select(e => e.Message))}");
        }
    }

    [Fact]
    public void A_truncated_payload_is_refused_rather_than_read_past()
    {
        // Two bytes where four are examined: the check must not throw.
        PackReport report = Pack("vsh_asset/x.dds", "background", "assets/x", new byte[] { (byte)'D', (byte)'D' })
            .Validate();

        Assert.True(report.HasErrors);
        Assert.Contains("only 2 bytes", report.Errors.First(e => e.Code == "format").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Suffixes_are_read_from_the_last_dot()
    {
        Assert.Equal(".zst", PayloadFormat.SuffixOf("vsh_asset/bg_hub_default.dds.zst"));
        Assert.Equal(".dds", PayloadFormat.SuffixOf("appmeta/PPSA01650/pic0.dds"));
        Assert.True(PayloadFormat.IsKnown("vsh_asset/Sce.Vsh.WebBrowser.rco"));
        Assert.False(PayloadFormat.IsKnown("vsh_asset/x.pak"));

        // A dotted directory must not be mistaken for the file's suffix.
        Assert.Equal(".dds", PayloadFormat.SuffixOf("appmeta/1.00/pic0.dds"));
    }
}
