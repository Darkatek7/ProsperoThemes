using ProsperoThemes.Shared;
using System;
using System.Linq;
using System.Buffers.Binary;

namespace ProsperoThemes.App.Library;

public sealed record ThemeLibraryEntry(string Path, ThemePackManifest Manifest, PackReport Report, byte[]? Preview)
{
    public bool Bundled { get; init; }
    public static ThemeLibraryEntry Inspect(string path, byte[] bytes)
    {
        var pack = ThemeArchive.Open(bytes);
        var report = pack.Validate();
        if (report.HasErrors)
            throw new ThemePackException(string.Join("; ", report.Errors.Select(e => e.Message)));
        byte[]? preview = pack.Manifest.Preview is { } name ? pack.Find(name) : null;
        if (preview is not null && !IsBoundedPng(preview))
        {
            report.Warn("preview", "Cover must be a PNG up to 1920x1080 and 4 MiB; artwork was skipped.");
            preview = null;
        }
        return new ThemeLibraryEntry(path, pack.Manifest, report, preview);
    }

    public static bool IsBoundedPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || bytes.Length > 4 * 1024 * 1024 ||
            !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        return width is > 0 and <= 1920 && height is > 0 and <= 1080;
    }
}
