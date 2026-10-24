using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>The System tab's App update row and the question behind it.</summary>
public class AppUpdateDialogTests
{
    private static readonly UiInput X = new(false, false, false, false, true, false);
    private static readonly UiInput O = new(false, false, false, false, false, true);
    private static readonly UiInput Down = new(false, true, false, false, false, false);
    private static readonly UiInput Up = new(true, false, false, false, false, false);

    private static ThemeBrowser OnUpdateRow()
    {
        var browser = new ThemeBrowser();
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.System, browser.View);
        while (browser.SystemSelected != (int)SystemRow.AppUpdate) browser.Update(Down);
        return browser;
    }

    [Fact]
    public void The_row_sits_between_firmware_report_and_restore_defaults()
    {
        Assert.Equal((int)SystemRow.FirmwareReport + 1, (int)SystemRow.AppUpdate);
        Assert.Equal((int)SystemRow.AppUpdate + 1, (int)SystemRow.Restore);
        Assert.Equal((int)SystemRow.Restore + 1, ThemeBrowser.SystemRowCount);
    }

    [Fact]
    public void X_on_the_row_with_a_release_to_install_asks_first_with_the_focus_on_Update_now()
    {
        var browser = OnUpdateRow();

        browser.Update(X, appUpdate: AppUpdate.State.Ready);

        Assert.Equal(DialogKind.AppUpdate, browser.Dialog);
        Assert.Equal(["Update now", "Cancel"], browser.DialogOptions);
        Assert.Equal(0, browser.DialogFocus);
        Assert.Equal(ThemeAction.None, browser.TakeAction());   // the X that opened it does not also confirm it
    }

    [Fact]
    public void Update_now_starts_the_update_and_closes_the_question()
    {
        var browser = OnUpdateRow();
        browser.Update(X, appUpdate: AppUpdate.State.Ready);

        browser.Update(X, appUpdate: AppUpdate.State.Ready);

        Assert.Equal(ThemeAction.UpdateApp, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
    }

    [Fact]
    public void Cancel_and_Circle_change_nothing()
    {
        var browser = OnUpdateRow();
        browser.Update(X, appUpdate: AppUpdate.State.Ready);
        browser.Update(Down, appUpdate: AppUpdate.State.Ready);
        Assert.Equal(1, browser.DialogFocus);
        browser.Update(X, appUpdate: AppUpdate.State.Ready);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);

        browser.Update(X, appUpdate: AppUpdate.State.Ready);
        browser.Update(O, appUpdate: AppUpdate.State.Ready);
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeView.System, browser.View);
    }

    [Fact]
    public void While_the_app_is_busy_the_question_does_not_open_and_a_confirmation_becomes_busy()
    {
        var busy = OnUpdateRow();
        busy.Update(X, busy: true, appUpdate: AppUpdate.State.Ready);
        Assert.Equal(ThemeAction.Busy, busy.TakeAction());
        Assert.Equal(DialogKind.None, busy.Dialog);

        var started = OnUpdateRow();
        started.Update(X, appUpdate: AppUpdate.State.Ready);
        started.Update(X, busy: true, appUpdate: AppUpdate.State.Ready);   // a job started while the question was open
        Assert.Equal(ThemeAction.Busy, started.TakeAction());
        Assert.Equal(DialogKind.None, started.Dialog);
    }

    [Theory]
    [InlineData(AppUpdate.State.None)]
    [InlineData(AppUpdate.State.Notice)]
    [InlineData(AppUpdate.State.Installed)]
    public void Without_a_release_to_install_X_only_looks(AppUpdate.State state)
    {
        var browser = OnUpdateRow();

        browser.Update(X, appUpdate: state);

        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.CheckAppUpdate, browser.TakeAction());
    }

    [Fact]
    public void A_scan_that_ends_under_the_question_does_not_close_it()
    {
        var browser = OnUpdateRow();
        browser.SetCount(3);
        browser.Update(X, appUpdate: AppUpdate.State.Ready);

        browser.SetCount(4);

        Assert.Equal(DialogKind.AppUpdate, browser.Dialog);
    }

    [Fact]
    public void The_state_follows_what_the_store_announced()
    {
        var plain = new AppUpdate.Release("01.000.027", "u");
        var withZip = plain with { Zip = new AppUpdate.ZipInfo("/api/app/download/01.000.027", 100, new string('a', 64)) };

        Assert.Equal(AppUpdate.State.None, AppUpdate.StateOf(null, "01.000.026", installed: false));
        Assert.Equal(AppUpdate.State.None, AppUpdate.StateOf(withZip, "01.000.027", installed: false));   // not newer
        Assert.Equal(AppUpdate.State.Notice, AppUpdate.StateOf(plain, "01.000.026", installed: false));
        Assert.Equal(AppUpdate.State.Ready, AppUpdate.StateOf(withZip, "01.000.026", installed: false));
        Assert.Equal(AppUpdate.State.Installed, AppUpdate.StateOf(withZip, "01.000.026", installed: true));
        Assert.Equal(AppUpdate.State.Installed, AppUpdate.StateOf(null, "01.000.026", installed: true));
    }
}
