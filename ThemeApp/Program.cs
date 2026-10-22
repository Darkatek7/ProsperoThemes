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
    private bool _busy, _pngAvailable, _jpegAvailable;
    // Frame timing readout, toggled with R3, for checking the pace on the device.
    private readonly FrameStats _stats = new();
    private bool _showStats;
    // User settings (background scope, store address, anonymous device id), kept in settings.json.
    private AppSettings _settings = null!;
    private bool _allAppsBackgrounds;
    /// <summary>The system software version ("13.000.000"), null when it could not be read.</summary>
    private string? _firmware;
    // The community store: the list on screen and one HTTP client (Http/Ssl resolved at run time, see
    // StoreHttpClient), created on first use by the worker.
    private IReadOnlyList<CommunityTheme> _community = Array.Empty<CommunityTheme>();
    private StoreHttpClient? _http;
    // Set by a finished download: the library is scanned again as soon as the worker is free.
    private bool _rescanAfterWork;
    // Store id -> pack hash of every theme downloaded here (CommunityList.DownloadsPath), for the badges.
    private Dictionary<string, string> _downloads = new(StringComparer.Ordinal);
    // Store ids this console has liked (CommunityList.LikesPath), for the hearts and for choosing like or unlike.
    // Changed on the frame thread only; the renderer reads the same set.
    private readonly HashSet<string> _liked = new(StringComparer.Ordinal);
    // The sort and the category (an index into CommunityCategories.List, -1 for all) the last list load was started with,
    // to notice a sort or a chip pressed while the app was busy (see StartCommunityWork).
    private int _listSort, _listCategory = -1;
    // A list load (or a load of the pictures it still lacks) is running (frame thread only). Another sort or chip, a
    // download or a like asks it to stop (_stopList, read by the worker between pictures); the worker sets _listStopped
    // when it did, and _listPosted once the list it loads is on screen. What comes next: the queued download or like,
    // then the list the pills and chips show (_reloadList when the stopped list never arrived), then the pictures the
    // cards still lack (_previewsMissing).
    private bool _listLoading, _reloadList, _previewsMissing;
    private volatile bool _stopList, _listStopped, _listPosted;
    private Action? _queuedJob;

    protected override void OnLoad()
    {
        try { SystemModule.Load(SystemModuleId.PngDec); _pngAvailable = true; }
        catch { _pngAvailable = false; }
        // The store's card pictures are JPEGs.
        try { SystemModule.Load(SystemModuleId.JpegDec); _jpegAvailable = true; }
        catch { _jpegAvailable = false; }
        _renderer = new ThemeRenderer(name => FileSystem.ReadAllBytes(DeviceThemeLibrary.AssetRoot + "/" + name), DecodeCover);
        string? json = null;
        try { if (FileSystem.Exists(AppSettings.Path)) json = FileSystem.ReadAllText(AppSettings.Path); }
        catch { json = null; }
        _settings = AppSettings.Parse(json, NewDeviceId);
        // A first start creates the device id; keep it so likes stay one per console.
        if (json is null || !json.Contains(_settings.DeviceId)) SaveSettings();
        _allAppsBackgrounds = _settings.AllAppsBackgrounds;
        _renderer.AllAppsBackgrounds = _allAppsBackgrounds;
        _renderer.ReplaceEarlierThemes = _settings.ReplaceEarlierThemes;
        _renderer.Sounds = _settings.Sounds;
        try { _firmware = SharpProspero.Platform.SystemInfo.SystemSoftwareVersion; }
        catch { _firmware = null; }
        _renderer.Firmware = _firmware ?? "";
        try { _renderer.AppVersion = JsonValue.Parse(FileSystem.ReadAllText("/app0/sce_sys/param.json")).GetString("contentVersion", ""); }
        catch { /* the System screen then shows no app version */ }
        _browser.StoreUrl = _settings.StoreUrl;
        try
        {
            if (FileSystem.Exists(CommunityList.DownloadsPath))
                _downloads = CommunityList.ParseDownloads(FileSystem.ReadAllText(CommunityList.DownloadsPath));
        }
        catch { /* no badges for earlier downloads; new ones are recorded again */ }
        try
        {
            if (FileSystem.Exists(CommunityList.LikesPath))
                foreach (string id in CommunityList.ParseLikedIds(FileSystem.ReadAllText(CommunityList.LikesPath))) _liked.Add(id);
        }
        catch { /* no hearts for earlier likes until they are pressed again (the store keeps one like per console) */ }
        _renderer.CommunityLiked = _liked;
        StartWork(DeviceThemeLibrary.Scan);
    }

    private PixelBuffer DecodeCover(byte[] bytes)
    {
        if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            if (!_jpegAvailable) throw new InvalidOperationException("JPEG decoder unavailable.");
            using var jpeg = JpegImage.Decode(bytes);
            var picture = new PixelBuffer(jpeg.Width, jpeg.Height);
            picture.AsSurface().Blit(jpeg.AsSurface(), 0, 0);
            return picture;
        }
        if (!_pngAvailable) throw new InvalidOperationException("PNG decoder unavailable.");
        using var image = PngImage.Decode(bytes);
        var result = new PixelBuffer(image.Width, image.Height);
        result.AsSurface().Blit(image.AsSurface(), 0, 0);
        return result;
    }

    /// <summary>
    /// Scans (or imports into) the library on the worker. The scan's message stays on the USB Import screen; an
    /// import also shows it as a notice, and a failure always does.
    /// </summary>
    private void StartWork(Func<LibrarySnapshot> work, bool announce = false, bool notify = false)
    {
        if (_busy) { Wait(); return; }
        _busy = true;
        _renderer.Busy = true;
        _renderer.Status = "Loading library…";
        _worker = new Thread(() =>
        {
            try
            {
                var snapshot = work();
                string? applied = ReadApplied();
                _results.Post(() =>
                {
                    _renderer.ResetCovers();
                    _entries = snapshot.Entries;
                    _browser.SetCount(_entries.Count);
                    _renderer.AppliedId = applied;
                    RefreshCommunityBadges();
                    // The USB Import screen shows the last import's result; a plain scan has nothing to say there.
                    if (announce)
                    {
                        _renderer.LibraryStatus = snapshot.Message;
                        _renderer.Notify(snapshot.Message, NoticeLevel.Info);
                    }
                    else if (notify) _renderer.Notify(snapshot.Message, NoticeLevel.Success);
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
                    _renderer.Notify(message, NoticeLevel.Error);
                    _renderer.Status = "Check file access";
                    _renderer.Busy = false;
                    _busy = false;
                });
            }
        }) { IsBackground = true, Name = "ThemeLibrary" };
        _worker.Start();
    }

    /// <summary>The manifest id of the theme the console shows now (state.json, see <see cref="AppliedTheme"/>), or null.</summary>
    private static string? ReadApplied()
    {
        try { return FileSystem.Exists(AppliedTheme.Path) ? AppliedTheme.Read(FileSystem.ReadAllText(AppliedTheme.Path)) : null; }
        catch { return null; }
    }

    /// <summary>A press that needs the app idle came while a job runs.</summary>
    private void Wait() => _renderer.Notify("Please wait…", NoticeLevel.Info);

    /// <summary>
    /// Runs a system-changing job on the worker: starts ThemeHelper if it is not running yet, then hands the
    /// job an applier whose theme-root writes go through it. The result is shown as a notice.
    /// </summary>
    private void StartSystemWork(string busyText, Func<ThemeApplier, ApplyResult> work, ThemePart parts = ThemePart.All) =>
        StartHelperWork(busyText, needsHelper: true, _ => work(new ThemeApplier(new HelperDeviceFiles(), HelperDeviceFiles.UnixSeconds)
            { IncludeAllApps = _allAppsBackgrounds, KeepEarlierThemes = !_settings.ReplaceEarlierThemes, Firmware = _firmware, Parts = parts }));

    /// <summary>
    /// Starts the helper, then runs <paramref name="work"/> on the worker with the helper's problem (null when it
    /// runs). With <paramref name="needsHelper"/> a problem ends the job before it starts.
    /// </summary>
    private void StartHelperWork(string busyText, bool needsHelper, Func<string?, ApplyResult> work)
    {
        if (_busy) { Wait(); return; }
        _busy = true;
        _renderer.Busy = true;
        _renderer.Status = busyText;
        _renderer.Notify(busyText, NoticeLevel.Info);
        _worker = new Thread(() =>
        {
            string message, status;
            bool ok = false;
            try
            {
                string? helperProblem = ThemeHelperClient.EnsureRunning();
                if (helperProblem is not null && needsHelper)
                {
                    (status, message) = ("Helper not ready", helperProblem);
                }
                else
                {
                    ApplyResult result = work(helperProblem);
                    (status, message) = (result.Success ? "Done" : "Not completed", result.Message);
                    ok = result.Success;
                }
            }
            catch (Exception ex)
            {
                (status, message) = ("Error", ex.Message);
            }
            string? applied = ReadApplied();
            _results.Post(() =>
            {
                _renderer.Status = status;
                _renderer.AppliedId = applied;
                _renderer.Notify(message, ok ? NoticeLevel.Success : NoticeLevel.Error);
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
            triangle: context.Pressed(ScePadButton.Triangle), asksScope: SelectedHasAllAppsBackground(),
            allAppsScope: _allAppsBackgrounds, busy: _busy,
            removable: _browser.Selected >= 0 && _browser.Selected < _entries.Count && !_entries[_browser.Selected].Bundled,
            parts: SelectedParts(), sounds: _settings.Sounds);
        switch (_browser.TakeAction())
        {
            case ThemeAction.Busy: Wait(); break;
            case ThemeAction.ToggleBackgroundScope: ToggleBackgroundScope(); break;
            case ThemeAction.ToggleEarlierThemes: ToggleEarlierThemes(); break;
            case ThemeAction.ToggleSounds: ToggleSounds(); break;
            case ThemeAction.ImportUsb: StartWork(DeviceThemeLibrary.ImportUsb, announce: true); break;
            case ThemeAction.Reload: StartWork(DeviceThemeLibrary.Scan); break;
            case ThemeAction.Apply when _browser.Selected >= 0:
                // The answer to "apps without their own image or all apps" is also the saved setting. The apply
                // uses it even when settings.json cannot be written; only the next start would not know it.
                if (_browser.ApplyAsksScope) SetBackgroundScope(_browser.ApplyAllApps);
                string path = _entries[_browser.Selected].Path;
                StartSystemWork("Applying theme…", applier => applier.Apply(ThemeArchive.Open(FileSystem.ReadAllBytes(path))), _browser.ApplyParts);
                break;
            case ThemeAction.RestoreOriginals:
                StartSystemWork("Restoring defaults…", applier => applier.RestoreOriginals());
                break;
            case ThemeAction.RemoveTheme when _browser.Selected >= 0 && !_entries[_browser.Selected].Bundled:
                ThemeLibraryEntry removed = _entries[_browser.Selected];
                StartWork(() => DeviceThemeLibrary.Remove(removed.Path, removed.Manifest.Name), notify: true);
                break;
            case ThemeAction.SaveStoreUrl: SaveStoreAddress(); break;
            case ThemeAction.FirmwareReport: StartHelperWork("Saving firmware report…", needsHelper: false, SaveFirmwareReport); break;
            case ThemeAction.CommunityRefresh: StartCommunityList(); break;
            case ThemeAction.CommunityDownload when SelectedCommunity() is { } theme: StartCommunityDownload(theme); break;
            case ThemeAction.CommunityLike when SelectedCommunity() is { } theme: StartCommunityLike(theme); break;
        }
        _renderer.SelectedCommunityId = SelectedCommunity()?.Id;
        _renderer.Draw(context.Surface, _browser, _entries, context.TotalSeconds, context.DeltaSeconds);
        _stats.Record((float)context.DeltaSeconds);
        if (context.Pressed(ScePadButton.R3)) _showStats = !_showStats;
        if (_showStats) _stats.Draw(context.Surface, 1500, 1060, scale: 1, Color.White);
    }

    /// <summary>
    /// Writes the firmware report to <see cref="FirmwareReport.Folder"/> and, when a USB drive is connected, next to
    /// it on the drive. Reads only, and works without the helper too: then it records why the helper did not
    /// start and reads what the app itself may read.
    /// </summary>
    private ApplyResult SaveFirmwareReport(string? helperProblem)
    {
        string? catalog = null;
        try { catalog = FileSystem.ReadAllText(FirmwareReport.BundledCatalog); }
        catch { /* the report then covers the backgrounds, tile icons and icons only */ }
        string version = "unknown";
        try { version = JsonValue.Parse(FileSystem.ReadAllText("/app0/sce_sys/param.json")).GetString("contentVersion", "unknown"); }
        catch { }
        List<string> paths = FirmwareReport.Paths(FirmwareReport.CatalogPaths(catalog));
        var files = new ReportDeviceFiles(helperRunning: helperProblem is null);
        byte[] report = FirmwareReport.Build(files, _firmware, version,
            ThemeApplier.FormatUtc(HelperDeviceFiles.UnixSeconds()), paths, after: () =>
            [
                ("helper", helperProblem ?? "running"),
                ("app_reads", files.AppReads.ToString()),
                ("helper_reads", files.HelperReads.ToString()),
            ]);
        string name = FirmwareReport.FileName(_firmware);
        Folders.Ensure(FirmwareReport.Folder);
        FileSystem.WriteAllBytes(FirmwareReport.Folder + "/" + name, report);
        string where = FirmwareReport.Folder + "/" + name;
        try
        {
            if (FileSystem.Exists("/mnt/usb0"))
            {
                FileSystem.WriteAllBytes("/mnt/usb0/" + name, report);
                where += " and on the USB drive";
            }
        }
        catch { /* the copy in /data is enough */ }
        string helper = helperProblem is null ? "" : " The theme helper did not start; the report says why.";
        return new ApplyResult(true, $"Firmware report ({paths.Count} files) saved to {where}.{helper}", Array.Empty<string>());
    }

    private void ToggleBackgroundScope()
    {
        if (SetBackgroundScope(!_allAppsBackgrounds) is { } problem)
        {
            _renderer.Notify(problem, NoticeLevel.Error);
            return;
        }
        _renderer.Notify(_allAppsBackgrounds
            ? "Takes effect on the next apply: backgrounds also replace the apps’ own images."
            : "Takes effect on the next apply: apps with their own image keep it.", NoticeLevel.Success);
    }

    /// <summary>The Music and sounds row on the System tab: whether an apply takes a theme's music and sounds.</summary>
    private void ToggleSounds()
    {
        bool on = !_settings.Sounds;
        _settings = _settings with { Sounds = on };
        _renderer.Sounds = on;
        try { WriteSettings(); }
        catch (Exception ex)
        {
            _renderer.Notify("Setting not saved: " + ex.Message, NoticeLevel.Error);
            return;
        }
        _renderer.Notify(on
            ? "Takes effect on the next apply: themes may change the music and sounds."
            : "Takes effect on the next apply: themes leave the music and sounds alone. Restore defaults puts back earlier ones.",
            NoticeLevel.Success);
    }

    // The parts of the selected pack, worked out once per pack (an icon theme has hundreds of targets).
    private string? _partsFor;
    private ThemePart _parts;

    private ThemePart SelectedParts()
    {
        if (_browser.Selected < 0 || _browser.Selected >= _entries.Count) return ThemePart.None;
        ThemeLibraryEntry entry = _entries[_browser.Selected];
        if (_partsFor != entry.Path) { _partsFor = entry.Path; _parts = ThemeParts.In(entry.Manifest); }
        return _parts;
    }

    /// <summary>The Earlier themes row on the System tab: whether an apply keeps or replaces earlier themes.</summary>
    private void ToggleEarlierThemes()
    {
        bool replace = !_settings.ReplaceEarlierThemes;
        _settings = _settings with { ReplaceEarlierThemes = replace };
        _renderer.ReplaceEarlierThemes = replace;
        try { WriteSettings(); }
        catch (Exception ex)
        {
            _renderer.Notify("Setting not saved: " + ex.Message, NoticeLevel.Error);
            return;
        }
        _renderer.Notify(replace
            ? "Takes effect on the next apply: a theme first puts back what earlier themes changed."
            : "Takes effect on the next apply: a theme keeps what earlier themes changed, so themes combine.", NoticeLevel.Success);
    }

    /// <summary>
    /// Sets and saves the background scope: the App backgrounds row on the System tab, or the answer when an apply
    /// asks. Null when it was saved, otherwise why not.
    /// </summary>
    private string? SetBackgroundScope(bool allApps)
    {
        _allAppsBackgrounds = allApps;
        _renderer.AllAppsBackgrounds = allApps;
        try
        {
            _settings = _settings with { AllAppsBackgrounds = allApps };
            WriteSettings();
            return null;
        }
        catch (Exception ex) { return "Setting not saved: " + ex.Message; }
    }

    /// <summary>Whether the selected pack has an all-apps background, so applying it asks where that goes.</summary>
    private bool SelectedHasAllAppsBackground()
    {
        if (_browser.Selected < 0) return false;
        foreach (var target in _entries[_browser.Selected].Manifest.Targets)
            if (target.Path == ProsperoThemes.Shared.AssetRoots.AllAppsBackground) return true;
        return false;
    }

    /// <summary>
    /// Options in the store-address keyboard: the typed address is checked and, when it is usable, stored in
    /// settings.json; the community list of the old store is dropped. A refused address reopens the keyboard.
    /// </summary>
    private void SaveStoreAddress()
    {
        string url = AppSettings.NormalizeStoreUrl(_browser.StoreEditor.Text);
        if (AppSettings.StoreUrlProblem(url) is { } problem) { _browser.ResumeStoreAddress(problem); return; }
        if (url == _settings.StoreUrl) { _renderer.Notify("Store address unchanged.", NoticeLevel.Info); return; }
        // A store job still running would deliver the old store's list into the new address.
        if (_busy) { _browser.ResumeStoreAddress("The app is still busy. Press Options again in a moment."); return; }
        _settings = _settings with { StoreUrl = url };
        _browser.StoreUrl = url;
        _community = Array.Empty<CommunityTheme>();
        _renderer.Community = _community;
        _renderer.ResetCommunity();
        _browser.SetCommunityCount(0);
        _renderer.CommunityStatus = "";
        _renderer.CommunityError = false;
        _renderer.CommunityListSort = -1;
        _renderer.CommunityListCategory = -1;
        try
        {
            WriteSettings();
            _renderer.Notify(url.Length == 0 ? "Store address removed." : "Store address saved.", NoticeLevel.Success);
        }
        catch (Exception ex) { _renderer.Notify("Store address not saved, so it only lasts until you quit: " + ex.Message, NoticeLevel.Error); }
    }

    private void SaveSettings()
    {
        try { WriteSettings(); }
        catch { /* the next start makes a new id; nothing else depends on it */ }
    }

    private void WriteSettings()
    {
        Folders.Ensure("/data/prospero-themes");
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

    /// <summary>What a store job reports: the text and how it reads.</summary>
    private readonly record struct StoreResult(string Message, NoticeLevel Level);

    /// <summary>
    /// Runs a store job on the worker with the shared HTTP client and shows the text it returns as a notice.
    /// A job that loads the list (<paramref name="loadsList"/>) also keeps its text as the Community tab's empty or
    /// error state. A result without text posts no notice (and takes the progress notice down, unless the job had no
    /// progress text either). Without a store address nothing is sent. Returns whether the job started now.
    /// While list pictures load, the job is queued instead: the pictures stop after the one on its way, the job runs, and
    /// the rest of the pictures follow.
    /// </summary>
    private bool StartCommunityWork(string busyText, Func<StoreHttpClient, StoreResult> work, bool loadsList = false)
    {
        if (_busy)
        {
            if (_listLoading && _queuedJob is null)
            {
                _queuedJob = () => StartCommunityWork(busyText, work, loadsList);
                _stopList = true;
                if (busyText.Length > 0) _renderer.Notify(busyText, NoticeLevel.Info);
            }
            else Wait();
            return false;
        }
        if (_settings.StoreUrl.Length == 0)
        {
            _renderer.CommunityStatus = "No store set. On the System tab, choose Store address and enter the address.";
            return false;
        }
        _busy = true;
        _renderer.Busy = true;
        if (loadsList) _renderer.CommunityStatus = busyText;
        if (busyText.Length > 0) _renderer.Notify(busyText, NoticeLevel.Info);
        _worker = new Thread(() =>
        {
            StoreResult result;
            try
            {
                _http ??= StoreHttpClient.Create("ProsperoThemes/1.0");
                result = work(_http);
            }
            catch (Exception ex) { result = new StoreResult("Can’t reach the store: " + ex.Message, NoticeLevel.Error); }
            _results.Post(() =>
            {
                // The state text is only drawn while the list is empty; a list that is on screen never carries it.
                if (loadsList)
                {
                    _renderer.CommunityStatus = result.Level == NoticeLevel.Error || _community.Count == 0 ? result.Message : "";
                    _renderer.CommunityError = result.Level == NoticeLevel.Error;
                }
                if (result.Message.Length > 0) _renderer.Notify(result.Message, result.Level);
                else if (busyText.Length > 0) _renderer.ClearNotice();
                _renderer.Busy = false;
                _busy = false;
                _listLoading = false;
                if (_listStopped)
                {
                    if (_listPosted) _previewsMissing = true;
                    else _reloadList = true;
                    _listStopped = false;
                }
                // A download or a like pressed while the pictures loaded goes first.
                if (_queuedJob is { } next)
                {
                    _queuedJob = null;
                    next();
                    return;
                }
                // A sort or a category chip pressed while this job ran moved the pills and chips but not the list (another
                // job refused it with "Please wait"; a list load stopped early for it). Load it now, with the sort and
                // category they show. A list stopped before it arrived is loaded again even when the pills went back to it.
                if (_browser.CommunitySort != _listSort || _browser.CommunityCategory != _listCategory || _reloadList) StartCommunityList();
                else if (_previewsMissing) StartCommunityPreviews();
            });
        }) { IsBackground = true, Name = "Community" };
        _worker.Start();
        return true;
    }

    private void StartCommunityList()
    {
        string baseUrl = _settings.StoreUrl;
        var sort = (CommunitySort)_browser.CommunitySort;
        int category = _browser.CommunityCategory;
        string? categoryId = CommunityCategories.IdAt(category);
        // "Loading themes · Anime · Top rated…", or without the category for all themes.
        string busyText = categoryId is null
            ? $"Loading themes · {CommunityList.Label(sort)}…"
            : $"Loading themes · {CommunityCategories.LabelAt(category)} · {CommunityList.Label(sort)}…";
        // A list or its pictures are loading already: the pills or chips moved on, so it stops after the picture it is
        // fetching, and its end starts this one (StartCommunityWork). Back on the list that is loading, it simply goes on
        // (unless a queued download or like is waiting for it to stop).
        if (_busy && _listLoading)
        {
            bool moved = (int)sort != _listSort || category != _listCategory;
            _stopList = moved || _queuedJob is not null;
            if (moved)
            {
                _renderer.Notify(busyText, NoticeLevel.Info);
                if (_community.Count == 0) _renderer.CommunityStatus = busyText;
            }
            return;
        }
        _stopList = false;
        _listStopped = false;
        _listPosted = false;
        bool started = StartCommunityWork(busyText, http =>
        {
            HttpResponse answer = http.GetWithRetry(CommunityList.ListUrl(baseUrl, sort, categoryId));
            if (!answer.IsSuccess) return new StoreResult("Store responded with status " + answer.StatusCode + ".", NoticeLevel.Error);
            List<CommunityTheme> list = CommunityList.Parse(System.Text.Encoding.UTF8.GetString(answer.Body), baseUrl);
            // Asked to stop while the list was on its way: the old list stays on screen until the new one is here.
            if (_stopList)
            {
                _listStopped = true;
                return new StoreResult("", NoticeLevel.Success);
            }
            _listPosted = true;
            _results.Post(() =>
            {
                _renderer.ResetCommunity();
                _community = list;
                _renderer.Community = list;
                // The header names the sort and category this list came with, not the ones the pills and chips show now.
                _renderer.CommunityListSort = (int)sort;
                _renderer.CommunityListCategory = category;
                _browser.SetCommunityCount(list.Count);
                RefreshCommunityBadges();
            });
            if (!LoadPreviews(http, list)) return new StoreResult("", NoticeLevel.Success);
            // A loaded list needs no toast: the screen shows the count, the category and the sort.
            return list.Count == 0
                ? new StoreResult(categoryId is null ? "No themes in the store yet." : "No themes in this category yet.", NoticeLevel.Info)
                : new StoreResult("", NoticeLevel.Success);
        }, loadsList: true);
        if (started)
        {
            _listSort = (int)sort;
            _listCategory = category;
            _listLoading = true;
            _reloadList = false;
            _previewsMissing = false;
        }
    }

    /// <summary>
    /// Fetches the pictures of <paramref name="themes"/> one by one on the worker; each card shows its picture as soon as
    /// it is here. Returns false when it was asked to stop (and stopped) before the last one.
    /// </summary>
    private bool LoadPreviews(StoreHttpClient http, List<CommunityTheme> themes)
    {
        foreach (CommunityTheme theme in themes)
        {
            if (_stopList)
            {
                _listStopped = true;
                return false;
            }
            if (theme.PictureUrl is null) continue;
            try
            {
                HttpResponse picture = http.Get(theme.PictureUrl);
                if (!picture.IsSuccess || picture.Body.Length == 0) continue;
                string id = theme.Id;
                byte[] body = picture.Body;
                _results.Post(() => _renderer.CommunityPreviews[id] = body);
            }
            catch { /* a missing preview leaves the placeholder */ }
        }
        return true;
    }

    /// <summary>
    /// Fetches the pictures the cards on screen still lack, after a download or a like stopped the list's pictures. It
    /// shows no notice and stops for the next sort, chip, download or like just like a list load.
    /// </summary>
    private void StartCommunityPreviews()
    {
        _previewsMissing = false;
        var missing = new List<CommunityTheme>();
        foreach (CommunityTheme theme in _community)
            if (theme.PictureUrl is not null && !_renderer.CommunityPreviews.ContainsKey(theme.Id)) missing.Add(theme);
        if (missing.Count == 0) return;
        _stopList = false;
        _listStopped = false;
        _listPosted = true;
        bool started = StartCommunityWork("", http =>
        {
            LoadPreviews(http, missing);
            return new StoreResult("", NoticeLevel.Success);
        });
        if (started) _listLoading = true;
    }

    private void StartCommunityDownload(CommunityTheme theme)
    {
        // The pack an earlier download of this theme stored; an update replaces it in the library.
        string? previous = _downloads.TryGetValue(theme.Id, out string? recorded) ? recorded : null;
        StartCommunityWork("Downloading “" + theme.Name + "”…", http =>
        {
            HttpResponse answer = http.GetWithRetry(theme.DownloadUrl);
            if (!answer.IsSuccess) return new StoreResult("Download failed (" + answer.StatusCode + ").", NoticeLevel.Error);
            // Checked exactly like a USB import before it is stored.
            bool added = DeviceThemeLibrary.AddToLibrary(answer.Body, theme.Name);
            string digest = SharpProspero.Security.Sha256.HashHex(answer.Body);
            bool replaced = false;
            if (previous is not null && previous != digest)
            {
                try { replaced = DeviceThemeLibrary.Retire(previous); }
                catch { /* the old version stays in the library next to the new one */ }
            }
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
            if (replaced)
                return new StoreResult("Updated “" + theme.Name + "”" + (theme.Version.Length > 0 ? " to v" + theme.Version : "") +
                    ". If it is applied, apply it again to see the changes.", NoticeLevel.Success);
            return added
                ? new StoreResult("“" + theme.Name + "” is now in your library.", NoticeLevel.Success)
                : new StoreResult("“" + theme.Name + "” is already in your library.", NoticeLevel.Info);
        });
    }

    /// <summary>Marks the store themes whose pack is in the library (after a scan, a list load or a download).</summary>
    private void RefreshCommunityBadges()
    {
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (ThemeLibraryEntry entry in _entries)
            if (CommunityList.LibraryDigest(entry.Path) is { } digest) digests.Add(digest);
        _renderer.CommunityInLibrary = CommunityList.InLibrary(_community, _downloads, digests);
        _renderer.CommunityUpdates = CommunityList.Updates(_community, _downloads, digests);
    }

    /// <summary>
    /// Triangle on a store card: likes the theme, or takes the like back when this console already liked it. The
    /// answer carries the new count and whether the theme is liked now; the heart follows that, and the ids this
    /// console liked are kept in likes.json so the hearts survive a restart.
    /// </summary>
    private void StartCommunityLike(CommunityTheme theme)
    {
        string url = CommunityList.LikeUrl(_settings.StoreUrl, theme.Id), device = _settings.DeviceId;
        bool unlike = _liked.Contains(theme.Id);
        StartCommunityWork(unlike ? "Removing like…" : "Sending like…", http =>
        {
            HttpResponse answer = http.Send(CommunityList.LikeMethod(unlike), url, default, ["X-Device-Id: " + device]);
            string body = answer.IsSuccess ? System.Text.Encoding.UTF8.GetString(answer.Body) : "";
            int likes = answer.IsSuccess ? CommunityList.ParseLikes(body) : -1;
            if (likes < 0) return new StoreResult((unlike ? "Like not removed (" : "Like not saved (") + answer.StatusCode + ").", NoticeLevel.Error);
            // The store says what it did; without an answer for it, the request decides.
            bool liked = CommunityList.ParseLiked(body) ?? !unlike;
            _results.Post(() =>
            {
                var updated = new List<CommunityTheme>(_community);
                int i = updated.FindIndex(t => t.Id == theme.Id);
                if (i >= 0) updated[i] = updated[i] with { Likes = likes };
                _community = updated;
                _renderer.Community = updated;
                if (liked) _liked.Add(theme.Id); else _liked.Remove(theme.Id);
                try
                {
                    Folders.Ensure("/data/prospero-themes");
                    FileSystem.WriteAllText(CommunityList.LikesPath, CommunityList.FormatLikedIds(_liked));
                }
                catch { /* the heart lasts until the app quits */ }
            });
            return new StoreResult(liked ? "Liked “" + theme.Name + "”." : "Like removed from “" + theme.Name + "”.", NoticeLevel.Success);
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
