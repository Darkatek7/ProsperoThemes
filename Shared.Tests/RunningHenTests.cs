using ProsperoThemes.App.Ui;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class RunningHenTests
{
    private static readonly UiInput X = new(false, false, false, false, true, false);
    private static readonly UiInput O = new(false, false, false, false, false, true);
    private static readonly UiInput Right = new(false, false, false, true, false, false);
    private static readonly UiInput Down = new(false, true, false, false, false, false);

    [Theory]
    [InlineData("OK HEN onionhen", HenKind.OnionHen)]
    [InlineData("OK HEN etahen", HenKind.EtaHen)]
    [InlineData("OK HEN none", HenKind.None)]
    [InlineData("OK HEN none ", HenKind.None)]
    [InlineData("OK HEN", HenKind.Unknown)]
    [InlineData("OK HEN OnionHEN", HenKind.Unknown)]
    [InlineData("OK HEN somethingnew", HenKind.Unknown)]
    [InlineData("ERR unknown", HenKind.Unknown)]   // a helper from before the command
    [InlineData("", HenKind.Unknown)]                // no answer
    [InlineData(null, HenKind.Unknown)]
    public void The_helper_answer_is_read_and_anything_else_is_unknown(string? reply, HenKind expected) =>
        Assert.Equal(expected, RunningHen.Parse(reply));

    [Theory]
    [InlineData(HenKind.Unknown, true)]
    [InlineData(HenKind.None, true)]
    [InlineData(HenKind.EtaHen, true)]
    [InlineData(HenKind.OnionHen, false)]
    public void Only_onionhen_forbids_the_home_screen_restart(HenKind hen, bool allowed)
    {
        Assert.Equal(allowed, RunningHen.AllowsShellRestart(hen));
        Assert.Equal(allowed, RunningHen.ShellRestartRefusal(hen) is null);
    }

    [Fact]
    public void The_restart_question_is_unchanged_without_onionhen()
    {
        foreach (HenKind hen in new[] { HenKind.Unknown, HenKind.None, HenKind.EtaHen })
        {
            var browser = new ThemeBrowser();
            browser.AskRestartShell(hen);
            Assert.Equal(DialogKind.RestartShell, browser.Dialog);
            Assert.Equal(new[] { "Show now", "Later" }, browser.DialogOptions);
            browser.Update(X);
            Assert.Equal(ThemeAction.RestartShell, browser.TakeAction());
        }
    }

    [Fact]
    public void Under_onionhen_the_question_has_later_only_and_never_restarts()
    {
        var browser = new ThemeBrowser();
        browser.AskRestartShell(HenKind.OnionHen);
        Assert.Equal(DialogKind.RestartShellBlocked, browser.Dialog);
        Assert.Equal(new[] { "Later" }, browser.DialogOptions);
        Assert.Equal(0, browser.DialogFocus);
        browser.SetCount(3);   // a scan meanwhile keeps it, as for the restart question
        Assert.Equal(DialogKind.RestartShellBlocked, browser.Dialog);

        // Moving the focus keeps it on the one option; X closes the question without an action.
        browser.Update(Right);
        browser.Update(Down);
        Assert.Equal(0, browser.DialogFocus);
        browser.Update(X);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        browser.AskRestartShell(HenKind.OnionHen);
        browser.Update(O);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.None, browser.TakeAction());

        // Another question already open keeps the screen.
        browser.AskReapply();
        browser.AskRestartShell(HenKind.OnionHen);
        Assert.Equal(DialogKind.Reapply, browser.Dialog);
    }
}
