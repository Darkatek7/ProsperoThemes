using System;
using System.Collections.Generic;
using System.Linq;

namespace ProsperoThemes.Shared;

/// <summary>
/// The set of roots a theme pack is allowed to write under.
/// </summary>
/// <remarks>
/// A pack addresses files by a <c>&lt;root&gt;/&lt;relative&gt;</c> logical path, never by an absolute
/// console path. The root is resolved here against a fixed allowlist, which is what stops a malformed or
/// malicious pack from naming a path anywhere else on a system partition.
///
/// Adding a layer that lives somewhere new means adding a root here and nothing else; the engine treats
/// every target identically regardless of what the bytes are.
/// </remarks>
public static class AssetRoots
{
    /// <summary>Root for <c>/system_ex/vsh_asset</c>, where the shell's UI assets live.</summary>
    public const string VshAsset = "vsh_asset";

    /// <summary>
    /// Root for <c>/user/appmeta</c>, where each installed title keeps its icons and its entry background.
    /// </summary>
    /// <remarks>
    /// A target under here names the title as part of the path, e.g. <c>appmeta/PPSA01650/pic0.dds</c>. Unlike
    /// the system roots this one is *dynamic*: which titles exist depends on what is installed, so it is the
    /// console rather than the catalog that decides the target set.
    /// </remarks>
    public const string AppMeta = "appmeta";

    /// <summary>
    /// Root for <c>/mnt/rnps/apps</c>, the live copies of the shell's React Native apps. Each system tile on
    /// the home screen (Store, Game Library, Media Gallery, PS Plus…) takes its icon from
    /// <c>&lt;app&gt;/appdb/&lt;entry&gt;/icon0.png</c> here, e.g. <c>rnps/NPXS40047/appdb/default/icon0.png</c>
    /// for the Store — proven on FW 13.00, read at shell start. A console without the live copy of an app reads the
    /// shipped one instead, see <see cref="Shipped"/>.
    /// </summary>
    public const string Rnps = "rnps";

    /// <summary>
    /// <c>/system_ex/rnps/apps</c>, the React Native apps the firmware ships. On 13.00 app.db points the tiles at the
    /// live copies in <c>/mnt/rnps/apps</c>, and a file written here changes nothing. On 13.60 (issue 8) and 10.01
    /// <c>/mnt/rnps/apps</c> is empty and app.db points at this folder. Not a root a pack names: the applier writes
    /// an <c>rnps/</c> target here when the console has no live copy of that app.
    /// </summary>
    public const string ShippedRnpsMount = "/system_ex/rnps/apps";

    /// <summary>
    /// For a logical <c>rnps/&lt;app&gt;/…</c> path: the live copy's app folder (<c>/mnt/rnps/apps/&lt;app&gt;</c>)
    /// and the same file in <see cref="ShippedRnpsMount"/>. False for any other path or an unsafe one.
    /// </summary>
    public static bool Shipped(string logical, out string liveApp, out string shipped)
    {
        liveApp = shipped = string.Empty;
        if (!logical.StartsWith(Rnps + "/", StringComparison.Ordinal)) return false;
        string relative = logical[(Rnps.Length + 1)..];
        int slash = relative.IndexOf('/');
        if (slash <= 0 || !IsSafeRelative(relative, out _)) return false;
        liveApp = Mounts[Rnps] + "/" + relative[..slash];
        shipped = ShippedRnpsMount + "/" + relative;
        return true;
    }

    /// <summary>
    /// Root for <c>/user/app</c>, the installed titles' own folders. Only ever reached through
    /// <see cref="AllAppsBackground"/>: the applier expands it to <c>userapp/&lt;TID&gt;/sce_sys/pic0.dds</c> per title.
    /// </summary>
    public const string UserApp = "userapp";

    /// <summary>
    /// Virtual target "every title's own entry background". It names no single file: at apply time it is
    /// expanded to <c>userapp/&lt;TID&gt;/sce_sys/pic0.dds</c> (homebrew, ShadowMount) and
    /// <c>appmeta/&lt;TID&gt;/pic0.dds</c> (store apps) for every title that has one. Titles without a
    /// <c>pic0</c> show <c>vsh_asset/bg_hub_default.dds.zst</c>, which a pack targets separately.
    /// </summary>
    public const string AllAppsBackground = "allapps/pic0.dds";

    /// <summary>The home background of every title without its own image, and the theme's main background.</summary>
    public const string HubBackground = VshAsset + "/bg_hub_default.dds.zst";

    /// <summary>
    /// The system tiles' own home backgrounds (FW 13.00, klog <c>SetBackgroundTransition NextImageUri</c>):
    /// Media Gallery <c>bg_NPXS40075</c>, All Apps <c>bg_NPXS40056</c>, App Library <c>bg_NPXS40139</c>, Game Library
    /// <c>bg_NPXS40071</c>, Store <c>bg_NPXS40047</c> and the rest (docs/13.00-screen-assets.md). Same format as
    /// <see cref="HubBackground"/> (3840×2160 BC7 in zstd), so the applier gives each of them the theme's main
    /// background unless the pack names the file itself; a console whose file differs keeps its own.
    /// </summary>
    public static readonly IReadOnlyList<string> SystemTileBackgrounds = new[]
    {
        "NPXS40037", "NPXS40047", "NPXS40053", "NPXS40054", "NPXS40056", "NPXS40063", "NPXS40071",
        "NPXS40075", "NPXS40080", "NPXS40110", "NPXS40139", "NPXS40144", "NPXS40154",
    }.Select(id => $"{VshAsset}/bg_{id}.dds.zst").ToArray();

    /// <summary>
    /// The name a <c>vsh_asset/*.dds.zst</c> texture has on firmware before 13.00, which keeps the home backgrounds
    /// as plain DDS: <c>vsh_asset/bg_hub_default.dds</c> (firmware reports from 9.60, 11.60 and 12.40 have none of the
    /// <c>.dds.zst</c> files). Null for any other path. The applier writes the unpacked payload there.
    /// </summary>
    public static string? Uncompressed(string logical) =>
        logical.StartsWith(VshAsset + "/", StringComparison.Ordinal) && logical.EndsWith(".dds.zst", StringComparison.Ordinal)
            ? logical[..^4]
            : null;

    /// <summary>
    /// Virtual target "this one title's own entry background": <c>apps/&lt;TID&gt;/pic0.dds</c>. Resolved at apply
    /// time like <see cref="AllAppsBackground"/>, but for one title, always (it does not depend on the all-apps
    /// setting), and it wins over the all-apps background for that title. A title without its own
    /// <c>pic0</c> is reported, never given a new file.
    /// </summary>
    public static bool IsAppBackground(string path, out string titleId)
    {
        titleId = string.Empty;
        if (!path.StartsWith("apps/", StringComparison.Ordinal) || !path.EndsWith("/pic0.dds", StringComparison.Ordinal))
            return false;
        string tid = path.Substring(5, path.Length - 5 - "/pic0.dds".Length);
        if (!IsTitleId(tid))
            return false;
        titleId = tid;
        return true;
    }

    /// <summary>Four upper-case letters and five digits, e.g. <c>PPSA99169</c>.</summary>
    public static bool IsTitleId(string value)
    {
        if (value.Length != 9) return false;
        for (int i = 0; i < 4; i++) if (value[i] < 'A' || value[i] > 'Z') return false;
        for (int i = 4; i < 9; i++) if (value[i] < '0' || value[i] > '9') return false;
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is one of the virtual, applier-expanded targets.</summary>
    public static bool IsVirtual(string path) => path == AllAppsBackground || IsAppBackground(path, out _);

    private static readonly Dictionary<string, string> Mounts = new(StringComparer.Ordinal)
    {
        [VshAsset] = "/system_ex/vsh_asset",
        [AppMeta] = "/user/appmeta",
        [Rnps] = "/mnt/rnps/apps",
        [UserApp] = "/user/app",
    };

    /// <summary>The root names a pack may use.</summary>
    public static IReadOnlyCollection<string> Known => Mounts.Keys;

    /// <summary>The console directory a root maps to.</summary>
    public static string? MountFor(string root) => Mounts.TryGetValue(root, out string? mount) ? mount : null;

    /// <summary>
    /// Resolves a logical <c>&lt;root&gt;/&lt;relative&gt;</c> path to an absolute console path.
    /// </summary>
    /// <returns><see langword="false"/> with a reason when the path is unknown or unsafe.</returns>
    public static bool TryResolve(string path, out string absolutePath, out string reason)
    {
        absolutePath = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "path is empty";
            return false;
        }

        int slash = path.IndexOf('/');
        if (slash <= 0 || slash == path.Length - 1)
        {
            reason = "path must be <root>/<relative>";
            return false;
        }

        string root = path[..slash];
        string relative = path[(slash + 1)..];

        if (!Mounts.TryGetValue(root, out string? mount))
        {
            reason = $"unknown asset root '{root}' (known roots: {string.Join(", ", Mounts.Keys)})";
            return false;
        }

        if (!IsSafeRelative(relative, out reason))
            return false;

        absolutePath = mount + "/" + relative;
        return true;
    }

    /// <summary>
    /// The logical <c>&lt;root&gt;/&lt;relative&gt;</c> path of a console file, the inverse of <see cref="TryResolve"/>. A
    /// tile icon in <see cref="ShippedRnpsMount"/> is an <c>rnps/</c> path too. False for any other or unsafe path.
    /// </summary>
    public static bool TryLogical(string device, out string logical)
    {
        logical = string.Empty;
        foreach (var (root, mount) in Mounts)
            if (Under(device, mount, out string relative)) { logical = root + "/" + relative; return true; }
        if (Under(device, ShippedRnpsMount, out string shipped)) { logical = Rnps + "/" + shipped; return true; }
        return false;

        static bool Under(string device, string mount, out string relative)
        {
            relative = device.StartsWith(mount + "/", StringComparison.Ordinal) ? device[(mount.Length + 1)..] : string.Empty;
            return relative.Length > 0 && IsSafeRelative(relative, out _);
        }
    }

    /// <summary>
    /// Whether a relative path is a plain descendant: no absolute form, no drive or backslash, no NUL, no
    /// empty segment and no <c>.</c> or <c>..</c> segment.
    /// </summary>
    public static bool IsSafeRelative(string relative, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrEmpty(relative))
        {
            reason = "relative path is empty";
            return false;
        }

        if (relative.Length > 512)
        {
            reason = "relative path is longer than 512 characters";
            return false;
        }

        if (relative[0] == '/' || relative[0] == '\\')
        {
            reason = "relative path must not be absolute";
            return false;
        }

        if (relative.Contains('\\'))
        {
            reason = "backslash is not a path separator here";
            return false;
        }

        if (relative.Contains('\0'))
        {
            reason = "relative path contains a NUL";
            return false;
        }

        if (relative.EndsWith('/'))
        {
            reason = "path must name a file, not a directory";
            return false;
        }

        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0)
            {
                reason = "relative path contains an empty segment";
                return false;
            }

            if (segment == "." || segment == "..")
            {
                reason = $"'{segment}' is not allowed in a path";
                return false;
            }
        }

        return true;
    }
}
