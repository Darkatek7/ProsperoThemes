using System;
using System.Collections.Generic;
using System.Linq;
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
    // The themes the console shows now (look.json), read after every scan and every job; the library's last card saves it.
    private List<LookLayer>? _look;
    // The pack a scan should focus once it is done (the look just saved), by manifest id; set on the worker.
    private volatile string? _focusAfterScan;
    // Whether the first scan looked for a system update since the theme was applied; the plan the question was about.
    private bool _updateChecked;
    private SystemUpdate.Plan? _reapply;
    // The backups of the icons and sounds the last job wrote, while the app offers to restart the home screen for them.
    private IReadOnlyList<(string Backup, string Device)>? _shellBackups;
    // The parts Restore defaults offers, from the last scan or job (ReadRestorable); none known lists no parts.
    private ThemePart _restorable;
    // The worker runs a fit check (CheckFit). It is short and the user did not ask for it, so questions still open and
    // a job confirmed meanwhile waits in _afterFit and starts when the check is done.
    private bool _fitChecking;
    private Action? _afterFit;
    // Library packs whose fit was asked for since the helper last started (CheckFit); each one is asked once.
    private readonly HashSet<string> _fitAsked = new(StringComparer.Ordinal);
    // When each library pack came in (LibraryOrder.AddedPath), for the "newest" order; from the last scan.
    private IReadOnlyDictionary<string, long> _added = new Dictionary<string, long>(StringComparer.Ordinal);
    private ThemeRenderer _renderer = null!;
    // The preview's pictures, icons and sounds, read on workers of its own (they do not make the app busy), and its sound.
    private PreviewController _preview = null!;
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
    private StockCatalog _stock = StockCatalog.Empty;
    // The community store: the list on screen and one HTTP client (Http/Ssl resolved at run time, see
    // StoreHttpClient), created on first use (Http) by the worker or the update check. Each request has its own
    // connection, so the two can use it at once.
    private IReadOnlyList<CommunityTheme> _community = Array.Empty<CommunityTheme>();
    private StoreHttpClient? _http;
    private readonly object _httpLock = new();
    // The notice that a later release is out (CheckAppVersion), until the app is idle to show it.
    private string? _updateNotice;
    // Set by a finished download: the library is scanned again as soon as the worker is free.
    private bool _rescanAfterWork;
    // Store id -> pack hash of every theme downloaded here (CommunityList.DownloadsPath), for the badges.
    private Dictionary<string, string> _downloads = new(StringComparer.Ordinal);
    // Store ids this console has liked (CommunityList.LikesPath), for the hearts and for choosing like or unlike.
    // Changed on the frame thread only; the renderer reads the same set.
    private readonly HashSet<string> _liked = new(StringComparer.Ordinal);
    // The sort, the category (an index into CommunityCategories.List, -1 for all) and the search term the last list load
    // was started with, to notice a sort, a chip or a search made while the app was busy (see StartCommunityWork).
    private int _listSort, _listCategory = -1;
    private string _listQuery = "";
    private bool _listHideAi;
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
        // ConsolePreviewAudio is the one class that links libSceAudioOut and libSceAudiodec. NullPreviewAudio here leaves the
        // preview silent and, because the class is then never used, takes both need entries out of the build.
        _preview = new PreviewController(_renderer, _browser, new ConsolePreviewAudio(), DecodeCover,
            path => AppData.Files.ReadAllBytes(path), new ThreadWorker(_results));
        // Where the jailbreak starts the app without /data, ThemeHelper keeps the app's folder (see AppData).
        string? dataProblem = AppData.Probe();
        string? json = null;
        try { if (AppData.Files.Exists(AppSettings.Path)) json = AppData.Files.ReadAllText(AppSettings.Path); }
        catch { json = null; }
        _settings = AppSettings.Parse(json, NewDeviceId);
        // A first start creates the device id; keep it so likes stay one per console.
        if (json is null || !json.Contains(_settings.DeviceId)) SaveSettings();
        _allAppsBackgrounds = _settings.AllAppsBackgrounds;
        _renderer.AllAppsBackgrounds = _allAppsBackgrounds;
        _renderer.ReplaceEarlierThemes = _settings.ReplaceEarlierThemes;
        _renderer.Sounds = _settings.Sounds;
        _renderer.SoundVolume = _settings.SoundVolume;
        _renderer.MusicVolume = _settings.MusicVolume;
        _renderer.LibrarySort = _settings.LibrarySort;
        _browser.SetCommunityHideAi(_settings.HideAi);
        try { _firmware = SharpProspero.Platform.SystemInfo.SystemSoftwareVersion; }
        catch { _firmware = null; }
        _renderer.Firmware = _firmware ?? "";
        try { _stock = StockCatalog.Parse(StockCatalog.PathFor(_firmware) is { } catalog ? FileSystem.ReadAllText(catalog) : null); }
        catch { /* a firmware without a catalog: only the app's own records tell a theme's file from an original */ }
        try { _renderer.AppVersion = JsonValue.Parse(FileSystem.ReadAllText("/app0/sce_sys/param.json")).GetString("contentVersion", ""); }
        catch { /* the System screen then shows no app version */ }
        _browser.StoreUrl = _settings.StoreUrl;
        try
        {
            if (AppData.Files.Exists(CommunityList.DownloadsPath))
                _downloads = CommunityList.ParseDownloads(AppData.Files.ReadAllText(CommunityList.DownloadsPath));
        }
        catch { /* no badges for earlier downloads; new ones are recorded again */ }
        try
        {
            if (AppData.Files.Exists(CommunityList.LikesPath))
                foreach (string id in CommunityList.ParseLikedIds(AppData.Files.ReadAllText(CommunityList.LikesPath))) _liked.Add(id);
        }
        catch { /* no hearts for earlier likes until they are pressed again (the store keeps one like per console) */ }
        _renderer.CommunityLiked = _liked;
        StartWork(DeviceThemeLibrary.Scan);
        if (dataProblem is not null) _renderer.Notify("Can’t use " + AppData.Root + ": " + dataProblem, NoticeLevel.Error);
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
        if (_fitChecking) { _afterFit = () => StartWork(work, announce, notify); return; }
        if (_busy) { Wait(); return; }
        _busy = true;
        _renderer.Busy = true;
        _renderer.Status = "Loading library…";
        // The first scan also looks for a system update since the theme was applied (SystemUpdate).
        bool checkUpdate = !_updateChecked;
        _updateChecked = true;
        string? firmware = _firmware, skipped = _settings.ReapplySkipped;
        bool sounds = _settings.Sounds;
        _worker = new Thread(() =>
        {
            try
            {
                var snapshot = work();
                string? applied = ReadApplied();
                ThemePart restorable = ReadRestorable();
                List<LookLayer>? look = ReadLook();
                SystemUpdate.Plan? update = checkUpdate ? ReadUpdatePlan(snapshot.Entries, firmware, skipped, sounds) : null;
                _results.Post(() =>
                {
                    _restorable = restorable;
                    _renderer.ResetCovers();
                    _added = snapshot.Added ?? _added;
                    OrderLibrary(snapshot.Entries, applied, scanned: true);
                    ShowLook(look);
                    if (_focusAfterScan is { } focus)
                    {
                        _focusAfterScan = null;
                        for (int i = 0; i < _entries.Count; i++)
                            if (_entries[i].Manifest.Id == focus) _browser.Select(i);
                    }
                    if (update is not null && firmware is not null)
                    {
                        _reapply = update;
                        _renderer.ReapplyText = SystemUpdate.Text(update, firmware);
                        _browser.AskReapply();
                    }
                    if (checkUpdate) CheckAppVersion();
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

    /// <summary>The look the console shows now (<see cref="Look"/>), or null; on the worker, /data may be the helper's.</summary>
    private static List<LookLayer>? ReadLook()
    {
        try { return AppData.Files.Exists(AppData.Root + "/" + Look.FileName) ? Look.Parse(AppData.Files.ReadAllText(AppData.Root + "/" + Look.FileName)) : null; }
        catch { return null; }
    }

    /// <summary>The look for the library's "Save current look" card, which shows when it combines packs of the library.</summary>
    private void ShowLook(List<LookLayer>? look)
    {
        _look = look;
        _renderer.Look = look;
        _browser.LookSavable = LookPack.Savable(look, _entries.Select(e => e.Path));
    }

    /// <summary>
    /// Saves the look as a pack of the library (<see cref="LookPack"/>) and focuses it. The same look saved before is
    /// found by its id and not stored twice.
    /// </summary>
    private void SaveLook(List<LookLayer> look)
    {
        string name = LookPack.NextName(_entries.Select(e => e.Manifest.Name));
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ThemeLibraryEntry entry in _entries) saved.TryAdd(entry.Manifest.Id, entry.Manifest.Name);
        _renderer.Notify("Saving your look…", NoticeLevel.Info);
        StartWork(() =>
        {
            LookPack.Mix mix = LookPack.Build(look, layer => ThemeArchive.Open(AppData.Files.ReadAllBytes(layer.Pack)), name);
            _focusAfterScan = mix.Manifest.Id;
            if (saved.TryGetValue(mix.Manifest.Id, out string? existing))
                return DeviceThemeLibrary.Scan() with { Message = "This look is already in your library as “" + existing + "”." };
            DeviceThemeLibrary.AddToLibrary(mix.Archive, name);
            return DeviceThemeLibrary.Scan() with { Message = "Saved your look as “" + name + "” in your library." };
        }, notify: true);
    }

    /// <summary>The manifest id of the theme the console shows now (state.json, see <see cref="AppliedTheme"/>), or null.</summary>
    private static string? ReadApplied()
    {
        try { return AppData.Files.Exists(AppliedTheme.Path) ? AppliedTheme.Read(AppData.Files.ReadAllText(AppliedTheme.Path)) : null; }
        catch { return null; }
    }

    /// <summary>Whether to ask to apply the theme again after a system update (<see cref="SystemUpdate.Check"/>); on the worker.</summary>
    private static SystemUpdate.Plan? ReadUpdatePlan(IReadOnlyList<ThemeLibraryEntry> library, string? firmware, string? skipped, bool sounds)
    {
        static string? Text(string path) => AppData.Files.Exists(path) ? AppData.Files.ReadAllText(path) : null;
        try { return SystemUpdate.Check(Text(AppliedTheme.Path), Text(AppData.Root + "/" + Look.FileName), firmware, skipped, library, sounds); }
        catch { return null; }
    }

    /// <summary>
    /// Applies the layers of <paramref name="plan"/> again, bottom first: the first one from the defaults, the others on
    /// top of it, so the look is what it was before the update. Stops at the first that fails.
    /// </summary>
    private ApplyResult ReapplyLook(SystemUpdate.Plan plan, string busyText)
    {
        ApplyResult? result = null;
        for (int i = 0; i < plan.Layers.Count; i++)
        {
            LookLayer layer = plan.Layers[i];
            var applier = new ThemeApplier(new HelperDeviceFiles(), HelperDeviceFiles.UnixSeconds)
            {
                IncludeAllApps = _allAppsBackgrounds, KeepEarlierThemes = i > 0, Firmware = _firmware, Stock = _stock, Parts = layer.Parts,
                Progress = new WorkProgress(this, busyText), SourcePath = layer.Pack, UiSoundVolume = _settings.SoundVolume / 100.0, MusicVolume = _settings.MusicVolume / 100.0,
            };
            result = applier.Apply(ThemeArchive.Open(AppData.Files.ReadAllBytes(layer.Pack)));
            if (!result.Success) return new ApplyResult(false, "“" + layer.Name + "”: " + result.Message, result.Written);
        }
        string gone = plan.Missing.Count == 0 ? "" : " Left out, no longer in the library: " + string.Join(", ", plan.Missing) + ".";
        string count = plan.Layers.Count > 1 ? "Applied " + plan.Layers.Count + " themes again. " : "";
        return new ApplyResult(true, count + result!.Message + gone, result.Written);
    }

    /// <summary>"Not now" after a system update: not asked again until the next one.</summary>
    private void SkipReapply()
    {
        _reapply = null;
        if (_firmware is null) return;
        _settings = _settings with { ReapplySkipped = _firmware };
        try { WriteSettings(); }
        catch { /* asked again on the next start */ }
    }

    /// <summary>The parts Restore defaults offers (<see cref="Look.Restorable"/>); read on the worker, /data may be the helper's.</summary>
    private static ThemePart ReadRestorable()
    {
        static string? Text(string path) => AppData.Files.Exists(path) ? AppData.Files.ReadAllText(path) : null;
        try { return Look.Restorable(Text(AppData.Root + "/" + Look.FileName), Text(AppData.Root + "/" + ThemeApplier.BackupIndex)); }
        catch { return ThemePart.None; }
    }

    /// <summary>
    /// Puts <paramref name="entries"/> in the library's order (the applied theme first, then <see cref="AppSettings.LibrarySort"/>).
    /// The focus stays on the pack it was on; a scan (<paramref name="scanned"/>) also passes the new count on.
    /// </summary>
    private void OrderLibrary(IReadOnlyList<ThemeLibraryEntry> entries, string? applied, bool scanned = false)
    {
        string? focused = _browser.Selected >= 0 && _browser.Selected < _entries.Count ? _entries[_browser.Selected].Path : null;
        List<ThemeLibraryEntry> ordered = LibraryOrder.Sort(entries, applied, _settings.LibrarySort, _added);
        _entries = ordered;
        if (scanned) _browser.SetCount(ordered.Count);
        int index = focused is null ? -1 : ordered.FindIndex(e => e.Path == focused);
        if (index >= 0) _browser.Select(index);
        _renderer.LibraryReordered();
    }

    /// <summary>Triangle in the library: name or newest first. The applied theme stays in front either way.</summary>
    private void ToggleLibrarySort()
    {
        LibrarySort sort = _settings.LibrarySort == LibrarySort.Name ? LibrarySort.Newest : LibrarySort.Name;
        _settings = _settings with { LibrarySort = sort };
        _renderer.LibrarySort = sort;
        OrderLibrary(_entries, _renderer.AppliedId);
        try { WriteSettings(); }
        catch { /* the order lasts until the app quits */ }
    }

    /// <summary>
    /// The theme open in Detail: which of its files this console lacks, asked once per pack in the background. Only when
    /// the helper already runs (the check never starts it) and nothing else is running; it holds the worker for a moment,
    /// without a notice. The answer shows in the list of Detail (<see cref="ThemeRenderer.PackFits"/>).
    /// </summary>
    private void CheckFit()
    {
        if (_busy || _browser.View != ThemeView.Detail || _browser.Selected < 0 || _browser.Selected >= _entries.Count) return;
        ThemeLibraryEntry entry = _entries[_browser.Selected];
        if (_renderer.PackFits.ContainsKey(entry.Path) || !_fitAsked.Add(entry.Path)) return;
        _busy = _fitChecking = true;
        bool allApps = _allAppsBackgrounds;
        _worker = new Thread(() =>
        {
            PackFit? fit = null;
            try
            {
                if (ThemeHelperClient.IsRunning())
                    fit = new ThemeApplier(new HelperDeviceFiles(), HelperDeviceFiles.UnixSeconds) { IncludeAllApps = allApps }
                        .CheckFit(entry.Manifest);
            }
            catch { /* no fit shown; the apply reports what it skips */ }
            _results.Post(() =>
            {
                if (fit is not null && allApps == _allAppsBackgrounds) _renderer.PackFits[entry.Path] = fit;
                else if (fit is not null) _fitAsked.Remove(entry.Path);   // the scope changed meanwhile: ask again
                _busy = _fitChecking = false;
                Action? next = _afterFit;
                _afterFit = null;
                next?.Invoke();
            });
        }) { IsBackground = true, Name = "ThemeFit" };
        _worker.Start();
    }

    /// <summary>A press that needs the app idle came while a job runs.</summary>
    private void Wait() => _renderer.Notify("Please wait…", NoticeLevel.Info);

    /// <summary>
    /// Runs a system-changing job on the worker: starts ThemeHelper if it is not running yet, then hands the
    /// job an applier whose theme-root writes go through it. The result is shown as a notice.
    /// </summary>
    private void StartSystemWork(string busyText, Func<ThemeApplier, ApplyResult> work, ThemePart parts = ThemePart.All, string? source = null) =>
        StartHelperWork(busyText, needsHelper: true, _ => work(new ThemeApplier(new HelperDeviceFiles(), HelperDeviceFiles.UnixSeconds)
        {
            IncludeAllApps = _allAppsBackgrounds, KeepEarlierThemes = !_settings.ReplaceEarlierThemes, Firmware = _firmware, Stock = _stock, Parts = parts,
            Progress = new WorkProgress(this, busyText), SourcePath = source, UiSoundVolume = _settings.SoundVolume / 100.0, MusicVolume = _settings.MusicVolume / 100.0,
        }));

    /// <summary>
    /// Passes an apply's or restore's progress from the worker to the toast, at most about ten times a second (each
    /// step's first file always), so a job of many small files does not flood the frame thread.
    /// </summary>
    private sealed class WorkProgress(ThemeApp app, string busyText) : IApplyProgress
    {
        private const long MinGapMs = 100;
        private long _last = long.MinValue / 2;

        public void Report(string step, int index, int total)
        {
            long now = Environment.TickCount64;
            if (index > 1 && now - _last < MinGapMs) return;
            _last = now;
            // "Restoring defaults… 3 of 12", not "Restoring defaults… Restoring 3 of 12".
            string label = busyText.StartsWith(step, StringComparison.Ordinal) ? "" : step + " ";
            string text = busyText + " " + label + index + " of " + total;
            float fraction = total > 0 ? (index - 1f) / total : 0f;
            // Ignored once the job's result is in: it is posted after the last report, but a toast must not come back.
            app._results.Post(() => { if (app._busy) app._renderer.ShowProgress(text, fraction); });
        }
    }

    /// <summary>
    /// Starts the helper, then runs <paramref name="work"/> on the worker with the helper's problem (null when it
    /// runs). With <paramref name="needsHelper"/> a problem ends the job before it starts.
    /// </summary>
    private void StartHelperWork(string busyText, bool needsHelper, Func<string?, ApplyResult> work, bool offerRestart = true,
        bool holdResult = false)
    {
        if (_fitChecking)
        {
            _afterFit = () => StartHelperWork(busyText, needsHelper, work, offerRestart, holdResult);
            _renderer.Notify(busyText, NoticeLevel.Info);
            return;
        }
        if (_busy) { Wait(); return; }
        _busy = true;
        _renderer.Busy = true;
        _renderer.Status = busyText;
        _renderer.Notify(busyText, NoticeLevel.Info);
        _worker = new Thread(() =>
        {
            string message, status;
            bool ok = false;
            IReadOnlyList<(string Backup, string Device)>? shellBackups = null;
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
                    // New icons or sounds show once the home screen restarts; the app offers that (RestartShell).
                    if (offerRestart && helperProblem is null && result.Written.Any(ThemeApplier.LoadsWithShell))
                        shellBackups = new ThemeApplier(new HelperDeviceFiles(), HelperDeviceFiles.UnixSeconds).ShellBackups(result.Written);
                }
            }
            catch (Exception ex)
            {
                (status, message) = ("Error", ex.Message);
            }
            string? applied = ReadApplied();
            ThemePart restorable = ReadRestorable();
            List<LookLayer>? look = ReadLook();
            _results.Post(() =>
            {
                _restorable = restorable;
                ShowLook(look);
                _renderer.Status = status;
                // The theme now on the console leads the row; the focus stays on the pack it was on.
                if (applied != _renderer.AppliedId) OrderLibrary(_entries, applied);
                _renderer.AppliedId = applied;
                _renderer.Notify(message, ok ? NoticeLevel.Success : NoticeLevel.Error, holdResult);
                _renderer.Busy = false;
                _busy = false;
                // The helper may run now: packs that could not be checked before are asked again.
                _fitAsked.Clear();
                if (shellBackups is not null)
                {
                    _shellBackups = shellBackups;
                    _browser.AskRestartShell();
                }
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
        // A job's notice keeps the toast; the update notice waits until the app is idle.
        if (_updateNotice is not null && !_busy)
        {
            _renderer.Notify(_updateNotice, NoticeLevel.Info);
            _updateNotice = null;
        }
        CheckFit();
        if (context.Input.Buttons != 0 && context.Input.Buttons != context.PreviousInput.Buttons) _renderer.ReleaseNotice();
        _preview.BeforeUpdate(_entries);
        _browser.Update(UiInput.From(context.Input, context.PreviousInput),
            preview: context.Pressed(ScePadButton.Square), options: context.Pressed(ScePadButton.Options),
            triangle: context.Pressed(ScePadButton.Triangle), asksScope: SelectedHasAllAppsBackground(),
            allAppsScope: _allAppsBackgrounds, busy: _busy && !_fitChecking,
            removable: _browser.Selected >= 0 && _browser.Selected < _entries.Count && !_entries[_browser.Selected].Bundled,
            parts: SelectedParts(), sounds: _settings.Sounds, restorable: _restorable);
        switch (_browser.TakeAction())
        {
            case ThemeAction.Busy: Wait(); break;
            case ThemeAction.ToggleBackgroundScope: ToggleBackgroundScope(); break;
            case ThemeAction.ToggleEarlierThemes: ToggleEarlierThemes(); break;
            case ThemeAction.ToggleSounds: ToggleSounds(); break;
            case ThemeAction.ChangeSoundVolume: ChangeSoundVolume(_browser.SoundVolumeStep); break;
            case ThemeAction.ChangeMusicVolume: ChangeMusicVolume(_browser.SoundVolumeStep); break;
            case ThemeAction.AskSoundVolume:
                if (LookHasSounds()) _browser.AskSoundVolume();
                else _renderer.Notify("Your look has no theme music or sounds. The volume counts for the next theme with them.", NoticeLevel.Info);
                break;
            case ThemeAction.ApplySoundVolume when _look is { } volumeLook && SystemUpdate.FromLook(volumeLook, _entries) is { } volumePlan:
                StartHelperWork("Applying your look again…", needsHelper: true, _ => ReapplyLook(volumePlan, "Applying your look again…"));
                break;
            case ThemeAction.ToggleLibrarySort: ToggleLibrarySort(); break;
            case ThemeAction.PreviewMusic: _preview.ToggleMusic(); break;
            case ThemeAction.ImportUsb: StartWork(DeviceThemeLibrary.ImportUsb, announce: true); break;
            case ThemeAction.Reload: StartWork(DeviceThemeLibrary.Scan); break;
            case ThemeAction.Apply when _browser.Selected >= 0:
                // The answer to "apps without their own image or all apps" is also the saved setting. The apply
                // uses it even when settings.json cannot be written; only the next start would not know it.
                if (_browser.ApplyAsksScope) SetBackgroundScope(_browser.ApplyAllApps);
                string path = _entries[_browser.Selected].Path;
                StartSystemWork("Applying theme…", applier => applier.Apply(ThemeArchive.Open(AppData.Files.ReadAllBytes(path))), _browser.ApplyParts, path);
                break;
            case ThemeAction.Reapply when _reapply is { } plan:
                _reapply = null;
                StartHelperWork("Applying your theme again…", needsHelper: true, _ => ReapplyLook(plan, "Applying your theme again…"));
                break;
            case ThemeAction.ReapplyLater: SkipReapply(); break;
            case ThemeAction.RestartShell when _shellBackups is { } backups:
                _shellBackups = null;
                // The home screen is in front meanwhile, so the result waits for the user to come back and press a button.
                StartHelperWork("Restarting the home screen…", needsHelper: true, _ => RestartShell(backups), offerRestart: false, holdResult: true);
                break;
            case ThemeAction.SaveLook when _look is { Count: > 1 } look: SaveLook(look); break;
            case ThemeAction.RestoreOriginals:
                ThemePart restoreParts = _browser.RestoreParts;
                StartSystemWork("Restoring defaults…", applier => applier.RestoreOriginals(restoreParts));
                break;
            case ThemeAction.RemoveTheme when _browser.Selected >= 0 && !_entries[_browser.Selected].Bundled:
                ThemeLibraryEntry removed = _entries[_browser.Selected];
                StartWork(() => DeviceThemeLibrary.Remove(removed.Path, removed.Manifest.Name), notify: true);
                break;
            case ThemeAction.SaveStoreUrl: SaveStoreAddress(); break;
            case ThemeAction.FirmwareReport:
                // "Save and send" also gives it to the store; without a store address it is only saved.
                string? reportStore = _browser.SendReport ? _settings.StoreUrl : null;
                StartHelperWork(reportStore is null ? "Saving firmware report…" : "Sending firmware report…", needsHelper: false,
                    problem => SaveFirmwareReport(problem, reportStore));
                break;
            case ThemeAction.CommunityRefresh: StartCommunityList(); break;
            case ThemeAction.ToggleAiThemes: ToggleAiThemes(); break;
            case ThemeAction.CommunityDownload when SelectedCommunity() is { } theme: StartCommunityDownload(theme); break;
            case ThemeAction.CommunityLike when SelectedCommunity() is { } theme: StartCommunityLike(theme); break;
        }
        _preview.Update(_entries);
        _renderer.SelectedCommunityId = SelectedCommunity()?.Id;
        _renderer.Draw(context.Surface, _browser, _entries, context.TotalSeconds, context.DeltaSeconds);
        _stats.Record((float)context.DeltaSeconds);
        if (context.Pressed(ScePadButton.R3)) _showStats = !_showStats;
        if (_showStats) _stats.Draw(context.Surface, 1500, 1060, scale: 1, Color.White);
    }

    /// <summary>
    /// Restarts the home screen so the icons and sounds just written show. When the restarted home screen keeps
    /// crashing, the helper has put the backups back; the restore then runs as usual for those parts, so state.json and
    /// look.json say what the console shows.
    /// </summary>
    private ApplyResult RestartShell(IReadOnlyList<(string Backup, string Device)> backups)
    {
        var (restarts, restored) = ThemeHelperClient.RestartShell(backups);
        if (restored > 0)
        {
            ApplyResult back = new ThemeApplier(new HelperDeviceFiles(), HelperDeviceFiles.UnixSeconds) { Firmware = _firmware, Stock = _stock }
                .RestoreOriginals(ThemePart.Icons | ThemePart.Tiles | ThemePart.Sounds);
            return back with { Success = false, Message = "The home screen kept crashing with this theme, so the original icons and sounds were put back. " + back.Message };
        }
        if (restarts >= ThemeHelperClient.ShellLoopRestarts)
            return new ApplyResult(false, "The home screen keeps crashing. Please restart the console; if it goes on, restore the defaults.", []);
        return restarts > 0
            ? new ApplyResult(true, "The home screen restarted with the new icons and sounds.", [])
            : new ApplyResult(false, "The home screen did not restart. The new icons and sounds show after the next restart of the console.", []);
    }

    /// <summary>
    /// Writes the firmware report to <see cref="FirmwareReport.Folder"/> and, when a USB drive is connected, next to
    /// it on the drive. Reads only, and works without the helper too: then it records why the helper did not
    /// start and reads what the app itself may read. With <paramref name="store"/> it is also sent there, and the code
    /// the store answers with is shown (the notice and the System row).
    /// </summary>
    private ApplyResult SaveFirmwareReport(string? helperProblem, string? store)
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
        AppData.Files.CreateDirectoryRecursive(FirmwareReport.Folder);
        AppData.Files.WriteAllBytes(FirmwareReport.Folder + "/" + name, report);
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
        if (store is null)
            return new ApplyResult(true, $"Firmware report ({paths.Count} files) saved to {where}.{helper}", Array.Empty<string>());
        if (store.Length == 0)
            return new ApplyResult(false, $"Firmware report saved to {where}, but not sent: no store address is set.", Array.Empty<string>());

        string? code = null, problem = null;
        try
        {
            HttpResponse answer = Http().Send(SharpProspero.Platform.HttpMethod.Post, FirmwareReport.UploadUrl(store), report,
                ["Content-Type: application/json"]);
            string text = System.Text.Encoding.UTF8.GetString(answer.Body);
            code = answer.IsSuccess ? FirmwareReport.ParseCode(text) : null;
            if (code is null) problem = FirmwareReport.SendProblem(answer.StatusCode, text);
        }
        catch (Exception ex) { problem = "can’t reach the store: " + ex.Message; }
        if (code is null)
            return new ApplyResult(false, $"Firmware report saved to {where}, but not sent: {problem}.", Array.Empty<string>());
        _results.Post(() => _renderer.ReportCode = code);
        return new ApplyResult(true, $"Firmware report sent. Your code is {code}: post it in your GitHub issue. Also saved to {where}.{helper}",
            Array.Empty<string>());
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
    /// <summary>Whether the look on the console has a theme's music or sounds (only then is a new volume worth applying now).</summary>
    private bool LookHasSounds()
    {
        if (_look is null) return false;
        foreach (LookLayer layer in _look)
            if ((layer.Parts & ThemePart.Sounds) != 0) return true;
        return false;
    }

    /// <summary>Left or Right on the Sound volume row: saves the next volume; X there applies the look again with it.</summary>
    private void ChangeSoundVolume(int step)
    {
        int volume = AppSettings.StepSoundVolume(_settings.SoundVolume, step);
        if (volume == _settings.SoundVolume) return;
        _settings = _settings with { SoundVolume = volume };
        _renderer.SoundVolume = volume;
        try { WriteSettings(); }
        catch (Exception ex)
        {
            _renderer.Notify("Setting not saved: " + ex.Message, NoticeLevel.Error);
            return;
        }
        _renderer.Notify(LookHasSounds()
            ? "Theme sounds at " + volume + "% from the next apply. X applies your look again now."
            : "Theme sounds at " + volume + "% from the next apply.", NoticeLevel.Info);
    }

    /// <summary>Left or Right on the Music volume row: saves the next volume; X there applies the look again with it.</summary>
    private void ChangeMusicVolume(int step)
    {
        int volume = AppSettings.StepMusicVolume(_settings.MusicVolume, step);
        if (volume == _settings.MusicVolume) return;
        _settings = _settings with { MusicVolume = volume };
        _renderer.MusicVolume = volume;
        try { WriteSettings(); }
        catch (Exception ex)
        {
            _renderer.Notify("Setting not saved: " + ex.Message, NoticeLevel.Error);
            return;
        }
        _renderer.Notify(LookHasSounds()
            ? "Theme music at " + volume + "% from the next apply. X applies your look again now."
            : "Theme music at " + volume + "% from the next apply.", NoticeLevel.Info);
    }

    /// <summary>
    /// System → AI themes: the same setting as the Community tab's "No AI" chip, kept in settings.json. A list on
    /// screen is loaded again with it; a list load that is running notices the change when it ends.
    /// </summary>
    private void ToggleAiThemes()
    {
        bool hide = _browser.CommunityHideAi;
        _settings = _settings with { HideAi = hide };
        try { WriteSettings(); }
        catch (Exception ex)
        {
            _renderer.Notify("Setting not saved: " + ex.Message, NoticeLevel.Error);
            return;
        }
        _renderer.Notify(hide ? "The Community tab leaves out themes with AI pictures." : "The Community tab lists themes with AI pictures too.",
            NoticeLevel.Info);
        if (!_busy && _community.Count > 0) StartCommunityList();
    }

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
        if (allApps != _allAppsBackgrounds)
        {
            // Whether the all-apps background fits depends on the scope: every pack is checked again.
            _renderer.PackFits.Clear();
            _fitAsked.Clear();
        }
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
        AppData.Files.CreateDirectoryRecursive(AppData.Root);
        AppData.Files.WriteAllText(AppSettings.Path, _settings.Serialize());
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
                result = work(Http());
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
                _renderer.CommunityPicturesLoading = false;
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
                if (_browser.CommunitySort != _listSort || _browser.CommunityCategory != _listCategory || _browser.CommunityQuery != _listQuery
                    || _browser.CommunityHideAi != _listHideAi
                    || _reloadList) StartCommunityList();
                else if (_previewsMissing) StartCommunityPreviews();
            });
        }) { IsBackground = true, Name = "Community" };
        _worker.Start();
        return true;
    }

    private StoreHttpClient Http()
    {
        lock (_httpLock) return _http ??= StoreHttpClient.Create("ProsperoThemes/1.0");
    }

    /// <summary>
    /// Asks the store for the newest release (<see cref="AppUpdate"/>) on a thread of its own, so a slow or missing
    /// network holds up nothing. A later one is shown on the System screen and, once the app is idle, as a notice. Any
    /// failure says nothing; the next start asks again.
    /// </summary>
    private void CheckAppVersion()
    {
        string baseUrl = _settings.StoreUrl, current = _renderer.AppVersion;
        if (baseUrl.Length == 0 || current.Length == 0) return;
        new Thread(() =>
        {
            try
            {
                HttpResponse answer = Http().Get(AppUpdate.LatestUrl(baseUrl));
                if (!answer.IsSuccess) return;
                if (AppUpdate.Parse(System.Text.Encoding.UTF8.GetString(answer.Body)) is not { } latest ||
                    !AppUpdate.IsNewer(latest.Version, current)) return;
                _results.Post(() =>
                {
                    _renderer.NewerVersion = latest.Version;
                    _updateNotice = AppUpdate.Text(latest.Version, current);
                });
            }
            catch { /* no notice */ }
        }) { IsBackground = true, Name = "AppUpdate" }.Start();
    }

    private void StartCommunityList()
    {
        string baseUrl = _settings.StoreUrl;
        var sort = (CommunitySort)_browser.CommunitySort;
        int category = _browser.CommunityCategory;
        string query = _browser.CommunityQuery;
        bool hideAi = _browser.CommunityHideAi;
        string? categoryId = CommunityCategories.IdAt(category);
        // The "No AI" chip is a setting of its own, kept for the next start.
        if (hideAi != _settings.HideAi)
        {
            _settings = _settings with { HideAi = hideAi };
            try { WriteSettings(); }
            catch { /* it lasts until the app quits */ }
        }
        // "Loading themes · Anime · Top rated…", or without the category for all themes; "Searching “kanto” · …" for a search.
        string busyText = (query.Length > 0 ? $"Searching “{query}”" : "Loading themes")
            + (categoryId is null ? "" : " · " + CommunityCategories.LabelAt(category)) + $" · {CommunityList.Label(sort)}…";
        // A list or its pictures are loading already: the pills or chips moved on, so it stops after the picture it is
        // fetching, and its end starts this one (StartCommunityWork). Back on the list that is loading, it simply goes on
        // (unless a queued download or like is waiting for it to stop).
        if (_busy && _listLoading)
        {
            bool moved = (int)sort != _listSort || category != _listCategory || query != _listQuery || hideAi != _listHideAi;
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
            HttpResponse answer = http.GetWithRetry(CommunityList.ListUrl(baseUrl, sort, categoryId, query: query, hideAi: hideAi));
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
                _renderer.CommunityListQuery = query;
                // The cards that wait for their picture shimmer until the pictures are in (StartCommunityWork ends it).
                _renderer.CommunityPicturesLoading = true;
                _browser.SetCommunityCount(list.Count);
                RefreshCommunityBadges();
            });
            if (!LoadPreviews(http, list)) return new StoreResult("", NoticeLevel.Success);
            // A loaded list needs no toast: the screen shows the count, the category and the sort.
            return list.Count == 0
                ? new StoreResult(query.Length > 0 ? $"No themes match “{query}”." : categoryId is null ? "No themes in the store yet." : "No themes in this category yet.", NoticeLevel.Info)
                : new StoreResult("", NoticeLevel.Success);
        }, loadsList: true);
        if (started)
        {
            _listSort = (int)sort;
            _listCategory = category;
            _listQuery = query;
            _listHideAi = hideAi;
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
        if (started) _listLoading = _renderer.CommunityPicturesLoading = true;
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
                try { AppData.Files.WriteAllText(CommunityList.DownloadsPath, CommunityList.SerializeDownloads(_downloads)); }
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
                    AppData.Files.CreateDirectoryRecursive(AppData.Root);
                    AppData.Files.WriteAllText(CommunityList.LikesPath, CommunityList.FormatLikedIds(_liked));
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
        _preview.Dispose();
        _renderer.Dispose();
    }

    /// <summary>Runs the preview's reading and decoding on a thread of its own and hands the result back to the frame thread.</summary>
    private sealed class ThreadWorker(Dispatcher results) : IPreviewWorker
    {
        public void Run(Action work, Action done) =>
            new Thread(() =>
            {
                try { work(); }
                catch { /* the controller treats what the work left undone as a failure */ }
                results.Post(done);
            }) { IsBackground = true, Name = "ThemePreview" }.Start();
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
