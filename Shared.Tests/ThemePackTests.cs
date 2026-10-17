using System.Text;
using SharpProspero.Compression;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// Tests for the theme pack format. Every one of these runs on the PC with no console attached, which is
/// the reason the format lives in dependency-free managed C#.
/// </summary>
public class ThemePackTests
{
    // ---- helpers ----------------------------------------------------------

    /// <summary>Escapes a value for embedding in the hand-written JSON below.</summary>
    private static string J(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string TargetJson(
        string path,
        string layer,
        string file,
        byte[] data,
        string? sha256 = null,
        long? bytes = null)
    {
        long size = bytes ?? data.LongLength;
        string hash = sha256 ?? ThemePack.Hash(data);
        return $$"""
            {"path":"{{J(path)}}","layer":"{{J(layer)}}","file":"{{J(file)}}","bytes":{{size}},"sha256":"{{hash}}"}
            """;
    }

    private static string ManifestJson(string targets, string id = "test", string unmapped = "keep") => $$"""
        {"format":2,"id":"{{id}}","name":"Test Pack","author":"tests","version":"1.0",
         "description":"a pack","unmapped":"{{unmapped}}","targets":[{{targets}}]}
        """;

    private static byte[] ZipPack(string manifestJson, params (string Name, byte[] Data)[] files)
    {
        var zip = new ZipBuilder().AddText("theme.json", manifestJson);
        foreach ((string name, byte[] data) in files)
            zip.Add(name, data);
        return zip.ToArray();
    }

    private static byte[] Payload(int size, byte seed = 7)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = (byte)((i * seed) & 0xFF);
        return data;
    }

    /// <summary>
    /// A payload that satisfies the format its path implies, so a test about something else is not tripped
    /// by the format check. Only the magics this suite needs are filled in.
    /// </summary>
    private static byte[] PayloadFor(string path, int size, byte seed = 7)
    {
        byte[] data = Payload(size, seed);
        if (path.EndsWith(".zst", StringComparison.Ordinal))
            new byte[] { 0x28, 0xB5, 0x2F, 0xFD }.CopyTo(data, 0);
        else if (path.EndsWith(".dds", StringComparison.Ordinal))
            "DDS "u8.ToArray().CopyTo(data, 0);
        else if (path.EndsWith(".png", StringComparison.Ordinal))
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        return data;
    }

    /// <summary>Builds a minimal ustar archive, so the tar path is tested rather than assumed.</summary>
    private static byte[] TarPack(params (string Name, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        foreach ((string name, byte[] data) in files)
        {
            var header = new byte[512];
            Encoding.ASCII.GetBytes(name, header.AsSpan(0, 100));
            WriteOctal(header, 100, 8, 0b100_000_000);   // mode
            WriteOctal(header, 108, 8, 0);               // uid
            WriteOctal(header, 116, 8, 0);               // gid
            WriteOctal(header, 124, 12, data.LongLength); // size
            WriteOctal(header, 136, 12, 0);              // mtime
            header[156] = (byte)'0';                     // regular file
            Encoding.ASCII.GetBytes("ustar", header.AsSpan(257, 5));
            header[262] = (byte)'0';
            header[263] = (byte)'0';
            WriteOctal(header, 148, 8, Checksum(header));
            ms.Write(header);

            ms.Write(data);
            int pad = (int)((512 - (data.LongLength % 512)) % 512);
            ms.Write(new byte[pad]);
        }

        ms.Write(new byte[1024]); // two zero blocks end the archive
        return ms.ToArray();
    }

    private static void WriteOctal(byte[] header, int offset, int length, long value)
    {
        string text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        Encoding.ASCII.GetBytes(text, header.AsSpan(offset, length - 1));
        header[offset + length - 1] = 0;
    }

    private static int Checksum(byte[] header)
    {
        int sum = 0;
        for (int i = 0; i < 512; i++)
            sum += i >= 148 && i < 156 ? 32 : header[i];
        return sum;
    }

    // ---- happy path -------------------------------------------------------

    [Fact]
    public void Valid_zip_pack_validates_clean()
    {
        byte[] payload = PayloadFor("vsh_asset/bg_hub_default.dds.zst", 1024);
        string manifest = ManifestJson(TargetJson("vsh_asset/bg_hub_default.dds.zst", "background", "assets/bg.dds.zst", payload));
        ThemePack pack = ThemePack.Open(ZipPack(manifest, ("assets/bg.dds.zst", payload)));

        PackReport report = pack.Validate();

        Assert.False(report.HasErrors);
        Assert.Empty(report.Issues);
        Assert.Equal("valid", report.Summary());
        Assert.Single(pack.ApplicableTargets());
    }

    [Fact]
    public void Valid_tar_pack_validates_clean()
    {
        byte[] payload = PayloadFor("vsh_asset/bg_hub_default.dds.zst", 512);
        string manifest = ManifestJson(TargetJson("vsh_asset/bg_hub_default.dds.zst", "background", "assets/bg.dds.zst", payload));
        ThemePack pack = ThemePack.Open(TarPack(("theme.json", Encoding.UTF8.GetBytes(manifest)), ("assets/bg.dds.zst", payload)));

        Assert.False(pack.Validate().HasErrors);
    }

    [Fact]
    public void Manifest_survives_a_write_and_parse_round_trip()
    {
        byte[] payload = Payload(64);
        var original = new ThemePackManifest(
            Format: 2,
            Id: "round-trip",
            Name: "Round Trip",
            Author: "tests",
            Version: "2.3.4",
            Description: "d",
            Unmapped: "stock",
            Targets: new[]
            {
                new ThemePackTarget("vsh_asset/x.dds.zst", "background", "assets/x", payload.LongLength,
                    ThemePack.Hash(payload), "hub_default", new string('a', 64)),
            },
            Preview: "preview.png");

        ThemePackManifest parsed = ThemePackManifest.Parse(original.Write());

        // Compared field by field: ThemePackManifest holds an IReadOnlyList, and record equality would
        // compare that by reference, so two structurally identical manifests would not be equal.
        Assert.Equal(original.Format, parsed.Format);
        Assert.Equal(original.Id, parsed.Id);
        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.Author, parsed.Author);
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.Description, parsed.Description);
        Assert.Equal(original.Unmapped, parsed.Unmapped);
        Assert.Equal(original.Preview, parsed.Preview);
        Assert.Equal(original.Targets, parsed.Targets);   // ThemePackTarget is all value fields
    }

    // ---- refusals ---------------------------------------------------------

    [Fact]
    public void Unknown_root_is_an_error()
    {
        byte[] payload = Payload(16);
        string manifest = ManifestJson(TargetJson("system_data/secret.bin", "ui", "assets/x", payload));
        PackReport report = ThemePack.Open(ZipPack(manifest, ("assets/x", payload))).Validate();

        Assert.True(report.HasErrors);
        Assert.Contains(report.Errors, e => e.Code == "path" && e.Message.Contains("unknown asset root"));
    }

    [Theory]
    [InlineData("vsh_asset/../system_data/x")]
    [InlineData("vsh_asset/a/../../x")]
    [InlineData("vsh_asset//x")]
    [InlineData("vsh_asset/x/")]
    [InlineData("/vsh_asset/x")]
    [InlineData("vsh_asset/sub\\x")]
    [InlineData("vsh_asset")]
    [InlineData("")]
    public void Unsafe_paths_are_errors(string path)
    {
        byte[] payload = Payload(16);
        string manifest = ManifestJson(TargetJson(path, "background", "assets/x", payload));
        PackReport report = ThemePack.Open(ZipPack(manifest, ("assets/x", payload))).Validate();

        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Missing_payload_is_an_error()
    {
        byte[] payload = Payload(16);
        string manifest = ManifestJson(TargetJson("vsh_asset/x", "background", "assets/missing", payload));
        PackReport report = ThemePack.Open(ZipPack(manifest)).Validate();

        Assert.True(report.HasErrors);
        Assert.Contains(report.Errors, e => e.Code == "missing");
    }

    [Fact]
    public void Declared_size_mismatch_is_an_error()
    {
        byte[] payload = Payload(16);
        string manifest = ManifestJson(
            TargetJson("vsh_asset/x", "background", "assets/x", payload, bytes: payload.LongLength + 1));
        PackReport report = ThemePack.Open(ZipPack(manifest, ("assets/x", payload))).Validate();

        Assert.True(report.HasErrors);
        Assert.Contains(report.Errors, e => e.Code == "bytes");
    }

    [Fact]
    public void Payload_hash_mismatch_is_an_error()
    {
        byte[] payload = Payload(16);
        string manifest = ManifestJson(
            TargetJson("vsh_asset/x", "background", "assets/x", payload, sha256: new string('0', 64)));
        PackReport report = ThemePack.Open(ZipPack(manifest, ("assets/x", payload))).Validate();

        Assert.True(report.HasErrors);
        Assert.Contains(report.Errors, e => e.Code == "sha256");
    }

    [Fact]
    public void Duplicate_targets_are_an_error()
    {
        byte[] payload = Payload(16);
        string one = TargetJson("vsh_asset/x", "background", "assets/x", payload);
        string two = TargetJson("vsh_asset/x", "background", "assets/x", payload);
        PackReport report = ThemePack.Open(ZipPack(ManifestJson($"{one},{two}"), ("assets/x", payload))).Validate();

        Assert.True(report.HasErrors);
        Assert.Contains(report.Errors, e => e.Code == "duplicate");
    }

    [Fact]
    public void Empty_target_list_is_an_error()
    {
        PackReport report = ThemePack.Open(ZipPack(ManifestJson(string.Empty))).Validate();
        Assert.Contains(report.Errors, e => e.Code == "targets");
    }

    [Fact]
    public void Wrong_format_version_is_an_error()
    {
        byte[] payload = Payload(16);
        string manifest = """
            {"format":99,"id":"x","name":"n","author":"a","version":"1","description":"d",
             "unmapped":"keep","targets":[]}
            """;
        PackReport report = ThemePack.Open(ZipPack(manifest)).Validate();
        Assert.Contains(report.Errors, e => e.Code == "format");
    }

    // ---- forward compatibility --------------------------------------------

    [Fact]
    public void Unknown_layer_warns_and_is_skipped_rather_than_refused()
    {
        byte[] payload = Payload(32);
        string manifest = ManifestJson(TargetJson("vsh_asset/x", "hologram", "assets/x", payload));
        ThemePack pack = ThemePack.Open(ZipPack(manifest, ("assets/x", payload)));

        PackReport report = pack.Validate();

        Assert.False(report.HasErrors);                       // a newer pack still applies
        Assert.Contains(report.Warnings, w => w.Code == "layer");
        Assert.Empty(pack.ApplicableTargets());               // but the unknown entry is skipped
    }

    // ---- structural failures ---------------------------------------------

    [Fact]
    public void Not_an_archive_is_rejected()
    {
        Assert.Throws<ThemePackException>(() => ThemePack.Open(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void Archive_without_a_manifest_is_rejected()
    {
        byte[] archive = new ZipBuilder().Add("assets/x", Payload(8)).ToArray();
        Assert.Throws<ThemePackException>(() => ThemePack.Open(archive));
    }

    [Fact]
    public void Malformed_manifest_is_rejected()
    {
        byte[] archive = ZipPack("{not json");
        Assert.Throws<ThemePackException>(() => ThemePack.Open(archive));
    }
}

/// <summary>Tests for the root allowlist, which is the format's main safety boundary.</summary>
public class AssetRootsTests
{
    [Fact]
    public void Resolves_a_known_root()
    {
        Assert.True(AssetRoots.TryResolve("vsh_asset/bg_hub_default.dds.zst", out string path, out _));
        Assert.Equal("/system_ex/vsh_asset/bg_hub_default.dds.zst", path);
    }

    [Fact]
    public void Preserves_nested_relative_paths()
    {
        Assert.True(AssetRoots.TryResolve("vsh_asset/sub/dir/file.png", out string path, out _));
        Assert.Equal("/system_ex/vsh_asset/sub/dir/file.png", path);
    }

    [Theory]
    [InlineData("vsh_asset/../x")]
    [InlineData("../vsh_asset/x")]
    [InlineData("vsh_asset/./x")]
    [InlineData("other_root/x")]
    [InlineData("vsh_asset/")]
    [InlineData("vsh_asset")]
    public void Refuses_unsafe_or_unknown(string path)
    {
        Assert.False(AssetRoots.TryResolve(path, out string resolved, out string reason));
        Assert.Empty(resolved);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Known_roots_are_published()
    {
        Assert.Contains(AssetRoots.VshAsset, AssetRoots.Known);
        Assert.Equal("/system_ex/vsh_asset", AssetRoots.MountFor(AssetRoots.VshAsset));
    }
}
