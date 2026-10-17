using ProsperoThemes.Shared;
using System;
using System.Buffers.Binary;
using SharpProspero.Security;
using SharpProspero.Compression;

namespace ProsperoThemes.App.Library;

public static class ThemeArchive
{
    public const int MaxEntries = 256;
    public const int MaxArchiveBytes = 48 * 1024 * 1024;
    public const int MaxExpandedBytes = 64 * 1024 * 1024;
    public static ThemePack Open(byte[] bytes, long expandedLimit = MaxExpandedBytes)
    {
        if (bytes.Length > MaxArchiveBytes)
            throw new ThemePackException("Pack exceeds the 48 MiB archive limit.");
        try
        {
            if (bytes.AsSpan().StartsWith("PK\x03\x04"u8))
            {
                var zip = ZipArchive.Open(bytes);
                if (zip.Entries.Count > MaxEntries)
                    throw new ThemePackException("Pack contains too many archive entries (maximum 256).");
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    if (entry.UncompressedSize < 0 || entry.UncompressedSize > expandedLimit - total)
                        throw new ThemePackException("Pack exceeds the expanded content limit.");
                    total += entry.UncompressedSize;
                }
                ValidateActualZipExpansion(bytes, zip);
            }
            else if (bytes.Length >= 512 && bytes.AsSpan(257, 5).SequenceEqual("ustar"u8))
            {
                // TAR is uncompressed, but still limit its entry count before the parser copies data.
                int offset = 0, count = 0;
                long total = 0;
                while (offset <= bytes.Length - 512 && bytes.AsSpan(offset, 512).IndexOfAnyExcept((byte)0) >= 0)
                {
                    if (++count > MaxEntries) throw new ThemePackException("Pack contains too many archive entries.");
                    if (bytes[offset + 156] is (byte)'x' or (byte)'g')
                        throw new ThemePackException("PAX extended TAR headers are unsupported by this bounded importer; use USTAR or ZIP.");
                    long length = 0;
                    foreach (byte b in bytes.AsSpan(offset + 124, 12))
                    {
                        if (b is 0 or 32) continue;
                        if (b < '0' || b > '7') throw new ThemePackException("Invalid TAR entry length.");
                        length = checked(length * 8 + b - '0');
                    }
                    if (length > expandedLimit - total) throw new ThemePackException("Pack exceeds the expanded content limit.");
                    total += length;
                    long next = offset + 512L + ((length + 511) / 512) * 512;
                    if (next > bytes.Length) throw new ThemePackException("Truncated TAR entry.");
                    offset = checked((int)next);
                }
            }
            return ThemePack.Open(bytes);
        }
        catch (ThemePackException) { throw; }
        catch (Exception ex) { throw new ThemePackException($"Cannot read theme archive: {ex.Message}", ex); }
    }

    private static void ValidateActualZipExpansion(byte[] archive, ZipArchive zip)
    {
        // The SDK uses directory sizes as allocation hints, not limits. Check the actual DEFLATE
        // output under a positive cap before handing the unchanged archive to its pack reader.
        int end = archive.AsSpan().LastIndexOf("PK\x05\x06"u8);
        if (end < 0 || end > archive.Length - 22) throw new ThemePackException("Invalid ZIP directory.");
        int central = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(end + 16, 4)));
        foreach (var entry in zip.Entries)
        {
            if (central < 0 || central > archive.Length - 46 ||
                !archive.AsSpan(central, 4).SequenceEqual("PK\x01\x02"u8))
                throw new ThemePackException("Invalid ZIP directory record.");
            int U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(p, 2));
            int local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(central + 42, 4)));
            int next = checked(central + 46 + U16(central + 28) + U16(central + 30) + U16(central + 32));
            if (next > archive.Length) throw new ThemePackException("Truncated ZIP directory record.");
            central = next;
            if (entry.IsDirectory) continue;
            if (local < 0 || local > archive.Length - 30 || !archive.AsSpan(local, 4).SequenceEqual("PK\x03\x04"u8))
                throw new ThemePackException("Invalid ZIP local header.");
            int start = checked(local + 30 + U16(local + 26) + U16(local + 28));
            if (entry.CompressedSize < 0 || start < 0 || start > archive.Length || entry.CompressedSize > archive.Length - start)
                throw new ThemePackException("Truncated ZIP payload.");
            if (entry.Method == 0 && entry.CompressedSize != entry.UncompressedSize)
                throw new ThemePackException("Stored ZIP entry exceeds its declared expanded limit.");
            if (entry.Method == 8)
            {
                int cap = Math.Max(1, checked((int)entry.UncompressedSize));
                byte[] data;
                try { data = Inflate.Raw(archive.AsSpan(start, (int)entry.CompressedSize), Math.Min(4096, cap), maxOutput: cap); }
                catch (CompressionException ex) { throw new ThemePackException("ZIP expanded output limit or content check failed: " + ex.Message, ex); }
                if (data.Length != entry.UncompressedSize || Crc32.Compute(data) != entry.Crc32)
                    throw new ThemePackException("ZIP expanded output did not match its declared length and checksum.");
            }
        }
    }
}
