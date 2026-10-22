using ProsperoThemes.App.Library;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoThemes.App.Ui;

/// <summary>StoreAddress is the keyboard screen: opened from System, not a header tab, and it returns there.</summary>
public enum ThemeView { Library, Detail, Preview, Community, Import, System, StoreAddress }
/// <summary>
/// SaveStoreUrl: Options in the store-address editor; the app reads <see cref="ThemeBrowser.StoreEditor"/>'s text.
/// ToggleBackgroundScope: the System row for app backgrounds. ToggleSounds: the System row for music and sounds. RemoveTheme: the selected pack leaves the library. Busy: a press that needed the app to be idle (the app
/// answers it with a "Please wait" notice); no system-changing action is ever emitted while the app is busy.
/// </summary>
public enum ThemeAction { None, ImportUsb, Reload, Apply, RestoreOriginals, CommunityRefresh, CommunityDownload, CommunityLike, SaveStoreUrl, FirmwareReport, ToggleBackgroundScope, ToggleEarlierThemes, ToggleSounds, RemoveTheme, Busy }

/// <summary>
/// The modal question over Detail or System. Apply asks to apply; ApplyScope is the same for a pack with an all-apps
/// background and asks where that background goes; Restore asks before the defaults are put back; Remove asks before
/// the selected pack leaves the library; ApplyParts lets the user tick the parts of a theme to apply (its options are
/// <see cref="ThemeBrowser.DialogParts"/>, then Apply and Cancel).
/// </summary>
public enum DialogKind { None, Apply, ApplyScope, Restore, Remove, ApplyParts }

/// <summary>The focusable rows of the System screen, top to bottom.</summary>
public enum SystemRow { Backgrounds, EarlierThemes, Sounds, StoreAddress, FirmwareReport, Restore }

public sealed class ThemeBrowser
{
    private static readonly string[] ApplyOptions = ["Apply theme", "Cancel"];
    private static readonly string[] ScopeOptions = ["Apps without their own image", "All apps", "Cancel"];
    private static readonly string[] RestoreOptions = ["Restore defaults", "Cancel"];
    private static readonly string[] RemoveOptions = ["Remove from library", "Cancel"];

    /// <summary>Number of focusable rows on the System screen.</summary>
    public const int SystemRowCount = 6;

    public ThemeView View { get; private set; }
    public int Selected { get; private set; } = -1;
    public bool ShowOverlay { get; private set; } = true;
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
        DialogKind.ApplyParts => _partsOptions,
        _ => Array.Empty<string>(),
    };
    /// <summary>The parts the open ApplyParts dialog lists, in order: its first options. Empty for every other dialog.</summary>
    public IReadOnlyList<ThemePart> DialogParts => Dialog == DialogKind.ApplyParts ? _dialogParts : Array.Empty<ThemePart>();
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
    /// The chip the cursor is on while <see cref="CommunityChipFocused"/>: 0 is "All", 1 and up are the categories in order
    /// (so the chip of <see cref="CommunityCategory"/> is that plus one). Moving into the row starts on the chosen chip.
    /// </summary>
    public int CommunityChip { get; private set; }
    /// <summary>The chips of the filter row: "All" and every category.</summary>
    public static int CommunityChipCount => CommunityCategories.Count + 1;
    /// <summary>The saved store address: shown on System and what the editor starts from. The app keeps it current.</summary>
    public string StoreUrl { get; set; } = "";
    /// <summary>The keyboard behind <see cref="ThemeView.StoreAddress"/>; keeps its text after the screen is left.</summary>
    public StoreAddressEditor StoreEditor { get; private set; } = new();
    private int _communityCount;
    private int _count;
    private ThemeView _previewOrigin = ThemeView.Library;
    // The ApplyParts dialog asks where an app background goes next, when the pack has one and it stays ticked.
    private bool _scopeAfterParts;
    private int _scopeDefault;
    private ThemeAction _action;

    public void SetCount(int count)
    {
        _count = Math.Max(0, count);
        Selected = _count == 0 ? -1 : Math.Clamp(Selected, 0, _count - 1);
        if (_count == 0 && View is ThemeView.Detail or ThemeView.Preview) View = ThemeView.Library;
        // The library changed under the question, so it may no longer be about the pack that is selected.
        Dialog = DialogKind.None;
    }

    public void SetCommunityCount(int count)
    {
        _communityCount = Math.Max(0, count);
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
    private static readonly ThemeView[] Tabs = [ThemeView.Library, ThemeView.Community, ThemeView.Import, ThemeView.System];

    /// <summary>
    /// One frame of buttons. <paramref name="preview"/> is Square, <paramref name="options"/> Options and
    /// <paramref name="triangle"/> Triangle. <paramref name="asksScope"/> says the selected pack has an all-apps
    /// background (its apply asks where that goes), <paramref name="allAppsScope"/> is the saved answer, and
    /// <paramref name="busy"/> that a worker job is running: then nothing that changes the console or the settings
    /// is started (the press becomes <see cref="ThemeAction.Busy"/> instead). <paramref name="removable"/> says the
    /// selected pack can leave the library (the theme that ships with the app cannot). <paramref name="parts"/> are the
    /// parts the selected pack has (<see cref="ThemeParts.In"/>; None when not known), and <paramref name="sounds"/> the
    /// sounds setting: the apply question lists the parts when there are several, or when sounds are off and the pack
    /// has some, and then starts with every part ticked but the sounds when they are off.
    /// </summary>
    public void Update(UiInput input, bool preview = false, bool options = false, bool triangle = false,
        bool asksScope = false, bool allAppsScope = false, bool busy = false, bool removable = false,
        ThemePart parts = ThemePart.None, bool sounds = true)
    {
        // One Update is one frame, and an open dialog answers first. The X that opens a dialog (below) returns
        // right after opening it, so the same X cannot also confirm it.
        if (Dialog != DialogKind.None)
        {
            UpdateDialog(input, busy);
            return;
        }
        if (View == ThemeView.Preview)
        {
            if (input.Cancel || preview) View = _previewOrigin;
            else if (input.Confirm) ShowOverlay = !ShowOverlay;
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
            return;
        }
        if (View == ThemeView.Community)
        {
            UpdateCommunity(input, preview, options, triangle);
            return;
        }
        if (View == ThemeView.System)
        {
            UpdateSystem(input, busy);
            return;
        }
        if (preview && Selected >= 0 && View is ThemeView.Library or ThemeView.Detail)
        {
            _previewOrigin = View;
            ShowOverlay = true;
            View = ThemeView.Preview;
            return;
        }
        if (View == ThemeView.Library && _count > 0)
        {
            if (input.Right || input.Down) Selected = Math.Min(_count - 1, Selected + 1);
            if (input.Left || input.Up) Selected = Math.Max(0, Selected - 1);
            if (input.Home) Selected = 0;
            if (input.End) Selected = _count - 1;
            if (input.Confirm) View = ThemeView.Detail;
        }
        else if (View == ThemeView.Detail)
        {
            // Writing to the console always goes through a question first.
            if (input.Confirm)
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
            else if (triangle && removable)
            {
                if (busy) _action = ThemeAction.Busy;
                else OpenDialog(DialogKind.Remove, RemoveOptions.Length - 1);   // starts on Cancel
            }
        }
        else if (View == ThemeView.Import && input.Confirm) _action = ThemeAction.ImportUsb;
        if (options && View == ThemeView.Library) _action = ThemeAction.Reload;
    }

    // The store tab has two rows to be in: the filter chips above the grid and the cards. Up from the cards' top row goes to
    // the chips, Down comes back; with no cards (an empty filter, a failed load) Up and Down toggle between the two, so the
    // chips can always be reached to pick another category. Square (sort) and Options (refresh) work from either row.
    private void UpdateCommunity(UiInput input, bool preview, bool options, bool triangle)
    {
        const int columns = 4;
        if (CommunityChipFocused)
        {
            int step = (input.Right ? 1 : 0) - (input.Left ? 1 : 0);
            if (step != 0) CommunityChip = Math.Clamp(CommunityChip + step, 0, CommunityChipCount - 1);
            if (input.Down) CommunityChipFocused = false;
            else if (input.Confirm)
            {
                // The list on screen stays until the filtered one arrives, like a new sort.
                CommunityCategory = CommunityChip - 1;
                CommunitySelected = _communityCount == 0 ? -1 : 0;
                _action = ThemeAction.CommunityRefresh;
            }
            // Triangle (like) does nothing here: the cards do not have the focus, so there is no card to like.
        }
        else if (input.Up && (_communityCount == 0 || CommunitySelected < columns))
        {
            CommunityChipFocused = true;
            CommunityChip = CommunityCategory + 1;
        }
        else if (_communityCount > 0)
        {
            if (input.Right) CommunitySelected = Math.Min(_communityCount - 1, CommunitySelected + 1);
            if (input.Left) CommunitySelected = Math.Max(0, CommunitySelected - 1);
            if (input.Down) CommunitySelected = Math.Min(_communityCount - 1, CommunitySelected + columns);
            if (input.Up) CommunitySelected = Math.Max(0, CommunitySelected - columns);
            if (input.Confirm) _action = ThemeAction.CommunityDownload;
            if (triangle) _action = ThemeAction.CommunityLike;
        }
        if (preview)
        {
            CommunitySort = (CommunitySort + 1) % 3;
            CommunitySelected = _communityCount == 0 ? -1 : 0;
            _action = ThemeAction.CommunityRefresh;
        }
        if (options) _action = ThemeAction.CommunityRefresh;
    }

    private void UpdateSystem(UiInput input, bool busy)
    {
        if (input.Up) SystemSelected = Math.Max(0, SystemSelected - 1);
        if (input.Down) SystemSelected = Math.Min(SystemRowCount - 1, SystemSelected + 1);
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
            case SystemRow.StoreAddress:
                // Typing starts on the next frame: this X only opens the keyboard.
                OpenStoreAddress(StoreUrl);
                break;
            case SystemRow.FirmwareReport:
                _action = busy ? ThemeAction.Busy : ThemeAction.FirmwareReport;
                break;
            case SystemRow.Restore:
                if (busy) _action = ThemeAction.Busy;
                else OpenDialog(DialogKind.Restore, RestoreOptions.Length - 1);   // starts on Cancel
                break;
        }
    }

    private void OpenDialog(DialogKind kind, int focus)
    {
        Dialog = kind;
        DialogFocus = focus;
    }

    // Left/Right and Up/Down move the focus, X takes the focused option, O closes; every other button is ignored.
    private void UpdateDialog(UiInput input, bool busy)
    {
        int last = DialogOptions.Count - 1;   // Cancel is always the last option
        if (input.Cancel) { Dialog = DialogKind.None; return; }
        if (input.Confirm && Dialog == DialogKind.ApplyParts && DialogFocus < _dialogParts.Count)
        {
            ApplyParts ^= _dialogParts[DialogFocus];   // a part is ticked or unticked; the dialog stays
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
            if (chosen >= last) return;   // Cancel
            if (busy) { _action = ThemeAction.Busy; return; }
            switch (kind)
            {
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
