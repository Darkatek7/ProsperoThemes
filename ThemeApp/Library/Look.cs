using System;
using System.Collections.Generic;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>One theme of the current look: the library pack it came from and the parts of it the console shows.</summary>
/// <param name="Pack">The pack's path in the library when the app applied it; empty when not known.</param>
public sealed record LookLayer(string Pack, string Id, string Name, ThemePart Parts);

/// <summary>
/// What the console shows now, as the themes applied on top of each other (<see cref="FileName"/> under the data root,
/// written by <see cref="ThemeApplier"/>). An apply that replaces earlier themes sets one layer; one that combines adds a
/// layer on top (the same pack moves up instead of showing twice). A full restore empties the look; a restore of some
/// parts takes them out of every layer. Later layers win where two write the same file. Re-applying after a system
/// update and saving the look as a theme read it. Plain functions, so the PC tests cover them.
/// </summary>
public static class Look
{
    public const string FileName = "look.json";

    private static readonly (ThemePart Part, string Name)[] Names =
    [
        (ThemePart.Background, "background"), (ThemePart.AppBackgrounds, "app_backgrounds"), (ThemePart.Icons, "icons"),
        (ThemePart.Tiles, "tiles"), (ThemePart.Sounds, "sounds"), (ThemePart.Other, "other"),
    ];

    /// <summary>The layers in <paramref name="json"/>, bottom first; null when there is no look file or it cannot be read.</summary>
    public static List<LookLayer>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !JsonValue.TryParse(json, out JsonValue root) || root.Type != JsonType.Object)
            return null;
        JsonValue layers = root["layers"];
        if (layers.Type != JsonType.Array) return null;
        var look = new List<LookLayer>(layers.Count);
        for (int i = 0; i < layers.Count; i++)
        {
            JsonValue layer = layers[i];
            ThemePart parts = ThemePart.None;
            JsonValue names = layer["parts"];
            for (int n = 0; n < names.Count; n++)
                foreach ((ThemePart part, string name) in Names)
                    if (names[n].AsString() == name) parts |= part;
            if (parts != ThemePart.None)
                look.Add(new LookLayer(layer.GetString("pack"), layer.GetString("id"), layer.GetString("name"), parts));
        }
        return look;
    }

    public static string Serialize(IReadOnlyList<LookLayer> look)
    {
        var layers = JsonValue.NewArray();
        foreach (LookLayer layer in look)
        {
            var parts = JsonValue.NewArray();
            foreach ((ThemePart part, string name) in Names)
                if ((layer.Parts & part) != 0) parts.Add(name);
            var o = JsonValue.NewObject();
            o["pack"] = layer.Pack;
            o["id"] = layer.Id;
            o["name"] = layer.Name;
            o["parts"] = parts;
            layers.Add(o);
        }
        var root = JsonValue.NewObject();
        root["layers"] = layers;
        return root.Write(indented: true) + "\n";
    }

    /// <summary>
    /// The look after <paramref name="layer"/> was applied: on top of <paramref name="look"/> when themes combine (an
    /// earlier layer of the same pack is taken out), alone when they replace each other.
    /// </summary>
    public static List<LookLayer> Applied(IReadOnlyList<LookLayer>? look, LookLayer layer, bool combine)
    {
        var next = new List<LookLayer>();
        if (combine && look is not null)
            foreach (LookLayer earlier in look)
                if (layer.Pack.Length == 0 || earlier.Pack != layer.Pack) next.Add(earlier);
        if (layer.Parts != ThemePart.None) next.Add(layer);
        return next;
    }

    /// <summary>The look after <paramref name="parts"/> went back to the defaults: layers left with no part are dropped.</summary>
    public static List<LookLayer> Without(IReadOnlyList<LookLayer> look, ThemePart parts)
    {
        var next = new List<LookLayer>(look.Count);
        foreach (LookLayer layer in look)
            if ((layer.Parts & ~parts) is var left && left != ThemePart.None) next.Add(layer with { Parts = left });
        return next;
    }

    /// <summary>The look without the layers of the pack at <paramref name="pack"/>.</summary>
    public static List<LookLayer> WithoutPack(IReadOnlyList<LookLayer> look, string pack)
    {
        var next = new List<LookLayer>(look.Count);
        foreach (LookLayer layer in look)
            if (layer.Pack != pack) next.Add(layer);
        return next;
    }

    /// <summary>
    /// The parts a restore can offer: what <paramref name="lookJson"/> shows, or, without a look or with an empty one,
    /// every part some file in <paramref name="backupIndex"/> (ThemeApplier.BackupIndex) belongs to.
    /// </summary>
    public static ThemePart Restorable(string? lookJson, string? backupIndex)
    {
        if (Parse(lookJson) is { Count: > 0 } look) return Parts(look);
        ThemePart parts = ThemePart.None;
        foreach (string line in (backupIndex ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            parts |= ThemeParts.OfLogical(line);
        return parts;
    }

    /// <summary>Every part some layer shows.</summary>
    public static ThemePart Parts(IReadOnlyList<LookLayer> look)
    {
        ThemePart parts = ThemePart.None;
        foreach (LookLayer layer in look) parts |= layer.Parts;
        return parts;
    }
}
