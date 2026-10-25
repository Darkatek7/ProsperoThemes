using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ProsperoThemes.Shared;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class AvatarPackTests
{
    // Written by the TypeScript writer (web/shared/scripts/gen-avatar-fixture.ts).
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "avatar-house.p5avatar"));

    [Fact]
    public void The_web_writers_pack_reads_and_validates()
    {
        AvatarPack pack = AvatarPack.Open(Fixture());
        PackReport report = pack.Validate();
        Assert.False(report.HasErrors, string.Join("\n", report.Issues));
        Assert.Equal("fixture-house", pack.Id);
        Assert.Equal("House", pack.Name);
        Assert.Equal(new[] { 64, 128, 260, 440 }, pack.Images.Select(i => i.Size));
        // The sizes of the console's own avatar files on 13.00.
        Assert.Equal(new[] { 4224, 16512, 67728, 193728 }, AvatarPack.Sizes.Select(s => pack.Image(s)!.Length));
        Assert.Equal(0xFF, pack.Preview()![0]);
    }

    [Fact]
    public void A_changed_or_missing_file_is_an_error()
    {
        byte[] changed = Rewrite(Fixture(), "avatar260.dds", data => { data[500] ^= 0xFF; return data; });
        Assert.Contains(AvatarPack.Open(changed).Validate().Errors, e => e.Code == "sha256" && e.Message.StartsWith("avatar260.dds"));

        byte[] missing = Rewrite(Fixture(), "avatar64.dds", _ => null);
        Assert.Contains(AvatarPack.Open(missing).Validate().Errors, e => e.Code == "missing");
    }

    [Fact]
    public void The_preview_must_be_a_small_jpeg()
    {
        byte[] png = Rewrite(Fixture(), "preview.jpg", _ => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Assert.Contains(AvatarPack.Open(png).Validate().Errors, e => e.Message == "preview.jpg is not a JPEG");
    }

    [Fact]
    public void Dds_check_follows_the_console_files()
    {
        byte[] good = AvatarPack.Open(Fixture()).Image(128)!;
        Assert.Null(AvatarPack.DdsProblem(128, good));
        Assert.Equal("is 128x128, not 64x64", AvatarPack.DdsProblem(64, good));
        byte[] dx10 = (byte[])good.Clone();
        "DX10"u8.CopyTo(dx10.AsSpan(84));
        Assert.Equal("is not DXT5", AvatarPack.DdsProblem(128, dx10));
        Assert.Equal("is not a DDS file", AvatarPack.DdsProblem(128, good.AsSpan(0, 64)));
        Assert.Contains("bytes, not", AvatarPack.DdsProblem(128, good.AsSpan(0, good.Length - 16)));
    }

    [Fact]
    public void A_theme_pack_is_not_an_avatar_pack()
    {
        byte[] theme = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ui-sound.p5theme"));
        Assert.Throws<ThemePackException>(() => AvatarPack.Open(theme));
        Assert.Throws<ThemePackException>(() => AvatarPack.Open(new byte[AvatarPack.ArchiveCap + 1]));
    }

    // The archive with one entry replaced (or removed when the function returns null).
    private static byte[] Rewrite(byte[] archive, string name, Func<byte[], byte[]?> change)
    {
        using var input = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in input.Entries)
            {
                using var source = new MemoryStream();
                using (Stream s = entry.Open()) s.CopyTo(source);
                byte[]? data = entry.FullName == name ? change(source.ToArray()) : source.ToArray();
                if (data is null) continue;
                using Stream target = zip.CreateEntry(entry.FullName).Open();
                target.Write(data);
            }
        }
        return output.ToArray();
    }
}
