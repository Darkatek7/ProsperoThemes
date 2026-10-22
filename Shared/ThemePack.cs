using System;
using System.Collections.Generic;
using SharpProspero.Security;
using System.Text;
using SharpProspero.Compression;
using SharpProspero.Storage;

namespace ProsperoThemes.Shared;

/// <summary>
/// A theme pack held in memory: its manifest and every payload, read from a zip or tar archive.
/// </summary>
/// <remarks>
/// The archive is sniffed by magic bytes rather than trusted by extension, because packs arrive as
/// <c>.zip</c> (what users actually make), <c>.tar</c> or the branded <c>.p5theme</c>. Both containers are
/// already handled by the SDK, so nothing here parses one by hand.
///
/// Nothing in this type touches the console. Reading and validating a pack is pure computation, which is
/// what makes the whole format testable on a PC and lets the applier refuse a pack before it writes
/// anything.
/// </remarks>
public sealed class ThemePack
{
    /// <summary>Manifest file name inside the archive.</summary>
    public const string ManifestName = "theme.json";

    private readonly Dictionary<string, byte[]> _files;

    private ThemePack(ThemePackManifest manifest, Dictionary<string, byte[]> files)
    {
        Manifest = manifest;
        _files = files;
    }

    /// <summary>The parsed manifest.</summary>
    public ThemePackManifest Manifest { get; }

    /// <summary>Every file in the archive, keyed by its normalised path.</summary>
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    /// <summary>Reads a pack from archive bytes.</summary>
    /// <exception cref="ThemePackException">The bytes are not a readable pack.</exception>
    public static ThemePack Open(ReadOnlySpan<byte> archive)
    {
        if (archive.Length == 0)
            throw new ThemePackException("pack archive is empty");

        Dictionary<string, byte[]> files = ReadFiles(archive);

        if (!files.TryGetValue(ManifestName, out byte[]? manifestBytes))
            throw new ThemePackException($"pack archive has no {ManifestName}");

        ThemePackManifest manifest = ThemePackManifest.Parse(Encoding.UTF8.GetString(manifestBytes));
        return new ThemePack(manifest, files);
    }

    /// <summary>Opens a pack from a file on disk.</summary>
    public static ThemePack OpenFile(string path) => Open(System.IO.File.ReadAllBytes(path));

    /// <summary>Whether the archive carries a given payload path.</summary>
    public bool HasPayload(string file) => _files.ContainsKey(NormaliseName(file));

    /// <summary>The payload bytes for a target.</summary>
    /// <exception cref="ThemePackException">The payload is missing.</exception>
    /// <summary>Layer whose targets are named SVG icons patched into a container, not whole files.</summary>
    public const string SvgLayer = "svg";

    /// <summary>Layer whose targets are named UI sounds (mono VAG) patched into a container, not whole files.</summary>
    public const string VagLayer = "vag";

    /// <summary>Whether a layer's targets are named resources patched into a container (several may share a path).</summary>
    public static bool IsPatchLayer(string layer) => layer is SvgLayer or VagLayer;

    public byte[] Payload(ThemePackTarget target)
    {
        if (!_files.TryGetValue(NormaliseName(target.File), out byte[]? bytes))
            throw new ThemePackException($"payload '{target.File}' is not in the archive");
        return bytes;
    }

    /// <summary>The payload bytes for an archive path, or null.</summary>
    public byte[]? Find(string file) => _files.TryGetValue(NormaliseName(file), out byte[]? bytes) ? bytes : null;

    /// <summary>Lower-case hex SHA-256 of some bytes, matching what a manifest records.</summary>
    public static string Hash(ReadOnlySpan<byte> data) =>
        Sha256.HashHex(data);

    /// <summary>
    /// Checks a pack end to end without writing anything. Safe to call before an apply, and the applier
    /// always should.
    /// </summary>
    /// <param name="sizeCap">Largest payload accepted, in bytes.</param>
    public PackReport Validate(long sizeCap = ThemePackManifest.DefaultSizeCap)
    {
        var report = new PackReport();
        ThemePackManifest m = Manifest;

        if (m.Format != ThemePackManifest.CurrentFormat)
            report.Error("format", $"unsupported format {m.Format}; this build understands {ThemePackManifest.CurrentFormat}");

        if (string.IsNullOrWhiteSpace(m.Id))
            report.Error("id", "id is required");
        if (string.IsNullOrWhiteSpace(m.Name))
            report.Error("name", "name is required");
        if (string.IsNullOrWhiteSpace(m.Version))
            report.Error("version", "version is required");
        if (m.Unmapped is not ("keep" or "stock"))
            report.Error("unmapped", $"unmapped must be 'keep' or 'stock', not '{m.Unmapped}'");

        if (m.Targets.Count == 0)
            report.Error("targets", "at least one target is required");

        if (m.Preview is not null && m.Preview.Length > 0 && !HasPayload(m.Preview))
            report.Warn("preview", $"preview '{m.Preview}' is not in the archive");

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ThemePackTarget t in m.Targets)
        {
            string label = string.IsNullOrEmpty(t.Path) ? "(no path)" : t.Path;

            if (!AssetRoots.IsVirtual(t.Path) && !AssetRoots.TryResolve(t.Path, out _, out string reason))
                report.Error("path", $"{label}: {reason}");

            // An svg or vag target edits one named icon or sound inside a container, so several may share a path.
            bool isPatch = IsPatchLayer(t.Layer);
            if (isPatch && string.IsNullOrWhiteSpace(t.Resource))
                report.Error("resource", $"{label}: an {t.Layer} target needs the {(t.Layer == SvgLayer ? "icon" : "sound")} name in 'resource'");
            if (!seen.Add(isPatch ? t.Path + "#" + t.Resource : t.Path))
                report.Error("duplicate", $"{label}{(isPatch ? "#" + t.Resource : "")}: target appears more than once");

            if (!ThemePackManifest.KnownLayers.Contains(t.Layer))
                report.Warn("layer", $"{label}: unknown layer '{t.Layer}' -- the applier will skip this entry");

            if (string.IsNullOrWhiteSpace(t.File))
            {
                report.Error("file", $"{label}: file is required");
                continue;
            }

            if (!HasPayload(t.File))
            {
                report.Error("missing", $"{label}: payload '{t.File}' is not in the archive");
                continue;
            }

            byte[] payload = Payload(t);

            if (payload.LongLength > sizeCap)
                report.Error("size", $"{label}: payload is {payload.LongLength} bytes, over the {sizeCap} cap");

            if (t.Bytes < 0)
                report.Error("bytes", $"{label}: manifest does not declare bytes");
            else if (payload.LongLength != t.Bytes)
                report.Error("bytes", $"{label}: manifest declares {t.Bytes} bytes but the payload is {payload.LongLength}");

            if (t.Sha256.Length != 64 || !IsHex(t.Sha256))
            {
                report.Error("sha256", $"{label}: sha256 must be 64 hex characters");
            }
            else
            {
                string actual = Hash(payload);
                if (!string.Equals(actual, t.Sha256, StringComparison.Ordinal))
                    report.Error("sha256", $"{label}: payload hash is {actual}, manifest says {t.Sha256}");
            }

            // Last, because it is the least specific check: the bytes are present, the declared length and
            // the declared hash all have to be right before it matters whether they are the right *kind* of
            // bytes. An unknown suffix is not an error - see PayloadFormat.
            if (t.Layer == VagLayer)
            {
                try { RcoVag.Check(payload); }
                catch (ThemePackException ex) { report.Error("format", $"{label}#{t.Resource}: {ex.Message}"); }
            }
            else if (t.Layer == SvgLayer)
            {
                string head = System.Text.Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 64)).TrimStart();
                if (!head.StartsWith("<svg", StringComparison.Ordinal))
                    report.Error("format", $"{label}#{t.Resource}: payload is not an SVG document");
            }
            else if (!PayloadFormat.Check(t.Path, payload, out string formatReason))
                report.Error("format", formatReason);
        }

        return report;
    }

    /// <summary>The targets an applier should act on: known layer, resolvable path, payload present.</summary>
    public IEnumerable<ThemePackTarget> ApplicableTargets()
    {
        foreach (ThemePackTarget t in Manifest.Targets)
        {
            if (!ThemePackManifest.KnownLayers.Contains(t.Layer))
                continue;
            if (!AssetRoots.IsVirtual(t.Path) && !AssetRoots.TryResolve(t.Path, out _, out _))
                continue;
            if (!HasPayload(t.File))
                continue;
            yield return t;
        }
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex)
                return false;
        }

        return true;
    }

    private static Dictionary<string, byte[]> ReadFiles(ReadOnlySpan<byte> archive)
    {
        // zip: "PK\x03\x04"
        if (archive.Length >= 4 && archive[0] == 0x50 && archive[1] == 0x4B && archive[2] == 0x03 && archive[3] == 0x04)
            return ReadZip(archive.ToArray());

        // tar: "ustar" at offset 257
        if (archive.Length > 262 &&
            archive[257] == (byte)'u' && archive[258] == (byte)'s' && archive[259] == (byte)'t' &&
            archive[260] == (byte)'a' && archive[261] == (byte)'r')
        {
            return ReadTar(archive.ToArray());
        }

        throw new ThemePackException(
            "archive is neither zip nor tar (expected PK\\x03\\x04 or ustar magic)");
    }

    private static Dictionary<string, byte[]> ReadZip(byte[] archive)
    {
        ZipArchive zip;
        try
        {
            zip = ZipArchive.Open(archive);
        }
        catch (Exception ex)
        {
            throw new ThemePackException($"could not read zip archive: {ex.Message}", ex);
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (ZipEntry entry in zip.Entries)
        {
            if (entry.IsDirectory)
                continue;

            string name = NormaliseName(entry.Name);
            try
            {
                files[name] = zip.Extract(entry);
            }
            catch (Exception ex)
            {
                throw new ThemePackException($"could not extract '{name}': {ex.Message}", ex);
            }
        }

        return files;
    }

    private static Dictionary<string, byte[]> ReadTar(byte[] archive)
    {
        List<TarEntry> entries;
        try
        {
            entries = TarArchive.Read(archive);
        }
        catch (Exception ex)
        {
            throw new ThemePackException($"could not read tar archive: {ex.Message}", ex);
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (TarEntry entry in entries)
        {
            if (entry.IsDirectory)
                continue;
            files[NormaliseName(entry.Name)] = entry.Data;
        }

        return files;
    }

    /// <summary>Archive paths are compared with one separator and no leading <c>./</c>.</summary>
    private static string NormaliseName(string name)
    {
        string n = name.Replace('\\', '/');
        while (n.StartsWith("./", StringComparison.Ordinal))
            n = n[2..];
        return n;
    }
}
