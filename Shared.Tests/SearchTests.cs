using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;
using SharpProspero.Graphics;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The searches of the app: the Avatars tab's (the store's <c>q=</c>, like the Themes tab's) and the Library's (a local filter of
/// the user's own collection), the keyboard they share, and the footer hints that point to them.
/// </summary>
public class SearchTests
{
    private static UiInput Input(bool right = false, bool left = false, bool confirm = false, bool cancel = false, bool up = false, bool down = false) =>
        new(up, down, left, right, confirm, cancel);

    private static readonly UiInput X = Input(confirm: true), O = Input(cancel: true), Up = Input(up: true), Down = Input(down: true),
        Left = Input(left: true), Right = Input(right: true);
    private static readonly UiInput R1 = UiInput.None with { PageDown = true }, L1 = UiInput.None with { PageUp = true };

    private static void Press(ThemeBrowser browser, UiInput input, int times = 1)
    {
        for (int i = 0; i < times; i++) browser.Update(input);
    }

    // Types text on the search keyboard the way a pad would: moves to each key and presses X.
    private static void Type(ThemeBrowser browser, string text)
    {
        foreach (char ch in text)
        {
            var keys = browser.SearchEditor.Keys;
            int row = -1, column = -1;
            for (int r = 0; r < keys.Count && row < 0; r++)
                for (int c = 0; c < keys[r].Count; c++)
                    if (keys[r][c].Text == ch.ToString()) { row = r; column = c; break; }
            Assert.True(row >= 0, $"no key types '{ch}'");
            while (browser.SearchEditor.Row != row) browser.Update(browser.SearchEditor.Row < row ? Down : Up);
            while (browser.SearchEditor.Column != column) browser.Update(browser.SearchEditor.Column < column ? Right : Left);
            browser.Update(X);
        }
    }

    // ---- Avatars tab -----------------------------------------------------------------------------------------------------

    private static ThemeBrowser OnAvatarTab(int cards)
    {
        var browser = new ThemeBrowser();
        browser.SetCommunityCount(1);
        browser.Update(R1);
        browser.Update(R1);
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
        browser.SetAvatarStoreCount(cards);
        return browser;
    }

    [Fact]
    public void Up_from_the_top_row_of_the_avatar_grid_reaches_the_search_chip_and_Down_comes_back()
    {
        var browser = OnAvatarTab(14);
        Press(browser, Down);                                        // second row
        Assert.Equal(ThemeBrowser.AvatarStoreColumns, browser.AvatarStoreSelected);
        browser.Update(Up);                                          // back to the first row, not onto the chip
        Assert.False(browser.AvatarSearchFocused);
        Assert.Equal(0, browser.AvatarStoreSelected);
        browser.Update(Right);
        browser.Update(Up);
        Assert.True(browser.AvatarSearchFocused);
        Assert.Equal(1, browser.AvatarStoreSelected);                // the cursor keeps its card
        Press(browser, Right, 3);                                    // nothing moves while the chip has the focus
        Assert.Equal(1, browser.AvatarStoreSelected);
        browser.Update(Down);
        Assert.False(browser.AvatarSearchFocused);
        Assert.Equal(1, browser.AvatarStoreSelected);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void The_avatar_search_opens_the_shared_keyboard_and_Options_asks_the_store_for_the_term()
    {
        var browser = OnAvatarTab(10);
        browser.Update(Up);
        browser.Update(X);
        Assert.Equal(ThemeView.Search, browser.View);
        Assert.Equal(ThemeView.Avatars, browser.SearchOrigin);
        Assert.Equal(ThemeAction.None, browser.TakeAction());        // the X only opened the keyboard
        Type(browser, "house");
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.Equal("house", browser.AvatarStoreQuery);
        Assert.Equal(0, browser.AvatarStoreSelected);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
        Assert.True(browser.AvatarSearchFocused);
        Assert.Equal("", browser.CommunityQuery);                    // the Themes tab's term is its own
        Assert.Equal("", browser.LibraryQuery);

        // X on the chip with the term takes it off and asks for the full list again.
        browser.Update(X);
        Assert.Equal("", browser.AvatarStoreQuery);
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
    }

    [Fact]
    public void O_leaves_the_avatar_keyboard_without_searching_and_an_unchanged_term_asks_nothing()
    {
        var browser = OnAvatarTab(4);
        browser.Update(Up);
        browser.Update(X);
        Type(browser, "abc");
        browser.Update(O);
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.Equal("", browser.AvatarStoreQuery);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X);                                           // the keyboard again (the chip still has the focus), empty
        Assert.Equal("", browser.SearchEditor.Text);
        browser.Update(UiInput.None, options: true);                 // searching for nothing when nothing was searched
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void The_avatar_search_chip_is_reachable_with_an_empty_list_and_Square_still_sorts_from_it()
    {
        var browser = OnAvatarTab(0);
        browser.Update(Up);
        Assert.True(browser.AvatarSearchFocused);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(1, browser.AvatarStoreSort);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
        // A search that found nothing leaves the chip as the way to the full list.
        browser.Update(X);
        Type(browser, "zzz");
        browser.Update(UiInput.None, options: true);
        browser.TakeAction();
        Assert.Equal("zzz", browser.AvatarStoreQuery);
        Assert.Equal(-1, browser.AvatarStoreSelected);
        browser.Update(X);
        Assert.Equal("", browser.AvatarStoreQuery);
    }

    [Fact]
    public void Changing_tabs_or_leaving_puts_the_search_chip_focus_down()
    {
        var browser = OnAvatarTab(3);
        browser.Update(Up);
        Assert.True(browser.AvatarSearchFocused);
        browser.Update(R1);
        Assert.Equal(ThemeView.Import, browser.View);
        browser.Update(L1);
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.False(browser.AvatarSearchFocused);
        browser.Update(Up);
        browser.Update(O);                                           // Circle leaves the tab for the Library like on every tab
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.False(browser.AvatarSearchFocused);
    }

    [Fact]
    public void The_avatar_request_carries_the_term_escaped()
    {
        string url = AvatarStore.ListUrl("http://store.example/", CommunitySort.Downloads, pageSize: 36, query: "red house & co");
        Assert.Equal("http://store.example/api/avatars?sort=downloads&page=1&pageSize=36&q=red%20house%20%26%20co", url);
        Assert.DoesNotContain("q=", AvatarStore.ListUrl("http://store.example", CommunitySort.Top, pageSize: 36));
        Assert.DoesNotContain("q=", AvatarStore.ListUrl("http://store.example", CommunitySort.Top, query: ""));
    }

    // ---- Library -------------------------------------------------------------------------------------------------------------

    private static ThemeBrowser OnLibrary(int shown, int total = -1)
    {
        var browser = new ThemeBrowser();
        browser.SetCount(shown, total);
        browser.SetAvatarCount(1);
        return browser;
    }

    [Fact]
    public void Up_from_the_packs_reaches_the_library_search_chip_and_Down_comes_back()
    {
        var browser = OnLibrary(3);
        Press(browser, Right);
        browser.Update(Up);
        Assert.True(browser.LibrarySearchFocused);
        Assert.Equal(1, browser.Selected);
        Assert.False(browser.AvatarRowFocused);
        Press(browser, Right, 2);                                    // the cards do not move while the chip has the focus
        Assert.Equal(1, browser.Selected);
        browser.Update(UiInput.None, preview: true);                 // and Square does not open a preview
        Assert.Equal(ThemeView.Library, browser.View);
        browser.Update(Down);
        Assert.False(browser.LibrarySearchFocused);
        browser.Update(Down);
        Assert.True(browser.AvatarRowFocused);                       // a second Down goes on to the avatars as before
        browser.Update(Up);
        browser.Update(Up);
        Assert.True(browser.LibrarySearchFocused);
    }

    [Fact]
    public void An_empty_library_has_nothing_to_search_but_an_empty_result_keeps_the_chip()
    {
        var empty = OnLibrary(0);
        empty.Update(Up);
        Assert.False(empty.LibrarySearchFocused);
        Assert.False(empty.LibrarySearchAvailable);

        var none = OnLibrary(0, total: 5);                           // five packs, none matches
        Assert.True(none.LibrarySearchAvailable);
        none.Update(Up);
        Assert.True(none.LibrarySearchFocused);
        Assert.Equal(-1, none.Selected);
        none.Update(Down);
        Assert.False(none.LibrarySearchFocused);
        none.Update(Down);
        Assert.True(none.AvatarRowFocused);
    }

    [Fact]
    public void The_library_search_types_on_the_shared_keyboard_and_asks_the_app_to_filter()
    {
        var browser = OnLibrary(5);
        browser.Update(Up);
        browser.Update(X);
        Assert.Equal(ThemeView.Search, browser.View);
        Assert.Equal(ThemeView.Library, browser.SearchOrigin);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Type(browser, "kan");
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.Equal("kan", browser.LibraryQuery);
        Assert.Equal(ThemeAction.LibraryFilter, browser.TakeAction());
        Assert.True(browser.LibrarySearchFocused);
        Assert.Equal("", browser.CommunityQuery);
        Assert.Equal("", browser.AvatarStoreQuery);

        // The app passes the filtered count on: two of five.
        browser.SetCount(2, 5);
        Assert.Equal(5, browser.LibraryTotal);

        // X on the chip with a term takes it off.
        browser.Update(X);
        Assert.Equal("", browser.LibraryQuery);
        Assert.Equal(ThemeAction.LibraryFilter, browser.TakeAction());
        Assert.Equal(ThemeView.Library, browser.View);
    }

    [Fact]
    public void O_on_the_library_keyboard_goes_back_without_a_filter_and_O_on_the_chip_goes_back_to_the_packs()
    {
        var browser = OnLibrary(3);
        browser.Update(Up);
        browser.Update(X);
        Type(browser, "zelda");
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.Equal("", browser.LibraryQuery);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.True(browser.LibrarySearchFocused);
        browser.Update(O);                                           // Circle on the chip: back onto the packs
        Assert.False(browser.LibrarySearchFocused);
        Assert.Equal(ThemeView.Library, browser.View);
    }

    [Fact]
    public void Triangle_and_Options_work_from_the_library_chip()
    {
        var browser = OnLibrary(3);
        browser.Update(Up);
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(ThemeAction.ToggleLibrarySort, browser.TakeAction());
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.Reload, browser.TakeAction());
    }

    [Fact]
    public void The_save_look_card_is_left_out_while_a_search_narrows_the_library()
    {
        var browser = OnLibrary(2, total: 4);
        browser.LookSavable = true;
        Assert.True(browser.LookCardShown);
        browser.Update(Up);
        browser.Update(X);
        Type(browser, "a");
        browser.Update(UiInput.None, options: true);
        browser.TakeAction();
        Assert.False(browser.LookCardShown);
        browser.Update(Down);
        browser.Update(UiInput.None with { End = true });
        Assert.False(browser.LookCardFocused);                       // the row ends at the last match
        Assert.Equal(1, browser.Selected);
        browser.Update(Up);
        browser.Update(X);                                           // the term comes off
        browser.TakeAction();
        browser.SetCount(4, 4);
        Assert.True(browser.LookCardShown);
        browser.Update(Down);
        browser.Update(UiInput.None with { End = true });
        Assert.True(browser.LookCardFocused);                        // the card is back and works as before
        browser.Update(X);
        Assert.Equal(ThemeAction.SaveLook, browser.TakeAction());
    }

    [Fact]
    public void Details_and_preview_of_a_filtered_pack_open_the_pack_at_the_selected_index()
    {
        var browser = OnLibrary(2, total: 5);
        browser.SetCount(2, 5);
        browser.Update(Right);
        Assert.Equal(1, browser.Selected);
        browser.Update(X);
        Assert.Equal(ThemeView.Detail, browser.View);
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Preview, browser.View);
    }

    [Fact]
    public void Switching_tabs_puts_the_library_chip_focus_down()
    {
        var browser = OnLibrary(3);
        browser.Update(Up);
        browser.Update(R1);
        browser.Update(L1);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.False(browser.LibrarySearchFocused);
    }

    [Fact]
    public void ClearLibraryQuery_takes_the_term_off_without_asking_for_anything()
    {
        var browser = OnLibrary(3);
        browser.Update(Up);
        browser.Update(X);
        Type(browser, "a");
        browser.Update(UiInput.None, options: true);
        browser.TakeAction();
        browser.ClearLibraryQuery();
        Assert.Equal("", browser.LibraryQuery);
        Assert.False(browser.LibrarySearchFocused);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void The_search_keyboard_returns_to_the_screen_it_was_opened_from()
    {
        // Themes tab
        var themes = new ThemeBrowser();
        themes.SetCommunityCount(3);
        themes.Update(R1);
        themes.Update(Up);
        Press(themes, Left, 3);
        Assert.Equal(ThemeBrowser.SearchChip, themes.CommunityChip);
        themes.Update(X);
        Assert.Equal(ThemeView.Community, themes.SearchOrigin);
        themes.Update(O);
        Assert.Equal(ThemeView.Community, themes.View);
        // Avatars and Library
        var avatars = OnAvatarTab(2);
        avatars.Update(Up);
        avatars.Update(X);
        Assert.Equal(ThemeView.Avatars, avatars.SearchOrigin);
        avatars.Update(O);
        Assert.Equal(ThemeView.Avatars, avatars.View);
        var library = OnLibrary(2);
        library.Update(Up);
        library.Update(X);
        Assert.Equal(ThemeView.Library, library.SearchOrigin);
        library.Update(O);
        Assert.Equal(ThemeView.Library, library.View);
    }

    // ---- The library filter ----------------------------------------------------------------------------------------------

    private static ThemeLibraryEntry Entry(string name, string author, string description, string path) =>
        new(path, new ThemePackManifest(2, "id-" + path, name, author, "1.0", description, "keep", []), new PackReport(), null);

    private static readonly ThemeLibraryEntry Kanto = Entry("Kanto Nights", "Aoi", "Pokémon region at night, neon and rain", "/lib/a.p5theme");
    private static readonly ThemeLibraryEntry Zelda = Entry("Hyrule Morning", "ZeldaFan", "Rolling hills", "/lib/b.p5theme");
    private static readonly ThemeLibraryEntry Cafe = Entry("Café Müller", "Strauß", "Warm wood and Kaffee", "/lib/c.p5theme");
    private static readonly ThemeLibraryEntry NoText = Entry("Plain", "", "", "/lib/d.p5theme");
    private static readonly ThemeLibraryEntry[] All = [Kanto, Zelda, Cafe, NoText];

    private static string[] Names(IEnumerable<ThemeLibraryEntry> entries) => entries.Select(e => e.Manifest.Name).ToArray();

    [Fact]
    public void The_filter_matches_name_author_and_description_in_the_library_order()
    {
        Assert.Equal(["Kanto Nights"], Names(LibraryFilter.Apply(All, "kanto")));                 // name
        Assert.Equal(["Hyrule Morning"], Names(LibraryFilter.Apply(All, "zeldafan")));            // author
        Assert.Equal(["Kanto Nights"], Names(LibraryFilter.Apply(All, "neon")));                  // description
        Assert.Equal(["Kanto Nights", "Hyrule Morning", "Café Müller"], Names(LibraryFilter.Apply(All, "n")).Take(3).ToArray());
        Assert.Equal(["Kanto Nights", "Café Müller"], Names(LibraryFilter.Apply(All, "a")).Where(n => n != "Hyrule Morning" && n != "Plain").ToArray());
        Assert.Empty(LibraryFilter.Apply(All, "zzz"));
    }

    [Fact]
    public void The_filter_ignores_case_accents_and_word_order()
    {
        Assert.Equal(["Kanto Nights"], Names(LibraryFilter.Apply(All, "KANTO")));
        Assert.Equal(["Kanto Nights"], Names(LibraryFilter.Apply(All, "pokemon")));               // é typed as e
        Assert.Equal(["Café Müller"], Names(LibraryFilter.Apply(All, "cafe muller")));
        Assert.Equal(["Café Müller"], Names(LibraryFilter.Apply(All, "MULLER CAFE")));            // both words, any order
        Assert.Equal(["Café Müller"], Names(LibraryFilter.Apply(All, "strauss")));                // ß as ss
        Assert.Equal(["Kanto Nights"], Names(LibraryFilter.Apply(All, "  night   neon ")));       // spaces around and between words
        Assert.Empty(LibraryFilter.Apply(All, "cafe neon"));                                      // every word has to appear
    }

    [Fact]
    public void An_empty_term_shows_the_whole_library_itself()
    {
        Assert.Same(All, LibraryFilter.Apply(All, ""));
        Assert.Same(All, LibraryFilter.Apply(All, "   "));
        Assert.Same(All, LibraryFilter.Apply(All, null));
        Assert.True(LibraryFilter.Matches(NoText, ""));
        Assert.False(LibraryFilter.Matches(NoText, "x"));
    }

    [Fact]
    public void A_filtered_row_indexed_by_Selected_is_the_pack_the_screen_shows()
    {
        // What the app does: the browser's Selected indexes the shown list, which the renderer and the preview get.
        IReadOnlyList<ThemeLibraryEntry> shown = LibraryFilter.Apply(All, "e");
        Assert.Equal(["Kanto Nights", "Hyrule Morning", "Café Müller"], Names(shown));
        using var screen = new LibraryScreen(shown, All.Length);
        screen.Browser.Update(Right);
        screen.Browser.Update(Right);
        screen.Frame();
        Assert.Same(Cafe, screen.Renderer.DrawnEntry);
        screen.Browser.Update(X);
        Assert.Equal(ThemeView.Detail, screen.Browser.View);
        screen.Frame();
        Assert.Same(Cafe, screen.Renderer.DrawnEntry);

        // The same Selected on another filter is another pack.
        screen.Browser.Update(O);
        screen.Entries = LibraryFilter.Apply(All, "morning");
        screen.Browser.SetCount(screen.Entries.Count, All.Length);
        screen.Frame();
        Assert.Same(Zelda, screen.Renderer.DrawnEntry);
    }

    // ---- Screens -------------------------------------------------------------------------------------------------------------

    private static ThemeRenderer NewRenderer() =>
        new(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), _ => new PixelBuffer(16, 9));

    private sealed class LibraryScreen : IDisposable
    {
        public readonly ThemeRenderer Renderer = NewRenderer();
        public readonly ThemeBrowser Browser = new();
        public readonly PixelBuffer Canvas = new(1920, 1080);
        public IReadOnlyList<ThemeLibraryEntry> Entries;
        private double _now = 5;

        public LibraryScreen(IReadOnlyList<ThemeLibraryEntry> shown, int total)
        {
            Entries = shown;
            Browser.SetCount(shown.Count, total);
            Browser.SetAvatarCount(0);
        }

        public Surface Frame(int frames = 1)
        {
            for (int i = 0; i < frames; i++)
            {
                Renderer.Draw(Canvas.AsSurface(), Browser, Entries, _now, 1.0 / 60);
                _now += 1.0 / 60;
            }
            return Canvas.AsSurface();
        }

        public Surface Settle()
        {
            _now += 2;
            return Frame(90);
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Canvas.Dispose();
        }
    }

    private sealed class AvatarScreen : IDisposable
    {
        public readonly ThemeRenderer Renderer = NewRenderer();
        public readonly ThemeBrowser Browser;
        public readonly PixelBuffer Canvas = new(1920, 1080);
        private double _now = 5;

        public AvatarScreen(int cards)
        {
            Browser = OnAvatarTab(cards);
            Browser.StoreUrl = "http://store.example";
            Renderer.AvatarStore = Enumerable.Range(0, cards).Select(i =>
                new StoreAvatar("a" + i, "House " + i, "Author", "1.0", "", 1000, i, 10 * i, null, "http://x/" + i, null)).ToList();
            Renderer.AvatarStoreListSort = 0;
        }

        public Surface Frame(int frames = 1)
        {
            for (int i = 0; i < frames; i++)
            {
                Renderer.Draw(Canvas.AsSurface(), Browser, Array.Empty<ThemeLibraryEntry>(), _now, 1.0 / 60);
                _now += 1.0 / 60;
            }
            return Canvas.AsSurface();
        }

        public Surface Settle()
        {
            _now += 2;
            return Frame(90);
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Canvas.Dispose();
        }
    }

    private static unsafe long Hash(Surface s, int x, int y, int w, int h)
    {
        long hash = 17;
        for (int j = y; j < y + h; j += 2)
            for (int i = x; i < x + w; i += 2)
                hash = hash * 31 + s.Pixels[j * s.Stride + i];
        return hash;
    }

    [Fact]
    public void The_avatar_header_names_the_term_and_the_chip_shows_it()
    {
        using var screen = new AvatarScreen(6);
        string Count() => screen.Renderer.AvatarStoreCountText(6, screen.Browser);
        Assert.Equal("6 avatars · Top rated", Count());
        screen.Renderer.AvatarStoreListQuery = "house";
        Assert.Equal("6 avatars · “house” · Top rated", Count());        // the count follows the term the list came with

        // The chip's area changes with the term (it is lit and shows the term and a cross).
        screen.Browser.Update(Up);
        screen.Renderer.AvatarStoreListQuery = "";
        Surface s = screen.Settle();
        long searchChip = Hash(s, 90, 100, 1740, 200);
        screen.Browser.Update(X);
        Type(screen.Browser, "house");
        screen.Browser.Update(UiInput.None, options: true);
        screen.Renderer.AvatarStoreListQuery = "house";
        s = screen.Settle();
        Assert.NotEqual(searchChip, Hash(s, 90, 100, 1740, 200));
        Assert.Equal("“house” ×", screen.Renderer.AvatarStoreChipText);
    }

    [Fact]
    public void An_avatar_search_without_a_match_says_so()
    {
        using var screen = new AvatarScreen(0);
        screen.Renderer.AvatarStoreListQuery = "zzz";
        screen.Renderer.AvatarStoreStatus = "No avatars match “zzz”.";
        long noMatch = Hash(screen.Settle(), 300, 380, 1300, 600);   // the canvas is reused: hash before the next frame
        screen.Renderer.AvatarStoreListQuery = "";
        long plain = Hash(screen.Settle(), 300, 380, 1300, 600);
        Assert.NotEqual(noMatch, plain);
    }

    [Fact]
    public void The_library_header_names_the_term_and_an_empty_result_has_its_own_message()
    {
        using var screen = new LibraryScreen(All, All.Length);
        Surface s = screen.Settle();
        long all = Hash(s, 90, 600, 1740, 70);
        Assert.Same(Kanto, screen.Renderer.DrawnEntry);

        // The term narrows the row: header, chip and cards change.
        screen.Browser.Update(Up);
        screen.Browser.Update(X);
        Type(screen.Browser, "morning");
        screen.Browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.LibraryFilter, screen.Browser.TakeAction());
        screen.Entries = LibraryFilter.Apply(All, screen.Browser.LibraryQuery);
        screen.Browser.SetCount(screen.Entries.Count, All.Length);
        screen.Browser.Select(0);
        screen.Renderer.LibraryReordered();
        s = screen.Settle();
        Assert.NotEqual(all, Hash(s, 90, 600, 1740, 70));
        Assert.Same(Zelda, screen.Renderer.DrawnEntry);
        Assert.Single(screen.Entries);

        // Nothing matches: the empty hero says so instead of the "No themes yet" message.
        screen.Browser.Update(X);                                        // takes the term off
        screen.Browser.TakeAction();
        Press(screen.Browser, O);
        screen.Entries = [];
        screen.Browser.Update(Up);
        screen.Browser.Update(X);
        Type(screen.Browser, "qqq");
        screen.Browser.Update(UiInput.None, options: true);
        screen.Browser.TakeAction();
        screen.Browser.SetCount(0, All.Length);
        screen.Renderer.LibraryReordered();
        Surface none = screen.Settle();
        long noMatch = Hash(none, 400, 280, 1100, 260);
        using var truly = new LibraryScreen([], 0);
        Assert.NotEqual(noMatch, Hash(truly.Settle(), 400, 280, 1100, 260));
        Assert.Null(screen.Renderer.DrawnEntry);
    }

    // ---- Footer hints --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_library_footer_points_to_the_search_and_fits()
    {
        using var screen = new LibraryScreen(All, All.Length);
        ThemeRenderer r = screen.Renderer;
        string[] Labels() => r.LibraryHints(screen.Browser).Select(h => h.Label).ToArray();
        Assert.Equal(["Details", "Preview", "Sort", "Reload", "Search"], Labels());
        Assert.Equal(PadButton.DPadUp, r.LibraryHints(screen.Browser)[^1].Button);
        Assert.Same(r.LibraryHints(screen.Browser), r.LibraryHints(screen.Browser));
        Assert.True(r.HintBarWidth(r.LibraryHints(screen.Browser)) <= FooterRoom);

        screen.Browser.Update(Up);
        Assert.Equal(["Search", "Sort", "Reload", "Back"], Labels());
        screen.Browser.Update(X);
        Type(screen.Browser, "a");
        screen.Browser.Update(UiInput.None, options: true);
        screen.Browser.TakeAction();
        Assert.Equal(["Clear search", "Sort", "Reload", "Back"], Labels());
        Assert.True(r.HintBarWidth(r.LibraryHints(screen.Browser)) <= FooterRoom);

        // An empty result still offers the way to the chip; a truly empty library does not.
        using var none = new LibraryScreen([], 3);
        Assert.Equal(["Reload", "Search"], none.Renderer.LibraryHints(none.Browser).Select(h => h.Label).ToArray());
        using var truly = new LibraryScreen([], 0);
        Assert.Equal(["Reload"], truly.Renderer.LibraryHints(truly.Browser).Select(h => h.Label).ToArray());

        // The look card gets the hint too.
        using var look = new LibraryScreen(All, All.Length);
        look.Browser.LookSavable = true;
        look.Browser.Update(UiInput.None with { End = true });
        Assert.True(look.Browser.LookCardFocused);
        Assert.Equal(["Save as theme", "Sort", "Reload", "Search"], look.Renderer.LibraryHints(look.Browser).Select(h => h.Label).ToArray());
    }

    // The footer bar is at the right edge; it may take the right ~60 percent of the content column before the toast has to rise above it.
    private const int FooterRoom = Theme.ContentWidth * 6 / 10;

    [Fact]
    public void The_avatars_footer_points_to_the_search_on_the_top_row_only()
    {
        using var screen = new AvatarScreen(14);
        ThemeRenderer r = screen.Renderer;
        string[] Labels() => r.AvatarStoreHints(screen.Browser).Select(h => h.Label).ToArray();
        Assert.Equal(["Download", "Like", "Sort", "Refresh", "Search"], Labels());
        Assert.True(r.HintBarWidth(r.AvatarStoreHints(screen.Browser)) <= FooterRoom);
        Assert.Same(r.AvatarStoreHints(screen.Browser), r.AvatarStoreHints(screen.Browser));
        screen.Browser.Update(Down);
        Assert.Equal(["Download", "Like", "Sort", "Refresh"], Labels());
        screen.Browser.Update(Up);
        screen.Browser.Update(Up);
        Assert.Equal(["Search", "Sort", "Refresh", "Back"], Labels());
        screen.Browser.Update(X);
        Type(screen.Browser, "a");
        screen.Browser.Update(UiInput.None, options: true);
        screen.Browser.TakeAction();
        Assert.Equal(["Clear search", "Sort", "Refresh", "Back"], Labels());
        screen.Renderer.AvatarStoreLiked = new HashSet<string> { "a0" };
        screen.Browser.Update(Down);
        Assert.Equal(["Download", "Unlike", "Sort", "Refresh", "Search"], Labels());

        using var empty = new AvatarScreen(0);
        empty.Renderer.AvatarStore = [];
        Assert.Equal(["Refresh", "Sort", "Search", "Back"], empty.Renderer.AvatarStoreHints(empty.Browser).Select(h => h.Label).ToArray());
    }

    [Fact]
    public void The_themes_footer_fits_with_its_fifth_hint()
    {
        var themes = new ThemeBrowser();
        using var renderer = NewRenderer();
        renderer.Community = Enumerable.Range(0, 3).Select(i =>
            new CommunityTheme("id" + i, "Theme " + i, "Author", "1.0", "", 1, 1, 1, null, "http://x/" + i, Fallback: true, AllApps: false, PerApp: 0, Icons: 0, Tiles: 0)).ToList();
        renderer.SelectedCommunityId = "id0";
        themes.SetCommunityCount(3);
        themes.Update(R1);
        Assert.Equal(PadButton.DPadUp, renderer.CommunityHints(themes)[^1].Button);
        renderer.CommunityUpdates = new HashSet<string> { "id0" };
        renderer.CommunityLiked = new HashSet<string> { "id0" };
        Assert.True(renderer.HintBarWidth(renderer.CommunityHints(themes)) <= FooterRoom);
    }

    [Fact]
    public unsafe void The_up_glyph_has_the_size_of_the_dpad_and_draws_something_different()
    {
        Assert.Equal(ButtonGlyph.Width(PadButton.DPad, 30), ButtonGlyph.Width(PadButton.DPadUp, 30));
        using var a = new PixelBuffer(40, 40);
        using var b = new PixelBuffer(40, 40);
        ButtonGlyph.Draw(a.AsSurface(), PadButton.DPad, 5, 5, 30, Theme.Muted, Theme.Muted);
        ButtonGlyph.Draw(b.AsSurface(), PadButton.DPadUp, 5, 5, 30, Theme.Muted, Theme.Muted);
        Assert.NotEqual(Hash(a.AsSurface(), 0, 0, 40, 40), Hash(b.AsSurface(), 0, 0, 40, 40));
        Assert.NotEqual(0u, b.AsSurface().Pixels[8 * b.AsSurface().Stride + 20]);   // the upper arm is there
    }

    // ---- Steady frames ---------------------------------------------------------------------------------------------------------

    // Paint.FocusRing allocates a few bytes in a Debug build and none when optimised: measure it under the same conditions.
    private static long FocusRingBytes(Surface scratch)
    {
        for (int i = 0; i < 3; i++) Paint.FocusRing(scratch, 100, 100, 200, 80, 14);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Paint.FocusRing(scratch, 100, 100, 200, 80, 14);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void AssertSteady(Func<Surface> frame, long ring)
    {
        for (int i = 0; i < 40; i++) frame();   // the runtime's own warm-up (tiered JIT) is over after a few dozen frames
        var perFrame = new long[40];
        for (int i = 0; i < perFrame.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            frame();
            perFrame[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        int run = 0, best = 0;
        foreach (long bytes in perFrame) { run = bytes <= ring ? run + 1 : 0; best = Math.Max(best, run); }
        Assert.True(best >= 10, $"bytes allocated per steady frame: {string.Join(", ", perFrame)} (a focus ring is {ring})");
    }

    [Fact]
    public void Steady_library_frames_with_the_chip_focused_and_a_term_allocate_nothing_beyond_the_focus_ring()
    {
        using var screen = new LibraryScreen(All, All.Length);
        screen.Browser.Update(Up);
        screen.Browser.Update(X);
        Type(screen.Browser, "e");
        screen.Browser.Update(UiInput.None, options: true);
        screen.Browser.TakeAction();
        screen.Entries = LibraryFilter.Apply(All, "e");
        screen.Browser.SetCount(screen.Entries.Count, All.Length);
        screen.Settle();
        AssertSteady(() => screen.Frame(), FocusRingBytes(screen.Canvas.AsSurface()));
    }

    [Fact]
    public void Steady_library_frames_with_no_match_allocate_nothing_beyond_the_focus_ring()
    {
        using var screen = new LibraryScreen([], 4);
        screen.Browser.Update(Up);
        screen.Browser.Update(X);
        Type(screen.Browser, "q");
        screen.Browser.Update(UiInput.None, options: true);
        screen.Browser.TakeAction();
        screen.Settle();
        AssertSteady(() => screen.Frame(), FocusRingBytes(screen.Canvas.AsSurface()));
    }

    [Fact]
    public void Steady_avatar_frames_with_the_chip_focused_and_a_term_allocate_nothing_beyond_the_focus_ring()
    {
        using var screen = new AvatarScreen(12);
        screen.Browser.Update(Up);
        screen.Browser.Update(X);
        Type(screen.Browser, "house");
        screen.Browser.Update(UiInput.None, options: true);
        screen.Browser.TakeAction();
        screen.Renderer.AvatarStoreListQuery = "house";
        screen.Settle();
        AssertSteady(() => screen.Frame(), FocusRingBytes(screen.Canvas.AsSurface()));
    }

    [Fact]
    public void The_keyboard_draws_for_all_three_searches()
    {
        using var screen = new LibraryScreen(All, All.Length);
        screen.Browser.Update(Up);
        screen.Browser.Update(X);
        Assert.Equal(ThemeView.Search, screen.Browser.View);
        long library = Hash(screen.Settle(), 90, 130, 1740, 300);
        using var avatars = new AvatarScreen(3);
        avatars.Browser.Update(Up);
        avatars.Browser.Update(X);
        long store = Hash(avatars.Settle(), 90, 130, 1740, 300);
        Assert.NotEqual(library, store);                                 // another title and another help line
    }
}
