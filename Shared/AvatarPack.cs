using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using SharpProspero.Storage;

namespace ProsperoThemes.Shared;

/// <summary>
/// A profile picture pack (<c>.p5avatar</c>), kept apart from theme packs: <c>avatar.json</c>, the four DXT5 files the
/// console's profile cache needs and a JPEG preview. The TypeScript writer is <c>web/shared/src/avatar.ts</c>; both must
/// agree (<c>Shared.Tests/fixtures/avatar-house.p5avatar</c> comes from it).
/// </summary>
/// <remarks>
/// Measured on 13.00 (docs/plan-avatar.md): <c>avatar64/128/260/440.dds</c> in <c>/system_data/priv/cache/profile/0x&lt;ID&gt;/</c>
/// plus any <c>online.json</c> make the home screen show the picture after a restart; PNG and <c>picture*</c> files are not
/// needed. The files are written as they are, so they are checked here the way the console reads them.
/// </remarks>
public sealed class AvatarPack
{
    public const string ManifestName = "avatar.json";
    public const int CurrentFormat = 1;
    public const string PreviewName = "preview.jpg";
    public const string Extension = ".p5avatar";

    /// <summary>Edge lengths of the avatar files, each stored and written as <c>avatar&lt;size&gt;.dds</c>.</summary>
    public static readonly int[] Sizes = [64, 128, 260, 440];

    /// <summary>Largest preview accepted, and its largest edge.</summary>
    public const int PreviewCap = 256 * 1024, PreviewMaxEdge = 512;

    /// <summary>Largest pack accepted: four DXT5 files are about 282 KB, the preview well under its cap.</summary>
    public const int ArchiveCap = 1024 * 1024;

    private const int DdsHeaderSize = 128;

    private readonly Dictionary<string, byte[]> _files;

    private AvatarPack(Dictionary<string, byte[]> files, string id, string name, string author, string version, string description,
        int format, string kind, string preview, IReadOnlyList<(int Size, string File, long Bytes, string Sha256)> images)
    {
        _files = files;
        Id = id;
        Name = name;
        Author = author;
        Version = version;
        Description = description;
        Format = format;
        Kind = kind;
        PreviewFile = preview;
        Images = images;
    }

    public string Id { get; }
    public string Name { get; }
    public string Author { get; }
    public string Version { get; }
    public string Description { get; }
    public int Format { get; }
    public string Kind { get; }
    public string PreviewFile { get; }

    /// <summary>The images avatar.json lists, as it lists them.</summary>
    public IReadOnlyList<(int Size, string File, long Bytes, string Sha256)> Images { get; }

    /// <summary>The file name of the avatar of one size, in the pack and on the console.</summary>
    public static string FileName(int size) => "avatar" + size + ".dds";

    /// <summary>Reads a pack. Throws <see cref="ThemePackException"/> when the bytes are not a readable pack.</summary>
    public static AvatarPack Open(ReadOnlySpan<byte> archive)
    {
        if (archive.Length == 0) throw new ThemePackException("avatar pack is empty");
        if (archive.Length > ArchiveCap) throw new ThemePackException($"avatar pack is {archive.Length} bytes, over {ArchiveCap}");
        Dictionary<string, byte[]> files = ThemePack.ReadFiles(archive, null);
        if (!files.TryGetValue(ManifestName, out byte[]? manifest)) throw new ThemePackException($"avatar pack has no {ManifestName}");

        JsonValue root;
        try { root = JsonValue.Parse(Encoding.UTF8.GetString(manifest)); }
        catch (Exception ex) { throw new ThemePackException($"{ManifestName} is not valid JSON: {ex.Message}", ex); }
        if (root.Type != JsonType.Object) throw new ThemePackException($"{ManifestName} must be a JSON object");

        var images = new List<(int, string, long, string)>();
        JsonValue list = root["images"];
        if (list.Type == JsonType.Array)
        {
            for (int i = 0; i < list.Count; i++)
            {
                JsonValue e = list[i];
                if (e.Type != JsonType.Object) continue;
                images.Add((e.GetInt("size"), e.GetString("file"), e.ContainsKey("bytes") ? e["bytes"].AsLong(-1) : -1, e.GetString("sha256").ToLowerInvariant()));
            }
        }
        return new AvatarPack(files, root.GetString("id"), root.GetString("name"), root.GetString("author"), root.GetString("version"),
            root.GetString("description"), root.GetInt("format"), root.GetString("kind"), root.GetString("preview"), images);
    }

    /// <summary>The bytes of the avatar of one size, or null when the pack has none.</summary>
    public byte[]? Image(int size) => _files.TryGetValue(FileName(size), out byte[]? data) ? data : null;

    /// <summary>The preview JPEG, or null.</summary>
    public byte[]? Preview() => _files.TryGetValue(PreviewName, out byte[]? data) ? data : null;

    /// <summary>Checks the pack end to end, as <c>validateAvatarPack</c> in web/shared does. Nothing is written.</summary>
    public PackReport Validate()
    {
        var report = new PackReport();
        if (Format != CurrentFormat) report.Error("format", $"unsupported format {Format}; this build understands {CurrentFormat}");
        if (Kind != "avatar") report.Error("kind", "kind must be \"avatar\"");
        if (string.IsNullOrWhiteSpace(Id)) report.Error("id", "id is required");
        if (string.IsNullOrWhiteSpace(Name)) report.Error("name", "name is required");
        if (string.IsNullOrWhiteSpace(Version)) report.Error("version", "version is required");

        foreach (int size in Sizes)
        {
            string label = FileName(size);
            int at = -1;
            for (int i = 0; i < Images.Count; i++) if (Images[i].Size == size) { at = i; break; }
            if (at < 0) { report.Error("images", $"{label}: not listed in images"); continue; }
            var entry = Images[at];
            if (entry.File != label) { report.Error("images", $"{label}: listed as '{entry.File}'"); continue; }
            byte[]? data = Image(size);
            if (data is null) { report.Error("missing", $"{label}: not in the archive"); continue; }
            if (entry.Bytes != data.LongLength) report.Error("bytes", $"{label}: avatar.json says {entry.Bytes} bytes, the file has {data.Length}");
            string hash = ThemePack.Hash(data);
            if (hash != entry.Sha256) report.Error("sha256", $"{label}: hash is {hash}, avatar.json says {entry.Sha256}");
            string? problem = DdsProblem(size, data);
            if (problem is not null) report.Error("format", $"{label} {problem}");
        }
        foreach (var image in Images)
            if (Array.IndexOf(Sizes, image.Size) < 0) report.Warn("images", $"images: size {image.Size} is not used");

        if (PreviewFile != PreviewName) report.Error("preview", $"preview must be '{PreviewName}'");
        else if (Preview() is not { } preview) report.Error("preview", $"{PreviewName}: not in the archive");
        else if (JpegSize(preview) is not { } edge) report.Error("preview", $"{PreviewName} is not a JPEG");
        else
        {
            if (preview.Length > PreviewCap) report.Error("preview", $"{PreviewName} is {preview.Length} bytes, over {PreviewCap}");
            if (edge.Width > PreviewMaxEdge || edge.Height > PreviewMaxEdge)
                report.Error("preview", $"{PreviewName} is {edge.Width}x{edge.Height}, over {PreviewMaxEdge}x{PreviewMaxEdge}");
        }
        return report;
    }

    /// <summary>
    /// Why an avatar file is not what the console reads, or null: a 128-byte DDS header (no DX10 part), FourCC DXT5, width and
    /// height <paramref name="size"/>, the linear size of the DXT5 data and exactly that much data after the header.
    /// </summary>
    public static string? DdsProblem(int size, ReadOnlySpan<byte> dds)
    {
        int blocks = (size + 3) / 4 * ((size + 3) / 4) * 16;
        if (dds.Length < DdsHeaderSize || !dds[..4].SequenceEqual("DDS "u8)) return "is not a DDS file";
        if (BinaryPrimitives.ReadUInt32LittleEndian(dds[4..]) != 124) return "has a DDS header of the wrong size";
        if (!dds.Slice(84, 4).SequenceEqual("DXT5"u8)) return "is not DXT5";
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(dds[12..]);
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(dds[16..]);
        if (width != size || height != size) return $"is {width}x{height}, not {size}x{size}";
        uint linear = BinaryPrimitives.ReadUInt32LittleEndian(dds[20..]);
        if (linear != blocks) return $"declares {linear} bytes of data, not {blocks}";
        if (dds.Length != DdsHeaderSize + blocks) return $"is {dds.Length} bytes, not {DdsHeaderSize + blocks}";
        return null;
    }

    // Width and height from a JPEG's first start-of-frame marker (as ImageHeader.JpegSize in the app), or null.
    private static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 4 <= bytes.Length)
        {
            if (bytes[pos] != 0xFF) return null;
            byte marker = bytes[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { pos += 2; continue; }
            if (marker is 0xD9 or 0xDA) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 2, 2));
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8 || pos + 9 > bytes.Length) return null;
                int height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 5, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 7, 2));
                return width > 0 && height > 0 ? (width, height) : null;
            }
            if (length < 2) return null;
            pos += 2 + length;
        }
        return null;
    }
}
