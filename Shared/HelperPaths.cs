using System;

namespace ProsperoThemes.Shared;

/// <summary>
/// The paths ThemeHelper (v9) accepts besides its theme roots and the app's own folder, as one allocation-free rule set
/// over UTF-8 bytes. ThemeHelper, the app and the PC tests compile this same file, so the app knows exactly which
/// install folders the helper will touch, and the helper's rules are tested without a console.
/// </summary>
/// <remarks>
/// Payload code: no LINQ, no formatting, no collections, only byte loops and constant spans.
/// <list type="bullet">
/// <item><b>The app's install folder.</b> <c>&lt;root&gt;/PPSA90210</c> where the root is one of ShadowMount's scan roots
/// (<c>/data/homebrew</c>, <c>/data/etaHEN/games</c>, <c>/mnt/ext0..1</c> and <c>/mnt/usb0..7</c>, each bare or followed by
/// <c>/homebrew</c> or <c>/etaHEN/games</c>). The match is exact, never a prefix match on arbitrary paths.</item>
/// <item><b>Avatar pictures.</b> A fixed set of file names inside <c>/system_data/priv/cache/profile/0x&lt;8 hex&gt;/</c>.</item>
/// </list>
/// A path is clean when it is absolute, at most <see cref="MaxPath"/> - 1 bytes, has only letters, digits and <c>. _ -</c>
/// inside its names, and contains no empty or dot segment, no <c>..</c>, no backslash, no space and no trailing slash.
/// </remarks>
public static class HelperPaths
{
    /// <summary>The helper's path buffer: a path must be shorter than this.</summary>
    public const int MaxPath = 256;

    /// <summary>The most the helper writes to one avatar file.</summary>
    public const int AvatarMaxBytes = 1024 * 1024;

    /// <summary>
    /// The mode (0777) of every file the helper writes into the install folder. The console refuses to start an app whose
    /// eboot.bin or modules are not executable ("Can't start the game or app", errno 13 after a 0644 write by another
    /// app's updater), and the helper creates files with mode 0666 less the umask, so it sets this after each write.
    /// </summary>
    public const ushort InstallFileMode = 0x1FF;

    /// <summary>The title the install folder belongs to.</summary>
    public const string InstallTitleId = "PPSA90210";

    // ---- the app's install folder -----------------------------------------------------------------------------------

    /// <summary>True for exactly <c>&lt;scan root&gt;/PPSA90210</c>.</summary>
    public static bool IsInstallFolder(ReadOnlySpan<byte> path) => IsClean(path) && InstallFolderLength(path) == path.Length;

    /// <summary>True for a file or folder strictly inside the install folder.</summary>
    public static bool IsInstallPath(ReadOnlySpan<byte> path)
    {
        if (!IsClean(path)) return false;
        int folder = InstallFolderLength(path);
        return folder > 0 && path.Length > folder + 1 && path[folder] == (byte)'/';
    }

    /// <summary>
    /// The mode the helper sets on a file it has written at <paramref name="path"/>: <see cref="InstallFileMode"/> inside the
    /// install folder, 0 (leave the mode as it was created) everywhere else.
    /// </summary>
    public static ushort ModeAfterWrite(ReadOnlySpan<byte> path) => IsInstallPath(path) ? InstallFileMode : (ushort)0;

    public static bool IsInstallFolder(string path) => Encode(path, stackalloc byte[MaxPath], out ReadOnlySpan<byte> bytes) && IsInstallFolder(bytes);

    public static bool IsInstallPath(string path) => Encode(path, stackalloc byte[MaxPath], out ReadOnlySpan<byte> bytes) && IsInstallPath(bytes);

    /// <summary>
    /// The install folder that <paramref name="path"/> is or lies in, or null when it is none. A path that is not clean is
    /// none either.
    /// </summary>
    public static string? InstallFolderOf(string path)
    {
        if (!Encode(path, stackalloc byte[MaxPath], out ReadOnlySpan<byte> bytes) || !IsClean(bytes)) return null;
        int folder = InstallFolderLength(bytes);
        if (folder <= 0 || (folder < bytes.Length && bytes[folder] != (byte)'/')) return null;
        return path[..folder];
    }

    /// <summary>
    /// Whether <paramref name="relative"/> (a name inside the install folder, no leading slash) is something the helper
    /// will write: one or more clean segments.
    /// </summary>
    public static bool IsInstallRelative(string relative)
    {
        if (relative.Length == 0 || relative.Length > MaxPath - 64) return false;
        // Checked as the path it would become, so the same rules decide.
        return Encode("/" + relative, stackalloc byte[MaxPath], out ReadOnlySpan<byte> bytes) && IsClean(bytes);
    }

    /// <summary>The length of "&lt;root&gt;/PPSA90210" at the start of <paramref name="p"/> (followed by the end or a slash), or -1.</summary>
    private static int InstallFolderLength(ReadOnlySpan<byte> p)
    {
        int at;
        if (StartsWith(p, "/data/homebrew"u8)) at = TitleAt(p, 14);
        else if (StartsWith(p, "/data/etaHEN/games"u8)) at = TitleAt(p, 18);
        else if (StartsWith(p, "/mnt/ext"u8) && p.Length > 8 && p[8] is (byte)'0' or (byte)'1') at = MountTitle(p, 9);
        else if (StartsWith(p, "/mnt/usb"u8) && p.Length > 8 && p[8] >= (byte)'0' && p[8] <= (byte)'7') at = MountTitle(p, 9);
        else return -1;
        return at;
    }

    // After /mnt/extN or /mnt/usbN: the title directly, or below /homebrew or /etaHEN/games.
    private static int MountTitle(ReadOnlySpan<byte> p, int root)
    {
        int direct = TitleAt(p, root);
        if (direct > 0) return direct;
        if (StartsWith(p[root..], "/homebrew"u8)) return TitleAt(p, root + 9);
        if (StartsWith(p[root..], "/etaHEN/games"u8)) return TitleAt(p, root + 13);
        return -1;
    }

    // "/PPSA90210" at offset, followed by the end or a slash: the offset after it, else -1.
    private static int TitleAt(ReadOnlySpan<byte> p, int offset)
    {
        if (p.Length < offset) return -1;
        ReadOnlySpan<byte> rest = p[offset..];
        if (!StartsWith(rest, "/PPSA90210"u8)) return -1;
        int end = offset + 10;
        return end == p.Length || p[end] == (byte)'/' ? end : -1;
    }

    // ---- avatar pictures --------------------------------------------------------------------------------------------

    private static ReadOnlySpan<byte> ProfileRoot => "/system_data/priv/cache/profile"u8;

    /// <summary>True for the folder of all profiles, with or without a trailing slash. Only its sub-folder names may be listed.</summary>
    public static bool IsProfileRoot(ReadOnlySpan<byte> path)
    {
        if (path.Length > 0 && path[^1] == (byte)'/') path = path[..^1];
        return path.SequenceEqual(ProfileRoot);
    }

    /// <summary>True for <c>/system_data/priv/cache/profile/0x&lt;8 hex digits&gt;</c>, either case, and nothing after it.</summary>
    public static bool IsAvatarFolder(ReadOnlySpan<byte> path) => AvatarFolderLength(path) == path.Length;

    /// <summary>
    /// True for one of the avatar files (or its .tmp) in such a folder. version.json and every other name are refused.
    /// </summary>
    public static bool IsAvatarPath(ReadOnlySpan<byte> path)
    {
        int folder = AvatarFolderLength(path);
        if (folder <= 0 || path.Length <= folder + 1 || path[folder] != (byte)'/') return false;
        ReadOnlySpan<byte> name = path[(folder + 1)..];
        if (name.Length > 4 && name[^4..].SequenceEqual(".tmp"u8)) name = name[..^4];
        return name.SequenceEqual("avatar64.dds"u8) || name.SequenceEqual("avatar128.dds"u8) || name.SequenceEqual("avatar260.dds"u8)
            || name.SequenceEqual("avatar440.dds"u8) || name.SequenceEqual("picture64.dds"u8) || name.SequenceEqual("picture128.dds"u8)
            || name.SequenceEqual("picture260.dds"u8) || name.SequenceEqual("picture440.dds"u8) || name.SequenceEqual("avatar.png"u8)
            || name.SequenceEqual("picture.png"u8) || name.SequenceEqual("online.json"u8);
    }

    public static bool IsAvatarFolder(string path) => Encode(path, stackalloc byte[MaxPath], out ReadOnlySpan<byte> bytes) && IsAvatarFolder(bytes);

    public static bool IsAvatarPath(string path) => Encode(path, stackalloc byte[MaxPath], out ReadOnlySpan<byte> bytes) && IsAvatarPath(bytes);

    // The length of ".../profile/0xXXXXXXXX" when the path starts with it (and goes on with a slash or ends), else -1.
    private static int AvatarFolderLength(ReadOnlySpan<byte> p)
    {
        int prefix = ProfileRoot.Length;
        if (p.Length < prefix + 11 || p.Length >= MaxPath || !StartsWith(p, ProfileRoot)) return -1;
        if (p[prefix] != (byte)'/' || p[prefix + 1] != (byte)'0' || p[prefix + 2] != (byte)'x') return -1;
        for (int i = prefix + 3; i < prefix + 11; i++)
            if (!IsHex(p[i])) return -1;
        int end = prefix + 11;
        if (end == p.Length) return end;
        // What follows is a name: letters, digits and . _ - only, with no second slash.
        if (p[end] != (byte)'/') return -1;
        for (int i = end + 1; i < p.Length; i++)
            if (!IsNameByte(p[i])) return -1;
        return end;
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    // ---- shared ----------------------------------------------------------------------------------------------------

    private static bool IsNameByte(byte b) =>
        b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
            or (byte)'.' or (byte)'_' or (byte)'-';

    /// <summary>
    /// Absolute, shorter than <see cref="MaxPath"/>, no trailing slash, and every segment is non-empty, is neither "." nor
    /// "..", holds no ".." and only letters, digits and . _ - .
    /// </summary>
    private static bool IsClean(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2 || p.Length >= MaxPath || p[0] != (byte)'/' || p[^1] == (byte)'/') return false;
        int segment = 0;
        for (int i = 1; i < p.Length; i++)
        {
            byte b = p[i];
            if (b == (byte)'/')
            {
                if (segment == 0) return false;       // "//"
                segment = 0;
                continue;
            }
            if (!IsNameByte(b)) return false;         // backslash, space, control, non-ASCII, anything else
            if (b == (byte)'.' && i + 1 < p.Length && p[i + 1] == (byte)'.') return false;
            segment++;
            if (segment == 1 && b == (byte)'.' && (i + 1 == p.Length || p[i + 1] == (byte)'/')) return false;   // "."
        }
        return segment > 0;
    }

    private static bool StartsWith(ReadOnlySpan<byte> text, ReadOnlySpan<byte> prefix) => text.Length >= prefix.Length && text[..prefix.Length].SequenceEqual(prefix);

    // ASCII only; anything else cannot be a path the helper accepts.
    private static bool Encode(string path, Span<byte> buffer, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (path.Length >= buffer.Length) return false;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] > 126) return false;
            buffer[i] = (byte)path[i];
        }
        bytes = buffer[..path.Length];
        return true;
    }
}
