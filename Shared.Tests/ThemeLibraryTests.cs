using ProsperoThemes.App.Library;
using SharpProspero.Compression;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class ThemeLibraryTests
{
    [Fact]
    public void Real_creator_pack_becomes_a_library_entry_without_retaining_its_payloads()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "two-screen.p5theme"));
        ThemeLibraryEntry entry = ThemeLibraryEntry.Inspect("two-screen.p5theme", bytes);
        Assert.False(entry.Report.HasErrors);
        Assert.Equal(2, entry.Manifest.Targets.Count);
        Assert.Equal("two-screen.p5theme", entry.Path);
        Assert.False(string.IsNullOrWhiteSpace(entry.Manifest.Name));
    }

    [Fact]
    public void Corrupt_archive_has_an_actionable_error()
    {
        Assert.Throws<ProsperoThemes.Shared.ThemePackException>(() => ThemeArchive.Open("not a pack"u8.ToArray()));
    }

    [Fact]
    public void Expanded_zip_budget_is_checked_before_payload_extraction()
    {
        byte[] archive = new ZipBuilder().Add("large.bin", new byte[4096]).ToArray();
        var error = Assert.Throws<ProsperoThemes.Shared.ThemePackException>(() => ThemeArchive.Open(archive, 2048));
        Assert.Contains("expanded", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Archive_with_too_many_entries_is_refused()
    {
        var builder = new ZipBuilder();
        for (int i = 0; i <= ThemeArchive.MaxEntries; i++) builder.AddText($"file-{i}", "x");
        Assert.Throws<ProsperoThemes.Shared.ThemePackException>(() => ThemeArchive.Open(builder.ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Forged_deflate_lengths_are_stopped_by_an_actual_output_limit(int declared)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry("payload", System.IO.Compression.CompressionLevel.Optimal).Open())
            entry.Write(new byte[16384]);
        byte[] bytes = stream.ToArray();
        int central = bytes.AsSpan().IndexOf("PK\x01\x02"u8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(central + 24, 4), declared);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), declared);
        var error = Assert.Throws<ProsperoThemes.Shared.ThemePackException>(() => ThemeArchive.Open(bytes, 2048));
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_nonzero_tar_header_with_a_nul_name_does_not_bypass_entry_limits()
    {
        byte[] pack = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "two-screen.p5theme"));
        byte[] manifest = ProsperoThemes.Shared.ThemePack.Open(pack).Find("theme.json")!;
        using var stream = new MemoryStream();
        stream.Write(TarHeader("", 0));
        for (int i = 0; i < ThemeArchive.MaxEntries; i++) stream.Write(TarHeader("file-" + i, 0));
        stream.Write(TarHeader("theme.json", manifest.Length)); stream.Write(manifest);
        stream.Write(new byte[(512 - manifest.Length % 512) % 512 + 1024]);
        var error = Assert.Throws<ProsperoThemes.Shared.ThemePackException>(() => ThemeArchive.Open(stream.ToArray()));
        Assert.Contains("entries", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] TarHeader(string name, int size)
    {
        var h = new byte[512];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
        System.Text.Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0')).CopyTo(h, 124);
        h[156] = (byte)'0'; "ustar"u8.CopyTo(h.AsSpan(257));
        h.AsSpan(148, 8).Fill(32);
        int checksum = h.Sum(b => (int)b);
        System.Text.Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0')).CopyTo(h, 148);
        h[154] = 0;
        return h;
    }
}
