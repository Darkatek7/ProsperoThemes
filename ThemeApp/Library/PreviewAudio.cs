using System;
using System.Collections.Generic;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The sound of a theme's preview: the pack's UI clips on the d-pad and the face buttons, and its music excerpt on a
/// button. Every call comes from the frame thread and returns at once; mixing and output belong to the implementation's
/// own thread, and a failure anywhere leaves the preview silent instead of broken.
/// </summary>
public interface IPreviewAudio : IDisposable
{
    /// <summary>The clips the pack brings, VAG files by sound. Replaces the earlier ones; an empty set makes the UI silent.</summary>
    void SetClips(IReadOnlyDictionary<PreviewSound, byte[]> vags);

    /// <summary>Plays the clip of <paramref name="sound"/> on top of whatever is playing. Does nothing for a sound the pack lacks.</summary>
    void Play(PreviewSound sound);

    /// <summary>Whether the music excerpt is playing (or being prepared to).</summary>
    bool MusicPlaying { get; }

    /// <summary>Starts the MP3 excerpt in a loop, or stops it when it is playing. Returns whether it plays now.</summary>
    bool ToggleMusic(byte[] mp3);

    /// <summary>
    /// The preview closed: the music stops, clips already playing finish, and the output closes. Starts nothing; the next
    /// <see cref="Play"/> opens the output again.
    /// </summary>
    void Close();
}

/// <summary>No sound: the PC preview, and the app when the output cannot be used.</summary>
public sealed class NullPreviewAudio : IPreviewAudio
{
    public void SetClips(IReadOnlyDictionary<PreviewSound, byte[]> vags) { }
    public void Play(PreviewSound sound) { }
    public bool MusicPlaying => false;
    public bool ToggleMusic(byte[] mp3) => false;
    public void Close() { }
    public void Dispose() { }
}
