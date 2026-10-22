using System;

namespace ProsperoThemes.App.Library;

/// <summary>Where the sound of an MP3 starts and what it is: the facts the console's decoder does not report for Layer III.</summary>
/// <param name="Start">Offset of the first frame with sound (after an ID3v2 tag and a Xing/Info frame).</param>
/// <param name="SamplesPerFrame">PCM samples per channel in one frame: 1152, or 576 for MPEG-2 and 2.5.</param>
public readonly record struct Mp3Info(int Start, int SampleRate, int Channels, int SamplesPerFrame);

/// <summary>
/// A small reader for the headers of an MP3 file, a port of <c>web/shared/src/mp3.ts</c>. The audio decoder of the SDK
/// finds the frames itself but never says the sample rate or the channel count of a Layer III stream, and a clip played
/// at the wrong rate is the wrong pitch (or, with no rate at all, silent), so the app reads them here.
/// </summary>
public static class Mp3Frames
{
    private static readonly int[] V1L1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] V1L2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] V1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] V2L1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] V2L23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[][] SampleRates = [[44100, 48000, 32000], [22050, 24000, 16000], [11025, 12000, 8000]];

    private readonly record struct Frame(int Length, int SampleRate, int Samples, int Version, int Layer, int Channels, bool Crc);

    /// <summary>
    /// The first frame with sound of a Layer III file, or null for anything else (no frames, another layer, free bitrate).
    /// A frame counts only when the next one follows at once (the sync pattern alone is easy to hit by chance); a Xing,
    /// Info or VBRI frame in front holds no sound and is skipped.
    /// </summary>
    public static Mp3Info? Find(ReadOnlySpan<byte> data)
    {
        int pos = Id3v2Length(data);
        while (pos + 4 <= data.Length)
        {
            if (FrameAt(data, pos) is { } frame && Follows(data, pos + frame.Length))
            {
                if (IsInfo(data, pos, frame))
                {
                    pos += frame.Length;
                    continue;
                }
                return frame.Layer == 3 ? new Mp3Info(pos, frame.SampleRate, frame.Channels, frame.Samples) : null;
            }
            pos++;
        }
        return null;
    }

    /// <summary>Bytes an ID3v2 tag takes at the start of the data (header, body and footer), or 0 when there is none.</summary>
    public static int Id3v2Length(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || data[0] != 0x49 || data[1] != 0x44 || data[2] != 0x33) return 0;
        // Four bytes of 7 bits each; a set top bit means this is not a tag.
        for (int i = 6; i < 10; i++)
            if ((data[i] & 0x80) != 0) return 0;
        int body = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
        int footer = (data[5] & 0x10) != 0 ? 10 : 0;
        return 10 + body + footer;
    }

    // The frame ends at the end of the data (give or take an ID3v1 tag), or the next one starts right there.
    private static bool Follows(ReadOnlySpan<byte> data, int end) =>
        end <= data.Length && (end >= data.Length - 3 || FrameAt(data, end) is not null || (data.Length - end == 128 && data[end] == 0x54 && data[end + 1] == 0x41 && data[end + 2] == 0x47));

    private static Frame? FrameAt(ReadOnlySpan<byte> data, int pos)
    {
        if (pos < 0 || pos + 4 > data.Length) return null;
        int b1 = data[pos + 1];
        if (data[pos] != 0xFF || (b1 & 0xE0) != 0xE0) return null;
        int versionBits = (b1 >> 3) & 3;   // 0 = MPEG2.5, 1 = reserved, 2 = MPEG2, 3 = MPEG1
        int layerBits = (b1 >> 1) & 3;     // 1 = III, 2 = II, 3 = I, 0 = reserved
        if (versionBits == 1 || layerBits == 0) return null;
        int b2 = data[pos + 2];
        int bitrateIndex = b2 >> 4, rateIndex = (b2 >> 2) & 3;
        if (bitrateIndex is 0 or 15 || rateIndex == 3) return null;

        int version = versionBits == 3 ? 1 : versionBits == 2 ? 2 : 3;
        int layer = layerBits == 3 ? 1 : layerBits == 2 ? 2 : 3;
        int[] table = version == 1 ? (layer == 1 ? V1L1 : layer == 2 ? V1L2 : V1L3) : (layer == 1 ? V2L1 : V2L23);
        int bitrate = table[bitrateIndex] * 1000;
        int sampleRate = SampleRates[version - 1][rateIndex];
        int padding = (b2 >> 1) & 1;

        int length, samples;
        if (layer == 1)
        {
            length = (12 * bitrate / sampleRate + padding) * 4;
            samples = 384;
        }
        else if (layer == 2 || version == 1)
        {
            length = 144 * bitrate / sampleRate + padding;
            samples = 1152;
        }
        else
        {
            length = 72 * bitrate / sampleRate + padding;
            samples = 576;
        }
        return new Frame(length, sampleRate, samples, version, layer, data[pos + 3] >> 6 == 3 ? 1 : 2, (b1 & 1) == 0);
    }

    // The Xing/Info/VBRI frame some encoders put first: it holds no sound and only counts the frames.
    private static bool IsInfo(ReadOnlySpan<byte> data, int pos, Frame frame)
    {
        int side = frame.Version == 1 ? (frame.Channels == 1 ? 17 : 32) : frame.Channels == 1 ? 9 : 17;
        int xing = pos + 4 + (frame.Crc ? 2 : 0) + side;
        if (xing + 4 <= pos + frame.Length && xing + 4 <= data.Length && (Tag(data, xing, "Xing") || Tag(data, xing, "Info"))) return true;
        int vbri = pos + 4 + 32;
        return vbri + 4 <= pos + frame.Length && vbri + 4 <= data.Length && Tag(data, vbri, "VBRI");
    }

    private static bool Tag(ReadOnlySpan<byte> data, int at, string tag) =>
        data[at] == tag[0] && data[at + 1] == tag[1] && data[at + 2] == tag[2] && data[at + 3] == tag[3];
}
