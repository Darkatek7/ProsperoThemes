using ProsperoThemes.App.Ui;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class ThemeBrowserTests
{
    private static UiInput Input(bool right = false, bool left = false, bool confirm = false, bool cancel = false) =>
        new(false, false, left, right, confirm, cancel);

    [Fact]
    public void Selection_stops_at_library_edges()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(2);
        browser.Update(Input(left: true));
        Assert.Equal(0, browser.Selected);
        browser.Update(Input(right: true));
        browser.Update(Input(right: true));
        Assert.Equal(1, browser.Selected);
    }

    [Fact]
    public void Preview_returns_to_the_detail_that_opened_it()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(2);
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeView.Detail, browser.View);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Preview, browser.View);
        browser.Update(Input(cancel: true));
        Assert.Equal(ThemeView.Detail, browser.View);
        browser.Update(Input(cancel: true));
        Assert.Equal(ThemeView.Library, browser.View);
    }

    [Fact]
    public void Empty_library_cannot_open_detail_or_preview()
    {
        var browser = new ThemeBrowser();
        browser.Update(Input(confirm: true), preview: true);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.Equal(-1, browser.Selected);
    }

    [Fact]
    public void Reload_clamps_focus_when_packs_disappear()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(3);
        browser.Update(Input(right: true));
        browser.Update(Input(right: true));
        browser.Update(Input(confirm: true));
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
        browser.Update(Input(confirm: true));
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
        browser.Update(Input(confirm: true));
        Assert.False(browser.ShowOverlay);
        Assert.Equal(ThemeView.Preview, browser.View);
    }

    [Fact]
    public void An_all_apps_pack_asks_where_its_background_goes()
    {
        var browser = new ThemeBrowser(); browser.SetCount(1);
        browser.Update(Input(confirm: true), asksScope: true);
        browser.Update(Input(confirm: true), asksScope: true);
        Assert.True(browser.ApplyAsksScope);
        browser.Update(Input(), triangle: true, asksScope: true);
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.True(browser.ApplyAllApps);
        Assert.Equal(ThemeAction.None, browser.Pending);

        browser.Update(Input(confirm: true), asksScope: true);
        browser.Update(Input(confirm: true), asksScope: true);
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.False(browser.ApplyAllApps);
    }

    [Fact]
    public void Triangle_only_answers_an_apply_that_asks()
    {
        var browser = new ThemeBrowser(); browser.SetCount(1);
        browser.Update(Input(confirm: true));
        browser.Update(Input(confirm: true));
        browser.Update(Input(), triangle: true);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(ThemeAction.None, browser.Pending);
    }

    [Fact]
    public void Apply_needs_a_second_confirm_and_cancel_disarms_it()
    {
        var browser = new ThemeBrowser(); browser.SetCount(1);
        browser.Update(Input(confirm: true));
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeAction.Apply, browser.Pending);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(Input(cancel: true));
        Assert.Equal(ThemeAction.None, browser.Pending);
        Assert.Equal(ThemeView.Detail, browser.View);
        browser.Update(Input(confirm: true));
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeAction.Apply, browser.TakeAction());
        Assert.Equal(ThemeAction.None, browser.Pending);
    }

    [Fact]
    public void System_tab_restores_originals_only_after_second_confirm()
    {
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.System, browser.View);
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeAction.RestoreOriginals, browser.TakeAction());
    }

    [Fact]
    public void Detail_scroll_returns_immediately_from_the_last_page()
    {
        var browser = new ThemeBrowser(); browser.SetCount(1);
        browser.Update(Input(confirm: true));
        for (int i = 0; i < 10; i++) browser.Update(new UiInput(false, true, false, false, false, false), targetCount: 6);
        browser.Update(new UiInput(true, false, false, false, false, false), targetCount: 6);
        Assert.Equal(0, browser.TargetOffset);
    }

    [Fact]
    public void Community_tab_moves_in_a_grid_downloads_likes_and_cycles_sort()
    {
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageDown = true });
        browser.TakeAction();
        browser.SetCommunityCount(10);
        Assert.Equal(0, browser.CommunitySelected);
        browser.Update(new UiInput(false, true, false, false, false, false));   // down = next row
        Assert.Equal(4, browser.CommunitySelected);
        browser.Update(Input(right: true));
        Assert.Equal(5, browser.CommunitySelected);
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeAction.CommunityDownload, browser.TakeAction());
        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(ThemeAction.CommunityLike, browser.TakeAction());
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(1, browser.CommunitySort);
        Assert.Equal(ThemeAction.CommunityRefresh, browser.TakeAction());
        Assert.Equal(0, browser.CommunitySelected);
    }

    // ---- Store address keyboard -------------------------------------------------------------------

    private static ThemeBrowser OnSystemTab(string storeUrl = "")
    {
        var browser = new ThemeBrowser { StoreUrl = storeUrl };
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.System, browser.View);
        return browser;
    }

    private static ThemeBrowser Editing(string storeUrl = "")
    {
        var browser = OnSystemTab(storeUrl);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        return browser;
    }

    [Fact]
    public void Square_on_the_system_tab_opens_the_keyboard_with_the_current_address()
    {
        var browser = OnSystemTab("http://10.0.0.22:8080");
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal("http://10.0.0.22:8080", browser.StoreEditor.Text);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(ThemeAction.None, browser.Pending);
    }

    [Fact]
    public void Square_opens_an_empty_keyboard_when_no_store_is_set()
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
        browser.Update(Input(confirm: true));
        Assert.Equal("http://", browser.StoreEditor.Text);
        browser.Update(Input(right: true));
        browser.Update(Input(confirm: true));
        Assert.Equal("http://https://", browser.StoreEditor.Text);
        browser.Update(UiInput.None, preview: true);               // Square = backspace
        Assert.Equal("http://https:/", browser.StoreEditor.Text);
        browser.Update(new UiInput(true, false, false, false, false, false));   // up
        Assert.Equal(3, browser.StoreEditor.Row);
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void Circle_discards_the_edit_and_returns_to_system_without_an_action()
    {
        var browser = Editing("http://old");
        browser.Update(Input(confirm: true));
        browser.Update(Input(cancel: true));
        Assert.Equal(ThemeView.System, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal("http://old", browser.StoreUrl);
        browser.Update(UiInput.None, preview: true);               // opening again starts from the saved address
        Assert.Equal("http://old", browser.StoreEditor.Text);
    }

    [Fact]
    public void Options_returns_to_system_and_asks_the_app_to_save_the_text()
    {
        var browser = Editing("http://old");
        browser.Update(Input(confirm: true));                      // focus on the digits: appends "1"
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
    public void Confirm_types_in_the_keyboard_instead_of_arming_a_restore()
    {
        var browser = Editing();
        browser.Update(Input(confirm: true));
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeAction.None, browser.Pending);
        Assert.Equal("http://http://", browser.StoreEditor.Text);
    }

    [Fact]
    public void Square_while_a_restore_is_armed_only_disarms_it()
    {
        var browser = OnSystemTab("http://x");
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeAction.RestoreOriginals, browser.Pending);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeAction.None, browser.Pending);
        Assert.Equal(ThemeView.System, browser.View);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        // The restore still needs its own two presses afterwards.
        browser.Update(Input(confirm: true));
        browser.Update(Input(confirm: true));
        Assert.Equal(ThemeAction.RestoreOriginals, browser.TakeAction());
    }

    [Fact]
    public void A_refused_save_goes_back_into_the_keyboard_with_text_focus_and_notice()
    {
        var browser = Editing();
        browser.Update(Input(right: true));
        browser.Update(Input(confirm: true));                      // "https://"
        browser.Update(UiInput.None, options: true);
        Assert.Equal(ThemeAction.SaveStoreUrl, browser.TakeAction());
        browser.ResumeStoreAddress("Es fehlt etwas.");
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal("https://", browser.StoreEditor.Text);
        Assert.Equal((4, 1), (browser.StoreEditor.Row, browser.StoreEditor.Column));
        Assert.Equal("Es fehlt etwas.", browser.StoreEditor.Notice);
    }

    [Fact]
    public void OpenStoreAddress_replaces_the_text_and_clears_an_armed_action()
    {
        var browser = OnSystemTab();
        browser.Update(Input(confirm: true));
        browser.OpenStoreAddress("http://a", "Hinweis");
        Assert.Equal(ThemeView.StoreAddress, browser.View);
        Assert.Equal(ThemeAction.None, browser.Pending);
        Assert.Equal("http://a", browser.StoreEditor.Text);
        Assert.Equal("Hinweis", browser.StoreEditor.Notice);
    }
}
