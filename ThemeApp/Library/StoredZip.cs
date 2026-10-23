using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpProspero.Security;

namespace ProsperoThemes.App.Library;

/// <summary>
/// Writes a zip whose entries are stored, not compressed: what a pack made on the console is. Its payloads are compressed
/// already and the console has no DEFLATE encoder; every reader of the format takes a zip (the store's upload and
/// <c>theme_pack.py verify</c> only a zip). The timestamps are fixed, so the same files always give the same bytes.
/// </summary>
public static class StoredZip
{
    private const ushort Version = 20, Utf8Names = 0x0800, Stored = 0;
    // 1980-01-01 00:00, the first day a DOS date can say.
    private const ushort DosDate = (0 << 9) | (1 << 5) | 1, DosTime = 0;

    public static byte[] Write(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var output = new MemoryStream();
        var central = new MemoryStream();
        byte[] local = new byte[30], entry = new byte[46];
        foreach ((string name, byte[] data) in files)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            uint crc = Crc32.Compute(data);
            uint offset = (uint)output.Length;

            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(0), 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(6), Utf8Names);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), Stored);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(10), DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(12), DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(28), 0);
            output.Write(local);
            output.Write(nameBytes);
            output.Write(data);

            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0), 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), Version);
            local.AsSpan(4, 26).CopyTo(entry.AsSpan(6));   // version needed to name length: the same as the local header
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(32), 0);   // comment
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(34), 0);   // disk
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(36), 0);   // internal attributes
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(38), 0);   // external attributes
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(42), offset);
            central.Write(entry);
            central.Write(nameBytes);
        }
        uint centralOffset = (uint)output.Length;
        central.Position = 0;
        central.CopyTo(output);

        byte[] end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(0), 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)files.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)files.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)central.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), centralOffset);
        output.Write(end);
        return output.ToArray();
    }
}
