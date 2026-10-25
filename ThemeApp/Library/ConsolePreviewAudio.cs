using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SharpProspero.Audio;
using SharpProspero.Interop.Audio;
using SharpProspero.Interop.Sysmodule;
using SharpProspero.Modules;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The sound of the preview on the console: the SDK's <see cref="AudioOutDevice"/> (libSceAudioOut) fed by an
/// <see cref="AudioMixer"/> for the UI clips and another for the music, with the MP3 excerpt decoded by
/// <see cref="AudioDecoder"/> (libSceAudiodec, whose module is loaded through the sysmodule service first). A theme's
/// own home music (ATRAC9) is decoded by the C# codec instead (<see cref="Atrac9Codec.DecodeStereo"/>, its first thirty
/// seconds): no further native call, and the same output loop.
/// </summary>
/// <remarks>
/// Nothing here runs on the frame thread but a lock-guarded queue write. The output port is opened by the audio thread
/// when the first sound is wanted and closed by it when the preview closes (after the clips already playing end); the
/// thread blocks in <c>Output</c>, which paces it to the audio clock. The music is decoded to PCM in one go on a short
/// thread of its own (at most a minute and a half of it), then handed to the audio thread. Any failure, in opening the
/// port, loading the decoder, or decoding a clip, leaves the preview silent and is never raised.
/// This file is the only one that links libSceAudioOut and libSceAudiodec into the app: the build's need entries gain
/// both when it is compiled in, and an app that does not create this class does not carry them.
/// </remarks>
public sealed class ConsolePreviewAudio : IPreviewAudio
{
    private const int SampleRate = 48000, Grain = 512, MaxMusicSeconds = 90, MaxAtrac9Seconds = 30;
    private const float ClipVolume = 0.9f, MusicVolume = 0.6f;
    // How long the port stays open after the preview closed, for the clips still playing (the back clip of leaving).
    private const long CloseGraceMs = 1500;

    private enum Kind { Clip, Music, StopMusic }
    private readonly record struct Command(Kind Kind, PcmAudio Audio);
    private enum MusicState { Off, Decoding, Playing }

    private readonly object _gate = new();
    private readonly Queue<Command> _commands = new();
    private Dictionary<PreviewSound, PcmAudio> _clips = new();
    private Thread? _thread;
    private bool _closeRequested, _failed;
    private MusicState _music;
    private int _musicGeneration;
    // The PCM of the last music decoded and the bytes it came from, so that Square again does not decode it again; dropped on Close.
    private byte[]? _decodedFrom;
    private PcmAudio _decoded;

    public bool MusicPlaying
    {
        get { lock (_gate) return _music != MusicState.Off; }
    }

    public void SetClips(IReadOnlyDictionary<PreviewSound, byte[]> vags)
    {
        var clips = new Dictionary<PreviewSound, PcmAudio>();
        foreach (var (sound, bytes) in vags)
        {
            try { clips[sound] = VagAudio.Decode(bytes); }
            catch { /* that sound stays silent */ }
        }
        lock (_gate) _clips = clips;
    }

    public void Play(PreviewSound sound)
    {
        lock (_gate)
        {
            if (_failed || !_clips.TryGetValue(sound, out PcmAudio clip)) return;
            _commands.Enqueue(new Command(Kind.Clip, clip));
            EnsureThread();
        }
    }

    public bool ToggleMusic(byte[] mp3) => Toggle(mp3, atrac9: false);

    public bool ToggleAtrac9Music(byte[] at9) => Toggle(at9, atrac9: true);

    private bool Toggle(byte[] data, bool atrac9)
    {
        int generation;
        PcmAudio? cached = null;
        lock (_gate)
        {
            if (_music != MusicState.Off)
            {
                _music = MusicState.Off;
                _musicGeneration++;
                _commands.Enqueue(new Command(Kind.StopMusic, default));
                return false;
            }
            if (_failed) return false;
            _music = MusicState.Decoding;
            generation = ++_musicGeneration;
            if (ReferenceEquals(_decodedFrom, data)) cached = _decoded;
        }
        new Thread(() => DecodeAndPlay(data, atrac9, cached, generation)) { IsBackground = true, Name = "PreviewMusic" }.Start();
        return true;
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_music != MusicState.Off)
            {
                _music = MusicState.Off;
                _musicGeneration++;
                _commands.Enqueue(new Command(Kind.StopMusic, default));
            }
            _closeRequested = true;
            _decodedFrom = null;
            _decoded = default;
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _musicGeneration++;
            _music = MusicState.Off;
            _commands.Clear();
            _commands.Enqueue(new Command(Kind.StopMusic, default));
            _closeRequested = true;
            _decodedFrom = null;
            _decoded = default;
            _clips = new Dictionary<PreviewSound, PcmAudio>();
        }
        thread?.Join(3000);
    }

    // Under _gate. A thread that is closing is asked to carry on instead; one that has gone is replaced.
    private void EnsureThread()
    {
        _closeRequested = false;
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "PreviewAudio" };
        _thread.Start();
    }

    private void DecodeAndPlay(byte[] data, bool atrac9, PcmAudio? cached, int generation)
    {
        PcmAudio? pcm = cached;
        if (pcm is null)
        {
            try { pcm = atrac9 ? DecodeAtrac9(data) : DecodeMp3(data); }
            catch { /* silent */ }
        }
        lock (_gate)
        {
            if (generation != _musicGeneration) return;
            if (cached is null && pcm is { } decoded)
            {
                _decodedFrom = data;
                _decoded = decoded;
            }
            if (pcm is not { } audio || _failed)
            {
                _music = MusicState.Off;
                return;
            }
            _music = MusicState.Playing;
            _commands.Enqueue(new Command(Kind.Music, audio));
            EnsureThread();
        }
    }

    private void Run()
    {
        AudioOutDevice? device = null;
        try
        {
            // A port that was just closed by the previous preview may still be draining: try a few times.
            for (int attempt = 0; device is null; attempt++)
            {
                try { device = AudioOutDevice.OpenStereo(Grain, SampleRate); }
                catch when (attempt < 3) { Thread.Sleep(80); }
            }
            var ui = new AudioMixer(SampleRate);
            var music = new AudioMixer(SampleRate) { MasterVolume = MusicVolume };
            var uiBlock = new short[device.SamplesPerBlock];
            var musicBlock = new short[device.SamplesPerBlock];
            long deadline = 0;
            while (true)
            {
                lock (_gate)
                {
                    while (_commands.TryDequeue(out Command command))
                    {
                        switch (command.Kind)
                        {
                            case Kind.Clip: ui.Play(command.Audio, ClipVolume); break;
                            case Kind.Music: music.Play(command.Audio, 1f, loop: true); break;
                            default: music.StopAll(); break;
                        }
                    }
                    if (_closeRequested)
                    {
                        if (deadline == 0) deadline = Environment.TickCount64 + CloseGraceMs;
                        if (ui.ActiveVoices == 0 || Environment.TickCount64 > deadline)
                        {
                            _thread = null;   // from here on a new sound starts a new thread
                            return;
                        }
                    }
                    else deadline = 0;
                }
                ui.Mix(uiBlock);
                music.Mix(musicBlock);
                for (int i = 0; i < uiBlock.Length; i++)
                    uiBlock[i] = (short)Math.Clamp(uiBlock[i] + musicBlock[i], short.MinValue, short.MaxValue);
                device.Output(uiBlock);
            }
        }
        catch
        {
            lock (_gate)
            {
                _failed = true;
                _thread = null;
                _music = MusicState.Off;
                _commands.Clear();
            }
        }
        finally
        {
            try { device?.Dispose(); }
            catch { /* the port is gone either way */ }
        }
    }

    // The first thirty seconds of the theme's home music as 48 kHz stereo PCM, or null when the file cannot be decoded.
    private static PcmAudio? DecodeAtrac9(byte[] at9)
    {
        short[] samples = Atrac9Codec.DecodeStereo(at9, MaxAtrac9Seconds, out int rate);
        if (samples.Length < 2 * 1024) return null;
        var pcm = new PcmAudio(samples, rate, 2);
        return rate == SampleRate ? pcm : AudioClip.Resample(pcm, SampleRate);
    }

    // The whole excerpt as 48 kHz PCM, or null when it cannot be decoded. The decoder reports no rate or channel count for
    // Layer III, so they come from the file's first frame header; the channel count of the output from what one frame made.
    private static PcmAudio? DecodeMp3(byte[] mp3)
    {
        if (Mp3Frames.Find(mp3) is not { } info) return null;
        SystemModule.Load(SystemModuleId.AudioDec);
        using AudioDecoder decoder = AudioDecoder.CreateMp3();
        var block = new byte[decoder.SuggestedOutputSize];
        var samples = new List<short>(SampleRate * 2 * 30);
        int maxSamples = info.SampleRate * MaxMusicSeconds * 2;
        int channels = 0, pos = info.Start;
        while (pos < mp3.Length && samples.Count < maxSamples)
        {
            AudioDecodeResult step;
            try { step = decoder.Decode(mp3.AsSpan(pos), block); }
            catch { break; }   // a cut or damaged frame at the end: keep what is decoded
            if (step.BytesConsumed <= 0) break;
            pos += step.BytesConsumed;
            if (step.BytesProduced <= 0) continue;
            if (channels == 0) channels = step.BytesProduced >= 4 * info.SamplesPerFrame ? 2 : 1;
            samples.AddRange(MemoryMarshal.Cast<byte, short>(block.AsSpan(0, step.BytesProduced & ~1)));
        }
        if (channels == 0 || samples.Count < channels * 1024) return null;
        var pcm = new PcmAudio(samples.ToArray(), info.SampleRate, channels);
        return info.SampleRate == SampleRate ? pcm : AudioClip.Resample(pcm, SampleRate);
    }
}
