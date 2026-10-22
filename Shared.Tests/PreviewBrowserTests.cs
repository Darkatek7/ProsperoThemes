using System.Linq;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The preview's navigation, which lives in <see cref="ThemeBrowser"/>: the d-pad along the home row or the control
/// centre, L1 and R1 between Games and Media, Triangle between the two scenes, X for the overlay, Square for the music,
/// Circle back; and the sound each press asks for.
/// </summary>
public class PreviewBrowserTests
{
    private static readonly UiInput Right = UiInput.None with { Right = true }, Left = UiInput.None with { Left = true },
        Up = UiInput.None with { Up = true }, Down = UiInput.None with { Down = true }, X = UiInput.None with { Confirm = true },
        O = UiInput.None with { Cancel = true }, L1 = UiInput.None with { PageUp = true }, R1 = UiInput.None with { PageDown = true };

    // A preview of a pack with 8 tiles in Games and 7 in Media, opened from the Library.
    private static ThemeBrowser Open(int games = 8, int media = 7)
    {
        var browser = new ThemeBrowser();
        browser.SetCount(1);
        browser.SetPreviewLayout(games, media);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Preview, browser.View);
        return browser;
    }

    private static void Press(ThemeBrowser browser, UiInput input, int times)
    {
        for (int i = 0; i < times; i++) browser.Update(input);
    }

    [Fact]
    public void The_preview_opens_on_the_first_tile_of_games_with_the_overlay_shown_and_makes_no_sound()
    {
        ThemeBrowser browser = Open();

        Assert.Equal(PreviewScene.Home, browser.PreviewScene);
        Assert.Equal(PreviewTab.Games, browser.PreviewTab);
        Assert.Equal(0, browser.PreviewFocus);
        Assert.Equal(0, browser.PreviewButton);
        Assert.True(browser.ShowOverlay);
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());
        Assert.Equal(ThemeAction.None, browser.TakeAction());
    }

    [Fact]
    public void The_dpad_moves_along_the_row_with_the_move_sound_and_stops_at_its_ends_with_the_end_sound()
    {
        ThemeBrowser browser = Open();

        browser.Update(Right);
        Assert.Equal(1, browser.PreviewFocus);
        Assert.Equal(PreviewSound.Move, browser.TakePreviewSound());
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());   // taken once

        Press(browser, Right, 20);
        browser.TakePreviewSound();
        Assert.Equal(7, browser.PreviewFocus);
        browser.Update(Right);
        Assert.Equal(7, browser.PreviewFocus);
        Assert.Equal(PreviewSound.End, browser.TakePreviewSound());

        Press(browser, Left, 20);
        browser.TakePreviewSound();
        Assert.Equal(0, browser.PreviewFocus);
        browser.Update(Left);
        Assert.Equal(0, browser.PreviewFocus);
        Assert.Equal(PreviewSound.End, browser.TakePreviewSound());
    }

    [Fact]
    public void Up_and_down_and_the_buttons_that_do_nothing_here_do_nothing()
    {
        ThemeBrowser browser = Open();
        browser.Update(Right);
        browser.TakePreviewSound();

        foreach (UiInput input in new[] { Up, Down, UiInput.None with { Home = true }, UiInput.None with { End = true } }) browser.Update(input);
        browser.Update(UiInput.None, options: true);

        Assert.Equal(1, browser.PreviewFocus);
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(ThemeView.Preview, browser.View);
    }

    [Fact]
    public void L1_and_r1_switch_the_sections_and_each_remembers_its_own_focus()
    {
        ThemeBrowser browser = Open();
        Press(browser, Right, 3);
        browser.TakePreviewSound();

        browser.Update(R1);
        Assert.Equal(PreviewTab.Media, browser.PreviewTab);
        Assert.Equal(0, browser.PreviewFocus);
        Assert.Equal(PreviewSound.Move, browser.TakePreviewSound());

        Press(browser, Right, 100);
        Assert.Equal(6, browser.PreviewFocus);   // seven tiles in Media
        browser.TakePreviewSound();

        browser.Update(R1);   // already in the last section
        Assert.Equal(PreviewTab.Media, browser.PreviewTab);
        Assert.Equal(PreviewSound.End, browser.TakePreviewSound());

        browser.Update(L1);
        Assert.Equal(PreviewTab.Games, browser.PreviewTab);
        Assert.Equal(3, browser.PreviewFocus);
        Assert.Equal(PreviewSound.Move, browser.TakePreviewSound());

        browser.Update(L1);
        Assert.Equal(PreviewSound.End, browser.TakePreviewSound());
    }

    [Fact]
    public void Triangle_goes_between_the_home_row_and_the_control_centre_which_has_its_own_twelve_buttons()
    {
        ThemeBrowser browser = Open();
        Press(browser, Right, 2);

        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(PreviewScene.ControlCentre, browser.PreviewScene);
        Assert.Equal(PreviewSound.Confirm, browser.TakePreviewSound());
        Assert.Equal(0, browser.PreviewButton);

        Press(browser, Right, 40);
        Assert.Equal(PreviewModel.ControlSlots.Count - 1, browser.PreviewButton);
        Assert.Equal(11, browser.PreviewButton);
        browser.TakePreviewSound();
        browser.Update(Right);
        Assert.Equal(PreviewSound.End, browser.TakePreviewSound());
        Press(browser, Left, 3);
        Assert.Equal(8, browser.PreviewButton);
        Assert.Equal(2, browser.PreviewFocus);   // the tiles' focus is where it was

        // L1 and R1 are for the sections: they do nothing among the buttons.
        browser.TakePreviewSound();
        browser.Update(R1);
        Assert.Equal(PreviewTab.Games, browser.PreviewTab);
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());

        browser.Update(UiInput.None, triangle: true);
        Assert.Equal(PreviewScene.Home, browser.PreviewScene);
        Assert.Equal(PreviewSound.Confirm, browser.TakePreviewSound());
        Assert.Equal(8, browser.PreviewButton);   // and the buttons' focus is kept for the next visit
    }

    [Fact]
    public void Circle_leaves_the_control_centre_first_and_then_the_preview_for_where_it_came_from()
    {
        ThemeBrowser browser = Open();
        browser.Update(UiInput.None, triangle: true);
        browser.TakePreviewSound();

        browser.Update(O);
        Assert.Equal(ThemeView.Preview, browser.View);
        Assert.Equal(PreviewScene.Home, browser.PreviewScene);
        Assert.Equal(PreviewSound.Back, browser.TakePreviewSound());

        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.Equal(PreviewSound.Back, browser.TakePreviewSound());   // the back clip of leaving is asked for as well

        // From the Detail screen the preview goes back to the Detail screen.
        browser.Update(X);
        Assert.Equal(ThemeView.Detail, browser.View);
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeView.Preview, browser.View);
        browser.Update(O);
        Assert.Equal(ThemeView.Detail, browser.View);
    }

    [Fact]
    public void X_hides_the_overlay_and_shows_it_again_and_while_it_is_hidden_only_x_circle_and_square_count()
    {
        ThemeBrowser browser = Open();

        browser.Update(X);
        Assert.False(browser.ShowOverlay);
        Assert.Equal(PreviewSound.Confirm, browser.TakePreviewSound());

        Press(browser, Right, 3);
        browser.Update(UiInput.None, triangle: true);
        browser.Update(R1);
        Assert.Equal(0, browser.PreviewFocus);
        Assert.Equal(PreviewScene.Home, browser.PreviewScene);
        Assert.Equal(PreviewTab.Games, browser.PreviewTab);
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());

        browser.Update(UiInput.None, preview: true);   // the music is Square, overlay or not
        Assert.Equal(ThemeAction.PreviewMusic, browser.TakeAction());

        browser.Update(X);
        Assert.True(browser.ShowOverlay);
        Assert.Equal(PreviewSound.Confirm, browser.TakePreviewSound());

        browser.Update(X);
        browser.Update(O);   // Circle leaves from a hidden overlay too
        Assert.Equal(ThemeView.Library, browser.View);
    }

    [Fact]
    public void Square_asks_for_the_music_and_no_longer_leaves_the_preview()
    {
        ThemeBrowser browser = Open();
        browser.Update(Right);
        browser.TakePreviewSound();

        browser.Update(UiInput.None, preview: true);

        Assert.Equal(ThemeView.Preview, browser.View);
        Assert.Equal(ThemeAction.PreviewMusic, browser.TakeAction());
        Assert.Equal(ThemeAction.None, browser.TakeAction());
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());
        Assert.Equal(1, browser.PreviewFocus);

        // In the control centre as well.
        browser.Update(UiInput.None, triangle: true);
        browser.TakePreviewSound();
        browser.Update(UiInput.None, preview: true);
        Assert.Equal(ThemeAction.PreviewMusic, browser.TakeAction());
    }

    [Fact]
    public void A_new_preview_starts_from_the_beginning()
    {
        ThemeBrowser browser = Open();
        Press(browser, Right, 4);
        browser.Update(R1);
        browser.Update(UiInput.None, triangle: true);
        Press(browser, Right, 5);
        browser.Update(X);
        browser.Update(X);
        browser.Update(O);
        browser.Update(O);
        Assert.Equal(ThemeView.Library, browser.View);
        browser.TakePreviewSound();

        browser.Update(UiInput.None, preview: true);

        Assert.Equal(PreviewScene.Home, browser.PreviewScene);
        Assert.Equal(PreviewTab.Games, browser.PreviewTab);
        Assert.Equal((0, 0), (browser.PreviewFocus, browser.PreviewButton));
        Assert.True(browser.ShowOverlay);
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());
    }

    [Fact]
    public void The_layout_keeps_the_focus_inside_the_rows_and_a_row_always_has_a_tile()
    {
        ThemeBrowser browser = Open(games: 8, media: 7);
        Press(browser, Right, 7);
        browser.Update(R1);
        Press(browser, Right, 5);
        Assert.Equal(5, browser.PreviewFocus);

        browser.SetPreviewLayout(games: 3, media: 2);   // another pack's shorter rows
        Assert.Equal(1, browser.PreviewFocus);
        browser.Update(L1);
        Assert.Equal(2, browser.PreviewFocus);

        browser.SetPreviewLayout(games: 0, media: -4);
        Assert.Equal(0, browser.PreviewFocus);
        browser.Update(Right);
        Assert.Equal(0, browser.PreviewFocus);   // one tile at least: nothing to move to
    }

    [Fact]
    public void Nothing_in_the_preview_makes_a_sound_before_it_is_open_or_after_it_is_left()
    {
        var browser = new ThemeBrowser();
        browser.SetCount(1);
        browser.Update(Right);
        browser.Update(X);
        Assert.Equal(ThemeView.Detail, browser.View);
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());
        browser.Update(O);
        Assert.Equal(PreviewSound.None, browser.TakePreviewSound());
    }
}
