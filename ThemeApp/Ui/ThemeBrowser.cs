using ProsperoThemes.App.Library;
using ProsperoThemes.Shared;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// StoreAddress is the keyboard screen: opened from System, not a header tab, and it returns there. Search is the same
/// keyboard for the Community search: opened from the search chip, and it returns to Community. Community is the Themes tab;
/// Avatars is the store's avatars (the tab after it). The app's own avatars are the Library's second row
/// (<see cref="ThemeBrowser.AvatarRowFocused"/>), not a view of their own.
/// </summary>
public enum ThemeView { Library, Detail, Preview, Community, Import, System, StoreAddress, Search, Avatars }
/// <summary>
/// SaveStoreUrl: Options in the store-address editor; the app reads <see cref="ThemeBrowser.StoreEditor"/>'s text.
/// PreviewMusic: Square in the preview, which starts or stops the theme's music excerpt (the app owns the sound).
/// CommunityDownload and CommunityDownloadApply: the answers of the download question over a store card (the second goes
/// straight into the apply question once the pack is in the library, see <see cref="ThemeBrowser.ApplyAfterDownload"/>);
/// CommunityPreview: Square on a store card, which fetches the pack for a preview without adding it to the library (the app
/// then calls <see cref="ThemeBrowser.OpenCommunityPreview"/>).
/// ToggleLibrarySort: Triangle in the library, which switches the order of the row (name or newest).
/// ReportLater: "Not now" (or O) on the ReportAsk question, which the app remembers so it is not asked again for this firmware.
/// UpdateApp: "Update now" in the app update question (the app downloads the release ZIP and replaces its own files);
/// CheckAppUpdate: X on the System tab's App update row when there is nothing to install (the app asks the store again, or says
/// what it knows).
/// ToggleBackgroundScope: the System row for app backgrounds. ToggleSounds: the System row for music and sounds. ToggleAiThemes: the System row (or the Community chip's setting) for AI themes. RemoveTheme: the selected pack leaves the library. Busy: a press that needed the app to be idle (the app
/// answers it with a "Please wait" notice); no system-changing action is ever emitted while the app is busy.
/// AvatarStoreRefresh: the Avatars tab asks for the store's list (entered with none, a new sort, or Options). AvatarDownload and
/// AvatarDownloadSet: the answers of the download question over a store avatar (the second also sets it as the profile picture once
/// it is in the app). AvatarLike: Triangle on a store avatar, which likes it or takes the like back.
/// AvatarApply, AvatarReset and AvatarRemove: the answers of the avatar questions, for the card at <see cref="ThemeBrowser.AvatarIndex"/>
/// of the Library's avatar row (AvatarReset is the Console picture card).
/// </summary>
public enum ThemeAction { None, ImportUsb, Reload, Apply, RestoreOriginals, CommunityRefresh, CommunityDownload, CommunityDownloadApply, CommunityPreview, CommunityLike, SaveStoreUrl, FirmwareReport, ToggleBackgroundScope, ToggleEarlierThemes, ToggleSounds, RemoveTheme, PreviewMusic, ToggleLibrarySort, Reapply, ReapplyLater, SaveLook, RestartShell, ChangeSoundVolume, ChangeMusicVolume, AskSoundVolume, ApplySoundVolume, ToggleAiThemes, ReportLater, UpdateApp, CheckAppUpdate, AvatarApply, AvatarReset, AvatarRemove, AvatarStoreRefresh, AvatarDownload, AvatarDownloadSet, AvatarLike, Busy }

/// <summary>The two scenes of the preview: the home screen with its row of tiles, and the control centre's row of buttons.</summary>
public enum PreviewScene { Home, ControlCentre }

/// <summary>
/// The modal question over Detail or System. Apply asks to apply; ApplyScope is the same for a pack with an all-apps
/// background and asks where that background goes; Restore asks before the defaults are put back; Remove asks before
/// the selected pack leaves the library; ApplyParts lets the user tick the parts of a theme to apply (its options are
/// <see cref="ThemeBrowser.DialogParts"/>, then Apply and Cancel); RestoreParts is Restore when the console shows more
/// than one part, with the parts to put back ticked the same way; Reapply asks after a system update whether to apply
/// the theme again (the app opens it, <see cref="ThemeBrowser.AskReapply"/>); FirmwareReport says what the report holds
/// and whether to only save it or also send it to the store; RestartShell offers to restart the home screen after a job
/// wrote icons or sounds (the app opens it, <see cref="ThemeBrowser.AskRestartShell"/>); SoundVolume asks before the look
/// is applied again at a new UI sound volume (the app opens it, <see cref="ThemeBrowser.AskSoundVolume"/>); ReportAsk is
/// the one-time request for a firmware report on a firmware the app has no catalog for (<see cref="ThemeBrowser.OfferReport"/>);
/// CommunityDownload is the question over a store card: download it, download it and go on to apply it, or cancel (an
/// update says Update); AppUpdate is the question behind the System tab's App update row: install the release the store
/// announced over the app's own files, or cancel; RestartShellBlocked is RestartShell under OnionHEN, which can crash a home screen
/// that restarts, so it has the option Later only and never emits <see cref="ThemeAction.RestartShell"/>. AvatarApply, AvatarReset and
/// AvatarRemove are the questions of the Library's avatar row: set the focused avatar as the profile picture, put the console's own picture
/// back, or take the focused avatar out of the app's avatars. AvatarDownload is the question over a store avatar on the Avatars tab:
/// download it, download it and set it as the profile picture, or cancel.
/// </summary>
public enum DialogKind { None, Apply, ApplyScope, Restore, Remove, ApplyParts, RestoreParts, Reapply, FirmwareReport, RestartShell, SoundVolume, ReportAsk, CommunityDownload, AppUpdate, RestartShellBlocked, AvatarApply, AvatarReset, AvatarRemove, AvatarDownload }

/// <summary>The focusable rows of the System screen, top to bottom.</summary>
public enum SystemRow { Backgrounds, EarlierThemes, Sounds, SoundVolume, MusicVolume, AiThemes, StoreAddress, FirmwareReport, AppUpdate, Restore }

public sealed class ThemeBrowser
{
    private static readonly string[] ApplyOptions = ["Apply theme", "Cancel"];
    private static readonly string[] ScopeOptions = ["Apps without their own image", "All apps", "Cancel"];
    private static readonly string[] RestoreOptions = ["Restore defaults", "Cancel"];
    private static readonly string[] RemoveOptions = ["Remove from library", "Cancel"];
    private static readonly string[] ReapplyOptions = ["Apply again", "Not now"];
    private static readonly string[] ReportOptions = ["Save", "Save and send", "Cancel"];
    private static readonly string[] RestartShellOptions = ["Show now", "Later"];
    private static readonly string[] RestartShellBlockedOptions = ["Later"];
    private static readonly string[] SoundVolumeOptions = ["Apply again", "Cancel"];
    private static readonly string[] ReportAskOptions = ["Send report", "Not now"];
    private static readonly string[] DownloadOptions = ["Download", "Download and apply", "Cancel"];
    private static readonly string[] UpdateOptions = ["Update", "Update and apply", "Cancel"];
    private static readonly string[] AppUpdateOptions = ["Update now", "Cancel"];
    private static readonly string[] AvatarApplyOptions = ["Set as profile picture", "Cancel"];
    private static readonly string[] AvatarResetOptions = ["Use console picture", "Cancel"];
    private static readonly string[] AvatarRemoveOptions = ["Remove avatar", "Cancel"];
    private static readonly string[] AvatarDownloadOptions = ["Download", "Download and set", "Cancel"];

    /// <summary>Number of focusable rows on the System screen.</summary>
    public const int SystemRowCount = 10;

    /// <summary>
    /// The step Left (-1) or Right (+1) on the Sound volume or Music volume row asked for, with
    /// <see cref="ThemeAction.ChangeSoundVolume"/> or <see cref="ThemeAction.ChangeMusicVolume"/>.
    /// </summary>
    public int SoundVolumeStep { get; private set; }

    public ThemeView View { get; private set; }
    public int Selected { get; private set; } = -1;
    /// <summary>
    /// Whether the current look combines themes that are all in the library, so it can be saved as a theme of its own
    /// (set by the app from look.json). The library row then ends with the card that does that.
    /// </summary>
    public bool LookSavable { get; set; }
    /// <summary>Whether the library's focus is on the "Save current look" card after the packs (<see cref="Selected"/> stays on the last pack).</summary>
    public bool LookCardFocused { get; private set; }
    public bool ShowOverlay { get; private set; } = true;
    /// <summary>The scene of the preview: the home row, or the control centre (Triangle goes between them).</summary>
    public PreviewScene PreviewScene { get; private set; }
    /// <summary>The section of the home row (L1 and R1 switch between Games and Media).</summary>
    public PreviewTab PreviewTab { get; private set; }
    /// <summary>The focused tile of the home row's <see cref="PreviewTab"/>; each section remembers its own.</summary>
    public int PreviewFocus => _previewFocus[(int)PreviewTab];
    /// <summary>The focused button of the control centre's row.</summary>
    public int PreviewButton { get; private set; }
    private readonly int[] _previewFocus = new int[2], _previewTiles = [1, 1];
    private static int PreviewButtons => PreviewModel.ControlSlots.Count;
    private PreviewSound _previewSound;
    /// <summary>The open dialog, or <see cref="DialogKind.None"/>. While one is open it takes every button.</summary>
    public DialogKind Dialog { get; private set; }
    /// <summary>The focused option of the open dialog (an index into <see cref="DialogOptions"/>).</summary>
    public int DialogFocus { get; private set; }
    /// <summary>The options of the open dialog, in order; the last one is always Cancel. Empty without a dialog.</summary>
    public IReadOnlyList<string> DialogOptions => Dialog switch
    {
        DialogKind.Apply => ApplyOptions,
        DialogKind.ApplyScope => ScopeOptions,
        DialogKind.Restore => RestoreOptions,
        DialogKind.Remove => RemoveOptions,
        DialogKind.Reapply => ReapplyOptions,
        DialogKind.FirmwareReport => ReportOptions,
        DialogKind.RestartShell => RestartShellOptions,
        DialogKind.RestartShellBlocked => RestartShellBlockedOptions,
        DialogKind.SoundVolume => SoundVolumeOptions,
        DialogKind.ReportAsk => ReportAskOptions,
        DialogKind.CommunityDownload => CommunityDialogUpdate ? UpdateOptions : DownloadOptions,
        DialogKind.AppUpdate => AppUpdateOptions,
        DialogKind.AvatarApply => AvatarApplyOptions,
        DialogKind.AvatarReset => AvatarResetOptions,
        DialogKind.AvatarRemove => AvatarRemoveOptions,
        DialogKind.AvatarDownload => AvatarDownloadOptions,
        DialogKind.ApplyParts or DialogKind.RestoreParts => _partsOptions,
        _ => Array.Empty<string>(),
    };
    /// <summary>Whether the open (or last) CommunityDownload question is about an update of a theme in the library: its options then say Update.</summary>
    public bool CommunityDialogUpdate { get; private set; }
    /// <summary>Whether the last <see cref="ThemeAction.FirmwareReport"/> also sends the report to the store ("Save and send").</summary>
    public bool SendReport { get; private set; }
    /// <summary>The parts the open ApplyParts or RestoreParts dialog lists, in order: its first options. Empty for every other dialog.</summary>
    public IReadOnlyList<ThemePart> DialogParts =>
        Dialog is DialogKind.ApplyParts or DialogKind.RestoreParts ? _dialogParts : Array.Empty<ThemePart>();
    /// <summary>The parts ticked in the open parts dialog.</summary>
    public ThemePart DialogTicked => Dialog == DialogKind.RestoreParts ? RestoreParts : ApplyParts;
    /// <summary>
    /// The parts the next restore puts back: every part (a full restore) unless the RestoreParts dialog had some of its
    /// parts unticked.
    /// </summary>
    public ThemePart RestoreParts { get; private set; } = ThemePart.All;
    // The parts the console shows, which a restore can offer (see Update).
    private ThemePart _restorable;
    /// <summary>
    /// The parts the next apply writes: what the user ticked in the ApplyParts dialog, or every part when the apply
    /// did not ask (a pack with one part).
    /// </summary>
    public ThemePart ApplyParts { get; private set; } = ThemePart.All;
    private List<ThemePart> _dialogParts = new();
    private string[] _partsOptions = Array.Empty<string>();
    /// <summary>The apply that was just confirmed came from the scope question (see <see cref="ApplyAllApps"/>).</summary>
    public bool ApplyAsksScope { get; private set; }
    /// <summary>The answer of the last apply that asked: true for "All apps", false for apps without their own image.</summary>
    public bool ApplyAllApps { get; private set; }
    /// <summary>The focused row of the System screen (a <see cref="SystemRow"/>).</summary>
    public int SystemSelected { get; private set; }
    /// <summary>Focused card on the Community tab, or -1 when the list is empty.</summary>
    public int CommunitySelected { get; private set; } = -1;
    /// <summary>The store's sort order shown on the Community tab (Top, Downloads, New).</summary>
    public int CommunitySort { get; private set; }
    /// <summary>The category the Community tab filters by: an index into <see cref="CommunityCategories.List"/>, or -1 for all themes.</summary>
    public int CommunityCategory { get; private set; } = -1;
    /// <summary>Whether the filter chip row above the grid has the focus (and not a card).</summary>
    public bool CommunityChipFocused { get; private set; }
    /// <summary>
    /// The chip the cursor is on while <see cref="CommunityChipFocused"/>: <see cref="SearchChip"/>,
    /// <see cref="NoAiChip"/>, then <see cref="AllChip"/> and the categories in order (<see cref="ChipOfCategory"/>).
    /// Moving into the row starts on the chosen category's chip.
    /// </summary>
    public int CommunityChip { get; private set; }
    /// <summary>The chips of the filter row: the search, "No AI", "All" and every category.</summary>
    public static int CommunityChipCount => CommunityCategories.Count + 3;
    /// <summary>The first chip: "Search" opens the keyboard; with a search term it shows the term, and X removes it.</summary>
    public const int SearchChip = 0;
    /// <summary>"No AI" switches <see cref="CommunityHideAi"/>; it goes with any category.</summary>
    public const int NoAiChip = 1;
    public const int AllChip = 2;
    /// <summary>The chip of a category (an index into <see cref="CommunityCategories.List"/>, -1 for "All").</summary>
    public static int ChipOfCategory(int category) => category + 3;
    /// <summary>The category of a chip after "No AI" (-1 for "All").</summary>
    public static int CategoryOfChip(int chip) => chip - 3;
    /// <summary>
    /// Whether the Community tab leaves out themes with AI-generated pictures (the store's <c>ai=hide</c>). The app keeps
    /// it in settings.json and sets it at start (<see cref="SetCommunityHideAi"/>).
    /// </summary>
    public bool CommunityHideAi { get; private set; }
    public void SetCommunityHideAi(bool hide) => CommunityHideAi = hide;
    /// <summary>What the Community tab searches for (the store's <c>q=</c>); empty for no search.</summary>
    public string CommunityQuery { get; private set; } = "";
    /// <summary>The keyboard behind <see cref="ThemeView.Search"/>.</summary>
    public StoreAddressEditor SearchEditor { get; private set; } = StoreAddressEditor.ForSearch();
    /// <summary>The saved store address: shown on System and what the editor starts from. The app keeps it current.</summary>
    public string StoreUrl { get; set; } = "";
    /// <summary>The keyboard behind <see cref="ThemeView.StoreAddress"/>; keeps its text after the screen is left.</summary>
    public StoreAddressEditor StoreEditor { get; private set; } = new();
    /// <summary>
    /// Whether the Library's focus is on its second row, "Your avatars", instead of the themes. Down from the themes (or the look
    /// card) goes there, Up or Circle comes back; the tabs still switch with L1 and R1.
    /// </summary>
    public bool AvatarRowFocused { get; private set; }
    /// <summary>
    /// The focused card of the avatar row: 0 is the Console picture card (the console's own picture), then the avatars in order.
    /// </summary>
    public int AvatarSelected { get; private set; }
    /// <summary>The avatar of the focused card (an index into the app's avatar list), or -1 on the Console picture card.</summary>
    public int AvatarIndex => AvatarSelected - 1;
    private int _avatarCount;
    /// <summary>The focused card of the Avatars tab's grid, or -1 when the store list is empty.</summary>
    public int AvatarStoreSelected { get; private set; } = -1;
    /// <summary>The store's sort order on the Avatars tab (a <see cref="CommunitySort"/> value: Top, Downloads, New).</summary>
    public int AvatarStoreSort { get; private set; }
    /// <summary>Cards in one row of the Avatars tab's grid.</summary>
    public const int AvatarStoreColumns = 6;
    private int _avatarStoreCount;
    private int _communityCount;
    private int _count;
    private ThemeView _previewOrigin = ThemeView.Library;
    // The ApplyParts dialog asks where an app background goes next, when the pack has one and it stays ticked.
    private bool _scopeAfterParts;
    private int _scopeDefault;
    private ThemeAction _action;
    // ApplyAfterDownload asked for the apply question; the next Update opens it.
    private bool _askApply;

    public void SetCount(int count)
    {
        _count = Math.Max(0, count);
        Selected = _count == 0 ? -1 : Math.Clamp(Selected, 0, _count - 1);
        // The preview of a store theme is not of a library pack, so it stays.
        if (_count == 0 && View is ThemeView.Detail or ThemeView.Preview && !PreviewOfStore) View = ThemeView.Library;
        // The library changed under the question, so it may no longer be about the pack that is selected. The question
        // after a system update and the offer to restart the home screen are about the look, not the selection, and the
        // download question is about a store card, so they stay.
        if (Dialog is not (DialogKind.Reapply or DialogKind.RestartShell or DialogKind.RestartShellBlocked or DialogKind.CommunityDownload or DialogKind.AppUpdate
            or DialogKind.AvatarApply or DialogKind.AvatarReset or DialogKind.AvatarRemove or DialogKind.AvatarDownload)) Dialog = DialogKind.None;
    }

    /// <summary>The number of avatars in the Library's avatar row (after the Console picture card); the focus is kept inside it.</summary>
    public void SetAvatarCount(int count)
    {
        _avatarCount = Math.Max(0, count);
        AvatarSelected = Math.Clamp(AvatarSelected, 0, _avatarCount);
        // Another list under the question may put another avatar on the focused tile.
        if (Dialog is DialogKind.AvatarApply or DialogKind.AvatarRemove) Dialog = DialogKind.None;
    }

    /// <summary>The number of avatars in the store list on the Avatars tab; the focus is kept inside it.</summary>
    public void SetAvatarStoreCount(int count)
    {
        _avatarStoreCount = Math.Max(0, count);
        // Another list under the question may put another avatar on the focused card.
        if (Dialog == DialogKind.AvatarDownload) Dialog = DialogKind.None;
        AvatarStoreSelected = _avatarStoreCount == 0 ? -1 : Math.Clamp(AvatarStoreSelected < 0 ? 0 : AvatarStoreSelected, 0, _avatarStoreCount - 1);
    }

    /// <summary>Puts the focus on the pack at <paramref name="index"/> (after the library was put in another order).</summary>
    public void Select(int index)
    {
        if (_count > 0) Selected = Math.Clamp(index, 0, _count - 1);
        LookCardFocused = false;
    }

    /// <summary>
    /// The number of tiles in each section of the preview's home row (from <see cref="PreviewModel"/>); the focus is kept
    /// inside them. The control centre always has the model's twelve buttons.
    /// </summary>
    public void SetPreviewLayout(int games, int media)
    {
        _previewTiles[(int)PreviewTab.Games] = Math.Max(1, games);
        _previewTiles[(int)PreviewTab.Media] = Math.Max(1, media);
        for (int i = 0; i < _previewFocus.Length; i++) _previewFocus[i] = Math.Clamp(_previewFocus[i], 0, _previewTiles[i] - 1);
    }

    /// <summary>The sound the last <see cref="Update"/> asks for (a move, a press, a step back), once; <see cref="PreviewSound.None"/> after that.</summary>
    public PreviewSound TakePreviewSound()
    {
        PreviewSound sound = _previewSound;
        _previewSound = PreviewSound.None;
        return sound;
    }

    public void SetCommunityCount(int count)
    {
        _communityCount = Math.Max(0, count);
        // Another list under the question may put another theme on the selected card.
        if (Dialog == DialogKind.CommunityDownload) Dialog = DialogKind.None;
        CommunitySelected = _communityCount == 0 ? -1 : Math.Clamp(CommunitySelected < 0 ? 0 : CommunitySelected, 0, _communityCount - 1);
    }

    /// <summary>Opens the keyboard with <paramref name="text"/>, optionally with a message about why it came back.</summary>
    public void OpenStoreAddress(string text, string notice = "")
    {
        StoreEditor = new StoreAddressEditor(text) { Notice = notice };
        Dialog = DialogKind.None;
        View = ThemeView.StoreAddress;
    }

    /// <summary>Goes back into the keyboard after a refused save, with the text and focus as they were.</summary>
    public void ResumeStoreAddress(string notice)
    {
        StoreEditor.Notice = notice;
        View = ThemeView.StoreAddress;
    }

    // The tabs in header order; Detail and Preview belong to the library.
    private static readonly ThemeView[] Tabs = [ThemeView.Library, ThemeView.Community, ThemeView.Avatars, ThemeView.Import, ThemeView.System];

    /// <summary>
    /// One frame of buttons. <paramref name="preview"/> is Square, <paramref name="options"/> Options and
    /// <paramref name="triangle"/> Triangle. <paramref name="asksScope"/> says the selected pack has an all-apps
    /// background (its apply asks where that goes), <paramref name="allAppsScope"/> is the saved answer, and
    /// <paramref name="busy"/> that a worker job is running: then nothing that changes the console or the settings
    /// is started (the press becomes <see cref="ThemeAction.Busy"/> instead). <paramref name="removable"/> says the
    /// selected pack can leave the library (the theme that ships with the app cannot). <paramref name="parts"/> are the
    /// parts the selected pack has (<see cref="ThemeParts.In"/>; None when not known), and <paramref name="sounds"/> the
    /// sounds setting: the apply question lists the parts when there are several, or when sounds are off and the pack
    /// has some, and then starts with every part ticked but the sounds when they are off. <paramref name="communityUpdate"/>
    /// says the selected store card is an update of a theme in the library (its download question then says Update).
    /// <paramref name="appUpdate"/> is what the app knows about a newer release of itself: only <see cref="AppUpdate.State.Ready"/>
    /// lets X on the System tab's App update row ask to install it.
    /// </summary>
    public void Update(UiInput input, bool preview = false, bool options = false, bool triangle = false,
        bool asksScope = false, bool allAppsScope = false, bool busy = false, bool removable = false,
        ThemePart parts = ThemePart.None, bool sounds = true, ThemePart restorable = ThemePart.None, bool communityUpdate = false,
        AppUpdate.State appUpdate = AppUpdate.State.None)
    {
        _restorable = restorable;
        // The apply question that follows "Download and apply" opens on the first frame after the app selected the pack, with
        // the parts and the scope of that pack (they are only known now).
        if (_askApply && Dialog == DialogKind.None)
        {
            _askApply = false;
            if (View == ThemeView.Detail && Selected >= 0)
            {
                AskApply(parts, asksScope, allAppsScope, sounds, busy);
                return;
            }
        }
        // One Update is one frame, and an open dialog answers first. The X that opens a dialog (below) returns
        // right after opening it, so the same X cannot also confirm it.
        if (Dialog != DialogKind.None)
        {
            UpdateDialog(input, busy);
            return;
        }
        if (View == ThemeView.Preview)
        {
            UpdatePreview(input, preview, triangle);
            return;
        }
        if (View == ThemeView.StoreAddress)
        {
            // Tabs stay put while typing: only the keyboard buttons count here.
            if (input.Cancel) View = ThemeView.System;
            else if (options) { _action = ThemeAction.SaveStoreUrl; View = ThemeView.System; }
            else
            {
                StoreEditor.Move((input.Right ? 1 : 0) - (input.Left ? 1 : 0), (input.Down ? 1 : 0) - (input.Up ? 1 : 0));
                if (input.Confirm) StoreEditor.Press();
                if (preview) StoreEditor.Backspace();
            }
            return;
        }
        if (View == ThemeView.Search)
        {
            // O goes back without searching; Options searches for what is typed (nothing typed shows every theme).
            if (input.Cancel) View = ThemeView.Community;
            else if (options)
            {
                View = ThemeView.Community;
                string query = SearchEditor.Text.Trim();
                if (query != CommunityQuery)
                {
                    CommunityQuery = query;
                    CommunitySelected = _communityCount == 0 ? -1 : 0;
                    _action = ThemeAction.CommunityRefresh;
                }
            }
            else
            {
                SearchEditor.Move((input.Right ? 1 : 0) - (input.Left ? 1 : 0), (input.Down ? 1 : 0) - (input.Up ? 1 : 0));
                if (input.Confirm) SearchEditor.Press();
                if (preview) SearchEditor.Backspace();
            }
            return;
        }
        if (View == ThemeView.Library && AvatarRowFocused && !(input.PageDown || input.PageUp))
        {
            UpdateAvatarRow(input, options, triangle, busy);
            return;
        }
        if (input.Cancel || options && View == ThemeView.Detail)
        {
            View = ThemeView.Library;
            CommunityChipFocused = false;
            return;
        }
        if (input.PageDown || input.PageUp)
        {
            // The store tab is entered on its cards, whatever had the focus when it was left.
            CommunityChipFocused = false;
            int tab = Math.Max(0, Array.IndexOf(Tabs, View));
            tab = (tab + (input.PageDown ? 1 : Tabs.Length - 1)) % Tabs.Length;
            View = Tabs[tab];
            if (View == ThemeView.Community && _communityCount == 0) _action = ThemeAction.CommunityRefresh;
            if (View == ThemeView.Avatars && _avatarStoreCount == 0) _action = ThemeAction.AvatarStoreRefresh;
            if (View == ThemeView.System && _reportOffer)
            {
                _reportOffer = false;   // once per start; the app remembers the answer
                OpenDialog(DialogKind.ReportAsk, 0);
            }
            return;
        }
        if (View == ThemeView.Community)
        {
            UpdateCommunity(input, preview, options, triangle, communityUpdate);
            return;
        }
        if (View == ThemeView.System)
        {
            UpdateSystem(input, busy, appUpdate);
            return;
        }
        if (View == ThemeView.Avatars)
        {
            UpdateAvatarStore(input, preview, options, triangle);
            return;
        }
        if (!LookSavable) LookCardFocused = false;
        // Down from the themes (or from the empty library) goes to the avatar row below them.
        if (View == ThemeView.Library && input.Down)
        {
            AvatarRowFocused = true;
            return;
        }
        if (preview && Selected >= 0 && (View == ThemeView.Detail || View == ThemeView.Library && !LookCardFocused))
        {
            StartPreview(View);
            return;
        }
        if (View == ThemeView.Library && _count > 0 && LookCardFocused)
        {
            // The card after the last pack: back onto the packs, or save the look.
            if (input.Left) LookCardFocused = false;
            if (input.Home) Select(0);
            if (input.Confirm) _action = busy ? ThemeAction.Busy : ThemeAction.SaveLook;
            else if (triangle) _action = ThemeAction.ToggleLibrarySort;
        }
        else if (View == ThemeView.Library && _count > 0)
        {
            if (input.Right && Selected == _count - 1 && LookSavable) LookCardFocused = true;
            else if (input.Right) Selected = Math.Min(_count - 1, Selected + 1);
            if (input.Left) Selected = Math.Max(0, Selected - 1);
            if (input.Home) Selected = 0;
            if (input.End)
            {
                Selected = _count - 1;
                LookCardFocused = LookSavable;
            }
            if (input.Confirm && !LookCardFocused) View = ThemeView.Detail;
            else if (triangle) _action = ThemeAction.ToggleLibrarySort;
        }
        else if (View == ThemeView.Detail)
        {
            // Writing to the console always goes through a question first.
            if (input.Confirm) AskApply(parts, asksScope, allAppsScope, sounds, busy);
            else if (triangle && removable)
            {
                if (busy) _action = ThemeAction.Busy;
                else OpenDialog(DialogKind.Remove, RemoveOptions.Length - 1);   // starts on Cancel
            }
        }
        else if (View == ThemeView.Import && input.Confirm) _action = ThemeAction.ImportUsb;
        if (options && View == ThemeView.Library) _action = ThemeAction.Reload;
    }

    // Opens the preview from origin (where Circle goes back to), at the start of the home row.
    private void StartPreview(ThemeView origin)
    {
        _previewOrigin = origin;
        ShowOverlay = true;
        PreviewScene = PreviewScene.Home;
        PreviewTab = PreviewTab.Games;
        Array.Clear(_previewFocus);
        PreviewButton = 0;
        _previewSound = PreviewSound.None;
        View = ThemeView.Preview;
    }

    // The question that starts an apply (the Detail screen's X, and "Download and apply" once the pack is in the library):
    // the parts to tick when the pack has several, else where an app background goes, else the plain question. Nothing is
    // written before one of them is confirmed; while the app is busy none opens.
    private void AskApply(ThemePart parts, bool asksScope, bool allAppsScope, bool sounds, bool busy)
    {
        List<ThemePart> list = ThemeParts.List(parts);
        if (busy) _action = ThemeAction.Busy;
        else if (list.Count > 1 || !sounds && (parts & ThemePart.Sounds) != 0)
        {
            _dialogParts = list;
            _partsOptions = [.. list.ConvertAll(ThemeParts.Label), "Apply theme", "Cancel"];
            ApplyParts = sounds ? parts : parts & ~ThemePart.Sounds;
            _scopeAfterParts = asksScope;
            _scopeDefault = allAppsScope ? 1 : 0;
            OpenDialog(DialogKind.ApplyParts, list.Count);   // starts on Apply theme
        }
        else
        {
            ApplyParts = ThemePart.All;
            if (asksScope) OpenDialog(DialogKind.ApplyScope, allAppsScope ? 1 : 0);
            else OpenDialog(DialogKind.Apply, 0);
        }
    }

    /// <summary>
    /// Whether the preview on screen is of a store theme (opened from a card with <see cref="OpenCommunityPreview"/>), not of a
    /// library pack: Circle then goes back to the Community tab, and the library's size does not matter.
    /// </summary>
    public bool PreviewOfStore => View == ThemeView.Preview && _previewOrigin == ThemeView.Community;

    /// <summary>
    /// Opens the preview for the store theme the app has just fetched (see <see cref="ThemeAction.CommunityPreview"/>). Does
    /// nothing, and returns false, when the user has left the Community tab or a question is open by now.
    /// </summary>
    public bool OpenCommunityPreview()
    {
        if (View != ThemeView.Community || Dialog != DialogKind.None) return false;
        StartPreview(ThemeView.Community);
        return true;
    }

    /// <summary>
    /// After "Download and apply": the pack is in the library and selected (<see cref="Select"/>). Goes to its Detail and, on the
    /// next <see cref="Update"/>, asks to apply it exactly as X on the Detail does. Nothing is applied before that question is
    /// confirmed. Does nothing, and returns false, unless the user is still on the Community tab with no question open.
    /// </summary>
    public bool ApplyAfterDownload()
    {
        if (View != ThemeView.Community || Dialog != DialogKind.None || Selected < 0) return false;
        CommunityChipFocused = false;
        View = ThemeView.Detail;
        _askApply = true;
        return true;
    }

    // The preview. The d-pad moves along the tiles of the home row, or along the buttons of the control centre; L1 and R1
    // switch Games and Media; Triangle goes between the two scenes; X hides the overlay (or brings it back); Square asks
    // the app to play or stop the music; Circle goes back (out of the control centre, then out of the preview). Every
    // press that the console would answer with a sound sets <see cref="TakePreviewSound"/>.
    private void UpdatePreview(UiInput input, bool music, bool triangle)
    {
        if (input.Cancel)
        {
            _previewSound = PreviewSound.Back;
            if (PreviewScene == PreviewScene.ControlCentre) PreviewScene = PreviewScene.Home;
            else View = _previewOrigin;
            return;
        }
        if (music) _action = ThemeAction.PreviewMusic;
        if (input.Confirm)
        {
            ShowOverlay = !ShowOverlay;
            _previewSound = PreviewSound.Confirm;
            return;
        }
        // With the overlay hidden the picture is all there is to look at.
        if (!ShowOverlay) return;
        if (triangle)
        {
            PreviewScene = PreviewScene == PreviewScene.Home ? PreviewScene.ControlCentre : PreviewScene.Home;
            _previewSound = PreviewSound.Confirm;
            return;
        }
        int step = (input.Right ? 1 : 0) - (input.Left ? 1 : 0);
        if (PreviewScene == PreviewScene.ControlCentre)
        {
            if (step != 0) PreviewButton = PreviewStep(PreviewButton, PreviewButtons, step);
            return;
        }
        if (input.PageUp || input.PageDown)
        {
            PreviewTab tab = input.PageDown ? PreviewTab.Media : PreviewTab.Games;
            _previewSound = tab == PreviewTab ? PreviewSound.End : PreviewSound.Move;
            PreviewTab = tab;
        }
        else if (step != 0)
        {
            int tab = (int)PreviewTab;
            _previewFocus[tab] = PreviewStep(_previewFocus[tab], _previewTiles[tab], step);
        }
    }

    // One step along a row of count items, with the sound it makes: a move, or the end of the row.
    private int PreviewStep(int index, int count, int step)
    {
        int next = index + step;
        if (next < 0 || next >= count)
        {
            _previewSound = PreviewSound.End;
            return index;
        }
        _previewSound = PreviewSound.Move;
        return next;
    }

    // The store tab has two rows to be in: the filter chips above the grid and the cards. Up from the cards' top row goes to
    // the chips, Down comes back; with no cards (an empty filter, a failed load) Up and Down toggle between the two, so the
    // chips can always be reached to pick another category. Options (refresh) works from either row. Square sorts from the
    // chips (and with no cards) and previews the theme on a card; X on a card opens the download question.
    private void UpdateCommunity(UiInput input, bool preview, bool options, bool triangle, bool update)
    {
        const int columns = 4;
        if (CommunityChipFocused)
        {
            int step = (input.Right ? 1 : 0) - (input.Left ? 1 : 0);
            if (step != 0) CommunityChip = Math.Clamp(CommunityChip + step, 0, CommunityChipCount - 1);
            if (input.Down) CommunityChipFocused = false;
            else if (input.Confirm && CommunityChip == SearchChip)
            {
                // Without a term X opens the keyboard (typing starts on the next frame); with one, X removes it.
                if (CommunityQuery.Length == 0)
                {
                    SearchEditor = StoreAddressEditor.ForSearch();
                    View = ThemeView.Search;
                }
                else
                {
                    CommunityQuery = "";
                    CommunitySelected = _communityCount == 0 ? -1 : 0;
                    _action = ThemeAction.CommunityRefresh;
                }
            }
            else if (input.Confirm && CommunityChip == NoAiChip)
            {
                CommunityHideAi = !CommunityHideAi;
                CommunitySelected = _communityCount == 0 ? -1 : 0;
                _action = ThemeAction.CommunityRefresh;
            }
            else if (input.Confirm)
            {
                // The list on screen stays until the filtered one arrives, like a new sort.
                CommunityCategory = CategoryOfChip(CommunityChip);
                CommunitySelected = _communityCount == 0 ? -1 : 0;
                _action = ThemeAction.CommunityRefresh;
            }
            // Triangle (like) does nothing here: the cards do not have the focus, so there is no card to like.
        }
        else if (input.Up && (_communityCount == 0 || CommunitySelected < columns))
        {
            CommunityChipFocused = true;
            CommunityChip = ChipOfCategory(CommunityCategory);
        }
        else if (_communityCount > 0)
        {
            if (input.Right) CommunitySelected = Math.Min(_communityCount - 1, CommunitySelected + 1);
            if (input.Left) CommunitySelected = Math.Max(0, CommunitySelected - 1);
            if (input.Down) CommunitySelected = Math.Min(_communityCount - 1, CommunitySelected + columns);
            if (input.Up) CommunitySelected = Math.Max(0, CommunitySelected - columns);
            // X asks first (download, download and apply, or cancel); the same X cannot also answer.
            if (input.Confirm)
            {
                CommunityDialogUpdate = update;
                OpenDialog(DialogKind.CommunityDownload, 0);
                return;
            }
            if (triangle) _action = ThemeAction.CommunityLike;
            // Square on a card previews that theme, as in the Library; the sort is Square on the chips.
            if (preview) _action = ThemeAction.CommunityPreview;
        }
        // Sort is Square on the chips (and with no cards, when there is nothing to preview).
        if (preview && (CommunityChipFocused || _communityCount == 0))
        {
            CommunitySort = (CommunitySort + 1) % 3;
            CommunitySelected = _communityCount == 0 ? -1 : 0;
            _action = ThemeAction.CommunityRefresh;
        }
        if (options) _action = ThemeAction.CommunityRefresh;
    }

    // The Avatars tab: the d-pad moves over the grid of store avatars, X asks to download the focused one (or to download and set
    // it), Triangle likes it or takes the like back, Square goes to the next sort, Options loads the list again. Circle goes back to
    // the Library like on every tab (handled before this).
    private void UpdateAvatarStore(UiInput input, bool sort, bool options, bool triangle)
    {
        if (_avatarStoreCount > 0)
        {
            const int columns = AvatarStoreColumns;
            if (input.Right) AvatarStoreSelected = Math.Min(_avatarStoreCount - 1, AvatarStoreSelected + 1);
            if (input.Left) AvatarStoreSelected = Math.Max(0, AvatarStoreSelected - 1);
            if (input.Down && AvatarStoreSelected / columns < (_avatarStoreCount - 1) / columns)
                AvatarStoreSelected = Math.Min(_avatarStoreCount - 1, AvatarStoreSelected + columns);
            if (input.Up) AvatarStoreSelected = Math.Max(0, AvatarStoreSelected - columns);
            if (input.Home) AvatarStoreSelected = 0;
            if (input.End) AvatarStoreSelected = _avatarStoreCount - 1;
            // X asks first; the same X cannot also answer.
            if (input.Confirm)
            {
                OpenDialog(DialogKind.AvatarDownload, 0);
                return;
            }
            if (triangle) _action = ThemeAction.AvatarLike;
        }
        if (sort)
        {
            AvatarStoreSort = (AvatarStoreSort + 1) % 3;
            AvatarStoreSelected = _avatarStoreCount == 0 ? -1 : 0;
            _action = ThemeAction.AvatarStoreRefresh;
        }
        else if (options) _action = ThemeAction.AvatarStoreRefresh;
    }

    // The Library's avatar row: Left and Right move over the cards (Console picture first), X asks to set the focused avatar or to
    // put the console's picture back, Triangle asks to remove the focused avatar, Up or Circle goes back to the themes, Options reads
    // the library again. Every question waits for an idle app; the questions about the console's picture and removing start on Cancel.
    private void UpdateAvatarRow(UiInput input, bool options, bool triangle, bool busy)
    {
        if (input.Up || input.Cancel)
        {
            AvatarRowFocused = false;
            return;
        }
        if (input.Right) AvatarSelected = Math.Min(_avatarCount, AvatarSelected + 1);
        if (input.Left) AvatarSelected = Math.Max(0, AvatarSelected - 1);
        if (input.Home) AvatarSelected = 0;
        if (input.End) AvatarSelected = _avatarCount;
        if (input.Confirm)
        {
            if (busy) _action = ThemeAction.Busy;
            else if (AvatarSelected == 0) OpenDialog(DialogKind.AvatarReset, AvatarResetOptions.Length - 1);
            else OpenDialog(DialogKind.AvatarApply, 0);
        }
        else if (triangle && AvatarSelected > 0)
        {
            if (busy) _action = ThemeAction.Busy;
            else OpenDialog(DialogKind.AvatarRemove, AvatarRemoveOptions.Length - 1);
        }
        else if (options) _action = ThemeAction.Reload;
    }

    private void UpdateSystem(UiInput input, bool busy, AppUpdate.State appUpdate)
    {
        if (input.Up) SystemSelected = Math.Max(0, SystemSelected - 1);
        if (input.Down) SystemSelected = Math.Min(SystemRowCount - 1, SystemSelected + 1);
        // Left and Right change the volume; it is only a setting, so it needs no idle app.
        if ((SystemRow)SystemSelected is SystemRow.SoundVolume or SystemRow.MusicVolume && (input.Left || input.Right))
        {
            SoundVolumeStep = input.Right ? 1 : -1;
            _action = (SystemRow)SystemSelected == SystemRow.MusicVolume ? ThemeAction.ChangeMusicVolume : ThemeAction.ChangeSoundVolume;
            return;
        }
        if (!input.Confirm) return;
        switch ((SystemRow)SystemSelected)
        {
            case SystemRow.Backgrounds:
                _action = busy ? ThemeAction.Busy : ThemeAction.ToggleBackgroundScope;
                break;
            case SystemRow.EarlierThemes:
                _action = busy ? ThemeAction.Busy : ThemeAction.ToggleEarlierThemes;
                break;
            case SystemRow.Sounds:
                _action = busy ? ThemeAction.Busy : ThemeAction.ToggleSounds;
                break;
            case SystemRow.SoundVolume:
            case SystemRow.MusicVolume:
                _action = busy ? ThemeAction.Busy : ThemeAction.AskSoundVolume;
                break;
            case SystemRow.AiThemes:
                // Only the store list changes, so it works while the app is busy too.
                CommunityHideAi = !CommunityHideAi;
                _action = ThemeAction.ToggleAiThemes;
                break;
            case SystemRow.StoreAddress:
                // Typing starts on the next frame: this X only opens the keyboard.
                OpenStoreAddress(StoreUrl);
                break;
            case SystemRow.FirmwareReport:
                if (busy) _action = ThemeAction.Busy;
                else OpenDialog(DialogKind.FirmwareReport, 0);   // starts on Save; sending is a choice of its own
                break;
            case SystemRow.AppUpdate:
                // Installing replaces the app's files, so it waits for an idle app; looking at what is known does not.
                if (appUpdate != AppUpdate.State.Ready) _action = ThemeAction.CheckAppUpdate;
                else if (busy) _action = ThemeAction.Busy;
                else OpenDialog(DialogKind.AppUpdate, 0);   // starts on Update now
                break;
            case SystemRow.Restore:
                RestoreParts = ThemePart.All;
                if (busy) _action = ThemeAction.Busy;
                else if (ThemeParts.List(_restorable) is { Count: > 1 } list)
                {
                    // Every part starts ticked (that is the full restore); the focus starts on Cancel.
                    _dialogParts = list;
                    _partsOptions = [.. list.ConvertAll(ThemeParts.Label), "Restore defaults", "Cancel"];
                    RestoreParts = _restorable;
                    OpenDialog(DialogKind.RestoreParts, list.Count + 1);
                }
                else OpenDialog(DialogKind.Restore, RestoreOptions.Length - 1);   // starts on Cancel
                break;
        }
    }

    /// <summary>
    /// Asks whether to apply the theme again after a system update, unless another question is open. The focus starts
    /// on "Apply again"; "Not now" and O answer <see cref="ThemeAction.ReapplyLater"/>.
    /// </summary>
    public void AskReapply()
    {
        if (Dialog == DialogKind.None) OpenDialog(DialogKind.Reapply, 0);
    }

    /// <summary>
    /// Offers to restart the home screen so new icons and sounds show at once, unless another question is open. The
    /// focus starts on "Show now"; "Later" and O just close it. Under OnionHEN (<paramref name="hen"/>) the question has the option
    /// "Later" only: a restart there can crash the home screen, so nothing in this dialog can answer
    /// <see cref="ThemeAction.RestartShell"/>.
    /// </summary>
    public void AskRestartShell(HenKind hen = HenKind.Unknown)
    {
        if (Dialog == DialogKind.None)
            OpenDialog(RunningHen.AllowsShellRestart(hen) ? DialogKind.RestartShell : DialogKind.RestartShellBlocked, 0);
    }

    /// <summary>
    /// Asks whether to apply the look again so its sounds play at the new volume, unless another question is open. The
    /// focus starts on "Apply again".
    /// </summary>
    public void AskSoundVolume()
    {
        if (Dialog == DialogKind.None) OpenDialog(DialogKind.SoundVolume, 0);
    }

    private bool _reportOffer;

    /// <summary>
    /// Asks for a firmware report the first time the System tab opens (the app calls it at start on a firmware without a
    /// catalog that was not asked about yet). The focus starts on "Send report"; "Not now" and O answer
    /// <see cref="ThemeAction.ReportLater"/>, "Send report" is <see cref="ThemeAction.FirmwareReport"/> with <see cref="SendReport"/>.
    /// </summary>
    public void OfferReport() => _reportOffer = true;

    private void OpenDialog(DialogKind kind, int focus)
    {
        Dialog = kind;
        DialogFocus = focus;
    }

    // Left/Right and Up/Down move the focus, X takes the focused option, O closes; every other button is ignored.
    private void UpdateDialog(UiInput input, bool busy)
    {
        int last = DialogOptions.Count - 1;   // Cancel is always the last option
        if (input.Cancel)
        {
            if (Dialog == DialogKind.Reapply) _action = ThemeAction.ReapplyLater;
            else if (Dialog == DialogKind.ReportAsk) _action = ThemeAction.ReportLater;
            Dialog = DialogKind.None;
            return;
        }
        if (input.Confirm && Dialog == DialogKind.ApplyParts && DialogFocus < _dialogParts.Count)
        {
            ApplyParts ^= _dialogParts[DialogFocus];   // a part is ticked or unticked; the dialog stays
            return;
        }
        if (input.Confirm && Dialog == DialogKind.RestoreParts && DialogFocus < _dialogParts.Count)
        {
            // The system icons and the UI sounds share one file, so they are ticked and unticked together.
            ThemePart part = _dialogParts[DialogFocus];
            if ((part & (ThemePart.Icons | ThemePart.Sounds)) != 0) part = (ThemePart.Icons | ThemePart.Sounds) & _restorable;
            RestoreParts = (RestoreParts & part) != 0 ? RestoreParts & ~part : RestoreParts | part;
            return;
        }
        if (input.Confirm && Dialog == DialogKind.RestoreParts && DialogFocus < last)
        {
            // Restore defaults: nothing happens with nothing ticked; everything the console shows is the full restore.
            if (RestoreParts == ThemePart.None) return;
            Dialog = DialogKind.None;
            if (busy) { _action = ThemeAction.Busy; return; }
            if (RestoreParts == _restorable) RestoreParts = ThemePart.All;
            _action = ThemeAction.RestoreOriginals;
            return;
        }
        if (input.Confirm && Dialog == DialogKind.ApplyParts && DialogFocus < last)
        {
            // Apply theme: nothing happens with nothing ticked. An app background still ticked asks where it goes.
            if (ApplyParts == ThemePart.None) return;
            if (busy) { Dialog = DialogKind.None; _action = ThemeAction.Busy; return; }
            if (_scopeAfterParts && (ApplyParts & ThemePart.AppBackgrounds) != 0)
            {
                OpenDialog(DialogKind.ApplyScope, _scopeDefault);
                return;
            }
            Dialog = DialogKind.None;
            ApplyAsksScope = false;
            ApplyAllApps = false;
            _action = ThemeAction.Apply;
            return;
        }
        if (input.Confirm)
        {
            DialogKind kind = Dialog;
            int chosen = DialogFocus;
            Dialog = DialogKind.None;
            if (chosen >= last)   // Cancel
            {
                if (kind == DialogKind.Reapply) _action = ThemeAction.ReapplyLater;
                else if (kind == DialogKind.ReportAsk) _action = ThemeAction.ReportLater;
                return;
            }
            // A download goes through the store queue, which waits for the card pictures; it is not a console change.
            if (kind == DialogKind.CommunityDownload)
            {
                _action = chosen == 1 ? ThemeAction.CommunityDownloadApply : ThemeAction.CommunityDownload;
                return;
            }
            // A plain avatar download only adds a file to the app's avatars; "Download and set" also changes the profile picture.
            if (kind == DialogKind.AvatarDownload && chosen == 0)
            {
                _action = ThemeAction.AvatarDownload;
                return;
            }
            if (busy) { _action = ThemeAction.Busy; return; }
            switch (kind)
            {
                case DialogKind.Reapply:
                    _action = ThemeAction.Reapply;
                    break;
                case DialogKind.AppUpdate:
                    _action = ThemeAction.UpdateApp;
                    break;
                case DialogKind.AvatarApply:
                    _action = ThemeAction.AvatarApply;
                    break;
                case DialogKind.AvatarReset:
                    _action = ThemeAction.AvatarReset;
                    break;
                case DialogKind.AvatarRemove:
                    _action = ThemeAction.AvatarRemove;
                    break;
                case DialogKind.AvatarDownload:
                    _action = ThemeAction.AvatarDownloadSet;
                    break;
                case DialogKind.RestartShell:
                    _action = ThemeAction.RestartShell;
                    break;
                case DialogKind.SoundVolume:
                    _action = ThemeAction.ApplySoundVolume;
                    break;
                case DialogKind.ReportAsk:
                    SendReport = true;
                    _action = ThemeAction.FirmwareReport;
                    break;
                case DialogKind.FirmwareReport:
                    SendReport = chosen == 1;
                    _action = ThemeAction.FirmwareReport;
                    break;
                case DialogKind.Apply:
                    ApplyAsksScope = false;
                    ApplyAllApps = false;
                    _action = ThemeAction.Apply;
                    break;
                case DialogKind.ApplyScope:
                    ApplyAsksScope = true;
                    ApplyAllApps = chosen == 1;
                    _action = ThemeAction.Apply;
                    break;
                case DialogKind.Restore:
                    RestoreParts = ThemePart.All;
                    _action = ThemeAction.RestoreOriginals;
                    break;
                case DialogKind.Remove:
                    // The pack is gone once the library is read again, so its detail is left right away.
                    _action = ThemeAction.RemoveTheme;
                    View = ThemeView.Library;
                    break;
            }
            return;
        }
        int step = (input.Right || input.Down ? 1 : 0) - (input.Left || input.Up ? 1 : 0);
        if (step != 0) DialogFocus = Math.Clamp(DialogFocus + step, 0, last);
    }

    public ThemeAction TakeAction()
    {
        var action = _action;
        _action = ThemeAction.None;
        return action;
    }
}
