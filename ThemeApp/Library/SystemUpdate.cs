using System;
using System.Collections.Generic;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// What to apply again after a system update. An update puts the console's own files back, so the theme is gone
/// although state.json still names it; state.json also records the system software the theme was applied on. When
/// that differs from the console's now, the app asks once whether to apply the look again (the layers of
/// <see cref="Look"/>, or the pack state.json names when there is no look file). Plain functions, so the PC tests
/// cover them.
/// </summary>
public static class SystemUpdate
{
    /// <summary>The question's answer: the layers to apply, bottom first, and the names of layers whose pack is gone.</summary>
    public sealed record Plan(IReadOnlyList<LookLayer> Layers, IReadOnlyList<string> Missing, string From);

    /// <summary>
    /// The plan when the app should ask, otherwise null: a theme is applied (<paramref name="stateJson"/>), it was
    /// applied on another system software than <paramref name="firmware"/>, the user did not say "Not now" for this
    /// one (<paramref name="skipped"/>), and some of it is still in <paramref name="library"/>. Without a look file the
    /// applied pack counts with all its parts, without sounds when <paramref name="sounds"/> is off.
    /// </summary>
    public static Plan? Check(string? stateJson, string? lookJson, string? firmware, string? skipped,
        IReadOnlyList<ThemeLibraryEntry> library, bool sounds)
    {
        if (string.IsNullOrEmpty(firmware) || firmware == skipped) return null;
        if (AppliedTheme.Read(stateJson) is not { } appliedId) return null;
        if (!JsonValue.TryParse(stateJson!, out JsonValue state)) return null;
        string from = state.GetString("firmware");
        if (from.Length == 0 || from == firmware) return null;

        var layers = new List<LookLayer>();
        var missing = new List<string>();
        if (Look.Parse(lookJson) is { Count: > 0 } look)
        {
            foreach (LookLayer layer in look)
            {
                if (FindByPath(library, layer.Pack) is not null) layers.Add(layer);
                else missing.Add(layer.Name);
            }
        }
        else if (FindById(library, appliedId) is { } entry)
        {
            ThemePart parts = ThemeParts.In(entry.Manifest) & (sounds ? ThemePart.All : ~ThemePart.Sounds);
            if (parts != ThemePart.None) layers.Add(new LookLayer(entry.Path, entry.Manifest.Id, entry.Manifest.Name, parts));
        }
        return layers.Count == 0 ? null : new Plan(layers, missing, from);
    }

    private static ThemeLibraryEntry? FindByPath(IReadOnlyList<ThemeLibraryEntry> library, string path)
    {
        foreach (ThemeLibraryEntry entry in library)
            if (entry.Path == path) return entry;
        return null;
    }

    private static ThemeLibraryEntry? FindById(IReadOnlyList<ThemeLibraryEntry> library, string id)
    {
        foreach (ThemeLibraryEntry entry in library)
            if (entry.Manifest.Id == id) return entry;
        return null;
    }

    /// <summary>"12.40" for "12.400.000": the version as the System tab shows it.</summary>
    public static string Short(string firmware)
    {
        string[] parts = firmware.Split('.');
        return parts.Length >= 2 && parts[1].Length >= 2 ? parts[0] + "." + parts[1][..2] : firmware;
    }

    /// <summary>The question's text: what changed and which themes it applies again.</summary>
    public static string Text(Plan plan, string firmware)
    {
        string names = plan.Layers.Count == 1 ? "“" + plan.Layers[0].Name + "”" : plan.Layers.Count + " themes of your look";
        string gone = plan.Missing.Count == 0 ? "" : $" {plan.Missing.Count} theme(s) of it are no longer in your library and are left out.";
        return $"The system software changed from {Short(plan.From)} to {Short(firmware)}, which puts the console’s own files back. " +
               $"Apply {names} again?{gone}";
    }
}
