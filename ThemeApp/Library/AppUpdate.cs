using System;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The update notice. After the first scan the app asks the store for the newest release (<c>GET /api/app/latest</c>;
/// the store asks GitHub and keeps the answer for an hour) and compares its version with its own <c>contentVersion</c>.
/// Plain functions, so the PC tests cover them.
/// </summary>
public static class AppUpdate
{
    /// <summary>A release: its version ("01.000.022"), its page and, when the store has the release ZIP, that file.</summary>
    public sealed record Release(string Version, string Url, ZipInfo? Zip = null);

    /// <summary>The release ZIP the store serves: its address (a store path), size and lower-case hex SHA-256.</summary>
    public sealed record ZipInfo(string Url, long Bytes, string Sha256);

    /// <summary>
    /// What the System tab's App update row knows. None: nothing newer is known (not asked, asked and up to date, or the store
    /// could not be reached). Notice: a later release exists but cannot be installed from here (no ZIP). Ready: the ZIP is
    /// known, X asks to install it. Installed: this session already replaced the app's files, so it has to be restarted.
    /// </summary>
    public enum State { None, Notice, Ready, Installed }

    /// <summary>The release ZIP is never larger than this (the store's limit, and the client's response cap).</summary>
    public const long MaxZipBytes = 64L * 1024 * 1024;

    /// <summary>The path every ZIP address starts with; the store serves the release ZIP only there.</summary>
    public const string ZipPathPrefix = "/api/app/download/";

    public static string LatestUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/api/app/latest";

    /// <summary>The store's answer, or null when it is not one (no version, or not in the "01.000.022" form).</summary>
    public static Release? Parse(string? json)
    {
        if (json is null || !JsonValue.TryParse(json, out JsonValue root)) return null;
        string version = root.GetString("version");
        return Parts(version) is null ? null : new Release(version, root.GetString("url"), ParseZip(root["zip"]));
    }

    // {url, bytes, sha256}; anything short of that is no ZIP, and the release is then only a notice.
    private static ZipInfo? ParseZip(JsonValue zip)
    {
        string url = zip.GetString("url"), sha = zip.GetString("sha256").ToLowerInvariant();
        long bytes = zip.GetNumber("bytes") is var n && n > 0 && n <= MaxZipBytes && n == Math.Floor(n) ? (long)n : 0;
        if (bytes == 0 || sha.Length != 64 || !IsHex(sha) || !url.StartsWith(ZipPathPrefix, StringComparison.Ordinal)) return null;
        // A store path only: nothing that could lead somewhere else than the store.
        if (url.Length == ZipPathPrefix.Length || url.Contains("..") || url.Contains('\\') || url.Contains("//") || url.Contains(' ') || url.Contains('?') || url.Contains('#'))
            return null;
        return new ZipInfo(url, bytes, sha);
    }

    private static bool IsHex(string text)
    {
        foreach (char c in text)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>The full address of the ZIP: the store's address and the path the store announced.</summary>
    public static string ZipUrl(string baseUrl, ZipInfo zip) => baseUrl.TrimEnd('/') + zip.Url;

    /// <summary>The state of the System row for the release the store announced (null when it announced none or none newer).</summary>
    public static State StateOf(Release? latest, string current, bool installed) =>
        installed ? State.Installed
        : latest is null || !IsNewer(latest.Version, current) ? State.None
        : latest.Zip is null ? State.Notice : State.Ready;

    /// <summary>The question's text: what is installed over what, and what happens next.</summary>
    public static string DialogText(string latest, string current, long bytes) =>
        $"Prospero Themes {latest} replaces this app’s files ({FormatSize(bytes)} download; this is {current}). " +
        "Your themes and settings stay. When it is done, close the app and start it again.";

    /// <summary>The System row's description for a state.</summary>
    public static string RowText(State state, string latest, string current) => state switch
    {
        State.Ready => $"Install {latest} over this app ({current})",
        State.Notice => $"{latest} is out. Update it where you installed it from",
        State.Installed => "Installed. Close the app and start it again",
        _ => "Check for a newer version of this app",
    };

    /// <summary>"8.2 MB" (binary megabytes, one decimal), "340 KB" under a megabyte.</summary>
    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, bytes / 1024) + " KB";

    /// <summary>Whether <paramref name="latest"/> is a later version than <paramref name="current"/>; false when either is not a version.</summary>
    public static bool IsNewer(string latest, string current)
    {
        if (Parts(latest) is not { } a || Parts(current) is not { } b) return false;
        for (int i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }

    /// <summary>The notice: which version is out and which one this is.</summary>
    public static string Text(string latest, string current, bool canUpdate = false) =>
        canUpdate
            ? $"Prospero Themes {latest} is out (this is {current}). System, App update installs it."
            : $"Prospero Themes {latest} is out (this is {current}). Update it where you installed it from.";

    // "01.000.022" -> [1, 0, 22]; null for anything else.
    private static int[]? Parts(string version)
    {
        string[] parts = version.Split('.');
        if (parts.Length != 3 || parts[0].Length != 2 || parts[1].Length != 3 || parts[2].Length != 3) return null;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            foreach (char c in parts[i])
                if (c is < '0' or > '9') return null;
            numbers[i] = int.Parse(parts[i]);
        }
        return numbers;
    }
}
