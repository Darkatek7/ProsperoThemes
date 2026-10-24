using System;

namespace ProsperoThemes.Shared;

/// <summary>Which HEN (the console's exploit host) ThemeHelper found running; see <see cref="RunningHen"/>.</summary>
public enum HenKind
{
    /// <summary>The helper could not be asked, or it answered something this app does not know.</summary>
    Unknown,
    /// <summary>No HEN is running.</summary>
    None,
    /// <summary>OnionHEN is running.</summary>
    OnionHen,
    /// <summary>etaHEN is running (the helper does not report this yet; reserved).</summary>
    EtaHen,
}

/// <summary>
/// The answer of ThemeHelper's HEN command and what it means for restarting the home screen. Measured on 13.00 (2026-10-25):
/// OnionHEN puts its Toolbox into the new SceShellUI about 14 s after a restart, and in 1 of 3 tries that failed and the new
/// SceShellUI crashed (CE-108262-9), so under OnionHEN the app never restarts the home screen.
/// </summary>
public static class RunningHen
{
    /// <summary>The command the app sends.</summary>
    public const string Command = "HEN";

    /// <summary>The reply start; the word follows ("OK HEN onionhen").</summary>
    public const string ReplyPrefix = "OK HEN ";

    /// <summary>Reads the helper's reply. Anything that is not exactly one of the known answers is <see cref="HenKind.Unknown"/>.</summary>
    public static HenKind Parse(string? reply)
    {
        if (reply is null || !reply.StartsWith(ReplyPrefix, StringComparison.Ordinal)) return HenKind.Unknown;
        return reply.AsSpan(ReplyPrefix.Length).Trim() switch
        {
            "onionhen" => HenKind.OnionHen,
            "etahen" => HenKind.EtaHen,
            "none" => HenKind.None,
            _ => HenKind.Unknown,
        };
    }

    /// <summary>
    /// Whether the app may restart the home screen. Only a helper that says OnionHEN runs forbids it: an unreachable helper or an
    /// unknown answer keeps the restart as it was.
    /// </summary>
    public static bool AllowsShellRestart(HenKind hen) => hen != HenKind.OnionHen;

    /// <summary>The refusal for a restart under OnionHEN; null when the restart is allowed.</summary>
    public static string? ShellRestartRefusal(HenKind hen) => AllowsShellRestart(hen)
        ? null
        : "OnionHEN is running, so the home screen is not restarted from the app. The new icons and sounds show after the next restart of the console.";
}
