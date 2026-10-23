using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;
using SharpProspero.Graphics;
using SharpProspero.Texture;
using SharpProspero.Ui;

// --decode <picture> <png>: what the preview's own decoder makes of a JPEG or PNG (to check it against another decoder).
if (args.Length == 3 && args[0] == "--decode")
{
    using var decoded = Decode(File.ReadAllBytes(args[1]));
    File.WriteAllBytes(args[2], PngWriter.Encode(decoded.AsSurface()));
    return;
}

// --demo-pack <file>: writes the pack the preview makes in code, to look at it with other tools.
if (args.Length == 2 && args[0] == "--demo-pack")
{
    File.WriteAllBytes(args[1], PreviewDemoPack.Build(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Shared.Tests/fixtures"))) ?? throw new InvalidOperationException("The fixtures the demo pack borrows from are missing."));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5185");
var app = builder.Build();
string assets = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../ThemeApp/assets"));
var entries = Directory.EnumerateFiles(assets, "*.p5theme").Select(p => ThemeLibraryEntry.Inspect(p, File.ReadAllBytes(p)) with { Bundled = true }).ToList();
// Real packs to review with: every pack in PREVIEW_LIBRARY (when set), then the repository's test fixtures.
string fixtures = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../Shared.Tests/fixtures"));
entries.AddRange(PreviewData.LoadPacks(Environment.GetEnvironmentVariable("PREVIEW_LIBRARY"), fixtures));
// A pack made in code with tile icons, pictures, music and sounds, to review the interactive preview with. Last in the
// library, so everything that counts packs from the front (the store cards, the applied one) stays as it was.
byte[]? demoPack = PreviewDemoPack.Build(fixtures);
if (demoPack is not null)
{
    ThemeLibraryEntry demo = ThemeLibraryEntry.Inspect(PreviewDemoPack.Path, demoPack);
    entries.Add(demo);
    Console.WriteLine($"loaded   {demo.Manifest.Name}  (made in code: {demoPack.Length / 1024} KiB, cover {(demo.Preview?.Length ?? 0) / 1024} KiB{string.Concat(demo.Report.Issues.Select(i => ", " + i.Message))})");
}
var browser = new ThemeBrowser();
browser.SetCount(entries.Count);
using var canvas = new PixelBuffer(1920, 1080);
using var renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(assets, name)), Decode);
renderer.Status = "PC preview";
renderer.LibraryStatus = "This preview uses the same renderer as the native PS5 app.";
var gate = new object();
// The preview of a theme: what the app does with workers and the console's audio, done at once here and without sound.
using var preview = new PreviewController(renderer, browser, new NullPreviewAudio(), Decode,
    path => path == PreviewDemoPack.Path ? demoPack! : File.ReadAllBytes(path), new InlineWorker());
// One frame of buttons the way the app feeds them to the browser and the preview (music is Square, controls are Triangle).
void Press(UiInput input, bool square = false, bool triangle = false)
{
    preview.BeforeUpdate(entries);
    browser.Update(input, preview: square, triangle: triangle);
    if (browser.TakeAction() == ThemeAction.PreviewMusic) preview.ToggleMusic();
    preview.Update(entries);
}

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
    string query = browser.CommunityQuery;
    List<CommunityTheme> shown = PreviewData.Listing(store, (CommunitySort)browser.CommunitySort, CommunityCategories.IdAt(browser.CommunityCategory), query);
    renderer.Community = shown;
    renderer.CommunityListSort = browser.CommunitySort;
    renderer.CommunityListCategory = browser.CommunityCategory;
    renderer.CommunityListQuery = query;
    renderer.CommunityStatus = shown.Count == 0 ? (query.Length > 0 ? $"No themes match “{query}”." : "No themes in this category yet.") : "";
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
        ("dialog-apply-parts", () => { browser.Update(cancel); browser.Update(press, parts: ThemePart.All, sounds: false); }),
        ("detail again", () => browser.Update(cancel)),
        ("system", () => { browser.Update(cancel); browser.Update(UiInput.None with { PageUp = true }); }),
        ("system-restore", () => { for (int i = 0; i < ThemeBrowser.SystemRowCount - 1; i++) browser.Update(down); }),
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

    // The preview, of a pack without previews (stand-in tiles on the cover) and of the demo pack (tile icons, pictures).
    // Each frame is timed apart, so the dearest one shows next to the average: the mixing of a picture change is the
    // heaviest frame, a fade step. Loading (the worker's job in the app) is done before the timing starts.
    void Preview(string name, int select, Action<int> input, int frames = 240)
    {
        while (browser.View != ThemeView.Library) browser.Update(UiInput.None with { PageUp = true });
        browser.Update(UiInput.None with { Home = true });   // each preview starts from the Library
        for (int i = 0; i < select; i++) browser.Update(UiInput.None with { Right = true });
        Press(UiInput.None, square: true);
        for (int i = 0; i < 180; i++)
        {
            renderer.Draw(canvas.AsSurface(), browser, entries, 3 + i / 60.0, 1 / 60.0);
            preview.Update(entries);
        }
        var times = new double[frames];
        var watch = new System.Diagnostics.Stopwatch();
        for (int i = 0; i < frames; i++)
        {
            input(i);
            preview.Update(entries);
            watch.Restart();
            renderer.Draw(canvas.AsSurface(), browser, entries, 10 + i / 60.0, 1 / 60.0);
            times[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        Console.WriteLine($"{name}: {times.Average():F2} ms/frame, median {times[frames / 2]:F2}, 95th {times[frames * 95 / 100]:F2}, worst {times[^1]:F2}");
        while (browser.View == ThemeView.Preview) Press(UiInput.None with { Cancel = true });
    }
    var right = UiInput.None with { Right = true };
    var left = UiInput.None with { Left = true };
    // Keeps the row sliding: a step every ten frames, back and forth over the Games section.
    void Walk(int i) { if (i % 10 == 0) Press(i / 10 % 8 < 4 ? right : left); }
    Preview("preview (pack without previews, at rest)", 1, _ => { });
    Preview("preview (pack without previews, walking)", 1, Walk);
    int demo = entries.Count - 1;
    if (demoPack is not null)
    {
        Preview("preview (demo pack, at rest)", demo, _ => { });
        // Tiles 0 to 2 and back: the pictures change (cover, own picture), so the cross-fade runs.
        Preview("preview (demo pack, walking and fading)", demo, i => { if (i % 30 == 0) Press(i / 30 % 6 < 3 ? right : left); });
        Preview("preview (demo pack, walking)", demo, Walk);
        // Along the whole Games row and back, a move every 20 frames: five distinct pictures (the cover, three apps' own and the
        // all-apps one), so pictures are made on the worker (not timed) and the cover and the washed layers are copied from the cache.
        Preview("preview (demo pack, walking the whole row and back)", demo, i => { if (i % 20 == 0) Press(i / 20 % 26 < 13 ? right : left); }, frames: 560);
        Preview("preview (demo pack, control centre)", demo, i => { if (i == 0) Press(UiInput.None, triangle: true); if (i % 10 == 0) Press(i / 10 % 8 < 4 ? right : left); });
    }
    var picture = new System.Diagnostics.Stopwatch();
    using (var source = new PixelBuffer(1920, 1080))
    {
        picture.Restart();
        ThemeRenderer.BuildPreviewPicture(source, smooth: false).Dispose();
        Console.WriteLine($"building a preview picture from a 1080p source: {picture.Elapsed.TotalMilliseconds:F1} ms");
        using var small = new PixelBuffer(1280, 720);
        picture.Restart();
        ThemeRenderer.BuildPreviewPicture(small, smooth: true).Dispose();
        Console.WriteLine($"building a preview picture from a 720p source (smooth): {picture.Elapsed.TotalMilliseconds:F1} ms");
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
    // The app's default store: its web creator is what the QR code on the USB Import card points to.
    browser.StoreUrl = AppSettings.DefaultStoreUrl;

    // Draws the frame as the app would at 60 Hz: 61 frames over one second, so the focus underline and the view's
    // slide-in have settled, and the store keyboard's caret is on at the saved frame (a whole second, 4.0 s).
    // The shot must show the view (and the dialog) it is named for.
    // The preview loads its pictures between frames (here at once), so a shot of it needs the controller after every frame.
    void Save(string name, ThemeView expected, DialogKind dialog = DialogKind.None, int frames = 60)
    {
        if (browser.View != expected) throw new InvalidOperationException($"{name}: the browser is on {browser.View}, not {expected}");
        if (browser.Dialog != dialog) throw new InvalidOperationException($"{name}: the dialog is {browser.Dialog}, not {dialog}");
        for (int i = 0; i <= frames; i++)
        {
            renderer.Draw(canvas.AsSurface(), browser, entries, 3 + i / 60.0, 1 / 60.0);
            preview.Update(entries);
        }
        File.WriteAllBytes(Path.Combine(output, name + ".png"), PngWriter.Encode(canvas.AsSurface()));
        Console.WriteLine("saved " + name + ".png");
    }
    // Frames of a screen as the app shows them while the pad is left alone (the eased scrolls settle).
    void Settle() { for (int i = 0; i <= 60; i++) renderer.Draw(canvas.AsSurface(), browser, entries, 3 + i / 60.0, 1 / 60.0); }
    Save("library", ThemeView.Library);
    browser.Update(UiInput.None with { Right = true }); Save("library-second", ThemeView.Library);   // Selected moved right by one: the applied pack
    browser.Update(UiInput.None with { Right = true }); Save("library-applied", ThemeView.Library);   // the badge on a card that is not focused
    renderer.LibrarySort = LibrarySort.Newest; Save("library-newest", ThemeView.Library);   // the order named next to the count
    renderer.LibrarySort = LibrarySort.Name;
    // Two themes combined: the row ends with the card that saves the look, and its hero says what goes in.
    renderer.Look = [new LookLayer(entries[0].Path, entries[0].Manifest.Id, entries[0].Manifest.Name, ThemePart.Background),
        new LookLayer(entries[1].Path, entries[1].Manifest.Id, entries[1].Manifest.Name, ThemePart.Sounds | ThemePart.Icons)];
    browser.LookSavable = true;
    browser.Update(UiInput.None with { End = true }); Save("library-look", ThemeView.Library);
    browser.LookSavable = false;
    browser.Select(2);   // back on the third pack, as before the card
    browser.Update(UiInput.None with { Left = true });
    // The rest of the sequence keeps the second pack selected, so detail and preview show a real pack.
    var press = new UiInput(false, false, false, false, true, false);
    var back = new UiInput(false, false, false, false, false, true);
    var down = new UiInput(false, true, false, false, false, false);
    var up = new UiInput(true, false, false, false, false, false);
    browser.Update(press); Save("detail", ThemeView.Detail);
    // What the console has of the pack, once the app checked it: everything, then a console without some of its files.
    {
        ThemeLibraryEntry open = entries[browser.Selected];
        renderer.PackFits[open.Path] = new PackFit(Array.Empty<ThemePackTarget>()); Save("detail-fit", ThemeView.Detail);
        var lacking = open.Manifest.Targets.Where(t => t.Path.StartsWith(AssetRoots.Rnps + "/", StringComparison.Ordinal)).Take(3).ToList();
        if (lacking.Count == 0) lacking = open.Manifest.Targets.TakeLast(1).ToList();
        renderer.PackFits[open.Path] = new PackFit(lacking); Save("detail-fit-missing", ThemeView.Detail);
        renderer.PackFits.Clear();
    }
    // Apply asks first: for a pack with an all-apps background it asks where that goes (focus on the saved answer).
    browser.Update(press, asksScope: true); Save("dialog-apply-scope", ThemeView.Detail, DialogKind.ApplyScope);
    browser.Update(back);
    browser.Update(press); Save("dialog-apply", ThemeView.Detail, DialogKind.Apply);
    browser.Update(back);
    // A pack with every part, sounds turned off: they start unticked; the focus is on Apply theme.
    browser.Update(press, parts: ThemePart.All, sounds: false); Save("dialog-apply-parts", ThemeView.Detail, DialogKind.ApplyParts);
    browser.Update(up); Save("dialog-apply-parts-focus", ThemeView.Detail, DialogKind.ApplyParts);
    browser.Update(back);
    // A pack without previews: stand-in tiles on its cover, the same as before the tiles got icons.
    Press(UiInput.None, square: true); Save("preview", ThemeView.Preview);
    Press(press); Save("artwork", ThemeView.Preview);
    Press(back); Press(back);   // leaves the preview for the Detail, then the Detail for the Library
    if (browser.View != ThemeView.Library) throw new InvalidOperationException("The preview did not lead back to the Library");
    // The demo pack, last in the library: the pack's tile icons, a picture behind the focused tile, the control centre.
    if (demoPack is not null)
    {
        var l1 = UiInput.None with { PageUp = true };
        var r1 = UiInput.None with { PageDown = true };
        var toRight = UiInput.None with { Right = true };
        var toLeft = UiInput.None with { Left = true };
        browser.Update(UiInput.None with { End = true });
        Press(UiInput.None, square: true); Save("preview-demo", ThemeView.Preview);                 // Store: no picture of its own, so the cover
        Press(toRight); Press(toRight); Save("preview-demo-system-picture", ThemeView.Preview);      // Game Library: its own picture
        for (int i = 0; i < 3; i++) Press(toRight);
        Save("preview-demo-app-picture", ThemeView.Preview);                                         // an app with a (progressive JPEG) picture of its own
        Press(toRight); Save("preview-demo-app-picture-2", ThemeView.Preview);
        Press(toRight); Save("preview-demo-all-apps", ThemeView.Preview);                            // an app without one: the all-apps picture
        for (int i = 0; i < 8; i++) Press(toRight);
        Save("preview-demo-end", ThemeView.Preview);                                                 // the last tile: the row has scrolled
        Press(r1); Save("preview-demo-media", ThemeView.Preview);
        Press(l1);
        for (int i = 0; i < 6; i++) Press(toLeft);
        Settle();
        // Part-way through the change of picture (the all-apps one to the sunset), a bit under half a second in.
        Press(toLeft); Save("preview-demo-fade", ThemeView.Preview, frames: 9);
        Press(UiInput.None, triangle: true); Save("preview-demo-control-centre", ThemeView.Preview);
        for (int i = 0; i < 3; i++) Press(toRight);
        Save("preview-demo-control-centre-focus", ThemeView.Preview);                                // a stand-in (Game Base) focused
        Press(toLeft); Save("preview-demo-control-centre-icon", ThemeView.Preview);                  // the pack's render (Notifications)
        Press(UiInput.None, triangle: true); Press(press); Save("preview-demo-artwork", ThemeView.Preview);   // overlay hidden
        Press(press); Press(back);
        if (browser.View != ThemeView.Library) throw new InvalidOperationException("The demo preview did not lead back to the Library");
        browser.Update(UiInput.None with { Home = true });
        browser.Update(UiInput.None with { Right = true });   // the second pack stays selected for the rest of the shots
    }
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
    // Back to all themes (the chip right of the search one), then the search and the empty store of the next shots.
    for (int i = 0; i < ThemeBrowser.CommunityChipCount; i++) browser.Update(left);
    browser.Update(right);
    PickCategory();
    browser.StoreUrl = AppSettings.DefaultStoreUrl;

    // The search: X on the chip at the start of the row opens the keyboard, Options searches for what was typed.
    // Typing moves the focus to each key as a pad would.
    void Type(string text)
    {
        foreach (char ch in text)
        {
            var keys = browser.SearchEditor.Keys;
            int row = -1, column = -1;
            for (int r = 0; r < keys.Count && row < 0; r++)
                for (int c = 0; c < keys[r].Count; c++)
                    if (keys[r][c].Text == ch.ToString()) { row = r; column = c; break; }
            if (row < 0) throw new InvalidOperationException($"No key types '{ch}'");
            while (browser.SearchEditor.Row != row) browser.Update(browser.SearchEditor.Row < row ? down : up);
            while (browser.SearchEditor.Column != column) browser.Update(browser.SearchEditor.Column < column ? right : left);
            browser.Update(press);
        }
    }
    void Search()
    {
        browser.Update(UiInput.None, options: true);
        if (browser.TakeAction() != ThemeAction.CommunityRefresh) throw new InvalidOperationException("Options did not ask for the search");
        ReloadStore();
    }
    browser.Update(left);
    Save("community-search-chip", ThemeView.Community);
    browser.Update(press);
    // A word from the first card's name, so the search finds it.
    string word = new string(store[0].Name.ToLowerInvariant().Where(char.IsAsciiLetterLower).Take(4).ToArray());
    Type(word);
    Save("community-search-keyboard", ThemeView.Search);
    Search();
    Settle();
    Save("community-search", ThemeView.Community);
    // A term nothing matches.
    browser.Update(press);                                   // takes the search off
    if (browser.TakeAction() != ThemeAction.CommunityRefresh) throw new InvalidOperationException("X on the search chip did not take the term off");
    browser.Update(press);
    Type("qqxz");
    Search();
    Save("community-search-none", ThemeView.Community);
    browser.Update(press);
    browser.TakeAction();
    ReloadStore();
    // Cards whose pictures are still on their way: the picture area shimmers until they are in.
    {
        var loaded = renderer.Community;
        renderer.Community = loaded.Select(t => t with { Id = t.Id + "-loading", PreviewUrl = "http://192.168.0.5:8080/p.png" }).ToList();
        renderer.CommunityPicturesLoading = true;
        browser.Update(down);
        Save("community-loading", ThemeView.Community, frames: 40);
        renderer.CommunityPicturesLoading = false;
        renderer.Community = loaded;
    }
    browser.StoreUrl = "";
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
    // USB Import: the card for the web creator (QR code) beside it; with no store set, the card alone; with a long address of
    // a store of one's own, the code is bigger and its modules smaller.
    browser.StoreUrl = AppSettings.DefaultStoreUrl;
    browser.Update(UiInput.None with { PageDown = true }); Save("import", ThemeView.Import);
    browser.StoreUrl = "";
    Save("import-nostore", ThemeView.Import);
    browser.StoreUrl = "https://my-own-theme-store.example.org:8443/prospero/themes/community/store";
    Save("import-long-store", ThemeView.Import);
    browser.StoreUrl = AppSettings.DefaultStoreUrl;
    browser.Update(UiInput.None with { PageDown = true });
    browser.StoreUrl = "http://192.168.0.5:8080"; Save("system", ThemeView.System);
    // The store knows of a later release (AppUpdate): the version row says so.
    renderer.AppVersion = "01.000.022";
    renderer.NewerVersion = "01.000.022"; Save("system-update", ThemeView.System);
    renderer.NewerVersion = "";
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
    // A console that shows every part: the parts to put back, all ticked, the focus on Cancel; then Background left out.
    browser.Update(press, restorable: ThemePart.All); Save("dialog-restore-parts", ThemeView.System, DialogKind.RestoreParts);
    for (int i = 0; i < 7; i++) browser.Update(up, restorable: ThemePart.All);
    browser.Update(press, restorable: ThemePart.All); Save("dialog-restore-parts-untick", ThemeView.System, DialogKind.RestoreParts);
    browser.Update(back);
    // Firmware report: X asks whether to only save it or send it too; a sent one shows the store's code on its row.
    browser.Update(up);
    browser.Update(press); Save("dialog-firmware-report", ThemeView.System, DialogKind.FirmwareReport);
    browser.Update(back);
    renderer.ReportCode = "K7Q2MX"; Save("system-report-code", ThemeView.System);
    renderer.ReportCode = "";
    browser.Update(up);   // Store address: X opens the keyboard
    browser.Update(press); Save("store-address", ThemeView.StoreAddress);
    // The toast, on the library: progress while busy, then a success and a failure that needs two lines.
    browser.Update(back); browser.Update(back);   // keyboard -> system -> library
    // After a system update the first scan asks to apply the look again (text as SystemUpdate.Text writes it).
    renderer.ReapplyText = "The system software changed from 12.40 to 13.00, which puts the console’s own files back. " +
        "Apply “Pokemon - Kanto Nights” again?";
    browser.AskReapply(); Save("dialog-reapply", ThemeView.Library, DialogKind.Reapply);
    browser.Update(back); browser.TakeAction();
    browser.AskRestartShell(); Save("dialog-restart-shell", ThemeView.Library, DialogKind.RestartShell);
    browser.Update(back); browser.TakeAction();
    renderer.Busy = true;
    renderer.Notify("Applying theme…", NoticeLevel.Info); Save("toast-info", ThemeView.Library);
    renderer.ShowProgress("Applying theme… Writing 5 of 12", 4 / 12f); Save("toast-progress", ThemeView.Library);
    renderer.Busy = false;
    renderer.Notify("Applied and verified 27 target(s).", NoticeLevel.Success); Save("toast-success", ThemeView.Library);
    renderer.Notify("Could not put /system_ex/vsh_asset/bg_hub_default.dds.zst back to its default (Operation not permitted), so the theme was not applied. " +
        "3 other file(s) were already back to default.", NoticeLevel.Error); Save("toast-error", ThemeView.Library);
    renderer.ClearNotice();
    // An empty library names the web creator too (and says nothing of it when there is no store).
    foreach (string address in new[] { AppSettings.DefaultStoreUrl, "" })
    {
        browser.StoreUrl = address;
        browser.SetCount(0);
        var none = Array.Empty<ThemeLibraryEntry>();
        for (int i = 0; i <= 60; i++) renderer.Draw(canvas.AsSurface(), browser, none, 3 + i / 60.0, 1 / 60.0);
        File.WriteAllBytes(Path.Combine(output, address.Length > 0 ? "library-empty.png" : "library-empty-nostore.png"), PngWriter.Encode(canvas.AsSurface()));
    }
    Console.WriteLine("Native-renderer screenshots saved to " + output);
    return;
}

// The interactive preview lists the built-in store, so the Community tab's empty states describe a filter that matches
// nothing, not a missing store address (the address is only a placeholder; the preview never connects to it).
browser.StoreUrl = "http://store.preview.test";
app.MapGet("/", () => Results.Content(PreviewPage.Html, "text/html; charset=utf-8"));
var clock = System.Diagnostics.Stopwatch.StartNew();
double lastFrame = 3;
bool allAppsScope = false;
app.MapGet("/frame.bmp", () =>
{
    lock (gate)
    {
        // The frame loop of the app, one iteration per request: the time is the clock's (so the slide and the fade play out
        // over the frames the page asks for), and a picture the frame asked for is loaded and drawn in the same request.
        double now = 3 + clock.Elapsed.TotalSeconds, delta = Math.Clamp(now - lastFrame, 1.0 / 60, .1);
        lastFrame = now;
        preview.Update(entries);
        renderer.Draw(canvas.AsSurface(), browser, entries, now, delta);
        for (int round = 0; round < 3 && renderer.Preview.Wanted is not null; round++)
        {
            preview.Update(entries);
            renderer.Draw(canvas.AsSurface(), browser, entries, now, delta);
        }
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
        ThemePart parts = browser.Selected >= 0 ? ThemeParts.In(entries[browser.Selected].Manifest) : ThemePart.None;
        preview.BeforeUpdate(entries);
        browser.Update(input, preview: key == "preview", options: key == "options", triangle: key == "triangle",
            asksScope: asksScope, allAppsScope: allAppsScope, busy: false, parts: parts, sounds: renderer.Sounds);
        var action = browser.TakeAction();
        if (action == ThemeAction.PreviewMusic) preview.ToggleMusic();
        preview.Update(entries);
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
        if (action == ThemeAction.ToggleSounds)
        {
            renderer.Sounds = !renderer.Sounds;
            renderer.Notify("Takes effect on the next apply (PC preview only).", NoticeLevel.Success);
        }
        if (action == ThemeAction.ChangeSoundVolume) renderer.SoundVolume = AppSettings.StepSoundVolume(renderer.SoundVolume, browser.SoundVolumeStep);
        if (action == ThemeAction.ChangeMusicVolume) renderer.MusicVolume = AppSettings.StepMusicVolume(renderer.MusicVolume, browser.SoundVolumeStep);
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
    // A pack's pictures are JPEGs; the SDK's PC texture library reads PNG only.
    if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) return JpegDecoder.Decode(bytes);
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

/// <summary>Runs the preview's reading and decoding at once: the PC preview has no frame loop to keep waiting.</summary>
sealed class InlineWorker : IPreviewWorker
{
    public void Run(Action work, Action done)
    {
        work();
        done();
    }
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
<nav><button data-key="l1">L1</button><button data-key="up">↑</button><button data-key="down">↓</button><button data-key="left">←</button><button data-key="right">→</button><button data-key="confirm">X · OK</button><button data-key="preview">□ · Preview / music</button><button data-key="triangle">△ · Like / control centre</button><button data-key="back">○ · Back</button><button data-key="options">Options</button><button data-key="r1">R1</button></nav></footer>
<script>
let queue=Promise.resolve();async function show(){let r=await fetch('/frame.bmp?t='+Date.now());let old=frame.src;frame.src=URL.createObjectURL(await r.blob());if(old.startsWith('blob:'))URL.revokeObjectURL(old);}
// After a key: the frame it made, and in the preview a few more over half a second, so the slide and the cross-fade play.
function input(key){queue=queue.then(async()=>{let state=await (await fetch('/input/'+key,{method:'POST'})).json();await show();if(state.view==='Preview'){for(let i=0;i<10;i++){await new Promise(r=>setTimeout(r,55));await show();}}});}
document.querySelectorAll('button').forEach(b=>b.onclick=()=>input(b.dataset.key));
document.addEventListener('keydown',e=>{let k={ArrowUp:'up',ArrowDown:'down',ArrowLeft:'left',ArrowRight:'right',Enter:'confirm',Escape:'back',v:'preview',V:'preview',q:'l1',Q:'l1',e:'r1',E:'r1',Home:'home',End:'end',o:'options',t:'triangle',T:'triangle'}[e.key];if(k&&!e.repeat){e.preventDefault();input(k);}});
</script></html>
""";
}
