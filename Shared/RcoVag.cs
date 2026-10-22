using System;
using System.Buffers.Binary;
using System.Text;

namespace ProsperoThemes.Shared;

/// <summary>
/// Size-preserving replacement of named UI sounds inside an RCO container (proven on FW 13.00 with
/// <c>Sce.PlayStation.PUI_UI3.rco</c>: the shell plays the replacement after a restart).
/// </summary>
/// <remarks>
/// The container keeps 107 sounds as plain <c>VAGp</c> clips: a 0x30-byte header (big-endian data size at +0x0C,
/// sample rate at +0x10, channel count at +0x1E, a 16-byte name at +0x20), then stereo PS-ADPCM frames of 16 bytes,
/// left and right taking turns. Every stock clip starts with a silent frame pair and ends with an end-marker pair
/// (flag 7). A replacement is a mono VAG file; it is written to both channels between the stock first and last pair
/// and padded with silent frames (or cut) to exactly the original's length, so no offset or size field in the
/// container moves. The stock clips are HEVAG, whose first five predictors are standard VAG's, so a standard VAG
/// (filters 0 to 4) plays unchanged. Mirrors <c>ThemePackCreator/vag.py</c>.
/// </remarks>
public static class RcoVag
{
    private static readonly byte[] Magic = "VAGp"u8.ToArray();
    private const int HeaderSize = 0x30, Frame = 16, NameSize = 16;

    /// <summary>
    /// Finds the clip named <paramref name="name"/>. The container keeps names cut to 16 characters, so a longer
    /// name matches by its first 16; the match has to be the only one.
    /// </summary>
    /// <returns>Offset and length of the clip's frames (after its header), and its sample rate.</returns>
    /// <exception cref="ThemePackException">No clip, or more than one, has that name, or the clip is not laid out like the stock ones.</exception>
    public static (int Offset, int Length, int Rate) Find(ReadOnlySpan<byte> container, string name)
    {
        string key = name.Length > NameSize ? name[..NameSize] : name;
        (int Offset, int Length, int Rate) hit = default;
        int matches = 0, i = 0;
        while ((i = IndexOf(container, Magic, i)) >= 0)
        {
            if (i + HeaderSize > container.Length) break;
            if (NameAt(container, i) == key)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(container.Slice(i + 0x0C, 4));
                int rate = BinaryPrimitives.ReadInt32BigEndian(container.Slice(i + 0x10, 4));
                hit = (i + HeaderSize, length, rate);
                matches++;
                if (container[i + 0x1E] != 2 || length < 4 * Frame || length % (2 * Frame) != 0 || length > container.Length - hit.Offset)
                    throw new ThemePackException($"sound '{name}' is not a stereo clip like the stock ones (another firmware?)");
            }
            i += Magic.Length;
        }
        if (matches != 1)
            throw new ThemePackException($"sound '{name}' matches {matches} clips in the container");
        ReadOnlySpan<byte> data = container.Slice(hit.Offset, hit.Length);
        if (data[..(2 * Frame)].IndexOfAnyExcept((byte)0) >= 0 || (data[data.Length - 2 * Frame + 1] & 0x0F) != 7)
            throw new ThemePackException($"sound '{name}' does not start and end like the stock clips (another firmware?)");
        return hit;
    }

    /// <summary>Every clip in the container, in order: its name, the length of its frames and its sample rate.</summary>
    public static System.Collections.Generic.List<(string Name, int Length, int Rate)> List(ReadOnlySpan<byte> container)
    {
        var clips = new System.Collections.Generic.List<(string, int, int)>();
        int i = 0;
        while ((i = IndexOf(container, Magic, i)) >= 0 && i + HeaderSize <= container.Length)
        {
            clips.Add((NameAt(container, i), BinaryPrimitives.ReadInt32BigEndian(container.Slice(i + 0x0C, 4)),
                BinaryPrimitives.ReadInt32BigEndian(container.Slice(i + 0x10, 4))));
            i += Magic.Length;
        }
        return clips;
    }

    /// <summary>
    /// The clip's new frames: the mono VAG <paramref name="vag"/> on both channels between the stock first and last
    /// pair of <paramref name="stock"/>, cut or padded with silence to the stock length.
    /// </summary>
    /// <exception cref="ThemePackException">It is not a mono VAG at the clip's sample rate, or it uses a predictor standard VAG lacks.</exception>
    public static byte[] Fit(ReadOnlySpan<byte> vag, ReadOnlySpan<byte> stock, int rate)
    {
        (int count, int fileRate) = Check(vag);
        if (fileRate != rate)
            throw new ThemePackException($"sound is {fileRate} Hz, the clip plays at {rate} Hz");
        byte[] fitted = new byte[stock.Length];
        stock[..(2 * Frame)].CopyTo(fitted);
        stock[^(2 * Frame)..].CopyTo(fitted.AsSpan(fitted.Length - 2 * Frame));
        int slots = stock.Length / (2 * Frame) - 2;
        ReadOnlySpan<byte> frames = vag.Slice(HeaderSize, count * Frame);
        Span<byte> frame = stackalloc byte[Frame];
        for (int f = 0; f < slots; f++)
        {
            if (f < count) frames.Slice(f * Frame, Frame).CopyTo(frame);
            else frame.Clear();
            frame[1] = (byte)(f == slots - 1 ? 1 : 0);   // the last frame ends the sound, as in the stock clips
            int at = (f + 1) * 2 * Frame;
            frame.CopyTo(fitted.AsSpan(at));
            frame.CopyTo(fitted.AsSpan(at + Frame));
        }
        return fitted;
    }

    /// <summary>
    /// Checks a mono VAG file and returns its number of sound frames (an end-marker frame and what follows it do not
    /// count) and its sample rate.
    /// </summary>
    /// <exception cref="ThemePackException">It is not one.</exception>
    public static (int Frames, int Rate) Check(ReadOnlySpan<byte> vag)
    {
        if (vag.Length < HeaderSize || !vag[..4].SequenceEqual(Magic))
            throw new ThemePackException("sound is not a VAG file");
        if (vag[0x1E] > 1)
            throw new ThemePackException($"sound has {vag[0x1E]} channels; a theme sound is mono");
        int size = BinaryPrimitives.ReadInt32BigEndian(vag.Slice(0x0C, 4));
        int rate = BinaryPrimitives.ReadInt32BigEndian(vag.Slice(0x10, 4));
        if (size < 0 || size % Frame != 0 || size > vag.Length - HeaderSize)
            throw new ThemePackException("sound's VAG header gives a size its data does not have");
        int frames = 0;
        for (int at = HeaderSize; at < HeaderSize + size; at += Frame, frames++)
        {
            if ((vag[at + 1] & 0x07) == 7) break;   // end marker: no sound in it
            if (vag[at] >> 4 > 4)
                throw new ThemePackException("sound uses a predictor standard VAG does not have");
        }
        return (frames, rate);
    }

    /// <summary>Replaces the clip <paramref name="name"/> in place.</summary>
    public static void Replace(byte[] container, string name, ReadOnlySpan<byte> vag)
    {
        (int offset, int length, int rate) = Find(container, name);
        Fit(vag, container.AsSpan(offset, length), rate).CopyTo(container, offset);
    }

    private static string NameAt(ReadOnlySpan<byte> container, int header)
    {
        ReadOnlySpan<byte> field = container.Slice(header + 0x20, NameSize);
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int from)
    {
        if (from >= haystack.Length) return -1;
        int at = haystack[from..].IndexOf(needle);
        return at < 0 ? -1 : from + at;
    }
}
