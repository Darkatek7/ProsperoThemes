using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;
using SharpProspero.Graphics;
using SharpProspero.Texture;
using SharpProspero.Ui;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5185");
var app = builder.Build();
string assets = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../ThemeApp/assets"));
var entries = Directory.EnumerateFiles(assets, "*.p5theme").Select(p => ThemeLibraryEntry.Inspect(p, File.ReadAllBytes(p)) with { Bundled = true }).ToList();
// Real packs to review with: every pack in PREVIEW_LIBRARY (when set), then the repository's test fixtures.
entries.AddRange(PreviewData.LoadPacks(Environment.GetEnvironmentVariable("PREVIEW_LIBRARY"),
    Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../Shared.Tests/fixtures"))));
var browser = new ThemeBrowser();
browser.SetCount(entries.Count);
using var canvas = new PixelBuffer(1920, 1080);
using var renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(assets, name)), Decode);
renderer.Status = "PC preview";
renderer.LibraryStatus = "This preview uses the same renderer as the native PS5 app.";
var gate = new object();

// The Community tab: a store list from the loaded packs, card 2 already in the library, card 3 with an update.
List<CommunityTheme> store = PreviewData.Store(entries);
renderer.Community = store;
for (int i = 0; i < store.Count; i++)
    if (entries[i].Preview is { } cover) renderer.CommunityPreviews[store[i].Id] = cover;
renderer.CommunityInLibrary = PreviewData.Card(store, 1);
renderer.CommunityUpdates = PreviewData.Card(store, 2);
renderer.CommunityLiked = PreviewData.Card(store, 3);
renderer.CommunityListSort = (int)CommunitySort.Top;
renderer.SelectedCommunityId = store.Count > 0 ? store[0].Id : null;
browser.SetCommunityCount(store.Count);

// What the app does when a list arrives for the browser's sort and category: the store's answer replaces the list, the sort
// and category it was loaded with, and the card count.
void ReloadStore()
{
    List<CommunityTheme> shown = PreviewData.Listing(store, (CommunitySort)browser.CommunitySort, CommunityCategories.IdAt(browser.CommunityCategory));
    renderer.Community = shown;
    renderer.CommunityListSort = browser.CommunitySort;
    renderer.CommunityListCategory = browser.CommunityCategory;
    renderer.CommunityStatus = shown.Count == 0 ? "No themes in this category yet." : "";
    renderer.SelectedCommunityId = shown.Count > 0 ? shown[0].Id : null;
    browser.SetCommunityCount(shown.Count);
}

if (args.Contains("--bench"))
{
    // Frame cost of the shared renderer per view; the console's budget at 60 Hz is 16.7 ms. Each step is one frame of
    // input; the views are measured in this order, so the dialogs open from the detail and system views before them.
    var press = new UiInput(false, false, false, false, true, false);
    var down = new UiInput(false, true, false, false, false, false);
    var cancel = new UiInput(false, false, false, false, false, true);
    var steps = new (string Name, Action Setup)[]
    {
        ("library", () => { }),
        ("detail", () => browser.Update(press)),
        ("dialog-apply-scope", () => browser.Update(press, asksScope: true)),
        ("dialog-apply", () => { browser.Update(cancel); browser.Update(press); }),
        ("detail again", () => browser.Update(cancel)),
        ("system", () => { browser.Update(cancel); browser.Update(UiInput.None with { PageUp = true }); }),
        ("system-restore", () => { for (int i = 0; i < 3; i++) browser.Update(down); }),
        ("dialog-restore", () => browser.Update(press)),
        ("toast", () => { browser.Update(cancel); renderer.Notify("Applied and verified 27 target(s).", NoticeLevel.Success); }),
        ("community", () => { browser.Update(UiInput.None with { PageUp = true }); browser.Update(UiInput.None with { PageUp = true }); renderer.ClearNotice(); }),
        ("community-chips", () => browser.Update(UiInput.None with { Up = true })),   // the first chip focused: its ring instead of the card's
        ("community-chips-end", () => { for (int i = 0; i < ThemeBrowser.CommunityChipCount; i++) browser.Update(UiInput.None with { Right = true }); }),   // the row scrolled to its last chip
    };
    foreach (var (name, setup) in steps)
    {
        setup();
        for (int i = 0; i < 20; i++) renderer.Draw(canvas.AsSurface(), browser, entries, 3 + i / 60.0, 1 / 60.0);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int frames = 200;
        for (int i = 0; i < frames; i++) renderer.Draw(canvas.AsSurface(), browser, entries, 4 + i / 60.0, 1 / 60.0);
        Console.WriteLine($"{name}: {watch.Elapsed.TotalMilliseconds / frames:F2} ms/frame");
    }
    return;
}

if (args.Contains("--screenshots"))
{
    string output = Path.Combine(app.Environment.ContentRootPath, "out");
    Directory.CreateDirectory(output);
    foreach (string old in Directory.GetFiles(output))
        if (Path.GetExtension(old).ToLowerInvariant() is ".bmp" or ".png") File.Delete(old);

    // The theme the console shows: the second pack, so its library card and its detail cover carry the Applied badge.
    renderer.AppliedId = entries[Math.Min(1, entries.Count - 1)].Manifest.Id;

    // Draws the frame as the app would at 60 Hz: 61 frames over one second, so the focus underline and the view's
    // slide-in have settled, and the store keyboard's caret is on at the saved frame (a whole second, 4.0 s).
    // The shot must show the view (and the dialog) it is named for.
    void Save(string name, ThemeView expected, DialogKind dialog = DialogKind.None)
    {
        if (browser.View != expected) throw new InvalidOperationException($"{name}: the browser is on {browser.View}, not {expected}");
        if (browser.Dialog != dialog) throw new InvalidOperationException($"{name}: the dialog is {browser.Dialog}, not {dialog}");
        for (int i = 0; i <= 60; i++) renderer.Draw(canvas.AsSurface(), browser, entries, 3 + i / 60.0, 1 / 60.0);
        File.WriteAllBytes(Path.Combine(output, name + ".png"), PngWriter.Encode(canvas.AsSurface()));
        Console.WriteLine("saved " + name + ".png");
    }
    // Frames of a screen as the app shows them while the pad is left alone (the eased scrolls settle).
    void Settle() { for (int i = 0; i <= 60; i++) renderer.Draw(canvas.AsSurface(), browser, entries, 3 + i / 60.0, 1 / 60.0); }
    Save("library", ThemeView.Library);
    browser.Update(UiInput.None with { Right = true }); Save("library-second", ThemeView.Library);   // Selected moved right by one: the applied pack
    browser.Update(UiInput.None with { Right = true }); Save("library-applied", ThemeView.Library);   // the badge on a card that is not focused
    browser.Update(UiInput.None with { Left = true });
    // The rest of the sequence keeps the second pack selected, so detail and preview show a real pack.
    var press = new UiInput(false, false, false, false, true, false);
    var back = new UiInput(false, false, false, false, false, true);
    var down = new UiInput(false, true, false, false, false, false);
    var up = new UiInput(true, false, false, false, false, false);
    browser.Update(press); Save("detail", ThemeView.Detail);
    // Apply asks first: for a pack with an all-apps background it asks where that goes (focus on the saved answer).
    browser.Update(press, asksScope: true); Save("dialog-apply-scope", ThemeView.Detail, DialogKind.ApplyScope);
    browser.Update(back);
    browser.Update(press); Save("dialog-apply", ThemeView.Detail, DialogKind.Apply);
    browser.Update(back);
    browser.Update(UiInput.None, preview: true); Save("preview", ThemeView.Preview);
    browser.Update(press); Save("artwork", ThemeView.Preview);
    browser.Update(back); browser.Update(back);   // preview -> detail -> library
    browser.Update(UiInput.None with { PageDown = true }); Save("community", ThemeView.Community);

    // The category filter. What the app does for a chip: X asks for the list of that category, and when it arrives the
    // list, the category it was loaded with and the card count are replaced (the store answers with the cards in it).
    var right = UiInput.None with { Right = true };
    var left = UiInput.None with { Left = true };
    void PickCategory()
    {
        browser.Update(press);
        if (browser.TakeAction() != ThemeAction.CommunityRefresh) throw new InvalidOperationException("X on a chip did not ask for a refresh");
        ReloadStore();
    }
    // Up from the first card focuses the chips; pick Games, then move on to Space: the shot shows the chosen chip (white)
    // next to the focused one (ring).
    browser.Update(up);
    if (!browser.CommunityChipFocused) throw new InvalidOperationException("Up from the first card did not focus the chips");
    browser.Update(right); browser.Update(right); PickCategory();
    for (int i = 0; i < 4; i++) browser.Update(right);   // Space
    Save("community-chips", ThemeView.Community);
    // The row scrolled to its last chip.
    for (int i = 0; i < ThemeBrowser.CommunityChipCount; i++) browser.Update(right);
    Save("community-chips-end", ThemeView.Community);
    // Back on the cards, the list is the filtered one and the count line says so.
    for (int i = 0; i < ThemeBrowser.CommunityChipCount; i++) browser.Update(left);
    Settle();
    browser.Update(down); Save("community-category", ThemeView.Community);
    // A category without themes: the chips stay focused so another one can be picked.
    browser.Update(up);
    for (int i = 0; i < 2; i++) browser.Update(right);   // Cartoon
    browser.StoreUrl = "http://192.168.0.5:8080";
    PickCategory(); Save("community-empty-category", ThemeView.Community);
    // Back to all themes, then the empty store of the next shot.
    for (int i = 0; i < ThemeBrowser.CommunityChipCount; i++) browser.Update(left);
    PickCategory();
    browser.Update(down);
    browser.StoreUrl = "";
    // An empty store on the same tab, with the app's own message for a store that is not set up.
    renderer.ResetCommunity();
    renderer.Community = Array.Empty<CommunityTheme>();
    renderer.CommunityInLibrary = new HashSet<string>(StringComparer.Ordinal);
    renderer.CommunityUpdates = new HashSet<string>(StringComparer.Ordinal);
    renderer.SelectedCommunityId = null;
    renderer.CommunityStatus = "No store set. On the System tab, choose Store address and enter the address.";
    browser.SetCommunityCount(0);
    Save("community-empty", ThemeView.Community);
    browser.Update(UiInput.None with { PageDown = true }); Save("import", ThemeView.Import);
    browser.Update(UiInput.None with { PageDown = true });
    browser.StoreUrl = "http://192.168.0.5:8080"; Save("system", ThemeView.System);
    // Each row focused in turn; the last one asks before restoring, with the focus on Cancel.
    renderer.AllAppsBackgrounds = false;
    for (int row = 0; row < ThemeBrowser.SystemRowCount; row++)
    {
        if (browser.SystemSelected != row) throw new InvalidOperationException($"system row {row} is not focused");
        Save($"system-row-{row}", ThemeView.System);
        if (row < ThemeBrowser.SystemRowCount - 1) browser.Update(down);
    }
    browser.Update(press); Save("dialog-restore", ThemeView.System, DialogKind.Restore);
    browser.Update(back);
    browser.Update(up); browser.Update(up);   // Store address: X opens the keyboard
    browser.Update(press); Save("store-address", ThemeView.StoreAddress);
    // The toast, on the library: progress while busy, then a success and a failure that needs two lines.
    browser.Update(back); browser.Update(back);   // keyboard -> system -> library
    renderer.Busy = true;
    renderer.Notify("Applying theme…", NoticeLevel.Info); Save("toast-info", ThemeView.Library);
    renderer.Busy = false;
    renderer.Notify("Applied and verified 27 target(s).", NoticeLevel.Success); Save("toast-success", ThemeView.Library);
    renderer.Notify("Could not put /system_ex/vsh_asset/bg_hub_default.dds.zst back to its default (Operation not permitted), so the theme was not applied. " +
        "3 other file(s) were already back to default.", NoticeLevel.Error); Save("toast-error", ThemeView.Library);
    renderer.ClearNotice();
    Console.WriteLine("Native-renderer screenshots saved to " + output);
    return;
}

// The interactive preview lists the built-in store, so the Community tab's empty states describe a filter that matches
// nothing, not a missing store address (the address is only a placeholder; the preview never connects to it).
browser.StoreUrl = "http://store.preview.test";
app.MapGet("/", () => Results.Content(PreviewPage.Html, "text/html; charset=utf-8"));
var clock = System.Diagnostics.Stopwatch.StartNew();
bool allAppsScope = false;
app.MapGet("/frame.bmp", () =>
{
    lock (gate)
    {
        renderer.Draw(canvas.AsSurface(), browser, entries, 3 + clock.Elapsed.TotalSeconds, .1);
        return Results.Bytes(BmpEncoder.Encode(canvas.AsSurface()), "image/bmp");
    }
});
app.MapPost("/input/{key}", (string key) =>
{
    lock (gate)
    {
        var input = new UiInput(key == "up", key == "down", key == "left", key == "right", key == "confirm", key == "back")
        { PageUp = key == "l1", PageDown = key == "r1", Home = key == "home", End = key == "end" };
        bool asksScope = browser.Selected >= 0 && entries[browser.Selected].Manifest.Targets.Any(t => t.Path == AssetRoots.AllAppsBackground);
        browser.Update(input, preview: key == "preview", options: key == "options", triangle: key == "triangle",
            asksScope: asksScope, allAppsScope: allAppsScope, busy: false);
        var action = browser.TakeAction();
        const string pcOnly = "This PC preview doesn’t write to a console; the PS5 app does.";
        // The preview's built-in store answers sort and category like the real one (Options reloads the same list).
        if (action == ThemeAction.CommunityRefresh) ReloadStore();
        if (action == ThemeAction.ImportUsb) renderer.LibraryStatus = "USB import is available in the PS5 app. This PC preview doesn’t read console files.";
        if (action == ThemeAction.Apply)
        {
            if (browser.ApplyAsksScope) { allAppsScope = browser.ApplyAllApps; renderer.AllAppsBackgrounds = allAppsScope; }
            renderer.AppliedId = entries[browser.Selected].Manifest.Id;
            renderer.Notify("Applied (PC preview only). " + pcOnly, NoticeLevel.Success);
        }
        if (action == ThemeAction.RestoreOriginals) { renderer.AppliedId = null; renderer.Notify("Restored (PC preview only). " + pcOnly, NoticeLevel.Success); }
        if (action == ThemeAction.ToggleBackgroundScope)
        {
            allAppsScope = !allAppsScope;
            renderer.AllAppsBackgrounds = allAppsScope;
            renderer.Notify("Takes effect on the next apply (PC preview only).", NoticeLevel.Success);
        }
        if (action == ThemeAction.FirmwareReport) renderer.Notify("The firmware report is written by the PS5 app. " + pcOnly, NoticeLevel.Info);
        if (action == ThemeAction.SaveStoreUrl)
        {
            string url = AppSettings.NormalizeStoreUrl(browser.StoreEditor.Text);
            if (AppSettings.StoreUrlProblem(url) is { } problem) browser.ResumeStoreAddress(problem);
            else { browser.StoreUrl = url; renderer.Notify("Store address applied for this PC preview only.", NoticeLevel.Success); }
        }
        return Results.Json(new { view = browser.View.ToString(), selected = browser.Selected, overlay = browser.ShowOverlay, dialog = browser.Dialog.ToString() });
    }
});
app.MapGet("/state", () => { lock (gate) return Results.Json(new { view = browser.View.ToString(), selected = browser.Selected, count = entries.Count, overlay = browser.ShowOverlay, dialog = browser.Dialog.ToString(), system = browser.SystemSelected }); });
app.Run();

static unsafe PixelBuffer Decode(byte[] bytes)
{
    var image = PngDecoder.Decode(bytes);
    var buffer = new PixelBuffer(image.Width, image.Height);
    uint* pixels = buffer.AsSurface().Pixels;
    for (int i = 0; i < image.Width * image.Height; i++)
    {
        int p = i * 4;
        pixels[i] = ((uint)image.Rgba[p + 3] << 24) | ((uint)image.Rgba[p] << 16) | ((uint)image.Rgba[p + 1] << 8) | image.Rgba[p + 2];
    }
    return buffer;
}

static class PreviewPage
{
    public const string Html = """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Prospero Themes · Native Preview</title><style>
*{box-sizing:border-box}body{margin:0;background:#070c15;color:#a7b6ca;font:14px system-ui;min-height:100vh;display:flex;flex-direction:column;justify-content:center}
img{display:block;width:100%;max-height:calc(100vh - 78px);object-fit:contain}footer{padding:18px 24px;display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap}
button{font:inherit;color:#dce8f6;background:#182334;border:1px solid #344158;border-radius:7px;padding:8px 14px;cursor:pointer}button:hover,button:focus-visible{background:#2a405a;outline:1px solid #9bd1ff}nav{display:flex;gap:8px;flex-wrap:wrap}.note{font-size:12px}
</style><img id="frame" src="/frame.bmp" alt="Prospero Themes, rendered with the native PS5 drawing code">
<footer><span class="note">Native PC preview · Arrow keys navigate · Enter = X · Esc = Circle · V = Square · T = Triangle · Q/E = L1/R1 · O = Options</span>
<nav><button data-key="l1">L1</button><button data-key="up">↑</button><button data-key="down">↓</button><button data-key="left">←</button><button data-key="right">→</button><button data-key="confirm">X · OK</button><button data-key="preview">□ · Preview</button><button data-key="triangle">△ · Like</button><button data-key="back">○ · Back</button><button data-key="options">Options</button><button data-key="r1">R1</button></nav></footer>
<script>
let queue=Promise.resolve();function input(key){queue=queue.then(async()=>{await fetch('/input/'+key,{method:'POST'});let r=await fetch('/frame.bmp?t='+Date.now());let old=frame.src;frame.src=URL.createObjectURL(await r.blob());if(old.startsWith('blob:'))URL.revokeObjectURL(old);});}
document.querySelectorAll('button').forEach(b=>b.onclick=()=>input(b.dataset.key));
document.addEventListener('keydown',e=>{let k={ArrowUp:'up',ArrowDown:'down',ArrowLeft:'left',ArrowRight:'right',Enter:'confirm',Escape:'back',v:'preview',V:'preview',q:'l1',Q:'l1',e:'r1',E:'r1',Home:'home',End:'end',o:'options',t:'triangle',T:'triangle'}[e.key];if(k&&!e.repeat){e.preventDefault();input(k);}});
</script></html>
""";
}
