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
    /// <summary>A release: its version ("01.000.022") and its page.</summary>
    public sealed record Release(string Version, string Url);

    public static string LatestUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/api/app/latest";

    /// <summary>The store's answer, or null when it is not one (no version, or not in the "01.000.022" form).</summary>
    public static Release? Parse(string? json)
    {
        if (json is null || !JsonValue.TryParse(json, out JsonValue root)) return null;
        string version = root.GetString("version");
        return Parts(version) is null ? null : new Release(version, root.GetString("url"));
    }

    /// <summary>Whether <paramref name="latest"/> is a later version than <paramref name="current"/>; false when either is not a version.</summary>
    public static bool IsNewer(string latest, string current)
    {
        if (Parts(latest) is not { } a || Parts(current) is not { } b) return false;
        for (int i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }

    /// <summary>The notice: which version is out and which one this is.</summary>
    public static string Text(string latest, string current) =>
        $"Prospero Themes {latest} is out (this is {current}). Update it where you installed it from.";

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
