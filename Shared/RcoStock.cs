using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace ProsperoThemes.Shared;

/// <summary>
/// The firmware's own bytes for the parts of an icon container that themes change, as one payload (the
/// <see cref="ThemePack.StockLayer"/>): it brings a container back to its original on a console whose backup is gone.
/// </summary>
/// <remarks>
/// A theme's icons have lost their titles and a stock clip is stereo HEVAG, so neither <see cref="RcoSvg"/> nor
/// <see cref="RcoVag"/> can find or fit these. The payload names places instead of names: <c>P5STOCK1</c>, a little-endian
/// count, that many (offset, length) pairs in ascending order, then the bytes of each. A theme never moves anything in the
/// container, so the places hold on any themed copy of that firmware's container. The applier takes that result only when
/// it is byte for byte the firmware's original. On another firmware the places mean nothing, so <see cref="PutBackByName"/>
/// goes by what each place holds: an icon with its title, a clip with its header.
/// </remarks>
public static class RcoStock
{
    private static readonly byte[] Magic = "P5STOCK1"u8.ToArray();
    private const int HeaderSize = 12, EntrySize = 8, ClipHeader = 0x30;

    /// <summary>The places of a payload, in order.</summary>
    /// <exception cref="ThemePackException">It is not such a payload.</exception>
    public static List<(int Offset, int Length)> Check(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize || !payload[..Magic.Length].SequenceEqual(Magic))
            throw new ThemePackException("payload is not a list of original bytes");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(Magic.Length, 4));
        if (count == 0 || count > (payload.Length - HeaderSize) / EntrySize)
            throw new ThemePackException($"original bytes: a count of {count} does not fit the payload");
        var places = new List<(int, int)>((int)count);
        long end = 0, total = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = payload.Slice(HeaderSize + i * EntrySize, EntrySize);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry), length = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            if (length == 0 || offset < end || offset + (long)length > int.MaxValue)
                throw new ThemePackException($"original bytes: place {i} is empty, out of order or out of range");
            end = offset + (long)length;
            total += length;
            places.Add(((int)offset, (int)length));
        }
        if (total != payload.Length - HeaderSize - count * EntrySize)
            throw new ThemePackException($"original bytes: the places add up to {total} bytes, the payload does not");
        return places;
    }

    /// <summary>What <see cref="PutBackByName"/> did: icons and sounds put back, and those of a theme it could not.</summary>
    public readonly record struct ByName(int Icons, int Sounds, int IconsLeft, int SoundsLeft);

    /// <summary>
    /// Puts a payload from another firmware's container into <paramref name="container"/> by name, for what a theme changed
    /// and nothing else. A theme's icon has lost its title, so it is a slot that has no title here and is not one of
    /// <paramref name="untitled"/> (this firmware's untitled icons); which icon it was shows in its length, among the
    /// payload's icons whose titles this container no longer has. Same length or no certainty: it stays. Without
    /// <paramref name="untitled"/>, or with another number of icons than <paramref name="icons"/> (a tool moved them),
    /// no icon is touched. A theme's sound keeps its name and has both channels alike. The icons a theme names by position
    /// (<see cref="RcoSvg.Aliases"/>) have no title to lose: where a theme's padded icon sits at such a position, the
    /// payload's icon with the measured length and hash goes back.
    /// </summary>
    /// <exception cref="ThemePackException">It is not such a payload.</exception>
    public static ByName PutBackByName(ReadOnlySpan<byte> payload, Span<byte> container, IReadOnlySet<int>? untitled, int icons)
    {
        List<(int Offset, int Length)> places = Check(payload);
        var theirs = new List<(int Data, int Length, string Title)>();
        var named = new List<(int Data, int Length)>();
        var clips = new List<(int Data, int Length)>();
        int data = HeaderSize + places.Count * EntrySize;
        foreach ((_, int length) in places)
        {
            ReadOnlySpan<byte> bytes = payload.Slice(data, length);
            if (bytes.StartsWith("VAGp"u8) && length > ClipHeader) clips.Add((data, length));
            else if (bytes.StartsWith("<svg"u8))
            {
                if (RcoSvg.TitleOf(bytes) is { } title) theirs.Add((data, length, title));
                else named.Add((data, length));
            }
            data += length;
        }
        var result = new ByName();
        List<(int Offset, int Length, string? Title)> slots = RcoSvg.Slots(container);
        var titled = new HashSet<string>(StringComparer.Ordinal);
        foreach ((_, _, string? title) in slots)
            if (title is not null) titled.Add(title);
        List<int> themed = new();
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Title is null && untitled is not null && !untitled.Contains(i)) themed.Add(i);
        if (untitled is null || slots.Count != icons)
            result = result with { IconsLeft = themed.Count };
        else
        {
            foreach (RcoSvg.Untitled alias in RcoSvg.Aliases.Values)
            {
                if (alias.Index >= slots.Count || slots[alias.Index] is not { Title: null } slot || slot.Length != alias.Length) continue;
                ReadOnlySpan<byte> live = container.Slice(slot.Offset, slot.Length);
                if (live.Length < 8 || live[^7] != (byte)' ' || ThemePack.Hash(live) == alias.Sha256) continue;
                bool put = false;
                foreach ((int at, int length) in named)
                {
                    if (length != alias.Length || ThemePack.Hash(payload.Slice(at, length)) != alias.Sha256) continue;
                    payload.Slice(at, length).CopyTo(container[slot.Offset..]);
                    put = true;
                    break;
                }
                result = put ? result with { Icons = result.Icons + 1 } : result with { IconsLeft = result.IconsLeft + 1 };
            }
            var lost = theirs.Where(t => !titled.Contains(t.Title)).ToList();
            foreach (int i in themed)
            {
                (int offset, int length, _) = slots[i];
                var same = lost.Where(t => t.Length == length).ToList();
                if (same.Count != 1)
                {
                    result = result with { IconsLeft = result.IconsLeft + 1 };
                    continue;
                }
                payload.Slice(same[0].Data, length).CopyTo(container[offset..]);
                result = result with { Icons = result.Icons + 1 };
            }
        }
        foreach ((int at, int length) in clips)
        {
            (string name, int rate) = RcoVag.ReadHeader(payload.Slice(at, ClipHeader));
            (int offset, int live, int liveRate) found;
            try
            {
                found = RcoVag.Find(container, name);
            }
            catch (ThemePackException)
            {
                continue;   // this firmware has no such clip, or one laid out in another way
            }
            if (!RcoVag.ChannelsAlike(container.Slice(found.offset, found.live)))
                continue;
            if (found.liveRate != rate)
            {
                result = result with { SoundsLeft = result.SoundsLeft + 1 };
                continue;
            }
            RcoVag.FitStereo(payload.Slice(at + ClipHeader, length - ClipHeader), found.live).CopyTo(container[found.offset..]);
            result = result with { Sounds = result.Sounds + 1 };
        }
        return result;
    }

    /// <summary>
    /// Writes the payload's bytes into <paramref name="container"/>. Returns the same list with the bytes that were there,
    /// which puts the container back when written in turn.
    /// </summary>
    /// <exception cref="ThemePackException">It is not such a payload, or a place is past the end of the container; nothing was written.</exception>
    public static byte[] Write(ReadOnlySpan<byte> payload, Span<byte> container)
    {
        List<(int Offset, int Length)> places = Check(payload);
        (int lastOffset, int lastLength) = places[^1];
        if (lastOffset + (long)lastLength > container.Length)
            throw new ThemePackException($"original bytes reach past the end of the {container.Length}-byte container");
        byte[] before = payload.ToArray();
        int data = HeaderSize + places.Count * EntrySize;
        foreach ((int offset, int length) in places)
        {
            container.Slice(offset, length).CopyTo(before.AsSpan(data));
            payload.Slice(data, length).CopyTo(container[offset..]);
            data += length;
        }
        return before;
    }
}
