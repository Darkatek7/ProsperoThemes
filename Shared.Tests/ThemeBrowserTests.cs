using ProsperoThemes.App.Ui;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class ThemeBrowserTests
{
    private static UiInput Input(bool right = false, bool left = false, bool confirm = false, bool cancel = false, bool up = false, bool down = false) =>
        new(up, down, left, right, confirm, cancel);

    private static readonly UiInput X = Input(confirm: true);
    private static readonly UiInput O = Input(cancel: true);
    private static readonly UiInput Up = Input(up: true);
    private static readonly UiInput Down = Input(down: true);
    private static readonly UiInput Left = Input(left: true);
    private static readonly UiInput Right = Input(right: true);

    [Fact]
    public void Selection_stops_at_library_edges()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(2);
        browser.Update(Left);
        Assert.Equal(0, browser.Selected);
        browser.Update(Right);
        browser.Update(Right);
        Assert.Equal(1, browser.Selected);
    }

    [Fact]
    public void Preview_returns_to_the_detail_that_opened_it()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(2);
        browser.Update(X);
        Assert.Equal(ThemeView.Detail, browser.View);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Preview, browser.View);
        browser.Update(O);
        Assert.Equal(ThemeView.Detail, browser.View);
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
    }

    [Fact]
    public void Empty_library_cannot_open_detail_or_preview()
    {
        var browser = new ThemeBrowser();
        browser.Update(X, preview: true);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.Equal(-1, browser.Selected);
    }

    [Fact]
    public void Reload_clamps_focus_when_packs_disappear()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(3);
        browser.Update(Right);
        browser.Update(Right);
        browser.Update(X);
        browser.SetCount(0);
        Assert.Equal(-1, browser.Selected);
        Assert.Equal(ThemeView.Library, browser.View);
    }

    [Fact]
    public void Tabs_are_accessible_and_usb_confirm_requests_import_once()
    {
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(ThemeView.Community, browser.View);
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());   // first visit loads the list
        browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(ThemeView.Import, browser.View);
        browser.Update(X);
        Assert.Equal(ThemeAction.ImportUsb, browser.TakeAction());
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(ThemeView.System, browser.View);
    }

    [Fact]
    public void Preview_overlay_can_be_hidden_without_changing_screen()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(1);
        browser.Update(UiInput.None, preview: true);
        Assert.True(browser.ShowOverlay);
        browser.Update(X);
        Assert.False(browser.ShowOverlay);
        Assert.Equal(ThemeView.Preview, browser.View);
    }

    [Fact]
    public void Community_tab_moves_in_a_grid_downloads_likes_and_cycles_sort()
    {
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageDown = true });
        browser.TakeAction();
        browser.SetCommunityCount(10);
        Assert.Equal(0, browser.CommunitySelected);
        browser.Update(Down);   // next row
        Assert.Equal(4, browser.CommunitySelected);
        browser.Update(Right);
        Assert.Equal(5, browser.CommunitySelected);
        browser.Update(X);
        Assert.Equal(ThemeAction.CommunityDownload, browser.TakeAction());
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(ThemeAction.CommunityLike, browser.TakeAction());
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(1, browser.CommunitySort);
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());
        Assert.Equal(0, browser.CommunitySelected);
    }

    // ---- Community: the category chips ---------------------------------------------------------------

    private static ThemeBrowser OnCommunity(int cards)
    {
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(ThemeView.Community, browser.View);
        browser.TakeAction();   // the first visit asks for the list
        browser.SetCommunityCount(cards);
        return browser;
    }

    private static void Press(ThemeBrowser browser, UiInput input, int times)
    {
        for (int i = 0; i < times; i++) browser.Update(input);
    }

    [Fact]
    public void Up_from_the_top_row_focuses_the_chips_and_down_returns_to_the_cards()
    {
        var browser = OnCommunity(10);
        Assert.False(browser.CommunityChipFocused);
        Assert.Equal(-1, browser.CommunityCategory);
        browser.Update(Right);   // card 1, still the top row
        browser.Update(Up);
        Assert.True(browser.CommunityChipFocused);
        Assert.Equal(1, browser.CommunitySelected);     // the card is remembered
        Assert.Equal(0, browser.CommunityChip);         // the cursor starts on "All", the chosen chip
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(Up);                              // nothing above the chips
        Assert.True(browser.CommunityChipFocused);
        browser.Update(Down);
        Assert.False(browser.CommunityChipFocused);
        Assert.Equal(1, browser.CommunitySelected);
        browser.Update(X);                               // and the cards answer again
        Assert.Equal(ThemeAction.CommunityDownload, browser.TakeAction());
    }

    [Fact]
    public void Up_from_a_lower_row_moves_in_the_grid_first()
    {
        var browser = OnCommunity(10);
        browser.Update(Down);
        browser.Update(Right);
        Assert.Equal(5, browser.CommunitySelected);
        browser.Update(Up);
        Assert.False(browser.CommunityChipFocused);
        Assert.Equal(1, browser.CommunitySelected);
        browser.Update(Up);
        Assert.True(browser.CommunityChipFocused);
    }

    [Fact]
    public void Left_and_right_move_between_the_chips_and_stop_at_the_ends()
    {
        var browser = OnCommunity(4);
        browser.Update(Up);
        browser.Update(Left);
        Assert.Equal(0, browser.CommunityChip);
        Assert.Equal(13, ThemeBrowser.CommunityChipCount);   // All and the twelve categories
        Press(browser, Right, 5);
        Assert.Equal(5, browser.CommunityChip);
        Press(browser, Right, 40);
        Assert.Equal(12, browser.CommunityChip);
        Press(browser, Left, 3);
        Assert.Equal(9, browser.CommunityChip);
        Assert.Equal(0, browser.CommunitySelected);          // the cards did not move
        Assert.Equal(-1, browser.CommunityCategory);         // and nothing is chosen until X
    }

    [Fact]
    public void X_on_a_chip_chooses_its_category_resets_the_card_and_asks_for_the_list()
    {
        var browser = OnCommunity(10);
        Press(browser, Right, 3);
        Assert.Equal(3, browser.CommunitySelected);
        browser.Update(Up);
        Press(browser, Right, 2);                            // Games
        browser.Update(X);
        Assert.Equal(1, browser.CommunityCategory);
        Assert.Equal("games", ProsperoThemes.App.Library.CommunityCategories.IdAt(browser.CommunityCategory));
        Assert.Equal(0, browser.CommunitySelected);
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.True(browser.CommunityChipFocused);           // the focus stays on the chips
        // X on "All" takes the filter off again.
        Press(browser, Left, 2);
        browser.Update(X);
        Assert.Equal(-1, browser.CommunityCategory);
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());
        // Moving into the row again starts on the chosen chip.
        Press(browser, Right, 7);
        browser.Update(X);
        Assert.Equal(6, browser.CommunityCategory);
        browser.TakeAction();
        browser.Update(Down);
        browser.Update(Up);
        Assert.Equal(7, browser.CommunityChip);
    }

    [Fact]
    public void Triangle_does_not_like_while_the_chips_have_the_focus()
    {
        var browser = OnCommunity(4);
        browser.Update(Up);
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(Down);
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(ThemeAction.CommunityLike, browser.TakeAction());
    }

    [Fact]
    public void Square_cycles_the_sort_and_options_refresh_from_both_rows()
    {
        var browser = OnCommunity(10);
        Press(browser, Right, 2);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(1, browser.CommunitySort);
        Assert.Equal(0, browser.CommunitySelected);
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());

        browser.Update(Up);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(2, browser.CommunitySort);
        Assert.True(browser.CommunityChipFocused);           // sorting does not take the focus off the chips
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(0, browser.CommunitySort);
        browser.TakeAction();

        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());
        Assert.Equal(-1, browser.CommunityCategory);         // a refresh keeps the filter as it is
    }

    [Fact]
    public void An_empty_list_can_still_reach_the_chips_and_leave_them_again()
    {
        var browser = OnCommunity(0);
        Assert.Equal(-1, browser.CommunitySelected);
        browser.Update(Down);
        Assert.False(browser.CommunityChipFocused);
        browser.Update(Up);
        Assert.True(browser.CommunityChipFocused);
        browser.Update(Down);
        Assert.False(browser.CommunityChipFocused);          // Up and Down toggle
        browser.Update(Up);
        Press(browser, Right, 4);
        browser.Update(X);
        Assert.Equal(3, browser.CommunityCategory);
        Assert.Equal(-1, browser.CommunitySelected);         // there is still no card
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());
        // The list that arrives takes the cards in, and the cursor goes to the first one.
        browser.SetCommunityCount(3);
        Assert.Equal(0, browser.CommunitySelected);
        browser.Update(Down);
        browser.Update(X);
        Assert.Equal(ThemeAction.CommunityDownload, browser.TakeAction());
    }

    [Fact]
    public void Leaving_the_store_tab_puts_the_focus_back_on_the_cards()
    {
        var browser = OnCommunity(4);
        browser.Update(Up);
        browser.Update(UiInput.None with { PageDown = true });
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.Community, browser.View);
        Assert.False(browser.CommunityChipFocused);
        browser.Update(Up);
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.False(browser.CommunityChipFocused);
    }

    // ---- Dialogs: Apply -----------------------------------------------------------------------------

    private static ThemeBrowser OnDetail(int packs = 1)
    {
        var browser = new ThemeBrowser();
        browser.SetCount(packs);
        browser.Update(X);
        Assert.Equal(ThemeView.Detail, browser.View);
        return browser;
    }

    [Fact]
    public void X_on_the_detail_opens_the_apply_dialog_and_does_not_confirm_it()
    {
        var browser = OnDetail();
        Assert.Equal(DialogKind.None, browser.Dialog);
        browser.Update(X);   // one Update: it opens the dialog and must not also take its first option
        Assert.Equal(DialogKind.Apply, browser.Dialog);
        Assert.Equal(0, browser.DialogFocus);
        Assert.Equal(new[] { "Apply theme", "Cancel" }, browser.DialogOptions);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Only_a_second_X_on_the_focused_option_applies()
    {
        var browser = OnDetail();
        browser.Update(X);
        browser.Update(UiInput.None);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.Apply, browser.Dialog);

        browser.Update(X);   // on "Apply theme"
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.False(browser.ApplyAsksScope);
        Assert.False(browser.ApplyAllApps);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(ThemeView.Detail, browser.View);
    }

    [Fact]
    public void Cancel_option_and_circle_close_the_dialog_without_an_action()
    {
        var browser = OnDetail();
        browser.Update(X);
        browser.Update(Right);                       // Cancel
        Assert.Equal(1, browser.DialogFocus);
        browser.Update(X);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X);
        browser.Update(O);                           // O closes, and stays on the detail
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.Detail, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X);                           // a fresh dialog starts on the first option again
        Assert.Equal(0, browser.DialogFocus);
    }

    [Fact]
    public void Dialog_focus_moves_with_all_four_directions_and_stops_at_the_ends()
    {
        var browser = OnDetail();
        browser.Update(X, asksScope: true);          // three options
        Assert.Equal(3, browser.DialogOptions.Count);
        browser.Update(Left);
        browser.Update(Up);
        Assert.Equal(0, browser.DialogFocus);
        browser.Update(Right);
        Assert.Equal(1, browser.DialogFocus);
        browser.Update(Down);
        browser.Update(Down);
        browser.Update(Right);
        Assert.Equal(2, browser.DialogFocus);
        browser.Update(Up);
        Assert.Equal(1, browser.DialogFocus);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Every_other_button_is_ignored_while_a_dialog_is_open()
    {
        var browser = OnDetail();
        browser.Update(X);
        browser.Update(UiInput.None, preview: true);
        browser.Update(UiInput.None, options: true);
        browser.Update(UiInput.None, triangle: true);
        browser.Update(UiInput.None with { PageDown = true });
        browser.Update(UiInput.None with { PageUp = true });
        browser.Update(UiInput.None with { Home = true, End = true });
        Assert.Equal(DialogKind.Apply, browser.Dialog);
        Assert.Equal(ThemeView.Detail, browser.View);
        Assert.Equal(0, browser.DialogFocus);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Triangle_is_no_longer_a_shortcut_for_all_apps()
    {
        var browser = OnDetail();
        browser.Update(UiInput.None, triangle: true, asksScope: true);   // on the detail: nothing
        Assert.Equal(DialogKind.None, browser.Dialog);
        browser.Update(X, asksScope: true);
        browser.Update(UiInput.None, triangle: true, asksScope: true);   // in the dialog: ignored
        Assert.Equal(DialogKind.ApplyScope, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void A_pack_with_an_all_apps_background_asks_where_it_goes_and_the_answer_is_taken()
    {
        var browser = OnDetail();
        browser.Update(X, asksScope: true);
        Assert.Equal(DialogKind.ApplyScope, browser.Dialog);
        Assert.Equal(new[] { "Apps without their own image", "All apps", "Cancel" }, browser.DialogOptions);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X, asksScope: true);   // first option: apps without their own image
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.True(browser.ApplyAsksScope);
        Assert.False(browser.ApplyAllApps);

        browser.Update(X, asksScope: true);
        browser.Update(Right, asksScope: true);
        browser.Update(X, asksScope: true);   // "All apps"
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.True(browser.ApplyAsksScope);
        Assert.True(browser.ApplyAllApps);

        browser.Update(X);                    // a pack without an all-apps background: plain apply, no answer
        browser.Update(X);
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.False(browser.ApplyAsksScope);
        Assert.False(browser.ApplyAllApps);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void The_scope_dialog_starts_on_the_saved_scope(bool allAppsScope, int focus)
    {
        var browser = OnDetail();
        browser.Update(X, asksScope: true, allAppsScope: allAppsScope);
        Assert.Equal(focus, browser.DialogFocus);
        browser.Update(X, asksScope: true, allAppsScope: allAppsScope);   // taking it as it stands keeps the saved scope
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.Equal(allAppsScope, browser.ApplyAllApps);
    }

    [Fact]
    public void Cancel_in_the_scope_dialog_applies_nothing()
    {
        var browser = OnDetail();
        browser.Update(X, asksScope: true);
        browser.Update(Right, asksScope: true);
        browser.Update(Right, asksScope: true);
        browser.Update(X, asksScope: true);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    // ---- Dialogs: Restore, and the System list ------------------------------------------------------

    private static ThemeBrowser OnSystemTab(string storeUrl = "")
    {
        var browser = new ThemeBrowser { StoreUrl = storeUrl };
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.System, browser.View);
        return browser;
    }

    private static ThemeBrowser OnRestoreRow()
    {
        var browser = OnSystemTab();
        for (int i = 0; i < 3; i++) browser.Update(Down);
        Assert.Equal((int)SystemRow.Restore, browser.SystemSelected);
        return browser;
    }

    [Fact]
    public void System_rows_move_with_up_and_down_and_stop_at_the_ends()
    {
        var browser = OnSystemTab();
        Assert.Equal(0, browser.SystemSelected);
        browser.Update(Up);
        Assert.Equal(0, browser.SystemSelected);
        for (int expected = 1; expected < ThemeBrowser.SystemRowCount; expected++)
        {
            browser.Update(Down);
            Assert.Equal(expected, browser.SystemSelected);
        }
        browser.Update(Down);
        Assert.Equal(ThemeBrowser.SystemRowCount - 1, browser.SystemSelected);
        browser.Update(Up);
        Assert.Equal(ThemeBrowser.SystemRowCount - 2, browser.SystemSelected);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Each_system_row_does_its_own_thing_on_X()
    {
        var browser = OnSystemTab("http://x");
        browser.Update(X);                                          // App backgrounds
        Assert.Equal(ThemeAction.ToggleBackgroundScope, browser.TakeAction());
        Assert.Equal(ThemeView.System, browser.View);

        browser.Update(Down);
        browser.Update(X);                                          // Store address
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal("http://x", browser.StoreEditor.Text);         // the X only opened it: nothing typed yet
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(O);

        browser.Update(Down);
        browser.Update(X);                                          // Firmware report
        Assert.Equal(ThemeAction.FirmwareReport, browser.TakeAction());
        Assert.Equal(ThemeView.System, browser.View);
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(Down);
        browser.Update(X);                                          // Restore defaults only asks
        Assert.Equal(DialogKind.Restore, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void The_old_system_hotkeys_are_gone()
    {
        var browser = OnSystemTab("http://x");
        browser.Update(UiInput.None, preview: true);
        browser.Update(UiInput.None, triangle: true);
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeView.System, browser.View);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void System_keeps_O_for_the_library_and_the_shoulders_for_the_tabs()
    {
        var browser = OnSystemTab();
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.System, browser.View);
        browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(ThemeView.Library, browser.View);
    }

    [Fact]
    public void The_restore_dialog_starts_on_cancel_and_only_a_chosen_restore_emits_the_action()
    {
        var browser = OnRestoreRow();
        browser.Update(X);
        Assert.Equal(DialogKind.Restore, browser.Dialog);
        Assert.Equal(new[] { "Restore defaults", "Cancel" }, browser.DialogOptions);
        Assert.Equal(1, browser.DialogFocus);                       // Cancel
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X);                                          // a reflex X on the focused Cancel closes it
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X);
        browser.Update(O);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.System, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.Update(X);
        browser.Update(Up);                                         // "Restore defaults"
        browser.Update(X);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.RestoreOriginals, browser.TakeAction());
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(ThemeView.System, browser.View);
    }

    [Fact]
    public void Other_buttons_only_leave_the_restore_dialog_open()
    {
        var browser = OnRestoreRow();
        browser.Update(X);
        browser.Update(UiInput.None, preview: true);
        browser.Update(UiInput.None, options: true);
        browser.Update(UiInput.None, triangle: true);
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(DialogKind.Restore, browser.Dialog);
        Assert.Equal(ThemeView.System, browser.View);
        Assert.Equal(1, browser.DialogFocus);
    }

    // ---- Busy ---------------------------------------------------------------------------------------

    [Fact]
    public void While_busy_X_on_the_detail_opens_no_dialog_and_asks_for_patience()
    {
        var browser = OnDetail();
        browser.Update(X, busy: true);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        browser.Update(X, asksScope: true, busy: true);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        browser.Update(X);                                          // once idle, it opens again
        Assert.Equal(DialogKind.Apply, browser.Dialog);
    }

    [Fact]
    public void While_busy_the_system_rows_emit_no_system_changing_action()
    {
        var browser = OnSystemTab();
        browser.Update(X, busy: true);                              // App backgrounds
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        browser.Update(Down);
        browser.Update(Down);
        browser.Update(X, busy: true);                              // Firmware report
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        browser.Update(Down);
        browser.Update(X, busy: true);                              // Restore defaults
        Assert.Equal(ThemeAction.Busy, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.System, browser.View);
    }

    [Fact]
    public void The_store_address_row_still_opens_the_keyboard_while_busy()
    {
        var browser = OnSystemTab();
        browser.Update(Down);
        browser.Update(X, busy: true);
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void A_dialog_that_was_open_when_the_app_got_busy_cannot_confirm_an_action()
    {
        var detail = OnDetail();
        detail.Update(X);
        detail.Update(X, busy: true);                               // "Apply theme", but a job started meanwhile
        Assert.Equal(DialogKind.None, detail.Dialog);
        Assert.Equal(ThemeAction.Busy, detail.TakeAction());

        var scope = OnDetail();
        scope.Update(X, asksScope: true);
        scope.Update(Right, asksScope: true, busy: true);
        scope.Update(X, asksScope: true, busy: true);               // "All apps"
        Assert.Equal(ThemeAction.Busy, scope.TakeAction());

        var restore = OnRestoreRow();
        restore.Update(X);
        restore.Update(Up);
        restore.Update(X, busy: true);                              // "Restore defaults"
        Assert.Equal(DialogKind.None, restore.Dialog);
        Assert.Equal(ThemeAction.Busy, restore.TakeAction());
        Assert.Equal(ThemeAction.None, restore.TakeAction());

        var cancel = OnRestoreRow();
        cancel.Update(X);
        cancel.Update(X, busy: true);                               // Cancel needs nothing from the app
        Assert.Equal(ThemeAction.None, cancel.TakeAction());
    }

    [Fact]
    public void A_new_library_closes_an_open_dialog()
    {
        var browser = OnDetail(2);
        browser.Update(X);
        Assert.Equal(DialogKind.Apply, browser.Dialog);
        browser.SetCount(3);                                        // the list changed: it may be another pack now
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    // ---- Store address keyboard -------------------------------------------------------------------

    // The keyboard is the Store address row of the System list.
    private static ThemeBrowser Editing(string storeUrl = "")
    {
        var browser = OnSystemTab(storeUrl);
        browser.Update(Down);
        browser.Update(X);
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        return browser;
    }

    [Fact]
    public void The_store_address_row_opens_the_keyboard_with_the_current_address()
    {
        var browser = Editing("http://10.0.0.22:8080");
        Assert.Equal("http://10.0.0.22:8080", browser.StoreEditor.Text);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
    }

    [Fact]
    public void The_keyboard_opens_empty_when_no_store_is_set()
    {
        Assert.Equal("", Editing().StoreEditor.Text);
    }

    [Fact]
    public void Square_elsewhere_does_not_open_the_keyboard()
    {
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageDown = true });   // Community
        browser.TakeAction();
        browser.Update(UiInput.None with { PageDown = true });   // Import
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Import, browser.View);
    }

    [Fact]
    public void Keyboard_buttons_move_type_and_delete()
    {
        var browser = Editing();                                   // focus starts on "http://"
        browser.Update(X);
        Assert.Equal("http://", browser.StoreEditor.Text);
        browser.Update(Right);
        browser.Update(X);
        Assert.Equal("http://https://", browser.StoreEditor.Text);
        browser.Update(UiInput.None, preview: true);               // Square = backspace
        Assert.Equal("http://https:/", browser.StoreEditor.Text);
        browser.Update(Up);
        Assert.Equal(3, browser.StoreEditor.Row);
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Circle_discards_the_edit_and_returns_to_system_without_an_action()
    {
        var browser = Editing("http://old");
        browser.Update(X);
        browser.Update(O);
        Assert.Equal(ThemeView.System, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal("http://old", browser.StoreUrl);
        browser.Update(X);                                         // the row is still focused: opening again starts from the saved address
        Assert.Equal("http://old", browser.StoreEditor.Text);
    }

    [Fact]
    public void Options_returns_to_system_and_asks_the_app_to_save_the_text()
    {
        var browser = Editing("http://old");
        browser.Update(X);                                         // focus on the digits: appends "1"
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeView.System, browser.View);
        Assert.Equal(ThemeAction.SaveStoreUrl, browser.TakeAction());
        Assert.Equal("http://old1", browser.StoreEditor.Text);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Options_in_the_keyboard_does_not_also_reload_the_library()
    {
        var browser = Editing();
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.SaveStoreUrl, browser.TakeAction());
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Tab_and_jump_buttons_are_ignored_while_typing()
    {
        var browser = Editing();
        browser.Update(UiInput.None with { PageDown = true });
        browser.Update(UiInput.None with { PageUp = true });
        browser.Update(UiInput.None with { Home = true, End = true });
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Confirm_types_in_the_keyboard_instead_of_opening_a_dialog()
    {
        var browser = Editing();
        browser.Update(X);
        browser.Update(X);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal("http://http://", browser.StoreEditor.Text);
    }

    [Fact]
    public void A_refused_save_goes_back_into_the_keyboard_with_text_focus_and_notice()
    {
        var browser = Editing();
        browser.Update(Right);
        browser.Update(X);                                         // "https://"
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.SaveStoreUrl, browser.TakeAction());
        browser.ResumeStoreAddress("Es fehlt etwas.");
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal("https://", browser.StoreEditor.Text);
        Assert.Equal((4, 1), (browser.StoreEditor.Row, browser.StoreEditor.Column));
        Assert.Equal("Es fehlt etwas.", browser.StoreEditor.Notice);
    }

    [Fact]
    public void OpenStoreAddress_replaces_the_text_and_closes_an_open_dialog()
    {
        var browser = OnRestoreRow();
        browser.Update(X);
        Assert.Equal(DialogKind.Restore, browser.Dialog);
        browser.OpenStoreAddress("http://a", "Hinweis");
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal("http://a", browser.StoreEditor.Text);
        Assert.Equal("Hinweis", browser.StoreEditor.Notice);
    }
}
