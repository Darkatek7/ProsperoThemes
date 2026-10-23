using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

public class SystemUpdateTests
{
    private static ThemeLibraryEntry Fixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        return ThemeLibraryEntry.Inspect(path, File.ReadAllBytes(path));
    }

    private static readonly ThemeLibraryEntry Background = Fixture("two-screen.p5theme");
    private static readonly ThemeLibraryEntry Sound = Fixture("audio-layer.p5theme");

    private static string State(string id, string action = "apply", string firmware = "12.400.000") =>
        $$"""{"pack":{"id":"{{id}}","name":"N","version":"1"},"action":"{{action}}","firmware":"{{firmware}}","targets":[]}""";

    private static string LookOf(params ThemeLibraryEntry[] entries) =>
        Look.Serialize(entries.Select(e => new LookLayer(e.Path, e.Manifest.Id, e.Manifest.Name, ThemeParts.In(e.Manifest))).ToArray());

    [Fact]
    public void Another_system_software_asks_to_apply_the_look_again()
    {
        var library = new[] { Background, Sound };

        SystemUpdate.Plan? plan = SystemUpdate.Check(State(Sound.Manifest.Id), LookOf(Background, Sound), "13.000.040", null, library, sounds: true);

        Assert.NotNull(plan);
        Assert.Equal(new[] { Background.Path, Sound.Path }, plan!.Layers.Select(l => l.Pack));
        Assert.Empty(plan.Missing);
        Assert.Equal("12.400.000", plan.From);
        Assert.StartsWith("The system software changed from 12.40 to 13.00", SystemUpdate.Text(plan, "13.000.040"));
        Assert.Contains("2 themes of your look", SystemUpdate.Text(plan, "13.000.040"));
    }

    [Fact]
    public void Nothing_is_asked_on_the_same_software_after_not_now_or_without_an_applied_theme()
    {
        var library = new[] { Background };
        string look = LookOf(Background);
        string id = Background.Manifest.Id;

        Assert.Null(SystemUpdate.Check(State(id, firmware: "13.000.040"), look, "13.000.040", null, library, true));
        Assert.Null(SystemUpdate.Check(State(id), look, "13.000.040", "13.000.040", library, true));
        Assert.Null(SystemUpdate.Check(State(id, action: "restore-originals"), look, "13.000.040", null, library, true));
        Assert.Null(SystemUpdate.Check(State(id, firmware: ""), look, "13.000.040", null, library, true));
        Assert.Null(SystemUpdate.Check(State(id), look, null, null, library, true));
        Assert.Null(SystemUpdate.Check(null, look, "13.000.040", null, library, true));
        // The packs of the look are gone from the library.
        Assert.Null(SystemUpdate.Check(State(id), look, "13.000.040", null, Array.Empty<ThemeLibraryEntry>(), true));
    }

    [Fact]
    public void A_layer_whose_pack_is_gone_is_named_and_left_out()
    {
        SystemUpdate.Plan? plan = SystemUpdate.Check(State(Background.Manifest.Id), LookOf(Background, Sound), "13.000.040", null,
            new[] { Background }, sounds: true);

        Assert.Equal(new[] { Background.Path }, plan!.Layers.Select(l => l.Pack));
        Assert.Equal(new[] { Sound.Manifest.Name }, plan.Missing);
        Assert.Contains("“" + Background.Manifest.Name + "” again?", SystemUpdate.Text(plan, "13.000.040"));
        Assert.Contains("1 theme(s) of it are no longer in your library", SystemUpdate.Text(plan, "13.000.040"));
    }

    [Fact]
    public void Without_a_look_file_the_applied_pack_is_applied_again_without_sounds_when_they_are_off()
    {
        var library = new[] { Background, Sound };

        SystemUpdate.Plan? plan = SystemUpdate.Check(State(Background.Manifest.Id), null, "13.000.040", null, library, sounds: false);
        Assert.Equal(ThemePart.Background, plan!.Layers.Single().Parts);

        // A sound-only theme with sounds turned off: nothing to apply, so nothing is asked.
        Assert.Null(SystemUpdate.Check(State(Sound.Manifest.Id), null, "13.000.040", null, library, sounds: false));
    }

    [Fact]
    public void Not_now_is_kept_in_the_settings()
    {
        var settings = AppSettings.Parse(null, () => "device-0001") with { ReapplySkipped = "13.000.040" };
        Assert.Equal("13.000.040", AppSettings.Parse(settings.Serialize(), () => "x").ReapplySkipped);
        Assert.Null(AppSettings.Parse("{}", () => "device-0001").ReapplySkipped);
    }

    [Fact]
    public void The_question_starts_on_apply_again_and_every_other_answer_is_not_now()
    {
        var x = new UiInput(false, false, false, false, true, false);
        var o = new UiInput(false, false, false, false, false, true);
        var down = new UiInput(false, true, false, false, false, false);

        var browser = new ThemeBrowser();
        browser.AskReapply();
        Assert.Equal(DialogKind.Reapply, browser.Dialog);
        Assert.Equal(0, browser.DialogFocus);
        // A scan that lands meanwhile keeps the question open (it is about the look, not the selection).
        browser.SetCount(3);
        Assert.Equal(DialogKind.Reapply, browser.Dialog);
        browser.Update(x);
        Assert.Equal(ThemeAction.Reapply, browser.TakeAction());

        browser.AskReapply();
        browser.Update(o);
        Assert.Equal(DialogKind.None, browser.Dialog);
        Assert.Equal(ThemeAction.ReapplyLater, browser.TakeAction());

        browser.AskReapply();
        browser.Update(down);
        browser.Update(x);
        Assert.Equal(ThemeAction.ReapplyLater, browser.TakeAction());
    }
}
