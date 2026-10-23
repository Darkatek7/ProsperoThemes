using System;
using System.Collections.Generic;
using ProsperoThemes.App.Library;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// One key of the on-screen keyboard. <see cref="Text"/> is what it types; a key without text clears everything,
/// and a <see cref="Replaces"/> key puts its text in place of the whole address. <see cref="Width"/> counts grid
/// columns, so a wide key sits over two narrow ones.
/// </summary>
public readonly record struct EditorKey(string Label, string? Text, int Width = 1, bool Replaces = false)
{
    public bool IsClear => Text is null;
}

/// <summary>
/// The text and keyboard focus behind the store-address screen, and behind the Community search
/// (<see cref="ForSearch"/>, with its own keys). Plain state with no drawing or I/O, like <see cref="ThemeBrowser"/>, so
/// the PC tests cover it. Lowercase only: store addresses need no capitals, and the store's search ignores case.
/// </summary>
public sealed class StoreAddressEditor
{
    public const int MaxLength = 200;
    public const int SearchMaxLength = 60;
    public const int Columns = 10;

    /// <summary>Rows of keys from the top; the last one is made of wide keys.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<EditorKey>> Layout =
    [
        Characters("1234567890"),
        Characters("abcdefghij"),
        Characters("klmnopqrst"),
        Characters("uvwxyz.:/-"),
        [new("http://", "http://", 2), new("https://", "https://", 2), new(":8080", ":8080", 2), new("Default", AppSettings.DefaultStoreUrl, 2, Replaces: true), new("Clear all", null, 2)],
    ];

    /// <summary>The keys of the search: digits, letters, a few signs that appear in theme names, and a wide space bar.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<EditorKey>> SearchLayout =
    [
        Characters("1234567890"),
        Characters("abcdefghij"),
        Characters("klmnopqrst"),
        Characters("uvwxyz-'&."),
        [new("Space", " ", 6), new("Clear all", null, 4)],
    ];

    private int _preferred;   // grid column vertical moves aim for, so up and down return to where they began

    /// <summary>Focus starts on "http://" for an empty address, since that is what comes first; otherwise on the digits.</summary>
    public StoreAddressEditor(string text = "") : this(text, Layout, MaxLength)
    {
        if (Text.Length == 0) Row = Layout.Count - 1;
    }

    private StoreAddressEditor(string text, IReadOnlyList<IReadOnlyList<EditorKey>> keys, int limit)
    {
        Keys = keys;
        Limit = limit;
        Text = text.Length > limit ? text[..limit] : text;
    }

    /// <summary>The keyboard of the Community search, with <paramref name="text"/>; the focus starts on the letters.</summary>
    public static StoreAddressEditor ForSearch(string text = "") => new(text, SearchLayout, SearchMaxLength) { Row = 1 };

    /// <summary>The rows of keys this keyboard shows: <see cref="Layout"/>, or <see cref="SearchLayout"/> for the search.</summary>
    public IReadOnlyList<IReadOnlyList<EditorKey>> Keys { get; }
    /// <summary>The longest text it takes.</summary>
    public int Limit { get; }
    public bool IsSearch => ReferenceEquals(Keys, SearchLayout);
    public string Text { get; private set; }
    public int Row { get; private set; }
    public int Column { get; private set; }
    public EditorKey Focused => Keys[Row][Column];

    /// <summary>A message for the person typing (why the last save failed); gone as soon as the text changes.</summary>
    public string Notice { get; set; } = "";

    /// <summary>Moves the focus by whole keys; it stops at the edges, and up/down land on the key above or below the same spot.</summary>
    public void Move(int dx, int dy)
    {
        if (dx != 0)
        {
            Column = Math.Clamp(Column + dx, 0, Keys[Row].Count - 1);
            _preferred = StartOf(Keys[Row], Column);
        }
        if (dy != 0)
        {
            Row = Math.Clamp(Row + dy, 0, Keys.Count - 1);
            Column = KeyAt(Keys[Row], _preferred);
        }
    }

    /// <summary>Grid column where a key of the store-address keyboard begins.</summary>
    public static int StartColumn(int row, int column) => StartOf(Layout[row], column);

    private static int StartOf(IReadOnlyList<EditorKey> row, int column)
    {
        int start = 0;
        for (int i = 0; i < column; i++) start += row[i].Width;
        return start;
    }

    private static int KeyAt(IReadOnlyList<EditorKey> row, int gridColumn)
    {
        int start = 0;
        for (int i = 0; i < row.Count; i++)
        {
            start += row[i].Width;
            if (gridColumn < start) return i;
        }
        return row.Count - 1;
    }

    /// <summary>Types the focused key (or clears everything). Returns false when the text is full.</summary>
    public bool Press()
    {
        EditorKey key = Focused;
        if (key.IsClear) { Clear(); return true; }
        if (key.Replaces)
        {
            Text = key.Text!.Length > Limit ? key.Text[..Limit] : key.Text;
            Notice = "";
            return true;
        }
        if (Text.Length + key.Text!.Length > Limit) return false;
        Text += key.Text;
        Notice = "";
        return true;
    }

    public void Backspace()
    {
        if (Text.Length == 0) return;
        Text = Text[..^1];
        Notice = "";
    }

    public void Clear()
    {
        Text = "";
        Notice = "";
    }

    private static EditorKey[] Characters(string chars)
    {
        var keys = new EditorKey[chars.Length];
        for (int i = 0; i < chars.Length; i++) keys[i] = new EditorKey(chars[i].ToString(), chars[i].ToString());
        return keys;
    }
}
