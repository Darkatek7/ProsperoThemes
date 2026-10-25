using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>The Library's avatar row (Console picture card, then the avatars), and the app's avatar storage.</summary>
public class AvatarScreenTests
{
    private static UiInput Input(bool right = false, bool left = false, bool confirm = false, bool cancel = false, bool up = false, bool down = false) =>
        new(up, down, left, right, confirm, cancel);

    private static readonly UiInput X = Input(confirm: true), O = Input(cancel: true), Up = Input(up: true), Down = Input(down: true),
        Left = Input(left: true), Right = Input(right: true);

    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "avatar-house.p5avatar"));

    // The Library with this many avatars after the Console picture card, focus on the avatar row (on the Console picture card).
    private static ThemeBrowser OnAvatars(int avatars, int themes = 3)
    {
        var browser = new ThemeBrowser();
        browser.SetCount(themes);
        browser.SetAvatarCount(avatars);
        Assert.False(browser.AvatarRowFocused);
        browser.Update(Down);
        Assert.True(browser.AvatarRowFocused);
        Assert.Equal(ThemeView.Library, browser.View);
        return browser;
    }

    [Fact]
    public void Down_from_the_themes_or_an_empty_library_reaches_the_avatar_row()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(3);
        browser.SetAvatarCount(2);
        Assert.False(browser.AvatarRowFocused);
        browser.Update(Down);
        Assert.True(browser.AvatarRowFocused);
        Assert.Equal(0, browser.AvatarSelected);                    // the Console picture card
        Assert.Equal(-1, browser.AvatarIndex);

        var empty = new ThemeBrowser();                             // no packs at all
        empty.SetAvatarCount(2);
        empty.Update(Down);
        Assert.True(empty.AvatarRowFocused);
        Assert.Equal(ThemeView.Library, empty.View);
    }

    [Fact]
    public void Down_from_the_look_card_reaches_the_avatar_row()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(2);
        browser.SetAvatarCount(1);
        browser.LookSavable = true;
        browser.Update(UiInput.None with { End = true });
        Assert.True(browser.LookCardFocused);
        browser.Update(Down);
        Assert.True(browser.AvatarRowFocused);
    }

    [Fact]
    public void Up_and_Circle_go_back_to_the_theme_row()
    {
        var browser = OnAvatars(3);
        browser.Update(Right);
        browser.Update(Up);
        Assert.False(browser.AvatarRowFocused);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.Equal(0, browser.Selected);                          // the theme row kept its card

        browser.Update(Down);
        Assert.True(browser.AvatarRowFocused);
        browser.Update(O);
        Assert.False(browser.AvatarRowFocused);
        Assert.Equal(ThemeView.Library, browser.View);              // Circle leaves the row, not the Library
    }

    [Fact]
    public void The_theme_row_moves_with_left_and_right_only()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(3);
        browser.Update(Right);
        Assert.Equal(1, browser.Selected);
        browser.Update(Up);
        Assert.Equal(1, browser.Selected);                          // Up does not move to the previous card
        browser.Update(Down);                                       // Down goes to the avatars, not to the next card
        Assert.Equal(1, browser.Selected);
        Assert.True(browser.AvatarRowFocused);
    }

    [Fact]
    public void The_tabs_still_switch_from_the_avatar_row()
    {
        var browser = OnAvatars(3);
        browser.Update(Right);
        browser.Update(UiInput.None with { PageDown = true });      // R1 goes to the next tab
        Assert.Equal(ThemeView.Community, browser.View);
        browser.Update(UiInput.None with { PageUp = true });        // and back
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.True(browser.AvatarRowFocused);                      // the avatar row and its card are as they were
        Assert.Equal(1, browser.AvatarSelected);

        browser.Update(UiInput.None with { PageUp = true });        // L1 from the Library wraps to System
        Assert.Equal(ThemeView.System, browser.View);
    }

    [Fact]
    public void X_on_an_avatar_asks_first_and_only_the_answer_sets_it()
    {
        var browser = OnAvatars(2);
        browser.Update(Right);
        Assert.Equal(0, browser.AvatarIndex);
        browser.Update(X);
        Assert.Equal(DialogKind.AvatarApply, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());       // the X that opened it does not answer it
        Assert.Equal(0, browser.DialogFocus);                       // the question starts on the answer that sets it
        browser.Update(X);
        Assert.Equal(ThemeAction.AvatarApply, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.True(browser.AvatarRowFocused);
    }

    [Fact]
    public void The_console_picture_and_remove_questions_start_on_cancel()
    {
        var browser = OnAvatars(2);
        browser.Update(X);                                          // the Console picture card
        Assert.Equal(DialogKind.AvatarReset, browser.Dialog);
        browser.Update(X);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(Right);
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(DialogKind.AvatarRemove, browser.Dialog);
        browser.Update(X);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
    }

    [Fact]
    public void The_console_picture_and_remove_questions_can_be_answered_yes()
    {
        var browser = OnAvatars(2);
        browser.Update(X);
        browser.Update(Up);                                         // from Cancel to the answer
        browser.Update(X);
        Assert.Equal(ThemeAction.AvatarReset, browser.TakeAction());

        browser.Update(Right);
        browser.Update(UiInput.None, triangle: true);
        browser.Update(Up);
        browser.Update(X);
        Assert.Equal(ThemeAction.AvatarRemove, browser.TakeAction());
        Assert.Equal(0, browser.AvatarIndex);
    }

    [Fact]
    public void Triangle_on_the_console_picture_does_nothing()
    {
        var browser = OnAvatars(2);
        browser.Update(UiInput.None, triangle: true);               // the console's picture cannot be removed
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void While_busy_no_avatar_question_opens()
    {
        var browser = OnAvatars(2);
        browser.Update(X, busy: true);
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        browser.Update(Right);
        browser.Update(X, busy: true);
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        browser.Update(UiInput.None, triangle: true, busy: true);
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(X);
        browser.Update(X, busy: true);                              // a question open when a job started cannot answer it
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
    }

    [Fact]
    public void The_focus_moves_along_the_row_and_stays_inside_it()
    {
        var browser = OnAvatars(8);
        browser.Update(Left);
        Assert.Equal(0, browser.AvatarSelected);                    // nothing before the Console picture card
        for (int i = 0; i < 12; i++) browser.Update(Right);
        Assert.Equal(8, browser.AvatarSelected);                    // nothing after the last avatar
        browser.Update(UiInput.None with { Home = true });
        Assert.Equal(0, browser.AvatarSelected);
        browser.Update(UiInput.None with { End = true });
        Assert.Equal(8, browser.AvatarSelected);
        browser.Update(Down);                                       // there is no row below
        Assert.Equal(8, browser.AvatarSelected);
        Assert.True(browser.AvatarRowFocused);

        browser.SetAvatarCount(2);                                  // a shorter list keeps the focus on a card
        Assert.Equal(2, browser.AvatarSelected);
        browser.SetAvatarCount(0);                                  // no avatars: only the Console picture card is left
        Assert.Equal(0, browser.AvatarSelected);
        browser.Update(Right);
        Assert.Equal(0, browser.AvatarSelected);
        browser.Update(UiInput.None with { End = true });
        Assert.Equal(0, browser.AvatarSelected);
    }

    [Fact]
    public void A_new_avatar_list_closes_a_question_about_an_avatar()
    {
        var browser = OnAvatars(2);
        browser.Update(Right);
        browser.Update(X);
        browser.SetAvatarCount(1);
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(UiInput.None, triangle: true);               // the remove question goes the same way
        Assert.Equal(DialogKind.AvatarRemove, browser.Dialog);
        browser.SetAvatarCount(2);
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(Left);                                       // the Console picture question is about no avatar, so it stays
        browser.Update(X);
        browser.SetAvatarCount(3);
        Assert.Equal(DialogKind.AvatarReset, browser.Dialog);
    }

    [Fact]
    public void A_library_scan_leaves_avatar_questions_open()
    {
        var browser = OnAvatars(2);
        browser.Update(Right);
        browser.Update(X);
        browser.SetCount(4);
        Assert.Equal(DialogKind.AvatarApply, browser.Dialog);

        browser.Update(O);                                          // Circle answers Cancel
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(DialogKind.AvatarRemove, browser.Dialog);
        browser.SetCount(5);
        Assert.Equal(DialogKind.AvatarRemove, browser.Dialog);

        browser.Update(O);
        browser.Update(Left);
        browser.Update(X);
        browser.SetCount(0);
        Assert.Equal(DialogKind.AvatarReset, browser.Dialog);
    }

    [Fact]
    public void Square_on_the_avatar_row_does_not_open_the_preview()
    {
        var browser = OnAvatars(2);
        browser.Update(Right);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.True(browser.AvatarRowFocused);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Options_on_the_avatar_row_reads_the_library_again()
    {
        var browser = OnAvatars(2);
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.Reload, browser.TakeAction());
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.True(browser.AvatarRowFocused);
    }

    [Fact]
    public void The_library_stores_a_pack_once_under_its_hash_and_reads_it_back()
    {
        var files = new MemoryFiles();
        byte[] pack = Fixture();
        Assert.True(AvatarLibrary.Add(files, pack));
        Assert.False(AvatarLibrary.Add(files, pack));               // the same pack again
        string stored = AvatarLibrary.Root + "/" + ThemePack.Hash(pack) + ".p5avatar";
        Assert.Equal(pack, files.Files[stored]);
        Assert.DoesNotContain(files.Files.Keys, k => k.EndsWith(".tmp"));

        files.Files[AvatarLibrary.Root + "/broken.p5avatar"] = [1, 2, 3];
        files.Files[AvatarLibrary.Root + "/notes.txt"] = [1];
        var (entries, problem) = AvatarLibrary.Read(files, files.Files.Keys.Select(k => k[(AvatarLibrary.Root.Length + 1)..]));
        AvatarEntry entry = Assert.Single(entries);
        Assert.Equal(stored, entry.Path);
        Assert.Equal("House", entry.Tile.Name);
        Assert.Equal(0xFF, entry.Tile.Preview![0]);
        Assert.Contains("broken.p5avatar", problem);
    }

    [Fact]
    public void A_theme_pack_never_gets_in()
    {
        var files = new MemoryFiles();
        byte[] theme = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ui-sound.p5theme"));
        Assert.Throws<ThemePackException>(() => AvatarLibrary.Add(files, theme));
        Assert.Empty(files.Files);
    }

    [Fact]
    public void Remove_keeps_the_file_in_the_attic()
    {
        var files = new MemoryFiles();
        byte[] pack = Fixture();
        AvatarLibrary.Add(files, pack);
        string stored = AvatarLibrary.Root + "/" + ThemePack.Hash(pack) + ".p5avatar";
        AvatarLibrary.Remove(files, stored);
        Assert.False(files.Files.ContainsKey(stored));
        Assert.Equal(pack, files.Files[AvatarLibrary.Attic + "/" + ThemePack.Hash(pack) + ".p5avatar"]);
        Assert.Throws<InvalidOperationException>(() => AvatarLibrary.Remove(files, "/data/prospero-themes/library/x.p5theme"));
    }

    private sealed class MemoryFiles : IDeviceFiles
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public bool Exists(string path) => Files.ContainsKey(path);
        public long Length(string path) => Files.TryGetValue(path, out byte[]? d) ? d.Length : -1;
        public byte[] ReadAllBytes(string path) => Files[path];
        public string Hash(string path) => ThemePack.Hash(Files[path]);
        public void WriteAllBytes(string path, byte[] data) => Files[path] = data;
        public void Move(string from, string to) { Files[to] = Files[from]; Files.Remove(from); }
        public void DeleteFile(string path) => Files.Remove(path);
        public void CreateDirectoryRecursive(string path) { }
        public void RemountSystemExRw() { }
        public IReadOnlyList<string> ListDirectories(string path) => [];
    }
}
