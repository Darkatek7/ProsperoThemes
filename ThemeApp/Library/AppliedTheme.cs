using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// Which theme the console currently shows, read from the applier's state file
/// (<see cref="Path"/>, written by <see cref="ThemeApplier"/> after every apply and restore).
/// </summary>
public static class AppliedTheme
{
    /// <summary>The applier's state file; the app only reads it.</summary>
    public const string Path = "/data/prospero-themes/state.json";

    /// <summary>
    /// The manifest id of the applied pack, or null when the last action was a restore (of a pack or of all
    /// originals) or the file is missing, empty or not a state file. Never throws.
    /// </summary>
    /// <remarks>Uses the SDK's parser: System.Text.Json's JsonDocument pulls OpenSSL imports into the module.</remarks>
    public static string? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Object)
            return null;
        if (root.GetString("action") != "apply") return null;
        string id = root["pack"].GetString("id");
        return id.Length > 0 ? id : null;
    }
}
