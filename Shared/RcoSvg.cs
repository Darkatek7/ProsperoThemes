using System;
using System.Text;
using System.Text.RegularExpressions;

namespace ProsperoThemes.Shared;

/// <summary>
/// Size-preserving replacement of named SVG icons inside an RCO container (proven on FW 13.00 with
/// <c>Sce.PlayStation.PUI_UI3.rco</c>: the shell draws the replacement after a restart).
/// </summary>
/// <remarks>
/// The container keeps 1,089 icons as plain SVG text; most carry their own name in a
/// <c>&lt;title&gt;group/iconid_name&lt;/title&gt;</c>. A replacement is minified and padded with spaces before
/// <c>&lt;/svg&gt;</c> to exactly the original's length, so no offset or size field in the container moves.
/// Mirrors <c>RcoProbe/rco_ui3.py</c>.
/// </remarks>
public static class RcoSvg
{
    private static readonly byte[] Open = "<svg"u8.ToArray();
    private static readonly byte[] Close = "</svg>"u8.ToArray();
    private static readonly Regex Title = new("<title>([^<]+)</title>", RegexOptions.CultureInvariant);

    /// <summary>Finds the icon whose title is <paramref name="name"/> or ends in <c>/name</c>.</summary>
    /// <returns>Offset and length of the whole <c>&lt;svg&gt;…&lt;/svg&gt;</c> element.</returns>
    /// <exception cref="ThemePackException">No icon, or more than one, has that name.</exception>
    public static (int Offset, int Length) Find(ReadOnlySpan<byte> container, string name)
    {
        // A few icons carry no <title>; they are named by alias or by their position ("#875").
        if (Aliases.TryGetValue(name, out string? alias)) name = alias;
        if (name.Length > 1 && name[0] == '#' && int.TryParse(name.AsSpan(1), out int index))
            return Nth(container, index);
        (int Offset, int Length) hit = default;
        int matches = 0, i = 0;
        while ((i = IndexOf(container, Open, i)) >= 0)
        {
            int end = IndexOf(container, Close, i);
            if (end < 0) break;
            end += Close.Length;
            Match m = Title.Match(Encoding.UTF8.GetString(container[i..end]));
            if (m.Success)
            {
                string title = m.Groups[1].Value;
                if (title == name || title.EndsWith("/" + name, StringComparison.Ordinal))
                {
                    hit = (i, end - i);
                    matches++;
                }
            }
            i = end;
        }
        if (matches != 1)
            throw new ThemePackException($"icon '{name}' matches {matches} entries in the container");
        return hit;
    }

    /// <summary>
    /// Untitled icons by name (FW 13.00, <c>Sce.PlayStation.PUI_UI3.rco</c>): the top-bar search magnifier is the
    /// 876th SVG in the container and has no <c>&lt;title&gt;</c>. Proven on hardware (recoloured red).
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["iconid_search"] = "#875",
    };

    private static (int Offset, int Length) Nth(ReadOnlySpan<byte> container, int index)
    {
        int n = 0, i = 0;
        while ((i = IndexOf(container, Open, i)) >= 0)
        {
            int end = IndexOf(container, Close, i);
            if (end < 0) break;
            end += Close.Length;
            if (n++ == index) return (i, end - i);
            i = end;
        }
        throw new ThemePackException($"icon #{index} is past the last of {n} icons in the container");
    }

    /// <summary>Minifies <paramref name="svg"/> and pads it to exactly <paramref name="length"/> bytes.</summary>
    /// <exception cref="ThemePackException">It is not an SVG, or it does not fit.</exception>
    public static byte[] Fit(ReadOnlySpan<byte> svg, int length)
    {
        string text = Encoding.UTF8.GetString(svg).Trim();
        text = Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @">\s+<", "><");
        if (!text.StartsWith("<svg", StringComparison.Ordinal) || !text.EndsWith("</svg>", StringComparison.Ordinal))
            throw new ThemePackException("replacement is not a single <svg> element");
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > length)
            throw new ThemePackException($"replacement is {bytes.Length} bytes, the icon slot holds {length}");
        byte[] fitted = new byte[length];
        int body = bytes.Length - Close.Length;
        bytes.AsSpan(0, body).CopyTo(fitted);
        fitted.AsSpan(body, length - bytes.Length).Fill((byte)' ');
        Close.CopyTo(fitted, length - Close.Length);
        return fitted;
    }

    /// <summary>Replaces the icon <paramref name="name"/> in place.</summary>
    public static void Replace(byte[] container, string name, ReadOnlySpan<byte> svg)
    {
        (int offset, int length) = Find(container, name);
        Fit(svg, length).CopyTo(container, offset);
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int from)
    {
        if (from >= haystack.Length) return -1;
        int at = haystack[from..].IndexOf(needle);
        return at < 0 ? -1 : from + at;
    }
}
