using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpProspero.Compression;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class RcoStockTests
{
    /// <summary>A payload with the bytes of <paramref name="stock"/> at <paramref name="places"/>.</summary>
    internal static byte[] Payload(byte[] stock, params (int Offset, int Length)[] places)
    {
        using var data = new MemoryStream();
        data.Write("P5STOCK1"u8);
        data.Write(BitConverter.GetBytes((uint)places.Length));
        foreach ((int offset, int length) in places)
        {
            data.Write(BitConverter.GetBytes((uint)offset));
            data.Write(BitConverter.GetBytes((uint)length));
        }
        foreach ((int offset, int length) in places)
            data.Write(stock, offset, length);
        return data.ToArray();
    }

    private static readonly byte[] Stock = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

    // ---- by name, from another firmware's bytes -------------------------------

    private static byte[] Svg(string? title, int length)
    {
        string head = "<svg>" + (title is null ? "" : $"<title>{title}</title>");
        return Encoding.ASCII.GetBytes(head + new string(' ', length - head.Length - 6) + "</svg>");
    }

    /// <summary>A stereo clip with its header: a silent pair, pairs of <paramref name="left"/> and <paramref name="right"/>, an end pair.</summary>
    private static byte[] Clip(string name, int pairs, byte left, byte right, int rate = 48000)
    {
        const int Frame = 16;
        byte[] data = new byte[(pairs + 2) * 2 * Frame];
        for (int p = 1; p <= pairs; p++)
        {
            data.AsSpan(p * 2 * Frame, Frame).Fill(left);
            data.AsSpan(p * 2 * Frame + Frame, Frame).Fill(right);
            data[p * 2 * Frame + 1] = data[p * 2 * Frame + Frame + 1] = 0;
        }
        data[data.Length - 2 * Frame + 1] = data[data.Length - Frame + 1] = 7;
        byte[] header = new byte[0x30];
        "VAGp"u8.CopyTo(header);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x0C), data.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x10), rate);
        header[0x1E] = 2;
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0x20);
        return [.. header, .. data];
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>
    /// The same icons and clips on two firmware: "theirs" (the payload's) and "stock" (this console's), which has another
    /// layout and an untitled icon first; "themed" is stock with a theme's icon and sound.
    /// </summary>
    internal static (byte[] Theirs, byte[] Stock, byte[] Themed) Firmwares()
    {
        byte[] theirs = Join(Svg("g/iconid_home", 80), Svg("g/iconid_power", 90), Clip("psfx_move", 3, 0x11, 0x22));
        byte[] stock = Join(new byte[7], Svg(null, 60), Svg("g/iconid_home", 80), Svg("g/iconid_power", 90), Clip("psfx_move", 3, 0x11, 0x22));
        byte[] themed = Join(new byte[7], Svg(null, 60), Svg(null, 80), Svg("g/iconid_power", 90), Clip("psfx_move", 3, 0x33, 0x33));
        return (theirs, stock, themed);
    }

    /// <summary>A payload of every icon and clip (with its header) of a container.</summary>
    internal static byte[] AllOf(byte[] container)
    {
        var places = RcoSvg.Slots(container).Select(s => (s.Offset, s.Length)).ToList();
        for (int at = 0; (at = container.AsSpan(at).IndexOf("VAGp"u8) is var i && i < 0 ? -1 : at + i) >= 0; at += 4)
            places.Add((at, 0x30 + System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(at + 0x0C))));
        return Payload(container, places.OrderBy(p => p.Item1).ToArray());
    }

    internal static ProsperoThemes.App.Library.StockCatalog Catalog(string logical, byte[] stock) => ProsperoThemes.App.Library.StockCatalog.Parse(
        $$"""{"firmware":"10.00","assets":[],"icon_container":{"path":"{{logical}}","stock_sha256":"{{ThemePack.Hash(stock)}}","icons":3,"untitled":[0]} }""");

    private static readonly System.Collections.Generic.HashSet<int> FirstUntitled = [0];

    [Fact]
    public void A_whole_container_becomes_a_payload_of_its_icons_and_clips()
    {
        (byte[] theirs, byte[] stock, byte[] themed) = Firmwares();

        Assert.Equal(AllOf(theirs), RcoStock.FromContainer(theirs));
        Assert.Equal(new RcoStock.ByName(1, 1, 0, 0), RcoStock.PutBackByName(RcoStock.FromContainer(theirs), themed, FirstUntitled, 3));
        Assert.Equal(stock, themed);
        Assert.Throws<ThemePackException>(() => RcoStock.FromContainer(new byte[100]));
    }

    [Fact]
    public void By_name_a_stock_clip_with_both_channels_alike_is_not_counted_when_it_is_already_there()
    {
        byte[] theirs = Join(Svg("g/iconid_home", 80), Clip("psfx_move", 3, 0x44, 0x44));
        byte[] stock = Join(new byte[7], Svg(null, 60), Svg("g/iconid_home", 80), Clip("psfx_move", 3, 0x44, 0x44));
        byte[] container = (byte[])stock.Clone();

        Assert.Equal(new RcoStock.ByName(), RcoStock.PutBackByName(RcoStock.FromContainer(theirs), container, FirstUntitled, 2));
        Assert.Equal(stock, container);
    }

    [Fact]
    public void By_name_a_lost_title_comes_back_by_its_length_and_a_theme_sound_by_its_name()
    {
        (byte[] theirs, byte[] stock, byte[] themed) = Firmwares();

        RcoStock.ByName done = RcoStock.PutBackByName(AllOf(theirs), themed, FirstUntitled, 3);

        Assert.Equal(new RcoStock.ByName(1, 1, 0, 0), done);
        Assert.Equal(stock, themed);
    }

    [Fact]
    public void By_name_a_stock_untitled_icon_and_a_stereo_clip_are_left_alone()
    {
        (byte[] theirs, byte[] stock, _) = Firmwares();
        byte[] container = (byte[])stock.Clone();

        Assert.Equal(new RcoStock.ByName(), RcoStock.PutBackByName(AllOf(theirs), container, FirstUntitled, 3));
        Assert.Equal(stock, container);
    }

    [Fact]
    public void By_name_no_icon_is_touched_without_the_list_or_with_another_number_of_icons()
    {
        (byte[] theirs, _, byte[] themed) = Firmwares();
        foreach ((System.Collections.Generic.HashSet<int>? list, int icons) in new[] { (null, 3), (FirstUntitled, 4) })
        {
            byte[] container = (byte[])themed.Clone();
            RcoStock.ByName done = RcoStock.PutBackByName(AllOf(theirs), container, list, icons);
            Assert.Equal(0, done.Icons);
            Assert.Equal(1, done.Sounds);
            Assert.Null(RcoSvg.List(container)[1].Title);
        }
    }

    [Fact]
    public void By_name_an_icon_named_by_position_comes_back_only_from_a_themes_padded_icon()
    {
        var alias = RcoSvg.Aliases["iconid_power_off"];
        byte[] original = new byte[alias.Length];
        // The measured icon's hash cannot be made up here, so the rule is seen from the other side: without it, nothing moves.
        byte[][] Fill(byte[] at) => Enumerable.Range(0, alias.Index + 1).Select(i => i == alias.Index ? at : Svg(null, 40)).ToArray();
        byte[] padded = Svg(null, alias.Length);
        byte[] themed = Join(Fill(padded));
        byte[] before = (byte[])themed.Clone();

        RcoStock.ByName done = RcoStock.PutBackByName(Payload(Join(Fill(padded)), (0, 40)), themed, Enumerable.Range(0, alias.Index + 1).ToHashSet(), alias.Index + 1);

        Assert.Equal(new RcoStock.ByName(0, 0, 1, 0), done);
        Assert.Equal(before, themed);
    }

    [Fact]
    public void By_name_two_icons_of_one_length_stay_rather_than_be_swapped()
    {
        byte[] theirs = Join(Svg("g/iconid_a", 80), Svg("g/iconid_b", 80));
        byte[] themed = Join(Svg(null, 80), Svg(null, 80));
        byte[] before = (byte[])themed.Clone();

        Assert.Equal(new RcoStock.ByName(0, 0, 2, 0), RcoStock.PutBackByName(AllOf(theirs), themed, new System.Collections.Generic.HashSet<int>(), 2));
        Assert.Equal(before, themed);
    }

    [Fact]
    public void By_name_a_clip_at_another_rate_stays_and_is_counted()
    {
        byte[] theirs = Clip("psfx_move", 3, 0x11, 0x22, rate: 24000);
        byte[] themed = Clip("psfx_move", 3, 0x33, 0x33);
        byte[] before = (byte[])themed.Clone();

        Assert.Equal(new RcoStock.ByName(0, 0, 0, 1), RcoStock.PutBackByName(AllOf(theirs), themed, null, 0));
        Assert.Equal(before, themed);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(1)]
    public void A_clip_of_another_length_keeps_its_ends_and_stops_where_it_should(int pairs)
    {
        byte[] whole = Clip("c", 3, 0x11, 0x22);
        byte[] frames = whole[0x30..];
        int length = (pairs + 2) * 32;

        byte[] fitted = RcoVag.FitStereo(frames, length);

        Assert.Equal(length, fitted.Length);
        Assert.Equal(frames[^32..], fitted[^32..]);
        Assert.Equal(frames[..32], fitted[..32]);
        Assert.Equal(1, fitted[length - 64 + 1]);   // the last frames before the end pair end the sound
        Assert.Equal(1, fitted[length - 48 + 1]);
    }

    [Fact]
    public void A_clip_of_the_same_length_is_taken_as_it_is()
    {
        byte[] frames = Clip("c", 3, 0x11, 0x22)[0x30..];
        Assert.Equal(frames, RcoVag.FitStereo(frames, frames.Length));
    }

    [Fact]
    public void The_summary_names_the_original_icons_and_sounds()
    {
        ThemePack pack = ThemePack.OpenFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "stock-layer.p5theme"));
        var summary = ProsperoThemes.App.Library.PackSummary.Of(pack.Manifest);
        Assert.True(summary.Originals);
        Assert.Equal(0, summary.Other);
        Assert.Equal("all system icons and sounds", summary.Text);
    }

    [Fact]
    public void Write_puts_the_bytes_at_their_places_and_returns_what_undoes_it()
    {
        byte[] themed = (byte[])Stock.Clone();
        themed[5] = 0xAA;
        themed[40] = 0xBB;
        byte[] before = (byte[])themed.Clone();

        byte[] undo = RcoStock.Write(Payload(Stock, (4, 4), (40, 8)), themed);
        Assert.Equal(Stock, themed);

        RcoStock.Write(undo, themed);
        Assert.Equal(before, themed);
    }

    [Fact]
    public void A_place_past_the_container_writes_nothing()
    {
        byte[] container = new byte[16];
        Assert.Throws<ThemePackException>(() => RcoStock.Write(Payload(Stock, (0, 4), (20, 4)), container));
        Assert.Equal(new byte[16], container);
    }

    public static IEnumerable<object[]> BadPayloads()
    {
        byte[] good = Payload(Stock, (0, 4), (8, 4));
        yield return [Array.Empty<byte>()];
        yield return [Encoding.ASCII.GetBytes("<svg></svg>")];
        yield return [good[..^1]];                                  // a byte short
        yield return [good.Append((byte)0).ToArray()];              // a byte over
        byte[] huge = (byte[])good.Clone();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(huge, 8);       // a count the payload cannot hold
        yield return [huge];
        yield return [Payload(Stock, (8, 4), (0, 4))];              // out of order
        yield return [Payload(Stock, (0, 8), (4, 4))];              // overlapping
        yield return [Payload(Stock, (0, 0))];                      // empty
        byte[] far = Payload(Stock, (0, 4));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(far, 12);       // past anything a container can be
        yield return [far];
    }

    [Theory]
    [MemberData(nameof(BadPayloads))]
    public void A_bad_payload_is_refused(byte[] payload) =>
        Assert.Throws<ThemePackException>(() => RcoStock.Check(payload));

    [Fact]
    public void The_fixture_validates_and_older_layers_are_unaffected()
    {
        ThemePack pack = ThemePack.OpenFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "stock-layer.p5theme"));
        Assert.False(pack.Validate().HasErrors, string.Join("; ", pack.Validate().Errors.Select(e => e.Message)));
        ThemePackTarget target = Assert.Single(pack.Manifest.Targets);
        Assert.Equal(ThemePack.StockLayer, target.Layer);
        Assert.True(ThemePack.IsPatchLayer(target.Layer));

        byte[] container = new byte[32];
        RcoStock.Write(pack.Payload(target), container);
        Assert.Equal(new byte[] { 4, 5, 6, 7, 8, 9, 10, 11 }, container[4..12]);
        Assert.Equal(new byte[] { 20, 21, 22, 23 }, container[20..24]);
    }

    private static ThemePack Pack(string resource, byte[] payload) => ThemePack.Open(new ZipBuilder()
        .AddText("theme.json", $$"""
            {"format":2,"id":"t","name":"T","author":"a","version":"1.0","unmapped":"keep","targets":[
             {"path":"vsh_asset/Sce.PlayStation.PUI_UI3.rco","layer":"stock","resource":"{{resource}}","file":"o.bin",
              "bytes":{{payload.Length}},"sha256":"{{ThemePack.Hash(payload)}}"}]}
            """)
        .Add("o.bin", payload).ToArray());

    [Theory]
    [InlineData("")]
    [InlineData("iconid_settings")]
    public void The_resource_has_to_be_a_hash(string resource) =>
        Assert.True(Pack(resource, Payload(Stock, (0, 4))).Validate().HasErrors);

    [Fact]
    public void A_broken_payload_fails_validation()
    {
        PackReport report = Pack(new string('a', 64), [1, 2, 3]).Validate();
        Assert.Contains(report.Errors, e => e.Message.Contains("not a list of original bytes"));
    }
}
