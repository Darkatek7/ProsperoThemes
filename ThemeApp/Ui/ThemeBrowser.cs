using SharpProspero.Ui;
using System;

namespace ProsperoThemes.App.Ui;

/// <summary>StoreAddress is the keyboard screen: opened from System, not a header tab, and it returns there.</summary>
public enum ThemeView { Library, Detail, Preview, Community, Import, System, StoreAddress }
/// <summary>SaveStoreUrl: Options in the store-address editor; the app reads <see cref="ThemeBrowser.StoreEditor"/>'s text.</summary>
public enum ThemeAction { None, ImportUsb, Reload, Apply, RestoreOriginals, CommunityRefresh, CommunityDownload, CommunityLike, SaveStoreUrl }

public sealed class ThemeBrowser
{
    public ThemeView View { get; private set; }
    public int Selected { get; private set; } = -1;
    public bool ShowOverlay { get; private set; } = true;
    public int TargetOffset { get; private set; }
    /// <summary>A system-changing action armed by one X press and waiting for the second; None otherwise.</summary>
    public ThemeAction Pending { get; private set; }
    /// <summary>Focused card on the Community tab, or -1 when the list is empty.</summary>
    public int CommunitySelected { get; private set; } = -1;
    /// <summary>The store's sort order shown on the Community tab (Top, Downloads, New).</summary>
    public int CommunitySort { get; private set; }
    /// <summary>The saved store address: shown on System and what the editor starts from. The app keeps it current.</summary>
    public string StoreUrl { get; set; } = "";
    /// <summary>The keyboard behind <see cref="ThemeView.StoreAddress"/>; keeps its text after the screen is left.</summary>
    public StoreAddressEditor StoreEditor { get; private set; } = new();
    private int _communityCount;
    private int _count;
    private ThemeView _previewOrigin = ThemeView.Library;
    private ThemeAction _action;

    public void SetCount(int count)
    {
        _count = Math.Max(0, count);
        Selected = _count == 0 ? -1 : Math.Clamp(Selected, 0, _count - 1);
        if (_count == 0 && View is ThemeView.Detail or ThemeView.Preview) View = ThemeView.Library;
        TargetOffset = 0;
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
        Pending = ThemeAction.None;
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

    public void Update(UiInput input, bool preview = false, bool options = false, int targetCount = 0, bool triangle = false)
    {
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
        if (Pending != ThemeAction.None)
        {
            // Writing to the system takes a second, deliberate X; any other button disarms it.
            if (input.Confirm) _action = Pending;
            if (input.Confirm || input.Cancel || input.Up || input.Down || input.Left || input.Right ||
                input.PageUp || input.PageDown || preview || options)
                Pending = ThemeAction.None;
            return;
        }
        if (input.Cancel || options && View == ThemeView.Detail)
        {
            View = ThemeView.Library;
            return;
        }
        if (input.PageDown || input.PageUp)
        {
            int tab = Math.Max(0, Array.IndexOf(Tabs, View));
            tab = (tab + (input.PageDown ? 1 : Tabs.Length - 1)) % Tabs.Length;
            View = Tabs[tab];
            if (View == ThemeView.Community && _communityCount == 0) _action = ThemeAction.CommunityRefresh;
            return;
        }
        if (View == ThemeView.Community)
        {
            const int columns = 4;
            if (_communityCount > 0)
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
            return;
        }
        if (preview && View == ThemeView.System)
        {
            OpenStoreAddress(StoreUrl);
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
            if (input.Confirm) { View = ThemeView.Detail; TargetOffset = 0; }
        }
        else if (View == ThemeView.Detail)
        {
            int maxOffset = Math.Max(0, targetCount - 5);
            TargetOffset = Math.Clamp(TargetOffset, 0, maxOffset);
            if (input.Down && TargetOffset < maxOffset) TargetOffset++;
            if (input.Up && TargetOffset > 0) TargetOffset--;
            if (input.Confirm) Pending = ThemeAction.Apply;
        }
        else if (View == ThemeView.Import && input.Confirm) _action = ThemeAction.ImportUsb;
        else if (View == ThemeView.System && input.Confirm) Pending = ThemeAction.RestoreOriginals;
        if (options && View is ThemeView.Library or ThemeView.System) _action = ThemeAction.Reload;
    }

    public ThemeAction TakeAction()
    {
        var action = _action;
        _action = ThemeAction.None;
        return action;
    }
}
