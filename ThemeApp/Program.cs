using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;
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
    // The library in its order (the applied theme first, then the sort), and the part of it the Library shows: the packs that match its
    // search (LibraryFilter), or all of them without one. Everything that works on "the selected pack" (_browser.Selected) reads
    // _entries, which is what the renderer and the preview draw; whatever is about the whole library (the look, names, badges) reads _library.
    private IReadOnlyList<ThemeLibraryEntry> _library = Array.Empty<ThemeLibraryEntry>();
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
    // The firmware the System tab asks a report for (see AppSettings.ReportToAsk) until it is answered; null when not asked.
    private string? _reportAsk;
    // The community store: the list on screen and one HTTP client (Http/Ssl resolved at run time, see
    // StoreHttpClient), created on first use (Http) by the worker or the update check. Each request has its own
    // connection, so the two can use it at once.
    private IReadOnlyList<CommunityTheme> _community = Array.Empty<CommunityTheme>();
    private StoreHttpClient? _http;
    private readonly object _httpLock = new();
    // The notice that a later release is out (CheckAppVersion), until the app is idle to show it.
    private string? _updateNotice;
    // The newer release the store announced (null when none, or not asked yet), and the version this session installed over
    // itself (null until it did): then the files on disk are the new ones and the app has to be started again.
    private AppUpdate.Release? _latest;
    private string? _installedVersion;
    private volatile bool _versionChecking;
    // Set by a finished download: the library is scanned again as soon as the worker is free.
    private bool _rescanAfterWork;
    // "Download and apply": the hash of the pack just downloaded. The scan that follows selects the library pack named after it
    // and opens the apply question for it (OfferApply); null when no download is waiting for that.
    private string? _applyAfterScan;
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
    // The downloads the user asked for while the app was busy, run one after another (StartQueuedDownload). Holds the store records,
    // not list positions, so a reload of the list leaves it alone. _downloadFailures counts the failed ones of the run, so the
    // notice of the last one can say so (each next "Downloading…" notice replaces the result of the one before).
    private readonly DownloadQueue<CommunityTheme> _downloadQueue = new(theme => theme.Id);
    private int _downloadFailures;
    // The list on screen comes a page at a time (CommunityList.PageSize): the page loaded last, the store's total for it (-1 when
    // it did not say) and whether the store has more after it. _communityRetryAt is the focused card when a page failed: the
    // next page is asked for again once the focus has moved, not on every frame (-1 for none).
    private int _communityPage = 1, _communityTotal = -1, _communityRetryAt = -1;
    private bool _communityMore;
    // The app's avatars (AvatarLibrary) and the user whose picture they set: the one who started the app (null when its id could
    // not be read). The Library's avatar row shows the list, so it is read after every library scan (_avatarScanAfterWork runs it
    // once the scan's job is done).
    private List<AvatarEntry> _avatars = new();
    private int? _userId;
    private bool _avatarScanAfterWork;
    // The offer to restart the home screen is about a profile picture, not a theme's icons and sounds.
    private bool _avatarRestart;
    // The Avatars tab: the store's avatar list on screen, and the store ids this console liked (AvatarStore.LikesPath). Changed on
    // the frame thread only; the renderer reads the same objects.
    private IReadOnlyList<StoreAvatar> _avatarStore = Array.Empty<StoreAvatar>();
    private readonly HashSet<string> _avatarLiked = new(StringComparer.Ordinal);
    // The sort the avatar list load that runs (or ran last) was started with: a Square press that moves the pills while it
    // runs is noticed against it. -1 before any load.
    private int _avatarJobSort = -1;
    // The search term of that load, for the same check (a new term while it runs moves on to the new list).
    private string _avatarJobQuery = "";
    // An avatar list load (or the load of the pictures it still lacks) runs. A new sort, a download or a like asks it to stop
    // (_stopAvatarList, read by the worker between pictures); the worker sets _avatarListStopped when it did, and
    // _avatarListPosted once the list it loads is on screen. OnFrame then loads the list again (_avatarListWanted: a sort pressed
    // during a job, a list stopped before it arrived) or the missing pictures (_avatarPreviewsMissing) once the app is idle.
    private bool _avatarListLoading, _avatarListWanted, _avatarPreviewsMissing;
    private volatile bool _stopAvatarList, _avatarListStopped, _avatarListPosted;
    // A download or like pressed while the list's pictures loaded: it runs when the list has stopped.
    private Action? _avatarQueued;
    // "Download and set": the pack just downloaded. The avatar scan that follows sets it as the profile picture; null otherwise.
    private byte[]? _avatarSetAfterScan;

    protected override void OnLoad()
    {
        try { SystemModule.Load(SystemModuleId.PngDec); _pngAvailable = true; }
        catch { _pngAvailable = false; }
        // The store's card pictures are JPEGs.
        try { SystemModule.Load(SystemModuleId.JpegDec); _jpegAvailable = true; }
        catch { _jpegAvailable = false; }
        _renderer = new ThemeRenderer(name => FileSystem.ReadAllBytes(DeviceThemeLibrary.AssetRoot + "/" + name), DecodeCover);
        // The queue's own set of waiting ids, which the cards' "Queued" badges read (it is never replaced).
        _renderer.CommunityQueued = _downloadQueue.PendingIds;
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
        // A named firmware without a bundled catalog is asked once, the first time System opens, for a firmware report.
        try
        {
            string? name = StockCatalog.FirmwareName(_firmware);
            _reportAsk = _settings.ReportToAsk(name, StockCatalog.PathFor(_firmware) is { } bundled && FileSystem.Exists(bundled));
        }
        catch { /* not asked */ }
        if (_reportAsk is not null)
        {
            _renderer.ReportFirmware = _reportAsk;
            _browser.OfferReport();
        }
        try { _renderer.AppVersion = JsonValue.Parse(FileSystem.ReadAllText("/app0/sce_sys/param.json")).GetString("contentVersion", ""); }
        catch { /* the System screen then shows no app version */ }
        _browser.StoreUrl = _settings.StoreUrl;
        try { _userId = SharpProspero.Platform.Users.InitialUserId; }
        catch { _userId = null; /* the avatar grid then says the picture can't be changed */ }
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
        try
        {
            if (AppData.Files.Exists(AvatarStore.LikesPath))
                foreach (string id in CommunityList.ParseLikedIds(AppData.Files.ReadAllText(AvatarStore.LikesPath))) _avatarLiked.Add(id);
        }
        catch { /* no hearts for earlier avatar likes until they are pressed again */ }
        _renderer.AvatarStoreLiked = _avatarLiked;
        _avatarScanAfterWork = true;
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
        int? user = _userId;
        _worker = new Thread(() =>
        {
            try
            {
                var snapshot = work();
                string? applied = ReadApplied();
                ThemePart restorable = ReadRestorable();
                List<LookLayer>? look = ReadLook();
                AvatarApplier.State? avatar = user is { } id ? AvatarApplier.Current(id, AppData.Files) : null;
                SystemUpdate.Plan? update = checkUpdate ? ReadUpdatePlan(snapshot.Entries, firmware, skipped, sounds) : null;
                _results.Post(() =>
                {
                    _restorable = restorable;
                    ShowAppliedAvatar(avatar);
                    _renderer.ResetCovers();
                    _added = snapshot.Added ?? _added;
                    OrderLibrary(snapshot.Entries, applied, scanned: true);
                    ShowLook(look);
                    if (_focusAfterScan is { } focus)
                    {
                        _focusAfterScan = null;
                        SelectInLibrary(e => e.Manifest.Id == focus);
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
                    _renderer.Status = ThemeRenderer.Plural(_library.Count, "theme");
                    _renderer.Busy = false;
                    _busy = false;
                    if (_applyAfterScan is { } downloaded)
                    {
                        _applyAfterScan = null;
                        OfferApply(downloaded);
                    }
                });
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                _results.Post(() =>
                {
                    _applyAfterScan = null;
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
        _browser.LookSavable = LookPack.Savable(look, _library.Select(e => e.Path));
    }

    /// <summary>
    /// Saves the look as a pack of the library (<see cref="LookPack"/>) and focuses it. The same look saved before is
    /// found by its id and not stored twice.
    /// </summary>
    private void SaveLook(List<LookLayer> look)
    {
        string name = LookPack.NextName(_library.Select(e => e.Manifest.Name));
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ThemeLibraryEntry entry in _library) saved.TryAdd(entry.Manifest.Id, entry.Manifest.Name);
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

    /// <summary>Keeps that the report was asked for this firmware, so it does not come back.</summary>
    private void RememberReportAsked()
    {
        if (_reportAsk is not { } firmware) return;
        _reportAsk = null;
        _settings = _settings with { ReportAsked = firmware };
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
    /// Puts <paramref name="entries"/> (the whole library) in the library's order (the applied theme first, then <see cref="AppSettings.LibrarySort"/>)
    /// and shows the packs that match the Library's search. The focus stays on the pack it was on (the first shown one when that is
    /// not shown any more); a scan (<paramref name="scanned"/>) also passes the count on.
    /// </summary>
    private void OrderLibrary(IReadOnlyList<ThemeLibraryEntry> entries, string? applied, bool scanned = false)
    {
        string? focused = _browser.Selected >= 0 && _browser.Selected < _entries.Count ? _entries[_browser.Selected].Path : null;
        _library = LibraryOrder.Sort(entries, applied, _settings.LibrarySort, _added);
        ShowLibrary(focused, announceCount: scanned);
    }

    /// <summary>
    /// Derives the shown packs (<see cref="_entries"/>) from the library and the browser's search term, and puts the focus on the pack
    /// at <paramref name="focused"/> (a path) when it is still shown (else on the first one with <paramref name="refocus"/>). The browser gets the counts when they may
    /// have changed (<paramref name="announceCount"/>).
    /// </summary>
    private void ShowLibrary(string? focused, bool announceCount, bool refocus = false)
    {
        _entries = LibraryFilter.Apply(_library, _browser.LibraryQuery);
        if (announceCount) _browser.SetCount(_entries.Count, _library.Count);
        int index = focused is null ? -1 : _entries.ToList().FindIndex(e => e.Path == focused);
        if (index >= 0) _browser.Select(index);
        else if (refocus && _entries.Count > 0) _browser.Select(0);   // the focused pack does not match: the first one that does
        _renderer.LibraryReordered();
    }

    /// <summary>The Library's search term changed: show the packs that match it, keeping the focus on the pack that was focused when it still matches.</summary>
    private void FilterLibrary()
    {
        string? focused = _browser.Selected >= 0 && _browser.Selected < _entries.Count ? _entries[_browser.Selected].Path : null;
        ShowLibrary(focused, announceCount: true, refocus: true);
    }

    /// <summary>
    /// Puts the focus on the library pack that <paramref name="match"/> names. When the Library's search hides it, the search is taken
    /// off first (a pack the user just downloaded or saved must not vanish behind a term). Returns whether such a pack exists.
    /// </summary>
    private bool SelectInLibrary(Func<ThemeLibraryEntry, bool> match)
    {
        ThemeLibraryEntry? wanted = _library.FirstOrDefault(match);
        if (wanted is null) return false;
        if (!_entries.Contains(wanted))
        {
            _browser.ClearLibraryQuery();
            ShowLibrary(wanted.Path, announceCount: true);
        }
        int index = _entries.ToList().FindIndex(e => e.Path == wanted.Path);
        if (index >= 0) _browser.Select(index);
        return index >= 0;
    }

    /// <summary>Triangle in the library: name or newest first. The applied theme stays in front either way.</summary>
    private void ToggleLibrarySort()
    {
        LibrarySort sort = _settings.LibrarySort == LibrarySort.Name ? LibrarySort.Newest : LibrarySort.Name;
        _settings = _settings with { LibrarySort = sort };
        _renderer.LibrarySort = sort;
        OrderLibrary(_library, _renderer.AppliedId);
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
        // /app0 is a live view of the install folder: after an update a helper would start from the new ELF, which this
        // (old) program does not expect.
        if (_installedVersion is not null && needsHelper)
        {
            _renderer.Notify("Prospero Themes was updated. Close the app and start it again first.", NoticeLevel.Info);
            return;
        }
        _busy = true;
        _renderer.Busy = true;
        _renderer.Status = busyText;
        _renderer.Notify(busyText, NoticeLevel.Info);
        _worker = new Thread(() =>
        {
            string message, status;
            bool ok = false;
            IReadOnlyList<(string Backup, string Device)>? shellBackups = null;
            HenKind hen = HenKind.Unknown;
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
                    {
                        shellBackups = new ThemeApplier(new HelperDeviceFiles(), HelperDeviceFiles.UnixSeconds).ShellBackups(result.Written);
                        hen = ThemeHelperClient.QueryHen();   // under OnionHEN the home screen is not restarted from here
                    }
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
                if (applied != _renderer.AppliedId) OrderLibrary(_library, applied);
                _renderer.AppliedId = applied;
                _renderer.Notify(message, ok ? NoticeLevel.Success : NoticeLevel.Error, holdResult);
                _renderer.Busy = false;
                _busy = false;
                // The helper may run now: packs that could not be checked before are asked again.
                _fitAsked.Clear();
                if (shellBackups is not null)
                {
                    // No backups are kept under OnionHEN, so nothing can start the restart there.
                    _shellBackups = RunningHen.AllowsShellRestart(hen) ? shellBackups : null;
                    _avatarRestart = _renderer.ShellRestartForAvatar = false;
                    _browser.AskRestartShell(hen);
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
        if (_avatarScanAfterWork && !_busy)
        {
            _avatarScanAfterWork = false;
            StartAvatarScan();
        }
        // The queued store downloads go before everything below: the list's pages, pictures and the avatar list wait for them.
        if (!_busy) StartQueuedDownload();
        // The Avatars tab's list or pictures, asked for while a job ran or stopped for one.
        if (!_busy && _avatarListWanted) StartAvatarStoreList();
        else if (!_busy && _avatarPreviewsMissing) StartAvatarPreviews();
        // The cursor is near the end of the Themes tab's list: its next page, when the store has one.
        if (!_busy && _communityMore && _browser.CommunityNearEnd) StartCommunityMore();
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
            parts: SelectedParts(), sounds: _settings.Sounds, restorable: _restorable,
            communityUpdate: SelectedCommunity() is { } card && _renderer.CommunityUpdates.Contains(card.Id),
            appUpdate: _renderer.UpdateState);
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
            case ThemeAction.ApplySoundVolume when _look is { } volumeLook && SystemUpdate.FromLook(volumeLook, _library) is { } volumePlan:
                StartHelperWork("Applying your look again…", needsHelper: true, _ => ReapplyLook(volumePlan, "Applying your look again…"));
                break;
            case ThemeAction.ToggleLibrarySort: ToggleLibrarySort(); break;
            case ThemeAction.LibraryFilter: FilterLibrary(); break;
            case ThemeAction.PreviewMusic: _preview.ToggleMusic(); break;
            case ThemeAction.ImportUsb:
                _avatarScanAfterWork = true;   // the import also takes avatars
                StockCatalog stock = _stock;   // the import also takes this firmware's originals
                StartWork(() => DeviceThemeLibrary.ImportUsb(stock), announce: true);
                break;
            case ThemeAction.Reload:
                _avatarScanAfterWork = true;
                StartWork(DeviceThemeLibrary.Scan);
                break;
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
                bool avatarRestart = _avatarRestart;
                // The home screen is in front meanwhile, so the result waits for the user to come back and press a button.
                StartHelperWork("Restarting the home screen…", needsHelper: true, _ => RestartShell(backups, avatarRestart), offerRestart: false, holdResult: true);
                break;
            case ThemeAction.AvatarApply when SelectedAvatar() is { } avatar:
                string avatarPath = avatar.Path;
                StartAvatarJob("Setting your profile picture…",
                    user => AvatarApplier.Apply(AvatarPack.Open(AppData.Files.ReadAllBytes(avatarPath)), user, new HelperAvatarFiles(), AppData.Files));
                break;
            case ThemeAction.AvatarReset:
                StartAvatarJob("Putting back the console picture…", user => AvatarApplier.Reset(user, new HelperAvatarFiles(), AppData.Files));
                break;
            case ThemeAction.AvatarRemove when SelectedAvatar() is { } removedAvatar:
                StartAvatarScan(() =>
                {
                    AvatarLibrary.Remove(AppData.Files, removedAvatar.Path);
                    return "Removed “" + removedAvatar.Tile.Name + "” from your avatars.";
                });
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
            case ThemeAction.ReportLater: RememberReportAsked(); break;
            case ThemeAction.FirmwareReport:
                RememberReportAsked();   // a report sent or saved by hand answers the question too
                // "Save and send" also gives it to the store; without a store address it is only saved.
                string? reportStore = _browser.SendReport ? _settings.StoreUrl : null;
                StartHelperWork(reportStore is null ? "Saving firmware report…" : "Sending firmware report…", needsHelper: false,
                    problem => SaveFirmwareReport(problem, reportStore));
                break;
            case ThemeAction.CommunityRefresh: StartCommunityList(); break;
            case ThemeAction.ToggleAiThemes: ToggleAiThemes(); break;
            case ThemeAction.CheckAppUpdate: AppUpdateInfo(); break;
            case ThemeAction.UpdateApp when _latest is { Zip: not null } release: StartAppUpdate(release); break;
            case ThemeAction.CommunityDownload when SelectedCommunity() is { } theme: StartCommunityDownload(theme); break;
            case ThemeAction.CommunityDownloadApply when SelectedCommunity() is { } theme: StartCommunityDownload(theme, thenApply: true); break;
            case ThemeAction.CommunityPreview when SelectedCommunity() is { } theme: StartCommunityPreview(theme); break;
            case ThemeAction.CommunityLike when SelectedCommunity() is { } theme: StartCommunityLike(theme); break;
            case ThemeAction.AvatarStoreRefresh: StartAvatarStoreList(); break;
            case ThemeAction.AvatarDownload when SelectedStoreAvatar() is { } storeAvatar: StartAvatarDownload(storeAvatar); break;
            case ThemeAction.AvatarDownloadSet when SelectedStoreAvatar() is { } storeAvatar: StartAvatarDownload(storeAvatar, thenSet: true); break;
            case ThemeAction.AvatarLike when SelectedStoreAvatar() is { } storeAvatar: StartAvatarLike(storeAvatar); break;
        }
        _preview.Update(_entries);
        _renderer.SelectedCommunityId = SelectedCommunity()?.Id;
        _renderer.CommunityDownloading = _downloadQueue.ActiveId;
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
    private ApplyResult RestartShell(IReadOnlyList<(string Backup, string Device)> backups, bool avatar = false)
    {
        var (restarts, restored) = ThemeHelperClient.RestartShell(backups);
        // A profile picture has no backups for the helper to put back (avatar), so only the restart itself can go wrong.
        if (avatar && restarts is > 0 and < ThemeHelperClient.ShellLoopRestarts)
            return new ApplyResult(true, "The home screen restarted with the profile picture.", []);
        if (avatar && restarts == 0)
            return new ApplyResult(false, "The home screen did not restart. The profile picture changes after the next restart of the console.", []);
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
        var looksThemed = new List<string>();
        byte[] report = FirmwareReport.Build(files, _firmware, version,
            ThemeApplier.FormatUtc(HelperDeviceFiles.UnixSeconds()), paths, after: () =>
            [
                ("helper", helperProblem ?? "running"),
                ("app_reads", files.AppReads.ToString()),
                ("helper_reads", files.HelperReads.ToString()),
            ], looksThemed: looksThemed);
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
        // The backups were made from another tool's theme: such a report shows no originals.
        if (looksThemed.Count > 0)
            helper += $" {looksThemed.Count} file(s) here look like a theme's, not the originals: a report from a console that never had a theme helps more.";
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
        _renderer.Notify(hide ? "The Themes tab leaves out themes with AI pictures." : "The Themes tab lists themes with AI pictures too.",
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
        _communityPage = 1;
        _communityTotal = _renderer.CommunityTotal = -1;
        _communityMore = false;
        // The Avatars tab lists the same store.
        _avatarStore = Array.Empty<StoreAvatar>();
        _renderer.AvatarStore = _avatarStore;
        _renderer.ResetAvatarStore();
        _browser.SetAvatarStoreCount(0);
        _renderer.AvatarStoreStatus = "";
        _renderer.AvatarStoreError = false;
        _renderer.AvatarStoreListSort = -1;
        _renderer.AvatarStoreListQuery = "";
        _avatarListWanted = _avatarPreviewsMissing = false;
        RefreshAvatarStoreBadges();
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

    // ---- Avatars ------------------------------------------------------------------------------------

    private AvatarEntry? SelectedAvatar() =>
        _browser.AvatarIndex >= 0 && _browser.AvatarIndex < _avatars.Count ? _avatars[_browser.AvatarIndex] : null;

    /// <summary>
    /// Reads the app's avatars and which one is set for this user, on the worker. <paramref name="change"/> runs first (a removal);
    /// its text, or its failure, is the notice. A plain read asked for while a job runs waits for the job.
    /// </summary>
    private void StartAvatarScan(Func<string>? change = null)
    {
        if (_fitChecking) { _afterFit = () => StartAvatarScan(change); return; }
        if (_busy)
        {
            if (change is null) _avatarScanAfterWork = true;
            else Wait();
            return;
        }
        _busy = true;
        _renderer.Busy = true;
        int? user = _userId;
        _worker = new Thread(() =>
        {
            string? message = null;
            bool failed = false;
            if (change is not null)
            {
                try { message = change(); }
                catch (Exception ex) { (message, failed) = (ex.Message, true); }
            }
            List<AvatarEntry> entries = new();
            string? problem = null;
            try { (entries, problem) = AvatarLibrary.Read(AppData.Files, AppData.Files.ListFiles(AvatarLibrary.Root)); }
            catch (Exception ex) { problem = "Can’t read your avatars: " + ex.Message; }
            AvatarApplier.State? state = user is { } id ? AvatarApplier.Current(id, AppData.Files) : null;
            _results.Post(() =>
            {
                _avatars = entries;
                _renderer.Avatars = entries.ConvertAll(e => e.Tile);
                _renderer.AvatarsLoaded = true;
                _browser.SetAvatarCount(entries.Count);
                ShowAppliedAvatar(state);
                RefreshAvatarStoreBadges();
                if (message is not null) _renderer.Notify(message, failed ? NoticeLevel.Error : NoticeLevel.Success);
                else if (problem is not null) _renderer.Notify(problem, NoticeLevel.Error);
                _renderer.Busy = false;
                _busy = false;
                // "Download and set": the pack is in the avatars now, so the picture is set from the bytes that were downloaded.
                if (_avatarSetAfterScan is { } pack)
                {
                    _avatarSetAfterScan = null;
                    StartAvatarJob("Setting your profile picture…",
                        target => AvatarApplier.Apply(AvatarPack.Open(pack), target, new HelperAvatarFiles(), AppData.Files));
                }
            });
        }) { IsBackground = true, Name = "Avatars" };
        _worker.Start();
    }

    /// <summary>The avatar set for this user, marked on the Library's avatar row; a backup whose apply did not finish counts as none.</summary>
    private void ShowAppliedAvatar(AvatarApplier.State? state)
    {
        bool set = state is { AvatarId.Length: > 0 };
        _renderer.AppliedAvatarId = set ? state!.AvatarId : null;
    }

    /// <summary>
    /// Sets or resets this user's picture through the helper (<see cref="AvatarApplier"/>). The home screen shows the change once it
    /// restarts, so a success offers that, as after a theme's icons (under OnionHEN only "Later").
    /// </summary>
    private void StartAvatarJob(string busyText, Func<int, AvatarResult> work)
    {
        if (_userId is not { } user)
        {
            _renderer.Notify("Can’t tell which user started the app, so the picture can’t be changed.", NoticeLevel.Error);
            return;
        }
        StartHelperWork(busyText, needsHelper: true, _ =>
        {
            AvatarResult result = work(user);
            AvatarApplier.State? state = AvatarApplier.Current(user, AppData.Files);
            HenKind hen = result.Success ? ThemeHelperClient.QueryHen() : HenKind.Unknown;
            _results.Post(() =>
            {
                ShowAppliedAvatar(state);
                if (!result.Success) return;
                _shellBackups = RunningHen.AllowsShellRestart(hen) ? [] : null;
                _avatarRestart = _renderer.ShellRestartForAvatar = true;
                _browser.AskRestartShell(hen);
            });
            return new ApplyResult(result.Success, result.Message, []);
        }, offerRestart: false);
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
    /// the rest of the pictures follow. <paramref name="finished"/> runs on the frame thread when the job is over, with its result,
    /// before the next queued job or download starts.
    /// </summary>
    private bool StartCommunityWork(string busyText, Func<StoreHttpClient, StoreResult> work, bool loadsList = false,
        Action<StoreResult>? finished = null)
    {
        if (_busy)
        {
            if (_listLoading && _queuedJob is null)
            {
                _queuedJob = () => StartCommunityWork(busyText, work, loadsList, finished);
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
                finished?.Invoke(result);
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
                // The queued downloads come before the list and its pictures, which go on when the queue is empty.
                if (StartQueuedDownload()) return;
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
    /// network holds up nothing. A later one is shown on the System screen and, once the app is idle, as a notice; with a
    /// ZIP in the answer the System tab's App update row can install it. Any failure says nothing at start (the next start
    /// asks again); when the user asked (<paramref name="userAsked"/>), the answer is a notice either way.
    /// </summary>
    private void CheckAppVersion(bool userAsked = false)
    {
        string baseUrl = _settings.StoreUrl, current = _renderer.AppVersion;
        if (baseUrl.Length == 0 || current.Length == 0)
        {
            if (userAsked) _renderer.Notify("No store address is set, so there is nothing to ask. System, Store address sets it.", NoticeLevel.Info);
            return;
        }
        if (_versionChecking) return;
        _versionChecking = true;
        if (userAsked) _renderer.Notify("Looking for a newer version…", NoticeLevel.Info);
        new Thread(() =>
        {
            AppUpdate.Release? latest = null;
            string? failure = null;
            try
            {
                HttpResponse answer = Http().Get(AppUpdate.LatestUrl(baseUrl));
                if (!answer.IsSuccess) failure = "The store answered with status " + answer.StatusCode + ".";
                else latest = AppUpdate.Parse(System.Text.Encoding.UTF8.GetString(answer.Body));
                if (failure is null && latest is null) failure = "The store did not name a release.";
            }
            catch (Exception ex) { failure = "Can’t reach the store: " + ex.Message; }
            _results.Post(() =>
            {
                _versionChecking = false;
                if (latest is null || !AppUpdate.IsNewer(latest.Version, current))
                {
                    if (userAsked)
                        _renderer.Notify(failure ?? "Prospero Themes is up to date (" + current + ").", failure is null ? NoticeLevel.Success : NoticeLevel.Error);
                    return;
                }
                _latest = latest;
                ShowAppUpdate();
                string text = AppUpdate.Text(latest.Version, current, latest.Zip is not null);
                if (userAsked) _renderer.Notify(text, NoticeLevel.Info);
                else _updateNotice = text;
            });
        }) { IsBackground = true, Name = "AppUpdate" }.Start();
    }

    /// <summary>Puts what is known about a newer release on the System screen: the version, the row's state and the question's text.</summary>
    private void ShowAppUpdate()
    {
        string current = _renderer.AppVersion;
        _renderer.NewerVersion = _latest is { } latest && AppUpdate.IsNewer(latest.Version, current) ? latest.Version : "";
        _renderer.UpdateState = _installedVersion is not null ? AppUpdate.State.Installed : AppUpdate.StateOf(_latest, current, installed: false);
        if (_latest is { Zip: { } zip } release) _renderer.AppUpdateText = AppUpdate.DialogText(release.Version, current, zip.Bytes);
    }

    /// <summary>X on the App update row when there is nothing to install: says what is known, or asks the store again.</summary>
    private void AppUpdateInfo()
    {
        string current = _renderer.AppVersion;
        if (_installedVersion is not null)
            _renderer.Notify("Prospero Themes " + _installedVersion + " is installed. Close the app and start it again.", NoticeLevel.Info);
        else if (_latest is { } latest && AppUpdate.IsNewer(latest.Version, current))
            _renderer.Notify(AppUpdate.Text(latest.Version, current, latest.Zip is not null), NoticeLevel.Info);
        else CheckAppVersion(userAsked: true);
    }

    /// <summary>
    /// "Update now": finds the install folder, downloads the release ZIP the store announced, checks it and writes its files over
    /// the app's own (<see cref="AppInstall"/>). Runs as a job, so nothing else runs meanwhile; the result stays on screen until a
    /// button is pressed.
    /// </summary>
    private void StartAppUpdate(AppUpdate.Release release)
    {
        if (_installedVersion is not null)
        {
            _renderer.Notify("Prospero Themes " + _installedVersion + " is installed. Close the app and start it again.", NoticeLevel.Info);
            return;
        }
        string baseUrl = _settings.StoreUrl, current = _renderer.AppVersion;
        StartHelperWork("Updating the app…", needsHelper: false, _ => RunAppUpdate(release, baseUrl, current), offerRestart: false, holdResult: true);
    }

    private ApplyResult RunAppUpdate(AppUpdate.Release release, string baseUrl, string current)
    {
        if (release.Zip is not { } zip || baseUrl.Length == 0) return new ApplyResult(false, "There is no release ZIP to install.", []);
        try
        {
            // The place first: a disk image or an unknown folder ends the update before anything is downloaded.
            var host = new ConsoleInstallHost();
            InstallTarget target = AppInstall.Locate(host, current);
            _results.Post(() => { if (_busy) _renderer.ShowProgress("Updating the app… Downloading " + AppUpdate.FormatSize(zip.Bytes), 0f); });
            HttpResponse answer = Http().GetWithRetry(AppUpdate.ZipUrl(baseUrl, zip));
            if (!answer.IsSuccess) return new ApplyResult(false, "The download failed (" + answer.StatusCode + "). Nothing was changed.", []);
            _results.Post(() => { if (_busy) _renderer.ShowProgress("Updating the app… Checking the download", 0f); });
            AppInstall.Package package = AppInstall.Open(answer.Body, zip, release.Version);
            AppInstall.InstallResult result = AppInstall.Write(package, target, new WorkProgress(this, "Updating the app…"));
            string version = release.Version;
            // So the home screen's copy of the version refreshes (untested on the console); a failure changes nothing.
            host.RequestScan();
            _results.Post(() =>
            {
                _installedVersion = version;
                ShowAppUpdate();
            });
            return new ApplyResult(true, AppInstall.DoneText(version, result), []);
        }
        catch (AppInstallException ex) { return new ApplyResult(false, ex.Message, []); }
        catch (Exception ex) { return new ApplyResult(false, "The update failed: " + ex.Message + " Nothing was changed that stops the old version from starting.", []); }
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
            CommunityPage first = CommunityList.ParsePage(System.Text.Encoding.UTF8.GetString(answer.Body), baseUrl);
            List<CommunityTheme> list = first.Items;
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
                // Later pages of this list follow as the cursor nears its end (StartCommunityMore).
                _communityPage = 1;
                _communityTotal = _renderer.CommunityTotal = first.Total;
                _communityMore = CommunityList.HasMore(first, 1);
                _communityRetryAt = -1;
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
    /// Loads the next page of the list on screen, with the sort, category, search and AI setting the list was loaded with, and
    /// adds its themes to the end of it: the focus, the scroll and the pictures already here stay. The pages after the first
    /// load as the cursor nears the end of the list (<see cref="ThemeBrowser.CommunityNearEnd"/>). Starts nothing while the pills
    /// or chips show another list than the one on screen (that one is about to load, and a page of the old list would not
    /// belong to it), while a stopped load is waiting to go on, or on a card where the last page failed. A page that arrives after
    /// the list changed or a job asked the loads to stop is dropped; a theme the list holds already (the order can shift
    /// between two requests) is not added again.
    /// </summary>
    private void StartCommunityMore()
    {
        if (_queuedJob is not null || _reloadList || _previewsMissing || _community.Count == 0 || _settings.StoreUrl.Length == 0) return;
        if (_browser.CommunitySort != _listSort || _browser.CommunityCategory != _listCategory || _browser.CommunityQuery != _listQuery
            || _browser.CommunityHideAi != _listHideAi || _browser.CommunitySelected == _communityRetryAt) return;
        string baseUrl = _settings.StoreUrl, query = _listQuery;
        var sort = (CommunitySort)_listSort;
        string? categoryId = CommunityCategories.IdAt(_listCategory);
        bool hideAi = _listHideAi;
        int page = _communityPage + 1, focused = _browser.CommunitySelected;
        IReadOnlyList<CommunityTheme> loaded = _community;
        _stopList = false;
        _listStopped = false;
        _listPosted = true;   // the list is on screen already: a stop leaves only the pictures to catch up on
        // No progress text: the spinner shows that a page is on its way, and a page that arrives needs no toast.
        bool started = StartCommunityWork("", http =>
        {
            StoreResult Failed(string text)
            {
                _results.Post(() => _communityRetryAt = focused);
                return new StoreResult(text, NoticeLevel.Error);
            }
            CommunityPage answer;
            try
            {
                HttpResponse response = http.GetWithRetry(CommunityList.ListUrl(baseUrl, sort, categoryId, page, query: query, hideAi: hideAi));
                if (!response.IsSuccess) return Failed("Can’t load more themes. Store responded with status " + response.StatusCode + ".");
                answer = CommunityList.ParsePage(System.Text.Encoding.UTF8.GetString(response.Body), baseUrl);
            }
            catch (Exception ex) { return Failed("Can’t load more themes: " + ex.Message); }
            if (_stopList)
            {
                _listStopped = true;
                return new StoreResult("", NoticeLevel.Success);
            }
            List<CommunityTheme> fresh = CommunityList.NewThemes(loaded, answer.Items);
            _results.Post(() =>
            {
                // Another sort, chip or search (or a job that needs the loads to stop) came while the page was on its way.
                if (_stopList || !ReferenceEquals(_community, loaded)) return;
                var grown = new List<CommunityTheme>(loaded.Count + fresh.Count);
                grown.AddRange(loaded);
                grown.AddRange(fresh);
                _community = grown;
                _renderer.Community = grown;
                _communityPage = page;
                if (answer.Total >= 0) _communityTotal = _renderer.CommunityTotal = answer.Total;
                _communityMore = CommunityList.HasMore(answer, page);
                _communityRetryAt = -1;
                _renderer.CommunityPicturesLoading = true;
                _browser.SetCommunityCount(grown.Count, keepQuestion: true);
                RefreshCommunityBadges();
            });
            LoadPreviews(http, fresh);
            return new StoreResult("", NoticeLevel.Success);
        });
        if (started) _listLoading = _renderer.CommunityPicturesLoading = true;
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

    /// <summary>
    /// The scan after "Download and apply": selects the library pack named after <paramref name="digest"/> and goes to its
    /// Detail, which asks to apply it as X there does (<see cref="ThemeBrowser.ApplyAfterDownload"/>). Nothing is applied
    /// before that question is confirmed. If the user has left the Community tab meanwhile, the download notice is all there is.
    /// </summary>
    private void OfferApply(string digest)
    {
        if (_browser.View != ThemeView.Community || _browser.Dialog != DialogKind.None) return;
        if (!SelectInLibrary(e => CommunityList.LibraryDigest(e.Path) == digest)) return;
        _browser.ApplyAfterDownload();
    }

    /// <summary>
    /// Fetches a store theme's pack without keeping it: held in memory, checked as a download is (<c>Inspect</c>), and handed
    /// to the preview, which closes over it and lets it go. Nothing is written to the library, <c>community.json</c> or the
    /// console. The store does not count the request as a download.
    /// </summary>
    private void StartCommunityPreview(CommunityTheme theme)
    {
        StartCommunityWork("Loading preview of “" + theme.Name + "”…", http =>
        {
            HttpResponse answer = http.GetWithRetry(CommunityList.PreviewDownloadUrl(theme.DownloadUrl));
            if (!answer.IsSuccess) return new StoreResult("Preview failed (" + answer.StatusCode + ").", NoticeLevel.Error);
            if (answer.Body.Length == 0 || answer.Body.Length > ThemeArchive.MaxArchiveBytes)
                return new StoreResult("Preview failed: the pack is empty or too large (48 MiB at most).", NoticeLevel.Error);
            ThemeLibraryEntry entry;
            try { entry = ThemeLibraryEntry.Inspect("store:" + ProsperoThemes.Shared.ThemePack.Hash(answer.Body), answer.Body); }
            catch (Exception ex) { return new StoreResult("Preview failed: " + ex.Message, NoticeLevel.Error); }
            byte[] pack = answer.Body;
            _results.Post(() =>
            {
                _preview.ShowStorePack(entry, pack);
                // The user may have left the tab while the pack was on its way: then there is no preview to show.
                if (!_browser.OpenCommunityPreview()) _preview.DropStorePack();
            });
            return new StoreResult("", NoticeLevel.Success);
        });
    }

    /// <summary>
    /// X on a store card: downloads the theme into the library, or, while the app is busy (another download, a list load, an
    /// apply, a scan…) or other downloads wait already, puts it at the end of the download queue (<see cref="StartQueuedDownload"/>
    /// runs them one after another). A theme that waits or downloads already is not queued twice. A download pressed while list
    /// pictures load stops the pictures and runs as soon as the one on its way is in; the others follow it. With
    /// <paramref name="thenApply"/> the scan that follows goes on to the apply question for that pack (<see cref="OfferApply"/>);
    /// a download that fails never does.
    /// </summary>
    private void StartCommunityDownload(CommunityTheme theme, bool thenApply = false)
    {
        if (_downloadQueue.Contains(theme.Id))
        {
            _renderer.Notify("“" + theme.Name + "” is already queued", NoticeLevel.Info);
            return;
        }
        // Not busy and nothing waiting: it starts at once, as it always did.
        if (!_busy && _downloadQueue.Count == 0)
        {
            RunCommunityDownload(theme, thenApply);
            return;
        }
        _downloadQueue.Enqueue(theme, thenApply);
        _renderer.Notify("Queued “" + theme.Name + "” · " + _downloadQueue.Count + " waiting", NoticeLevel.Info);
        if (!_busy) return;   // held back by the apply question: OnFrame starts it when that is answered
        // The pictures of a list hold the worker for a long time: they stop after the one on its way, and the job that ends them
        // starts the first download of the queue (a like queued already runs first, then this).
        if (_listLoading && _queuedJob is null)
        {
            _queuedJob = () => StartQueuedDownload();
            _stopList = true;
        }
        else if (_avatarListLoading) _stopAvatarList = true;
    }

    /// <summary>
    /// Starts the oldest queued download when the app is idle; false when nothing started. It runs from the end of every store
    /// job and from OnFrame, before the list's paging and pictures, so the downloads follow each other with nothing in between.
    /// A theme list may be reloaded meanwhile: the queue holds the store's records. While the question to apply a pack is open,
    /// the next download waits for the answer (an apply refused with "Please wait" while a download runs would be the worse).
    /// "Download and apply" asks to apply only when no other download waits behind it: the run's last pack is the one to apply,
    /// and one question for it is simpler than a question for every pack. Without a store address nothing can run, so the queue
    /// is dropped.
    /// </summary>
    private bool StartQueuedDownload()
    {
        if (_busy || _downloadQueue.Count == 0) return false;
        if (_browser.Dialog is DialogKind.ApplyParts or DialogKind.ApplyScope) return false;
        _downloadQueue.TryDequeue(out CommunityTheme theme, out bool thenApply);
        if (RunCommunityDownload(theme, thenApply)) return true;
        _downloadQueue.Finish();
        _downloadQueue.Clear();
        return false;
    }

    /// <summary>Downloads one store theme now (the app is idle). Returns whether the job started.</summary>
    private bool RunCommunityDownload(CommunityTheme theme, bool thenApply)
    {
        // The pack an earlier download of this theme stored; an update replaces it in the library.
        string? previous = _downloads.TryGetValue(theme.Id, out string? recorded) ? recorded : null;
        string waiting = _downloadQueue.Count > 0 ? " · " + _downloadQueue.Count + " waiting" : "";
        bool started = StartCommunityWork("Downloading “" + theme.Name + "”…" + waiting, http =>
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
                if (thenApply && _downloadQueue.Count == 0) _applyAfterScan = digest;
                try { AppData.Files.WriteAllText(CommunityList.DownloadsPath, CommunityList.SerializeDownloads(_downloads)); }
                catch { /* the badge lasts until the app quits */ }
                // The store counted this download; show it without reloading the list.
                var updated = new List<CommunityTheme>(_community);
                int i = updated.FindIndex(t => t.Id == theme.Id);
                if (i >= 0) updated[i] = updated[i] with { Downloads = updated[i].Downloads + 1 };
                _community = updated;
                _renderer.Community = updated;
                // The card says "In library" at once: the scan that makes it official waits until the queue is through.
                _renderer.CommunityInLibrary = new HashSet<string>(_renderer.CommunityInLibrary, StringComparer.Ordinal) { theme.Id };
                _renderer.CommunityUpdates = new HashSet<string>(_renderer.CommunityUpdates.Where(id => id != theme.Id), StringComparer.Ordinal);
                _rescanAfterWork = true;
            });
            if (replaced)
                return new StoreResult("Updated “" + theme.Name + "”" + (theme.Version.Length > 0 ? " to v" + theme.Version : "") +
                    ". If it is applied, apply it again to see the changes.", NoticeLevel.Success);
            return added
                ? new StoreResult("“" + theme.Name + "” is now in your library.", NoticeLevel.Success)
                : new StoreResult("“" + theme.Name + "” is already in your library.", NoticeLevel.Info);
        }, finished: result =>
        {
            _downloadQueue.Finish();
            if (result.Level == NoticeLevel.Error) _downloadFailures++;
            if (_downloadQueue.Count > 0) return;
            // The last of a run: the notice of an earlier failure was replaced by the next "Downloading…", so it is said here.
            int earlier = result.Level == NoticeLevel.Error ? _downloadFailures - 1 : _downloadFailures;
            _downloadFailures = 0;
            if (earlier > 0)
                _renderer.Notify(result.Message + " " + earlier + (earlier == 1 ? " earlier download" : " earlier downloads") + " failed.", NoticeLevel.Error);
        });
        if (started) _downloadQueue.Begin(theme.Id);
        return started;
    }

    /// <summary>Marks the store themes whose pack is in the library (after a scan, a list load or a download).</summary>
    private void RefreshCommunityBadges()
    {
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (ThemeLibraryEntry entry in _library)
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

    // ---- Avatar store (the Avatars tab) -------------------------------------------------------------

    private StoreAvatar? SelectedStoreAvatar() =>
        _browser.AvatarStoreSelected >= 0 && _browser.AvatarStoreSelected < _avatarStore.Count ? _avatarStore[_browser.AvatarStoreSelected] : null;

    /// <summary>
    /// Runs a store job for the Avatars tab on the worker, like <see cref="StartCommunityWork"/> but with the tab's own state: the
    /// theme list's reload and queue logic never runs for it. A job that loads the list (<paramref name="loadsList"/>) also keeps
    /// its text as the tab's empty or error state. While the avatar pictures (or the Themes tab's) load, the job is queued and the
    /// pictures stop after the one on its way. Without a store address nothing is sent. Returns whether the job started now.
    /// </summary>
    private bool StartAvatarStoreWork(string busyText, Func<StoreHttpClient, StoreResult> work, bool loadsList = false)
    {
        if (_busy)
        {
            if (_avatarListLoading && _avatarQueued is null)
            {
                _avatarQueued = () => StartAvatarStoreWork(busyText, work, loadsList);
                _stopAvatarList = true;
                if (busyText.Length > 0) _renderer.Notify(busyText, NoticeLevel.Info);
            }
            else if (_listLoading && _queuedJob is null)
            {
                // The Themes tab's pictures hold the worker: they stop and run this job from their own end.
                _queuedJob = () => StartAvatarStoreWork(busyText, work, loadsList);
                _stopList = true;
                if (busyText.Length > 0) _renderer.Notify(busyText, NoticeLevel.Info);
            }
            else Wait();
            return false;
        }
        if (_settings.StoreUrl.Length == 0)
        {
            _renderer.AvatarStoreStatus = "No store set. On the System tab, choose Store address and enter the address.";
            _renderer.AvatarStoreError = false;
            return false;
        }
        _busy = true;
        _renderer.Busy = true;
        _stopAvatarList = false;
        if (loadsList) _renderer.AvatarStoreStatus = busyText;
        if (loadsList) _renderer.AvatarStoreError = false;
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
                    _renderer.AvatarStoreStatus = result.Level == NoticeLevel.Error || _avatarStore.Count == 0 ? result.Message : "";
                    _renderer.AvatarStoreError = result.Level == NoticeLevel.Error;
                }
                if (result.Message.Length > 0) _renderer.Notify(result.Message, result.Level);
                else if (busyText.Length > 0) _renderer.ClearNotice();
                _renderer.Busy = false;
                _busy = false;
                _avatarListLoading = false;
                _renderer.AvatarStorePicturesLoading = false;
                if (_avatarListStopped)
                {
                    // Stopped after the list arrived: only pictures are missing. Before: the list is loaded again (OnFrame).
                    if (_avatarListPosted) _avatarPreviewsMissing = true;
                    else _avatarListWanted = true;
                    _avatarListStopped = false;
                }
                // A download or like pressed while the pictures loaded goes first.
                if (_avatarQueued is { } next)
                {
                    _avatarQueued = null;
                    next();
                    return;
                }
                // A theme download queued on the Themes tab goes before that tab's list goes on.
                if (StartQueuedDownload()) return;
                // The Themes tab's list may have been stopped for this job; it goes on now.
                if (_reloadList) StartCommunityList();
                else if (_previewsMissing) StartCommunityPreviews();
            });
        }) { IsBackground = true, Name = "AvatarStore" };
        _worker.Start();
        return true;
    }

    /// <summary>
    /// Loads the store's avatars in the sort the Avatars tab shows: the list, then each card's preview one by one (a card shows its
    /// picture as soon as it is here). A sort pressed while a load runs stops it after the picture on its way and loads again; one
    /// pressed during another job loads when the app is idle again (OnFrame).
    /// </summary>
    private void StartAvatarStoreList()
    {
        string baseUrl = _settings.StoreUrl;
        var sort = (CommunitySort)_browser.AvatarStoreSort;
        string query = _browser.AvatarStoreQuery;
        string busyText = (query.Length > 0 ? $"Searching avatars for “{query}”" : "Loading avatars") + $" · {CommunityList.Label(sort)}…";
        if (_busy)
        {
            if (_avatarListLoading)
            {
                // Back on the sort and term that load: it simply goes on (unless a queued download or like waits for it to stop).
                bool moved = (int)sort != _avatarJobSort || query != _avatarJobQuery;
                _stopAvatarList = moved || _avatarQueued is not null;
                _avatarListWanted = moved;
                if (moved)
                {
                    _renderer.Notify(busyText, NoticeLevel.Info);
                    if (_avatarStore.Count == 0) _renderer.AvatarStoreStatus = busyText;
                }
            }
            else
            {
                _avatarListWanted = true;
                if (_avatarStore.Count == 0) _renderer.AvatarStoreStatus = busyText;
            }
            return;
        }
        _avatarListWanted = false;
        _avatarPreviewsMissing = false;
        _avatarListStopped = false;
        _avatarListPosted = false;
        bool started = StartAvatarStoreWork(busyText, http =>
        {
            HttpResponse answer = http.GetWithRetry(AvatarStore.ListUrl(baseUrl, sort, pageSize: 36, query: query));
            if (!answer.IsSuccess) return new StoreResult("Store responded with status " + answer.StatusCode + ".", NoticeLevel.Error);
            List<StoreAvatar> list = AvatarStore.Parse(System.Text.Encoding.UTF8.GetString(answer.Body), baseUrl);
            // Asked to stop while the list was on its way: the old list stays on screen until the new one is here.
            if (_stopAvatarList)
            {
                _avatarListStopped = true;
                return new StoreResult("", NoticeLevel.Success);
            }
            _avatarListPosted = true;
            // An avatar the store renamed since it was downloaded gets the new name in the library too.
            bool renamed = false;
            try { renamed = AvatarLibrary.RememberNames(AppData.Files, list, AppData.Files.ListFiles(AvatarLibrary.Root)); }
            catch { /* the names stay as they were */ }
            _results.Post(() =>
            {
                if (renamed) _avatarScanAfterWork = true;
                _renderer.ResetAvatarStore();
                _avatarStore = list;
                _renderer.AvatarStore = list;
                // The header names the sort and the search this list came with, not the ones the pill and the chip show now.
                _renderer.AvatarStoreListSort = (int)sort;
                _renderer.AvatarStoreListQuery = query;
                // The cards that wait for their picture show a placeholder until the pictures are in (StartAvatarStoreWork ends it).
                _renderer.AvatarStorePicturesLoading = true;
                _browser.SetAvatarStoreCount(list.Count);
                RefreshAvatarStoreBadges();
            });
            if (!LoadAvatarPreviews(http, list)) return new StoreResult("", NoticeLevel.Success);
            // A loaded list needs no toast: the screen shows the cards.
            return list.Count == 0
                ? new StoreResult(query.Length > 0 ? $"No avatars match “{query}”." : "No avatars in the store yet.", NoticeLevel.Info)
                : new StoreResult("", NoticeLevel.Success);
        }, loadsList: true);
        if (started)
        {
            _avatarJobSort = (int)sort;
            _avatarJobQuery = query;
            _avatarListLoading = true;
        }
    }

    /// <summary>
    /// Fetches the preview pictures of <paramref name="avatars"/> one by one on the worker. Returns false when it was asked to stop
    /// (and stopped) before the last one.
    /// </summary>
    private bool LoadAvatarPreviews(StoreHttpClient http, List<StoreAvatar> avatars)
    {
        foreach (StoreAvatar avatar in avatars)
        {
            if (_stopAvatarList)
            {
                _avatarListStopped = true;
                return false;
            }
            if (avatar.PreviewUrl is null) continue;
            try
            {
                HttpResponse picture = http.Get(avatar.PreviewUrl);
                if (!picture.IsSuccess || picture.Body.Length == 0) continue;
                string id = avatar.Id;
                byte[] body = picture.Body;
                _results.Post(() => _renderer.AvatarStorePreviews[id] = body);
            }
            catch { /* a missing preview leaves the placeholder */ }
        }
        return true;
    }

    /// <summary>
    /// Fetches the previews the cards on screen still lack, after a download or a like stopped the list's pictures. It shows no
    /// notice and stops for the next sort, download or like just like a list load.
    /// </summary>
    private void StartAvatarPreviews()
    {
        _avatarPreviewsMissing = false;
        var missing = new List<StoreAvatar>();
        foreach (StoreAvatar avatar in _avatarStore)
            if (avatar.PreviewUrl is not null && !_renderer.AvatarStorePreviews.ContainsKey(avatar.Id)) missing.Add(avatar);
        if (missing.Count == 0) return;
        _avatarListStopped = false;
        _avatarListPosted = true;
        bool started = StartAvatarStoreWork("", http =>
        {
            LoadAvatarPreviews(http, missing);
            return new StoreResult("", NoticeLevel.Success);
        });
        if (started) _avatarListLoading = _renderer.AvatarStorePicturesLoading = true;
    }

    /// <summary>Marks the store avatars whose pack is among the app's avatars (after a scan, a list load or a download).</summary>
    private void RefreshAvatarStoreBadges() =>
        _renderer.AvatarStoreInLibrary = AvatarStore.InLibrary(_avatarStore, _avatars.Select(a => a.Path));

    /// <summary>Replaces one avatar of the list on screen (the records are immutable); the renderer gets the new list.</summary>
    private void ChangeStoreAvatar(string id, Func<StoreAvatar, StoreAvatar> change)
    {
        var updated = new List<StoreAvatar>(_avatarStore);
        int i = updated.FindIndex(a => a.Id == id);
        if (i < 0) return;
        updated[i] = change(updated[i]);
        _avatarStore = updated;
        _renderer.AvatarStore = updated;
    }

    /// <summary>
    /// Downloads a store avatar into the app's avatars (<see cref="AvatarLibrary.Add"/> checks it as an import does). The avatar scan
    /// that follows updates the Library's row and the badges. With <paramref name="thenSet"/> that scan goes on to set it as the
    /// profile picture (the user answered "Download and set"); a download that fails never sets anything.
    /// </summary>
    private void StartAvatarDownload(StoreAvatar avatar, bool thenSet = false)
    {
        StartAvatarStoreWork("Downloading “" + avatar.Name + "”…", http =>
        {
            HttpResponse answer = http.GetWithRetry(avatar.DownloadUrl);
            if (!answer.IsSuccess) return new StoreResult("Download failed (" + answer.StatusCode + ").", NoticeLevel.Error);
            byte[] pack = answer.Body;
            if (pack.Length == 0 || pack.Length > AvatarPack.ArchiveCap)
                return new StoreResult("Download failed: the avatar is empty or too large (1 MiB at most).", NoticeLevel.Error);
            bool added;
            try { added = AvatarLibrary.Add(AppData.Files, pack); }
            catch (Exception ex) { return new StoreResult("Download failed: " + ex.Message, NoticeLevel.Error); }
            // The library shows the store's name, which can differ from the one in the pack (the store fixes bad names).
            string digest = ThemePack.Hash(pack);
            try { AvatarLibrary.RememberNames(AppData.Files, [avatar with { Sha256 = digest }], [digest + AvatarPack.Extension]); }
            catch { /* the pack's own name then */ }
            _results.Post(() =>
            {
                // The store counted this download; show it without reloading the list.
                ChangeStoreAvatar(avatar.Id, a => a with { Downloads = a.Downloads + 1 });
                if (thenSet) _avatarSetAfterScan = pack;
                _avatarScanAfterWork = true;
            });
            return added
                ? new StoreResult("“" + avatar.Name + "” is now in Your avatars.", NoticeLevel.Success)
                : new StoreResult("“" + avatar.Name + "” is already in Your avatars.", NoticeLevel.Info);
        });
    }

    /// <summary>
    /// Triangle on a store avatar: likes it, or takes the like back when this console already liked it. The answer carries the new
    /// count and whether it is liked now; the ids this console liked are kept in avatar-likes.json so the hearts survive a restart.
    /// </summary>
    private void StartAvatarLike(StoreAvatar avatar)
    {
        string url = AvatarStore.LikeUrl(_settings.StoreUrl, avatar.Id), device = _settings.DeviceId;
        bool unlike = _avatarLiked.Contains(avatar.Id);
        StartAvatarStoreWork(unlike ? "Removing like…" : "Sending like…", http =>
        {
            HttpResponse answer = http.Send(CommunityList.LikeMethod(unlike), url, default, ["X-Device-Id: " + device]);
            string body = answer.IsSuccess ? System.Text.Encoding.UTF8.GetString(answer.Body) : "";
            int likes = answer.IsSuccess ? CommunityList.ParseLikes(body) : -1;
            if (likes < 0) return new StoreResult((unlike ? "Like not removed (" : "Like not saved (") + answer.StatusCode + ").", NoticeLevel.Error);
            // The store says what it did; without an answer for it, the request decides.
            bool liked = CommunityList.ParseLiked(body) ?? !unlike;
            _results.Post(() =>
            {
                ChangeStoreAvatar(avatar.Id, a => a with { Likes = likes });
                if (liked) _avatarLiked.Add(avatar.Id); else _avatarLiked.Remove(avatar.Id);
                try
                {
                    AppData.Files.CreateDirectoryRecursive(AppData.Root);
                    AppData.Files.WriteAllText(AvatarStore.LikesPath, CommunityList.FormatLikedIds(_avatarLiked));
                }
                catch { /* the heart lasts until the app quits */ }
            });
            return new StoreResult(liked ? "Liked “" + avatar.Name + "”." : "Like removed from “" + avatar.Name + "”.", NoticeLevel.Success);
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
