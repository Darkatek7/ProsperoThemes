using System;
using System.Collections.Generic;

namespace ProsperoThemes.Shared;

/// <summary>
/// What a payload's first bytes have to be, decided by the suffix of the path it is written to.
/// </summary>
/// <remarks>
/// The engine is deliberately format-blind: it writes whatever bytes a target carries, which is what lets
/// backgrounds, icons, sounds and video ride one manifest. That also means nothing else stops a pack from
/// declaring a PNG where the shell expects a DDS — and a wrong-format file on a system partition is the
/// failure this whole project is most careful to avoid.
///
/// So the check is by suffix, and only where the answer is unambiguous. An unknown suffix is not an error:
/// this build has no opinion about `.gnf`, `.pak`, `.wad` or `.fbxd`, and inventing one would refuse packs
/// that are perfectly fine. A `.zst` suffix means the payload is a Zstandard frame; what is inside it is
/// the target's business, not this class's.
/// </remarks>
public static class PayloadFormat
{
    private readonly record struct Magic(long Offset, byte[] Bytes, string Name, string Suffix);

    private static readonly Magic[] Known =
    [
        new(0, "DDS "u8.ToArray(), "a DDS texture", ".dds"),
        new(0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "a PNG image", ".png"),
        new(0, "RIFF"u8.ToArray(), "a RIFF container (at9 audio)", ".at9"),
        new(4, "ftyp"u8.ToArray(), "an ISO base media file (mp4)", ".mp4"),
        new(0, "RCOF"u8.ToArray(), "an RCO resource container", ".rco"),
        new(0, "GNF "u8.ToArray(), "a GNF texture", ".gnf"),
        new(0, [0x28, 0xB5, 0x2F, 0xFD], "a Zstandard frame", ".zst"),
    ];

    private static readonly Dictionary<string, Magic> BySuffix = Build();

    private static Dictionary<string, Magic> Build()
    {
        var map = new Dictionary<string, Magic>(StringComparer.OrdinalIgnoreCase);
        foreach (Magic m in Known)
            map[m.Suffix] = m;
        return map;
    }

    /// <summary>The suffix a path's format is judged by, or an empty string when it is not known.</summary>
    public static string SuffixOf(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // The last suffix decides, so "bg_hub_default.dds.zst" is judged as a Zstandard frame.
        int dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1)
            return string.Empty;

        return path[dot..];
    }

    /// <summary>Whether this build knows what a path's payload must start with.</summary>
    public static bool IsKnown(string path) => BySuffix.ContainsKey(SuffixOf(path));

    /// <summary>
    /// Checks a payload against the format its path implies.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when it matches or when this build has no opinion; otherwise
    /// <see langword="false"/> with a reason naming what was expected and what arrived.
    /// </returns>
    public static bool Check(string path, ReadOnlySpan<byte> payload, out string reason)
    {
        reason = string.Empty;

        string suffix = SuffixOf(path);
        if (!BySuffix.TryGetValue(suffix, out Magic expected))
            return true;

        if (payload.Length < expected.Offset + expected.Bytes.Length)
        {
            reason = $"{path} should be {expected.Name}, but the payload is only {payload.Length} bytes";
            return false;
        }

        ReadOnlySpan<byte> got = payload.Slice((int)expected.Offset, expected.Bytes.Length);
        if (!got.SequenceEqual(expected.Bytes))
        {
            reason = $"{path} should be {expected.Name}, but the payload starts {Convert.ToHexString(got)} " +
                     $"instead of {Convert.ToHexString(expected.Bytes)}";
            return false;
        }

        return true;
    }
}
