using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using SharpProspero.Texture;
using SharpProspero.Ui;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5185");
var app = builder.Build();
string assets = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../ThemeApp/assets"));
var entries = Directory.EnumerateFiles(assets, "*.p5theme").Select(p => ThemeLibraryEntry.Inspect(p, File.ReadAllBytes(p)) with { Bundled = true }).ToList();
var browser = new ThemeBrowser();
browser.SetCount(entries.Count);
using var canvas = new PixelBuffer(1920, 1080);
using var renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(assets, name)), Decode);
renderer.Status = "PC preview";
renderer.LibraryStatus = "This preview uses the same renderer as the native PS5 app.";
var gate = new object();

if (args.Contains("--bench"))
{
    // Frame cost of the shared renderer per view; the console's budget at 60 Hz is 16.7 ms.
    foreach (var (name, input) in new[] { ("library", UiInput.None), ("detail", new UiInput(false, false, false, false, true, false)) })
    {
        browser.Update(input, targetCount: entries[browser.Selected].Manifest.Targets.Count);
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
    void Save(string name)
    {
        renderer.Draw(canvas.AsSurface(), browser, entries, 3, .1);
        renderer.Draw(canvas.AsSurface(), browser, entries, 4, .1);   // second frame: the view's slide-in has settled
        File.WriteAllBytes(Path.Combine(output, name + ".bmp"), BmpEncoder.Encode(canvas.AsSurface()));
    }
    Save("library");
    browser.Update(new UiInput(false, false, false, false, true, false), targetCount: entries[browser.Selected].Manifest.Targets.Count); Save("detail");
    browser.Update(UiInput.None, preview: true, targetCount: entries[browser.Selected].Manifest.Targets.Count); Save("preview");
    browser.Update(new UiInput(false, false, false, false, true, false), targetCount: entries[browser.Selected].Manifest.Targets.Count); Save("artwork");
    var back = new UiInput(false, false, false, false, false, true);
    browser.Update(back); browser.Update(back);   // preview -> detail -> library
    browser.Update(UiInput.None with { PageDown = true }); Save("community");
    browser.Update(UiInput.None with { PageDown = true }); Save("import");
    browser.Update(UiInput.None with { PageDown = true });
    browser.StoreUrl = "http://192.168.0.5:8080"; Save("system");
    browser.Update(UiInput.None, preview: true); Save("store-address");
    Console.WriteLine("Native-renderer screenshots saved to " + output);
    return;
}

app.MapGet("/", () => Results.Content(PreviewPage.Html, "text/html; charset=utf-8"));
app.MapGet("/frame.bmp", () =>
{
    lock (gate)
    {
        renderer.Draw(canvas.AsSurface(), browser, entries, 3, .1);
        return Results.Bytes(BmpEncoder.Encode(canvas.AsSurface()), "image/bmp");
    }
});
app.MapPost("/input/{key}", (string key) =>
{
    lock (gate)
    {
        var input = new UiInput(key == "up", key == "down", key == "left", key == "right", key == "confirm", key == "back")
        { PageUp = key == "l1", PageDown = key == "r1", Home = key == "home", End = key == "end" };
        browser.Update(input, key == "preview", key == "options",
            browser.Selected >= 0 ? entries[browser.Selected].Manifest.Targets.Count : 0);
        var action = browser.TakeAction();
        if (action == ThemeAction.ImportUsb) renderer.LibraryStatus = "USB import is available in the PS5 app. This PC preview doesn’t read console files.";
        if (action == ThemeAction.SaveStoreUrl)
        {
            string url = AppSettings.NormalizeStoreUrl(browser.StoreEditor.Text);
            if (AppSettings.StoreUrlProblem(url) is { } problem) browser.ResumeStoreAddress(problem);
            else { browser.StoreUrl = url; renderer.ActionStatus = "Store address applied for this PC preview only."; }
        }
        return Results.Json(new { view = browser.View.ToString(), selected = browser.Selected, overlay = browser.ShowOverlay });
    }
});
app.MapGet("/state", () => { lock (gate) return Results.Json(new { view = browser.View.ToString(), selected = browser.Selected, count = entries.Count, overlay = browser.ShowOverlay }); });
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
<footer><span class="note">Native PC preview · Arrow keys navigate · Enter = X · Esc = Circle · V = Square · Q/E = L1/R1 · O = Options</span>
<nav><button data-key="l1">L1</button><button data-key="left">←</button><button data-key="right">→</button><button data-key="confirm">X · Open</button><button data-key="preview">□ · Preview</button><button data-key="back">○ · Back</button><button data-key="options">Options</button><button data-key="r1">R1</button></nav></footer>
<script>
let queue=Promise.resolve();function input(key){queue=queue.then(async()=>{await fetch('/input/'+key,{method:'POST'});let r=await fetch('/frame.bmp?t='+Date.now());let old=frame.src;frame.src=URL.createObjectURL(await r.blob());if(old.startsWith('blob:'))URL.revokeObjectURL(old);});}
document.querySelectorAll('button').forEach(b=>b.onclick=()=>input(b.dataset.key));
document.addEventListener('keydown',e=>{let k={ArrowUp:'up',ArrowDown:'down',ArrowLeft:'left',ArrowRight:'right',Enter:'confirm',Escape:'back',v:'preview',V:'preview',q:'l1',Q:'l1',e:'r1',E:'r1',Home:'home',End:'end',o:'options'}[e.key];if(k&&!e.repeat){e.preventDefault();input(k);}});
</script></html>
""";
}
