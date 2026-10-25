using System;
using System.Collections.Generic;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The bytes a theme's preview shows or plays, out of its pack: tile icons, control-centre icons, the pictures behind the
/// tiles, the UI sounds and the music (the MP3 excerpt, or else the home music as ATRAC9). They are the pack's own files, still encoded (PNG, JPEG, VAG, MP3, ATRAC9); the
/// pictures are decoded one at a time when a tile needs them. Only the files <see cref="PreviewModel"/> names are
/// read, so a pack with tens of megabytes of unrelated payloads costs no more than its small previews. The home
/// backgrounds (zstd'd BC7 DDS, a few MB each, still compressed here) are the biggest of them: a few, within a budget.
/// </summary>
public sealed class PreviewAssets
{
    // Per kind of file, to keep a pack from filling the heap with something that is not a preview.
    public const int MaxTileIcon = 1 << 20, MaxControlIcon = 256 << 10, MaxPicture = 2 << 20, MaxSound = 512 << 10, MaxMusic = 3 << 20,
        // The theme's own home music (ATRAC9, a few MB; the pack format caps a payload at 32 MiB).
        MaxMusicAt9 = 32 << 20,
        // A home background (3840x2160 BC7 in zstd is 1 to 4 MB; the decoded texture is capped in PreviewVisuals), and what
        // all of a preview's backgrounds may take together (PreviewModel.MaxBackgrounds files).
        MaxBackground = 16 << 20, MaxBackgroundTotal = 24 << 20;

    private readonly Dictionary<string, byte[]> _files;

    private PreviewAssets(Dictionary<string, byte[]> files) => _files = files;

    /// <summary>The bytes of a file the model names, or null when the pack lacks it or it was too big to keep.</summary>
    public byte[]? Get(string? file) => file is not null && _files.TryGetValue(file, out byte[]? bytes) ? bytes : null;

    /// <summary>How many files were read.</summary>
    public int Count => _files.Count;

    /// <summary>
    /// Reads the files of <paramref name="model"/> out of the archive. A file the pack lacks, or one over the limit for
    /// its kind, is left out; the preview then draws a stand-in or stays silent for it. Raises
    /// <see cref="ThemePackException"/> only when the archive itself cannot be read.
    /// </summary>
    public static PreviewAssets Load(PreviewModel model, byte[] archive)
    {
        HashSet<string> wanted = model.Files();
        ThemePack pack = ThemePack.OpenSelected(archive, name => wanted.Contains(name));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Take(string? file, int limit)
        {
            if (file is not null && pack.Find(file) is { } bytes && bytes.Length > 0 && bytes.Length <= limit) files[file] = bytes;
        }
        foreach (PreviewTile tile in model.Games) TakeTile(tile);
        foreach (PreviewTile tile in model.Media) TakeTile(tile);
        foreach (PreviewControl control in model.Controls) Take(control.IconFile, MaxControlIcon);
        foreach (string file in model.SoundFiles.Values) Take(file, MaxSound);
        Take(model.AllAppsFile, MaxPicture);
        Take(model.MusicFile, MaxMusic);
        Take(model.MusicAt9File, MaxMusicAt9);
        // The main background first; one that would take the total over the budget is left out and its tiles fall back.
        int backgroundBytes = 0;
        foreach (string file in model.BackgroundFiles)
        {
            if (pack.Find(file) is not { Length: > 0 } bytes || bytes.Length > MaxBackground || backgroundBytes + bytes.Length > MaxBackgroundTotal) continue;
            files[file] = bytes;
            backgroundBytes += bytes.Length;
        }
        return new PreviewAssets(files);

        void TakeTile(PreviewTile tile)
        {
            Take(tile.IconFile, MaxTileIcon);
            Take(tile.PictureFile, MaxPicture);
        }
    }

    /// <summary>An empty set, for a preview whose pack could not be read.</summary>
    public static PreviewAssets Empty() => new(new Dictionary<string, byte[]>(StringComparer.Ordinal));
}
