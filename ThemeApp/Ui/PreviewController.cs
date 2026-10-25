using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;
using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// Runs work off the frame thread. <paramref name="work"/> runs somewhere else (a thread in the app, at once on the PC);
/// <paramref name="done"/> then runs on the frame thread, where the result may be used. <paramref name="work"/> must not
/// throw.
/// </summary>
public interface IPreviewWorker
{
    void Run(Action work, Action done);
}

/// <summary>
/// Everything around the preview that is not drawing or key handling: it opens the preview when the browser shows one
/// (reading the pack's pictures, icons and sounds on a worker, never on the frame thread), loads the picture behind a tile
/// when the renderer asks for it, plays the sounds the browser asks for, starts and stops the music, and lets go of it all
/// when the browser leaves. The app and the PC preview use the same controller; only the worker, the audio and the decoder
/// differ.
/// </summary>
/// <remarks>
/// Every method is for the frame thread. A result from a worker counts only if the preview it was started for is still the
/// open one (a generation number), otherwise it is thrown away and its pixels freed. Failures are never raised: a picture
/// that cannot be made is marked failed (the renderer falls back to the next best), an icon or sound that cannot be made
/// is left out, and a pack that cannot be read at all leaves the drawn stand-ins.
/// </remarks>
public sealed class PreviewController : IDisposable
{
    private readonly ThemeRenderer _renderer;
    private readonly ThemeBrowser _browser;
    private readonly IPreviewAudio _audio;
    private readonly Func<byte[], PixelBuffer> _decode;
    private readonly Func<string, byte[]> _readPack;
    private readonly IPreviewWorker _worker;
    private ThemeLibraryEntry? _entry;
    // The pack of a store theme being previewed (see ShowStorePack): kept in memory only while its preview is open.
    private ThemeLibraryEntry? _storeEntry;
    private byte[]? _storeBytes;
    private PreviewModel? _model;
    private PreviewAssets? _assets;
    private int _generation;
    private bool _pictureJob;

    /// <param name="decode">PNG or JPEG bytes to pixels (the app's decoder, or the PC preview's).</param>
    /// <param name="readPack">The bytes of the pack at a library path.</param>
    public PreviewController(ThemeRenderer renderer, ThemeBrowser browser, IPreviewAudio audio, Func<byte[], PixelBuffer> decode,
        Func<string, byte[]> readPack, IPreviewWorker worker)
    {
        _renderer = renderer;
        _browser = browser;
        _audio = audio;
        _decode = decode;
        _readPack = readPack;
        _worker = worker;
    }

    /// <summary>Whether a preview is open (the browser shows one and its pack has been taken in hand).</summary>
    public bool IsOpen => _entry is not null;

    /// <summary>
    /// Takes in the pack of a store theme to preview without adding it to the library: <paramref name="entry"/> is its
    /// inspected manifest (give it a path of its own, such as "store:" and the pack's hash: the renderer's caches are keyed by
    /// it) and <paramref name="pack"/> its bytes, which stay in memory until the preview closes (or <see cref="DropStorePack"/>).
    /// The app calls this and then <see cref="ThemeBrowser.OpenCommunityPreview"/>, which opens the preview over it. Nothing is
    /// written to the console or the library.
    /// </summary>
    public void ShowStorePack(ThemeLibraryEntry entry, byte[] pack)
    {
        _storeEntry = entry;
        _storeBytes = pack;
        _renderer.StorePreview = entry;
    }

    /// <summary>Lets go of a pack given to <see cref="ShowStorePack"/> that no preview was opened for (the user had left the tab).</summary>
    public void DropStorePack()
    {
        if (_entry is not null && ReferenceEquals(_entry, _storeEntry)) return;   // its preview is open: Close lets go
        _storeEntry = null;
        _storeBytes = null;
        _renderer.StorePreview = null;
    }

    // The entry the preview is, or would be, of: the store theme's, or the selected library pack.
    private ThemeLibraryEntry? Subject(IReadOnlyList<ThemeLibraryEntry> entries)
    {
        if (_browser.PreviewOfStore) return _storeEntry;
        int index = _browser.Selected;
        return index >= 0 && index < entries.Count ? entries[index] : null;
    }

    /// <summary>
    /// Before <see cref="ThemeBrowser.Update"/> each frame: tells the browser how long the preview's rows are for the
    /// selected pack, so the first press after the preview opens is already clamped to them.
    /// </summary>
    public void BeforeUpdate(IReadOnlyList<ThemeLibraryEntry> entries)
    {
        if (_browser.View is not (ThemeView.Library or ThemeView.Detail or ThemeView.Preview)) return;
        if (Subject(entries) is not { } subject) return;
        PreviewModel model = _renderer.PreviewModelFor(subject);
        _browser.SetPreviewLayout(model.Games.Count, model.Media.Count);
    }

    /// <summary>After <see cref="ThemeBrowser.Update"/> each frame: opens and closes the preview, plays sounds, starts the picture jobs.</summary>
    public void Update(IReadOnlyList<ThemeLibraryEntry> entries)
    {
        ThemeLibraryEntry? wanted = _browser.View == ThemeView.Preview ? Subject(entries) : null;

        // A press that makes a sound is played before the preview closes, so the back clip of leaving is heard.
        PreviewSound sound = _browser.TakePreviewSound();
        if (sound != PreviewSound.None && (wanted is not null || _entry is not null)) _audio.Play(sound);

        if (!ReferenceEquals(wanted, _entry))
        {
            if (_entry is not null) Close();
            if (wanted is not null) Open(wanted);
        }
        if (_entry is null) return;

        _renderer.PreviewMusicPlaying = _audio.MusicPlaying;
        if (_renderer.Preview.Wanted is { } key && !_pictureJob) LoadPicture(key);
    }

    /// <summary>
    /// Square in the preview: starts the music, or stops it. The pack's MP3 excerpt plays when it has one, else the first
    /// part of its home music (ATRAC9). Does nothing for a pack without music.
    /// </summary>
    public void ToggleMusic()
    {
        if (_entry is null || _model is null) return;
        if (_assets?.Get(_model.MusicFile) is { } mp3) _renderer.PreviewMusicPlaying = _audio.ToggleMusic(mp3);
        else if (_assets?.Get(_model.MusicAt9File) is { } at9) _renderer.PreviewMusicPlaying = _audio.ToggleAtrac9Music(at9);
    }

    private void Open(ThemeLibraryEntry entry)
    {
        _entry = entry;
        _model = _renderer.PreviewModelFor(entry);
        _assets = null;
        _audio.SetClips(NoClips);   // the earlier theme's sounds must not play while this one's are still being read
        int generation = ++_generation;
        PreviewModel model = _model;
        // A store theme's bytes are already here; the worker takes its own reference, so Close can let go of ours at any time.
        byte[]? pack = ReferenceEquals(entry, _storeEntry) ? _storeBytes : null;
        PreviewAssets assets = PreviewAssets.Empty();
        PreviewLoad load = new();
        _worker.Run(() =>
        {
            try
            {
                assets = PreviewAssets.Load(model, pack ?? _readPack(entry.Path));
                load = PreviewVisuals.Load(model, assets, _decode);
            }
            catch { /* the stand-ins stay and the preview is silent */ }
        }, () =>
        {
            if (generation != _generation)
            {
                load.Dispose();
                return;
            }
            _assets = assets;
            _renderer.Preview.Apply(load);
            _audio.SetClips(ClipsOf(model, assets));
        });
        if (entry.Preview is { } cover) LoadSmoothCover(cover, generation);
    }

    /// <summary>
    /// The cover behind the system tiles, scaled to the screen bilinearly on the worker. The renderer makes the first one at
    /// once on the frame thread with a nearest-neighbour stretch; packs carry small covers (the creator writes 640x360), so
    /// every pixel of it is a 3x3 block. This one replaces it when it is here. A cover that already is screen-sized gains
    /// nothing and is left alone; one that cannot be made leaves the quick one.
    /// </summary>
    private void LoadSmoothCover(byte[] bytes, int generation)
    {
        (int Width, int Height)? size = ImageHeader.JpegSize(bytes) ?? ImageHeader.PngSize(bytes);
        if (size is not { } measured || measured.Width > 2560 || measured.Height > 2560
            || (measured.Width == Theme.ScreenWidth && measured.Height == Theme.ScreenHeight)) return;
        PreviewPicture? picture = null;
        _worker.Run(() =>
        {
            try
            {
                using PixelBuffer source = _decode(bytes);
                picture = ThemeRenderer.BuildPreviewPicture(source, smooth: true);
            }
            catch { picture?.Dispose(); picture = null; }
        }, () =>
        {
            if (generation != _generation) picture?.Dispose();
            else if (picture is not null) _renderer.Preview.AddPicture(PreviewModel.CoverKey, picture);
        });
    }

    private void Close()
    {
        _generation++;
        // The store theme's pack is let go with its preview (a pack handed over for the next one stays).
        if (ReferenceEquals(_entry, _storeEntry))
        {
            _storeEntry = null;
            _storeBytes = null;
            _renderer.StorePreview = null;
        }
        _entry = null;
        _model = null;
        _assets = null;
        _pictureJob = false;
        _renderer.PreviewMusicPlaying = false;
        _renderer.ReleasePreview();
        _audio.Close();
    }

    // The picture behind a tile, decoded and scaled to the screen on the worker: a JPEG or PNG of the pack's previews, or one of its home
    // backgrounds (a key from PreviewModel.IsBackgroundKey).
    private void LoadPicture(string key)
    {
        byte[]? bytes = _assets?.Get(_model?.PictureFile(key));
        if (bytes is null)
        {
            // Not read yet (the pack is still loading): wait. Read and not there: the renderer falls back.
            if (_assets is not null) _renderer.Preview.Fail(key);
            return;
        }
        _pictureJob = true;
        int generation = _generation;
        bool background = PreviewModel.IsBackgroundKey(key);
        PreviewPicture? picture = null;
        _worker.Run(() =>
        {
            try
            {
                if (background)
                {
                    // The pack's real home background (zstd'd BC7): the texture is decoded to screen size, then only the bands
                    // are made; the 8 MB it inflated to is garbage before the picture exists.
                    picture = ThemeRenderer.BuildPreviewPicture(PreviewVisuals.DecodeBackground(bytes), smooth: true, consume: true);
                    return;
                }
                // A JPEG as the pack format says; a PNG is taken too. Measured before a decoder sees it.
                (int Width, int Height)? size = ImageHeader.JpegSize(bytes) ?? ImageHeader.PngSize(bytes);
                if (size is not { } measured || measured.Width > 2560 || measured.Height > 2560) return;
                using PixelBuffer source = _decode(bytes);
                picture = ThemeRenderer.BuildPreviewPicture(source, smooth: true);
            }
            catch { picture?.Dispose(); picture = null; }
        }, () =>
        {
            if (generation != _generation)
            {
                picture?.Dispose();
                return;
            }
            _pictureJob = false;
            if (picture is null) _renderer.Preview.Fail(key);
            else _renderer.Preview.AddPicture(key, picture);
        });
    }

    private static readonly IReadOnlyDictionary<PreviewSound, byte[]> NoClips = new Dictionary<PreviewSound, byte[]>();

    private static IReadOnlyDictionary<PreviewSound, byte[]> ClipsOf(PreviewModel model, PreviewAssets assets)
    {
        var clips = new Dictionary<PreviewSound, byte[]>();
        foreach (var (sound, clip) in PreviewModel.Clips)
            if (model.SoundFiles.TryGetValue(clip, out string? file) && assets.Get(file) is { } vag) clips[sound] = vag;
        return clips;
    }

    public void Dispose()
    {
        _generation++;
        _entry = null;
        _storeEntry = null;
        _storeBytes = null;
        _audio.Dispose();
    }
}
