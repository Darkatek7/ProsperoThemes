using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class StoreAddressEditorTests
{
    // Moves the focus onto a key and presses it. Letters live in rows 0-3, the wide keys in the last row.
    private static void Key(StoreAddressEditor editor, string label)
    {
        for (int row = 0; row < StoreAddressEditor.Layout.Count; row++)
            for (int column = 0; column < StoreAddressEditor.Layout[row].Count; column++)
            {
                if (StoreAddressEditor.Layout[row][column].Label != label) continue;
                while (editor.Row != row) editor.Move(0, Math.Sign(row - editor.Row));
                while (editor.Column != column) editor.Move(Math.Sign(column - editor.Column), 0);
                editor.Press();
                return;
            }
        throw new ArgumentException("No key " + label);
    }

    private static void Type(StoreAddressEditor editor, string text)
    {
        foreach (char c in text) Key(editor, c.ToString());
    }

    [Fact]
    public void Typing_builds_an_address_from_single_and_wide_keys()
    {
        var editor = new StoreAddressEditor();
        Key(editor, "http://");
        Type(editor, "10.0.0.22");
        Key(editor, ":8080");
        Assert.Equal("http://10.0.0.22:8080", editor.Text);
        editor.Clear();
        Key(editor, "https://");
        Type(editor, "my-store");
        Assert.Equal("https://my-store", editor.Text);
    }

    [Fact]
    public void Every_character_of_a_normal_address_can_be_typed()
    {
        var editor = new StoreAddressEditor();
        const string address = "https://themes.example-store.de:8443/api-v2/0123456789";
        Key(editor, "https://");
        Type(editor, address["https://".Length..]);
        Assert.Equal(address, editor.Text);
    }

    [Fact]
    public void Backspace_removes_the_last_character_and_is_harmless_when_empty()
    {
        var editor = new StoreAddressEditor("http://ab");
        editor.Backspace();
        Assert.Equal("http://a", editor.Text);
        var empty = new StoreAddressEditor();
        empty.Backspace();
        Assert.Equal("", empty.Text);
    }

    [Fact]
    public void The_clear_key_empties_the_text()
    {
        var editor = new StoreAddressEditor("http://x.de");
        Key(editor, "Clear all");
        Assert.Equal("", editor.Text);
        editor.Clear();
        Assert.Equal("", editor.Text);
    }

    [Fact]
    public void Text_never_grows_past_the_maximum()
    {
        var editor = new StoreAddressEditor(new string('a', StoreAddressEditor.MaxLength - 2));
        editor.Move(0, 10);                       // bottom row
        editor.Move(-10, 0);                      // "http://" is 7 characters: refused whole
        Assert.False(editor.Press());
        Assert.Equal(StoreAddressEditor.MaxLength - 2, editor.Text.Length);
        Key(editor, "1");
        Key(editor, "2");
        Assert.Equal(StoreAddressEditor.MaxLength, editor.Text.Length);
        Key(editor, "3");
        Assert.Equal(StoreAddressEditor.MaxLength, editor.Text.Length);
        Assert.EndsWith("12", editor.Text);
        Assert.Equal(StoreAddressEditor.MaxLength, new StoreAddressEditor(new string('b', 500)).Text.Length);
    }

    [Fact]
    public void Focus_starts_on_http_for_an_empty_address_and_on_the_digits_otherwise()
    {
        Assert.Equal("http://", new StoreAddressEditor().Focused.Label);
        Assert.Equal("1", new StoreAddressEditor("http://a").Focused.Label);
    }

    [Fact]
    public void Focus_stops_at_the_edges_of_the_keyboard()
    {
        var editor = new StoreAddressEditor("x");
        editor.Move(-1, 0);
        editor.Move(0, -1);
        Assert.Equal((0, 0), (editor.Row, editor.Column));
        editor.Move(50, 0);
        Assert.Equal((0, 9), (editor.Row, editor.Column));
        editor.Move(0, 50);
        Assert.Equal(StoreAddressEditor.Layout.Count - 1, editor.Row);
        editor.Move(50, 0);
        Assert.Equal(StoreAddressEditor.Layout[^1].Count - 1, editor.Column);
        Assert.True(editor.Focused.IsClear);
    }

    [Fact]
    public void Moving_down_into_the_wide_row_lands_on_the_key_under_the_spot_and_up_returns_there()
    {
        var editor = new StoreAddressEditor("x");
        editor.Move(7, 0);                       // column 7 of the digits
        editor.Move(0, 4);                       // last row: wide keys cover two columns each
        Assert.Equal("Default", editor.Focused.Label);
        editor.Move(0, -1);
        Assert.Equal((3, 7), (editor.Row, editor.Column));
        editor.Move(2, 0);                       // column 9 -> the clear key
        editor.Move(0, 1);
        Assert.True(editor.Focused.IsClear);
        editor.Move(-1, 0);
        editor.Move(0, -1);
        Assert.Equal((3, 6), (editor.Row, editor.Column));   // left edge of the "Default" key
    }

    [Fact]
    public void Default_key_puts_the_public_store_in_place_of_the_address()
    {
        var editor = new StoreAddressEditor("http://10.0.0.22:8080") { Notice = "Error" };
        Key(editor, "Default");
        Assert.Equal(AppSettings.DefaultStoreUrl, editor.Text);
        Assert.Equal("", editor.Notice);
        Assert.Null(AppSettings.StoreUrlProblem(editor.Text));
    }

    [Fact]
    public void A_notice_stays_until_the_text_changes()
    {
        var editor = new StoreAddressEditor("http://x") { Notice = "Error" };
        editor.Move(1, 1);
        Assert.Equal("Error", editor.Notice);
        editor.Press();
        Assert.Equal("", editor.Notice);
        editor.Notice = "Error";
        editor.Backspace();
        Assert.Equal("", editor.Notice);
    }

    [Fact]
    public void Layout_rows_fill_the_ten_grid_columns_and_keys_have_no_whitespace()
    {
        foreach (var row in StoreAddressEditor.Layout)
        {
            Assert.Equal(StoreAddressEditor.Columns, row.Sum(k => k.Width));
            foreach (var key in row)
            {
                Assert.NotEqual("", key.Label);
                if (key.Text is { } text) Assert.All(text, c => Assert.False(char.IsWhiteSpace(c)));
            }
        }
    }
}
