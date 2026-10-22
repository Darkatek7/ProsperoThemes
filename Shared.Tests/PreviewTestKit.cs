using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Compression;
using SharpProspero.Graphics;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// What the preview tests share: pictures and packs made in code (a PNG writer and reader for flat colours, a stand-in for
/// a JPEG that only carries the header the app measures and its colour), and fakes for the sound and the worker.
/// </summary>
internal static class PreviewKit
{
    // ---- Pictures ---------------------------------------------------------------------------------------------------

    /// <summary>A PNG of one colour (8-bit RGBA, filter 0), with a real header, so the app's size checks and a real reader see it.</summary>
    public static byte[] Png(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var raw = new byte[height * (1 + 4 * width)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = y * (1 + 4 * width) + 1 + 4 * x;
                (raw[at], raw[at + 1], raw[at + 2], raw[at + 3]) = (r, g, b, a);
            }
        var data = new MemoryStream();
        using (var z = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", data.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(four, (uint)data.Length);
        s.Write(four);
        byte[] name = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(name);
        s.Write(data);
        // The CRC is not checked by the readers here, but a PNG should have one.
        uint crc = 0xFFFFFFFF;
        foreach (byte b in name.Concat(data))
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(four, ~crc);
        s.Write(four);
    }

    /// <summary>Reads what <see cref="Png"/> wrote (and the 8-bit RGBA filter-0 PNGs like it) into pixels.</summary>
    public static PixelBuffer DecodePng(byte[] png)
    {
        (int width, int height) = ImageHeader.PngSize(png) ?? throw new InvalidDataException("not a PNG");
        var idat = new MemoryStream();
        for (int p = 8; p + 12 <= png.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p, 4));
            if (System.Text.Encoding.ASCII.GetString(png, p + 4, 4) == "IDAT") idat.Write(png, p + 8, length);
            p += 12 + length;
        }
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        byte[] bytes = raw.ToArray();
        var buffer = new PixelBuffer(width, height);
        unsafe
        {
            uint* pixels = buffer.AsSurface().Pixels;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = y * (1 + 4 * width) + 1 + 4 * x;
                    pixels[y * width + x] = ((uint)bytes[at + 3] << 24) | ((uint)bytes[at] << 16) | ((uint)bytes[at + 1] << 8) | bytes[at + 2];
                }
        }
        return buffer;
    }

    /// <summary>
    /// Stands in for a JPEG: it starts like one (a start-of-image and a frame header of the size, which is all the app
    /// measures before it hands the bytes to the decoder), ends like one, and carries its colour after the end marker
    /// where <see cref="Decode"/> finds it. The real decoders are the console's and the PC preview's own.
    /// </summary>
    public static byte[] FakeJpeg(int width, int height, byte r, byte g, byte b) =>
        [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9, r, g, b];

    /// <summary>The decoder of the tests: a real PNG, or one of <see cref="FakeJpeg"/>'s, as a flat picture.</summary>
    public static PixelBuffer Decode(byte[] bytes)
    {
        if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            (int width, int height) = ImageHeader.JpegSize(bytes)!.Value;
            var picture = new PixelBuffer(width, height);
            picture.Clear(Color.FromRgb(bytes[^3], bytes[^2], bytes[^1]));
            return picture;
        }
        return DecodePng(bytes);
    }

    public static unsafe uint PixelAt(PixelBuffer buffer, int x, int y) => buffer.AsSurface().Pixels[y * buffer.AsSurface().Stride + x];

    // ---- Packs ------------------------------------------------------------------------------------------------------

    public static ThemePackManifest Manifest(IEnumerable<ThemePackTarget>? targets = null, ThemePackPreviews? previews = null) =>
        new(2, "kit", "Kit", "tests", "1.0.0", "", "keep", (targets ?? []).ToList()) { Previews = previews ?? ThemePackPreviews.None };

    public static ThemePackTarget Target(string path, string file = "assets/x.bin", string layer = "icon", string? resource = null) =>
        new(path, layer, file, 1, new string('0', 64), Resource: resource);

    public static ThemePackPreviews Previews(IEnumerable<(string Tid, string File)>? apps = null, string? allApps = null, string? music = null,
        IEnumerable<(string Slot, string File)>? icons = null) =>
        new((apps ?? []).ToDictionary(a => a.Tid, a => a.File), allApps, music, (icons ?? []).ToDictionary(i => i.Slot, i => i.File));

    /// <summary>A pack (as bytes) with a manifest and files; the manifest's targets need not have payloads, the preview never asks.</summary>
    public static byte[] Archive(ThemePackManifest manifest, params (string Name, byte[] Data)[] files)
    {
        var zip = new ZipBuilder().AddText("theme.json", manifest.Write());
        foreach ((string name, byte[] data) in files) zip.Add(name, data);
        return zip.ToArray();
    }

    // ---- Fakes ------------------------------------------------------------------------------------------------------

    public sealed class RecordingAudio : IPreviewAudio
    {
        public List<PreviewSound> Played { get; } = [];
        public List<string> Calls { get; } = [];
        public IReadOnlyDictionary<PreviewSound, byte[]> Clips { get; private set; } = new Dictionary<PreviewSound, byte[]>();
        public byte[]? MusicStarted { get; private set; }
        public bool MusicPlaying { get; private set; }
        public int Closed { get; private set; }
        public void SetClips(IReadOnlyDictionary<PreviewSound, byte[]> vags) { Clips = vags; Calls.Add("clips " + vags.Count); }
        public void Play(PreviewSound sound) { Played.Add(sound); Calls.Add("play " + sound); }
        public bool ToggleMusic(byte[] mp3) { MusicPlaying = !MusicPlaying; MusicStarted = mp3; Calls.Add("music " + MusicPlaying); return MusicPlaying; }
        public void Close() { Closed++; MusicPlaying = false; Calls.Add("close"); }
        public void Dispose() { }
    }

    /// <summary>Holds the work until the test says; <see cref="RunAll"/> does the work, then the callbacks (as the frame thread would).</summary>
    public sealed class QueueWorker : IPreviewWorker
    {
        private readonly List<(Action Work, Action Done)> _jobs = [];
        public int Pending => _jobs.Count;
        public void Run(Action work, Action done) => _jobs.Add((work, done));

        /// <summary>Runs the queued jobs (and any they queue meanwhile). Work first, all of it, then each callback.</summary>
        public void RunAll()
        {
            while (_jobs.Count > 0)
            {
                var jobs = _jobs.ToArray();
                _jobs.Clear();
                foreach (var job in jobs) job.Work();
                foreach (var job in jobs) job.Done();
            }
        }

        /// <summary>Does only the work of the queued jobs and keeps their callbacks for <see cref="Finish"/>: a result that is late.</summary>
        public Action[] WorkOnly()
        {
            var jobs = _jobs.ToArray();
            _jobs.Clear();
            foreach (var job in jobs) job.Work();
            return jobs.Select(j => j.Done).ToArray();
        }
    }
}
