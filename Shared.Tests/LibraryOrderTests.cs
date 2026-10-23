using System;
using System.Collections.Generic;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class LibraryOrderTests
{
    private static string Digest(char c) => new(c, 64);

    private static ThemeLibraryEntry Entry(string name, string id, string path, bool bundled = false) =>
        new(path, new ThemePackManifest(2, id, name, "a", "1.0", "", "keep", []), new PackReport(), null) { Bundled = bundled };

    private static readonly ThemeLibraryEntry Bundled = Entry("Blue Hour", "blue-hour", "/app0/sce_sys/prospero-themes/blue-hour.p5theme", bundled: true);
    private static readonly ThemeLibraryEntry Zelda = Entry("zelda", "zelda", $"/data/prospero-themes/library/{Digest('a')}.p5theme");
    private static readonly ThemeLibraryEntry Kanto = Entry("Kanto", "kanto", $"/data/prospero-themes/library/{Digest('b')}.p5theme");
    private static readonly ThemeLibraryEntry Alien = Entry("Alien", "alien", $"/data/prospero-themes/library/{Digest('c')}.p5theme");
    private static readonly ThemeLibraryEntry[] All = [Zelda, Bundled, Kanto, Alien];
    private static readonly Dictionary<string, long> Added = new() { [Digest('a')] = 300, [Digest('b')] = 100, [Digest('c')] = 200 };

    private static string[] Names(IEnumerable<ThemeLibraryEntry> entries) => entries.Select(e => e.Manifest.Name).ToArray();

    [Fact]
    public void By_name_ignores_case_and_puts_the_applied_theme_first()
    {
        Assert.Equal(["Alien", "Blue Hour", "Kanto", "zelda"], Names(LibraryOrder.Sort(All, null, LibrarySort.Name, Added)));
        Assert.Equal(["Kanto", "Alien", "Blue Hour", "zelda"], Names(LibraryOrder.Sort(All, "kanto", LibrarySort.Name, Added)));
    }

    [Fact]
    public void Newest_goes_by_the_recorded_time_and_the_bundled_theme_is_the_oldest()
    {
        Assert.Equal(["zelda", "Alien", "Kanto", "Blue Hour"], Names(LibraryOrder.Sort(All, null, LibrarySort.Newest, Added)));
        Assert.Equal(["Blue Hour", "zelda", "Alien", "Kanto"], Names(LibraryOrder.Sort(All, "blue-hour", LibrarySort.Newest, Added)));
    }

    [Fact]
    public void Record_adds_new_packs_and_forgets_removed_ones()
    {
        var added = new Dictionary<string, long> { [Digest('a')] = 5, [Digest('d')] = 6 };
        Assert.True(LibraryOrder.Record(added, All, 99));
        Assert.Equal(new Dictionary<string, long> { [Digest('a')] = 5, [Digest('b')] = 99, [Digest('c')] = 99 }, added);
        Assert.False(LibraryOrder.Record(added, All, 120));
    }

    [Fact]
    public void Added_times_survive_a_round_trip_and_junk_is_dropped()
    {
        Dictionary<string, long> back = LibraryOrder.ParseAdded(LibraryOrder.SerializeAdded(Added));
        Assert.Equal(Added, back);
        Assert.Empty(LibraryOrder.ParseAdded("{\"not-a-digest\": 5, \"" + Digest('e') + "\": \"x\"}"));
        Assert.Empty(LibraryOrder.ParseAdded("garbage"));
        Assert.Null(LibraryOrder.DigestOf("/data/prospero-themes/library/" + new string('A', 64) + ".p5theme"));
    }

    [Fact]
    public void The_sort_is_kept_in_the_settings()
    {
        AppSettings settings = AppSettings.Parse("{\"library_sort\":\"newest\",\"device_id\":\"abcdefgh12\"}", () => "x");
        Assert.Equal(LibrarySort.Newest, settings.LibrarySort);
        Assert.Equal(LibrarySort.Newest, AppSettings.Parse(settings.Serialize(), () => "x").LibrarySort);
        Assert.Equal(LibrarySort.Name, AppSettings.Parse("{}", () => "abcdefgh12").LibrarySort);
    }

    [Fact]
    public void Triangle_in_the_library_asks_for_the_other_order_and_select_moves_the_focus()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(4);
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(ThemeAction.ToggleLibrarySort, browser.TakeAction());
        browser.Select(3);
        Assert.Equal(3, browser.Selected);
        browser.Select(9);
        Assert.Equal(3, browser.Selected);
    }
}
