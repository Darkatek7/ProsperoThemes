using System;
using System.Collections.Generic;
using System.Threading;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Application;
using SharpProspero.Diagnostics;
using SharpProspero.Graphics;
using SharpProspero.Modules;
using HttpResponse = SharpProspero.Platform.HttpResponse;
using SharpProspero.Interop.Sysmodule;
using SharpProspero.Interop.Pad;
using SharpProspero.Storage;
using SharpProspero.Threading;
using SharpProspero.Ui;

namespace ProsperoThemes.App;

internal sealed class ThemeApp : ProsperoApp
{
    private readonly ThemeBrowser _browser = new();
    private readonly Dispatcher _results = new();
    private IReadOnlyList<ThemeLibraryEntry> _entries = Array.Empty<ThemeLibraryEntry>();
    private ThemeRenderer _renderer = null!;
    private Thread? _worker;
    private bool _busy, _pngAvailable;
    // Frame timing readout, toggled with R3, for checking the pace on the device.
    private readonly FrameStats _stats = new();
    private bool _showStats;
    // User settings (background scope, store address, anonymous device id), kept in settings.json.
    private AppSettings _settings = null!;
    private bool _allAppsBackgrounds;
    // The community store: the list on screen and one HTTP client (Http/Ssl resolved at run time, see
    // StoreHttpClient), created on first use by the worker.
    private IReadOnlyList<CommunityTheme> _community = Array.Empty<CommunityTheme>();
    private StoreHttpClient? _http;
    // Set by a finished download: the library is scanned again as soon as the worker is free.
    private bool _rescanAfterWork;
    // Store id -> pack hash of every theme downloaded here (CommunityList.DownloadsPath), for the badges.
    private Dictionary<string, string> _downloads = new(StringComparer.Ordinal);

    protected override void OnLoad()
    {
        try { SystemModule.Load(SystemModuleId.PngDec); _pngAvailable = true; }
        catch { _pngAvailable = false; }
        _renderer = new ThemeRenderer(name => FileSystem.ReadAllBytes(DeviceThemeLibrary.AssetRoot + "/" + name), DecodeCover);
        string? json = null;
        try { if (FileSystem.Exists(AppSettings.Path)) json = FileSystem.ReadAllText(AppSettings.Path); }
        catch { json = null; }
        _settings = AppSettings.Parse(json, NewDeviceId);
        // A first start creates the device id; keep it so likes stay one per console.
        if (json is null || !json.Contains(_settings.DeviceId)) SaveSettings();
        _allAppsBackgrounds = _settings.AllAppsBackgrounds;
        _renderer.AllAppsBackgrounds = _allAppsBackgrounds;
        _browser.StoreUrl = _settings.StoreUrl;
        try
        {
            if (FileSystem.Exists(CommunityList.DownloadsPath))
                _downloads = CommunityList.ParseDownloads(FileSystem.ReadAllText(CommunityList.DownloadsPath));
        }
        catch { /* no badges for earlier downloads; new ones are recorded again */ }
        StartWork(DeviceThemeLibrary.Scan);
    }

    private PixelBuffer DecodeCover(byte[] bytes)
    {
        if (!_pngAvailable) throw new InvalidOperationException("PNG decoder unavailable.");
        using var image = PngImage.Decode(bytes);
        var result = new PixelBuffer(image.Width, image.Height);
        result.AsSurface().Blit(image.AsSurface(), 0, 0);
        return result;
    }

    private void StartWork(Func<LibrarySnapshot> work)
    {
        if (_busy) return;
        _busy = true;
        _renderer.Busy = true;
        _renderer.Status = "Loading library…";
        _worker = new Thread(() =>
        {
            try
            {
                var snapshot = work();
                _results.Post(() =>
                {
                    _renderer.ResetCovers();
                    _entries = snapshot.Entries;
                    _browser.SetCount(_entries.Count);
                    RefreshCommunityBadges();
                    _renderer.LibraryStatus = snapshot.Message;
                    _renderer.Status = ThemeRenderer.Plural(_entries.Count, "theme");
                    _renderer.Busy = false;
                    _busy = false;
                });
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                _results.Post(() =>
                {
                    _renderer.LibraryStatus = message;
                    _renderer.Status = "Check file access";
                    _renderer.Busy = false;
                    _busy = false;
                });
            }
        }) { IsBackground = true, Name = "ThemeLibrary" };
        _worker.Start();
    }

    /// <summary>
    /// Runs a system-changing job on the worker: starts ThemeHelper if it is not running yet, then hands the
    /// job an applier whose theme-root writes go through it. The result lands in the status line.
    /// </summary>
    private void StartSystemWork(string busyText, Func<ThemeApplier, ApplyResult> work)
    {
        if (_busy) return;
        _busy = true;
        _renderer.Busy = true;
        _renderer.Status = busyText;
        _renderer.ActionStatus = busyText;
        _worker = new Thread(() =>
        {
            string message, status;
            try
            {
                string? helperProblem = ThemeHelperClient.EnsureRunning();
                if (helperProblem is not null)
                {
                    (status, message) = ("Helper not ready", helperProblem);
                }
                else
                {
                    ApplyResult result = work(new ThemeApplier(new HelperDeviceFiles(), HelperDeviceFiles.UnixSeconds)
                        { IncludeAllApps = _allAppsBackgrounds });
                    (status, message) = (result.Success ? "Done" : "Not completed", result.Message);
                }
            }
            catch (Exception ex)
            {
                (status, message) = ("Error", ex.Message);
            }
            _results.Post(() =>
            {
                _renderer.Status = status;
                _renderer.ActionStatus = message;
                _renderer.Busy = false;
                _busy = false;
            });
        }) { IsBackground = true, Name = "ThemeApply" };
        _worker.Start();
    }

    protected override void OnFrame(FrameContext context)
    {
        _results.RunPending();
        if (_rescanAfterWork && !_busy)
        {
            _rescanAfterWork = false;
            StartWork(DeviceThemeLibrary.Scan);
        }
        _browser.Update(UiInput.From(context.Input, context.PreviousInput),
            preview: context.Pressed(ScePadButton.Square), options: context.Pressed(ScePadButton.Options),
            targetCount: _browser.Selected >= 0 ? _entries[_browser.Selected].Manifest.Targets.Count : 0,
            triangle: context.Pressed(ScePadButton.Triangle));
        switch (_browser.TakeAction())
        {
            case ThemeAction.ImportUsb: StartWork(DeviceThemeLibrary.ImportUsb); break;
            case ThemeAction.Reload: StartWork(DeviceThemeLibrary.Scan); break;
            case ThemeAction.Apply when _browser.Selected >= 0:
                string path = _entries[_browser.Selected].Path;
                StartSystemWork("Applying theme…", applier => applier.Apply(ThemeArchive.Open(FileSystem.ReadAllBytes(path))));
                break;
            case ThemeAction.RestoreOriginals:
                StartSystemWork("Restoring defaults…", applier => applier.RestoreOriginals());
                break;
            case ThemeAction.SaveStoreUrl: SaveStoreAddress(); break;
            case ThemeAction.CommunityRefresh: StartCommunityList(); break;
            case ThemeAction.CommunityDownload when SelectedCommunity() is { } theme: StartCommunityDownload(theme); break;
            case ThemeAction.CommunityLike when SelectedCommunity() is { } theme: StartCommunityLike(theme); break;
        }
        _renderer.Draw(context.Surface, _browser, _entries, context.TotalSeconds, context.DeltaSeconds);
        _stats.Record((float)context.DeltaSeconds);
        if (context.Pressed(ScePadButton.R3)) _showStats = !_showStats;
        if (_browser.View == ThemeView.System && _browser.Pending == ThemeAction.None && !_busy
            && context.Pressed(ScePadButton.Triangle))
            ToggleBackgroundScope();
        if (_showStats) _stats.Draw(context.Surface, 1500, 1060, scale: 1, Color.White);
    }

    private void ToggleBackgroundScope()
    {
        _allAppsBackgrounds = !_allAppsBackgrounds;
        _renderer.AllAppsBackgrounds = _allAppsBackgrounds;
        try
        {
            _settings = _settings with { AllAppsBackgrounds = _allAppsBackgrounds };
            WriteSettings();
            _renderer.ActionStatus = _allAppsBackgrounds
                ? "Takes effect on the next apply: backgrounds also replace the apps’ own images."
                : "Takes effect on the next apply: apps with their own image keep it.";
        }
        catch (Exception ex) { _renderer.ActionStatus = "Setting not saved: " + ex.Message; }
    }

    /// <summary>
    /// Options in the store-address keyboard: the typed address is checked and, when it is usable, stored in
    /// settings.json; the community list of the old store is dropped. A refused address reopens the keyboard.
    /// </summary>
    private void SaveStoreAddress()
    {
        string url = AppSettings.NormalizeStoreUrl(_browser.StoreEditor.Text);
        if (AppSettings.StoreUrlProblem(url) is { } problem) { _browser.ResumeStoreAddress(problem); return; }
        if (url == _settings.StoreUrl) { _renderer.ActionStatus = "Store address unchanged."; return; }
        // A store job still running would deliver the old store's list into the new address.
        if (_busy) { _browser.ResumeStoreAddress("The app is still busy. Press Options again in a moment."); return; }
        _settings = _settings with { StoreUrl = url };
        _browser.StoreUrl = url;
        _community = Array.Empty<CommunityTheme>();
        _renderer.Community = _community;
        _renderer.ResetCommunity();
        _browser.SetCommunityCount(0);
        _renderer.CommunityStatus = "";
        try
        {
            WriteSettings();
            _renderer.ActionStatus = url.Length == 0 ? "Store address removed." : "Store address saved.";
        }
        catch (Exception ex) { _renderer.ActionStatus = "Store address not saved, so it only lasts until you quit: " + ex.Message; }
    }

    private void SaveSettings()
    {
        try { WriteSettings(); }
        catch { /* the next start makes a new id; nothing else depends on it */ }
    }

    private void WriteSettings()
    {
        FileSystem.CreateDirectoryRecursive("/data/prospero-themes");
        FileSystem.WriteAllText(AppSettings.Path, _settings.Serialize());
    }

    // 32 hex characters that only have to differ between consoles. Not from HardwareEntropy: that adds a
    // libSceRandom need entry, and the module list is kept to what the app is known to start with.
    private static string NewDeviceId()
    {
        string seed = DateTime.UtcNow.Ticks + "-" + Environment.TickCount64 + "-" + Environment.ProcessId + "-" + GC.GetTotalAllocatedBytes();
        return SharpProspero.Security.Sha256.HashHex(System.Text.Encoding.UTF8.GetBytes(seed))[..32];
    }

    // ---- Community store ----------------------------------------------------------------------------

    private CommunityTheme? SelectedCommunity() =>
        _browser.CommunitySelected >= 0 && _browser.CommunitySelected < _community.Count ? _community[_browser.CommunitySelected] : null;

    /// <summary>
    /// Runs a store job on the worker with the shared HTTP client; the text it returns lands in the
    /// Community status line. Without a store address nothing is sent.
    /// </summary>
    private void StartCommunityWork(string busyText, Func<StoreHttpClient, string> work)
    {
        if (_busy) return;
        if (_settings.StoreUrl.Length == 0)
        {
            _renderer.CommunityStatus = "No store set. On the System tab, press Square and enter the store address.";
            return;
        }
        _busy = true;
        _renderer.Busy = true;
        _renderer.CommunityStatus = busyText;
        _worker = new Thread(() =>
        {
            string message;
            try
            {
                _http ??= StoreHttpClient.Create("ProsperoThemes/1.0");
                message = work(_http);
            }
            catch (Exception ex) { message = "Can’t reach the store: " + ex.Message; }
            _results.Post(() =>
            {
                _renderer.CommunityStatus = message;
                _renderer.Busy = false;
                _busy = false;
            });
        }) { IsBackground = true, Name = "Community" };
        _worker.Start();
    }

    private void StartCommunityList()
    {
        string baseUrl = _settings.StoreUrl;
        var sort = (CommunitySort)_browser.CommunitySort;
        StartCommunityWork($"Loading themes · {CommunityList.Label(sort)}…", http =>
        {
            HttpResponse answer = http.Get(CommunityList.ListUrl(baseUrl, sort));
            if (!answer.IsSuccess) return "Store responded with status " + answer.StatusCode + ".";
            List<CommunityTheme> list = CommunityList.Parse(System.Text.Encoding.UTF8.GetString(answer.Body), baseUrl);
            _results.Post(() =>
            {
                _renderer.ResetCommunity();
                _community = list;
                _renderer.Community = list;
                _browser.SetCommunityCount(list.Count);
                RefreshCommunityBadges();
            });
            // Previews follow one by one; each card shows its picture as soon as it is here.
            foreach (CommunityTheme theme in list)
            {
                if (theme.PreviewUrl is null) continue;
                try
                {
                    HttpResponse png = http.Get(theme.PreviewUrl);
                    if (!png.IsSuccess || png.Body.Length == 0) continue;
                    string id = theme.Id;
                    byte[] body = png.Body;
                    _results.Post(() => _renderer.CommunityPreviews[id] = body);
                }
                catch { /* a missing preview leaves the placeholder */ }
            }
            return list.Count == 0 ? "No themes in the store yet." : ThemeRenderer.Plural(list.Count, "theme") + " · " + CommunityList.Label(sort);
        });
    }

    private void StartCommunityDownload(CommunityTheme theme)
    {
        StartCommunityWork("Downloading “" + theme.Name + "”…", http =>
        {
            HttpResponse answer = http.Get(theme.DownloadUrl);
            if (!answer.IsSuccess) return "Download failed (" + answer.StatusCode + ").";
            // Checked exactly like a USB import before it is stored.
            bool added = DeviceThemeLibrary.AddToLibrary(answer.Body, theme.Name);
            string digest = SharpProspero.Security.Sha256.HashHex(answer.Body);
            _results.Post(() =>
            {
                _downloads[theme.Id] = digest;
                try { FileSystem.WriteAllText(CommunityList.DownloadsPath, CommunityList.SerializeDownloads(_downloads)); }
                catch { /* the badge lasts until the app quits */ }
                // The store counted this download; show it without reloading the list.
                var updated = new List<CommunityTheme>(_community);
                int i = updated.FindIndex(t => t.Id == theme.Id);
                if (i >= 0) updated[i] = updated[i] with { Downloads = updated[i].Downloads + 1 };
                _community = updated;
                _renderer.Community = updated;
                _rescanAfterWork = true;
            });
            return added ? "“" + theme.Name + "” is now in your library." : "“" + theme.Name + "” is already in your library.";
        });
    }

    /// <summary>Marks the store themes whose pack is in the library (after a scan, a list load or a download).</summary>
    private void RefreshCommunityBadges()
    {
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (ThemeLibraryEntry entry in _entries)
            if (CommunityList.LibraryDigest(entry.Path) is { } digest) digests.Add(digest);
        _renderer.CommunityInLibrary = CommunityList.InLibrary(_community, _downloads, digests);
    }

    private void StartCommunityLike(CommunityTheme theme)
    {
        string url = CommunityList.LikeUrl(_settings.StoreUrl, theme.Id), device = _settings.DeviceId;
        StartCommunityWork("Sending like…", http =>
        {
            HttpResponse answer = http.Send(SharpProspero.Platform.HttpMethod.Post, url, default, ["X-Device-Id: " + device]);
            int likes = answer.IsSuccess ? CommunityList.ParseLikes(System.Text.Encoding.UTF8.GetString(answer.Body)) : -1;
            if (likes < 0) return "Like not saved (" + answer.StatusCode + ").";
            _results.Post(() =>
            {
                var updated = new List<CommunityTheme>(_community);
                int i = updated.FindIndex(t => t.Id == theme.Id);
                if (i >= 0) updated[i] = updated[i] with { Likes = likes };
                _community = updated;
                _renderer.Community = updated;
            });
            return "Thanks! “" + theme.Name + "” now has " + ThemeRenderer.Plural(likes, "like") + ".";
        });
    }

    protected override void OnUnload()
    {
        _worker?.Join();
        _results.RunPending();
        _http?.Dispose();
        _renderer.Dispose();
    }
}

internal static class Program
{
    private static void Main()
    {
        using var app = new ThemeApp();
        app.Run();
    }
}
