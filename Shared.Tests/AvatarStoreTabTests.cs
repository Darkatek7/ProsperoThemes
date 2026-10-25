using System.Linq;
using ProsperoThemes.App.Ui;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>The Avatars tab: the store's avatar grid, its download question, like, sort and refresh.</summary>
public class AvatarStoreTabTests
{
    private static UiInput Input(bool right = false, bool left = false, bool confirm = false, bool cancel = false, bool up = false, bool down = false) =>
        new(up, down, left, right, confirm, cancel);

    private static readonly UiInput X = Input(confirm: true);
    private static readonly UiInput O = Input(cancel: true);
    private static readonly UiInput Up = Input(up: true);
    private static readonly UiInput Down = Input(down: true);
    private static readonly UiInput Left = Input(left: true);
    private static readonly UiInput Right = Input(right: true);
    private static readonly UiInput R1 = UiInput.None with { PageDown = true };
    private static readonly UiInput L1 = UiInput.None with { PageUp = true };

    private static readonly string[] DownloadOptions = ["Download", "Download and set", "Cancel"];

    // The Avatars tab entered the way the app does it: with no list, so the first entry asks the store for it. The list then
    // has this many cards, the focus on the first.
    private static ThemeBrowser OnAvatarTab(int cards)
    {
        var browser = new ThemeBrowser();
        browser.SetCommunityCount(1);                               // the Community tab keeps its list: no refresh there
        browser.Update(R1);
        Assert.Equal(ThemeView.Community, browser.View);
        browser.Update(R1);
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
        browser.SetAvatarStoreCount(cards);
        return browser;
    }

    [Fact]
    public void The_avatars_tab_sits_between_the_store_and_the_usb_import()
    {
        var browser = OnAvatarTab(2);
        browser.Update(R1);
        Assert.Equal(ThemeView.Import, browser.View);
        browser.Update(L1);
        Assert.Equal(ThemeView.Avatars, browser.View);
        browser.Update(L1);
        Assert.Equal(ThemeView.Community, browser.View);
        Assert.Equal(ThemeView.Community, browser.View);
        browser.Update(L1);
        Assert.Equal(ThemeView.Library, browser.View);
        browser.Update(L1);                                         // L1 from the Library wraps to System
        Assert.Equal(ThemeView.System, browser.View);
    }

    [Fact]
    public void R1_from_the_System_tab_wraps_to_the_Library_over_five_tabs()
    {
        var browser = new ThemeBrowser();
        browser.Update(R1);                                         // Community
        browser.TakeAction();
        browser.Update(R1);                                         // Avatars
        browser.TakeAction();
        browser.Update(R1);                                         // Import
        browser.Update(R1);                                         // System
        Assert.Equal(ThemeView.System, browser.View);
        browser.Update(R1);
        Assert.Equal(ThemeView.Library, browser.View);
    }

    [Fact]
    public void Entering_the_avatars_tab_without_a_list_asks_the_store_for_it()
    {
        var browser = new ThemeBrowser();
        browser.SetCommunityCount(1);
        browser.Update(R1);
        browser.TakeAction();
        browser.Update(R1);
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
        Assert.Equal(-1, browser.AvatarStoreSelected);              // nothing to focus yet
    }

    [Fact]
    public void Entering_the_avatars_tab_with_a_list_keeps_it()
    {
        var browser = OnAvatarTab(3);
        browser.Update(R1);                                         // Import
        browser.Update(L1);                                         // back to Avatars
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(0, browser.AvatarStoreSelected);

        browser = OnAvatarTab(3);
        browser.SetAvatarStoreCount(0);                             // an empty list counts as none: asked again
        browser.Update(L1);
        browser.Update(R1);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
    }

    [Fact]
    public void The_focus_starts_on_the_first_card_and_an_empty_list_has_none()
    {
        var browser = OnAvatarTab(0);
        Assert.Equal(-1, browser.AvatarStoreSelected);
        browser.SetAvatarStoreCount(5);
        Assert.Equal(0, browser.AvatarStoreSelected);
        browser.SetAvatarStoreCount(0);
        Assert.Equal(-1, browser.AvatarStoreSelected);
    }

    [Fact]
    public void Right_and_Left_move_one_card_and_stop_at_the_ends()
    {
        var browser = OnAvatarTab(4);
        browser.Update(Left);
        Assert.Equal(0, browser.AvatarStoreSelected);
        browser.Update(Right);
        Assert.Equal(1, browser.AvatarStoreSelected);
        for (int i = 0; i < 10; i++) browser.Update(Right);
        Assert.Equal(3, browser.AvatarStoreSelected);
        browser.Update(Left);
        Assert.Equal(2, browser.AvatarStoreSelected);
    }

    [Fact]
    public void Down_and_Up_move_one_row_of_six_cards()
    {
        var browser = OnAvatarTab(14);                              // rows: 0-5, 6-11, 12-13
        browser.Update(Right);
        browser.Update(Down);
        Assert.Equal(7, browser.AvatarStoreSelected);
        browser.Update(Down);
        Assert.Equal(13, browser.AvatarStoreSelected);              // the last row is shorter: its last card
        browser.Update(Down);
        Assert.Equal(13, browser.AvatarStoreSelected);              // no row below
        browser.Update(Up);
        Assert.Equal(7, browser.AvatarStoreSelected);
        browser.Update(Up);
        Assert.Equal(1, browser.AvatarStoreSelected);
    }

    [Fact]
    public void Down_from_a_card_over_a_short_last_row_lands_on_its_last_card()
    {
        var browser = OnAvatarTab(9);                               // rows: 0-5, 6-8
        for (int i = 0; i < 4; i++) browser.Update(Right);
        Assert.Equal(4, browser.AvatarStoreSelected);
        browser.Update(Down);
        Assert.Equal(8, browser.AvatarStoreSelected);
        browser.Update(Down);
        Assert.Equal(8, browser.AvatarStoreSelected);
    }

    [Fact]
    public void Down_on_a_single_row_does_nothing()
    {
        var browser = OnAvatarTab(4);
        browser.Update(Right);
        browser.Update(Down);
        Assert.Equal(1, browser.AvatarStoreSelected);
    }

    [Fact]
    public void Home_and_End_go_to_the_first_and_the_last_card()
    {
        var browser = OnAvatarTab(9);
        browser.Update(UiInput.None with { End = true });
        Assert.Equal(8, browser.AvatarStoreSelected);
        browser.Update(UiInput.None with { Home = true });
        Assert.Equal(0, browser.AvatarStoreSelected);
    }

    [Fact]
    public void X_on_a_store_avatar_asks_first_and_only_the_answer_downloads()
    {
        var browser = OnAvatarTab(3);
        browser.Update(Right);
        browser.Update(X);
        Assert.Equal(DialogKind.AvatarDownload, browser.Dialog);
        Assert.Equal(DownloadOptions, browser.DialogOptions);
        Assert.Equal(0, browser.DialogFocus);                       // starts on "Download"
        Assert.Equal(ThemeAction.None, browser.TakeAction());       // the X that opened it does not answer it

        browser.Update(X);
        Assert.Equal(ThemeAction.AvatarDownload, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.Avatars, browser.View);
        Assert.Equal(1, browser.AvatarStoreSelected);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Download_and_set_is_the_second_answer_and_cancel_or_circle_do_nothing()
    {
        var browser = OnAvatarTab(3);
        browser.Update(X);
        browser.Update(Down);
        Assert.Equal(1, browser.DialogFocus);
        browser.Update(X);
        Assert.Equal(ThemeAction.AvatarDownloadSet, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(X);
        browser.Update(Down);
        browser.Update(Down);
        Assert.Equal(2, browser.DialogFocus);                       // Cancel is the last option
        browser.Update(X);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(X);
        browser.Update(O);                                          // Circle answers nothing
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.Avatars, browser.View);
    }

    [Fact]
    public void While_busy_a_plain_download_still_goes_but_download_and_set_waits()
    {
        var browser = OnAvatarTab(3);
        browser.Update(X, busy: true);                              // opening the question is not a change: it opens
        Assert.Equal(DialogKind.AvatarDownload, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(X, busy: true);
        Assert.Equal(ThemeAction.AvatarDownload, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(X);
        browser.Update(Down);
        browser.Update(X, busy: true);
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
    }

    [Fact]
    public void Triangle_likes_the_focused_store_avatar_and_does_nothing_on_an_empty_list()
    {
        var browser = OnAvatarTab(3);
        browser.Update(Right);
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(ThemeAction.AvatarLike, browser.TakeAction());
        Assert.Equal(1, browser.AvatarStoreSelected);
        Assert.Equal(ThemeView.Avatars, browser.View);

        var empty = OnAvatarTab(0);
        empty.Update(UiInput.None, triangle: true);
        Assert.Equal(ThemeAction.None, empty.TakeAction());
        Assert.Equal(DialogKind.None, empty.Dialog);
    }

    [Fact]
    public void Square_goes_to_the_next_sort_and_asks_the_store_again_from_the_first_card()
    {
        var browser = OnAvatarTab(5);
        browser.Update(Right);
        browser.Update(Right);
        Assert.Equal(2, browser.AvatarStoreSelected);

        browser.Update(UiInput.None, preview: true);
        Assert.Equal(1, browser.AvatarStoreSort);
        Assert.Equal(0, browser.AvatarStoreSelected);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
        Assert.Equal(ThemeView.Avatars, browser.View);

        browser.Update(UiInput.None, preview: true);
        Assert.Equal(2, browser.AvatarStoreSort);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(0, browser.AvatarStoreSort);                   // wraps round to the first sort
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
    }

    [Fact]
    public void Square_on_an_empty_list_sorts_and_leaves_the_focus_on_none()
    {
        var browser = OnAvatarTab(0);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(1, browser.AvatarStoreSort);
        Assert.Equal(-1, browser.AvatarStoreSelected);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
    }

    [Fact]
    public void Options_asks_the_store_again_and_keeps_the_card()
    {
        var browser = OnAvatarTab(4);
        browser.Update(Right);
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.AvatarStoreRefresh, browser.TakeAction());
        Assert.Equal(1, browser.AvatarStoreSelected);
        Assert.Equal(0, browser.AvatarStoreSort);
        Assert.Equal(ThemeView.Avatars, browser.View);
    }

    [Fact]
    public void Circle_goes_back_to_the_library()
    {
        var browser = OnAvatarTab(3);
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void A_new_store_list_closes_the_download_question_and_a_library_scan_does_not()
    {
        var browser = OnAvatarTab(3);
        browser.Update(X);
        browser.SetCount(4);                                        // the library was read again: the question stays
        Assert.Equal(DialogKind.AvatarDownload, browser.Dialog);

        browser.SetAvatarStoreCount(3);                             // the store sent another list: it goes
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void A_shorter_store_list_keeps_the_focus_on_a_card()
    {
        var browser = OnAvatarTab(8);
        for (int i = 0; i < 6; i++) browser.Update(Right);
        Assert.Equal(6, browser.AvatarStoreSelected);
        browser.SetAvatarStoreCount(3);
        Assert.Equal(2, browser.AvatarStoreSelected);
    }
}
