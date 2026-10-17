# Native renderer preview

An interactive PC host for the actual PS5 renderer, not a separate HTML recreation of the app.

```powershell
dotnet run --project ThemeApp.Preview/ThemeApp.Preview.csproj -p:SharpProsperoRoot=$env:SHARPPROSPERO_ROOT
```

Open http://127.0.0.1:5185. Arrow keys select, Enter opens, Escape goes back, V toggles fullscreen preview,
T is Triangle (Like), Q/E switch tabs, O is Options. On the System tab the arrows move between the rows and Enter
acts on the focused one: the Store address row opens the keyboard (arrows move, Enter types the key, V deletes a
character, Escape cancels, O saves), and Restore defaults asks in a dialog first, as Apply does on Detail. The buttons
beneath the picture provide
the same controls. The server binds only to loopback and uses one preview session.

Append `-- --screenshots` to write native-rendered PNG files to `out/` for Library (also with the Applied badge),
Detail, the Apply, scope and Restore dialogs, Preview, Artwork, Community, Import, System (every row focused),
the store-address keyboard and the three toasts. Set `PREVIEW_LIBRARY` to a folder of `.p5theme` packs to add them to
the library (the fixtures in `Shared.Tests/fixtures` are always loaded). `-- --bench` prints the frame cost per view. Decode uses the SDK's PC texture library; the PS5
app uses the system PNG decoder. USB actions in this preview only display a message; actual USB import runs in
the native app.
