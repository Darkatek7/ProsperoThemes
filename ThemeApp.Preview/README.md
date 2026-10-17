# Native renderer preview

An interactive PC host for the actual PS5 renderer, not a separate HTML recreation of the app.

```powershell
dotnet run --project ThemeApp.Preview/ThemeApp.Preview.csproj -p:SharpProsperoRoot=$env:SHARPPROSPERO_ROOT
```

Open http://127.0.0.1:5185. Arrow keys select, Enter opens, Escape goes back, V toggles fullscreen preview,
Q/E switch tabs, O is Options. On the System tab V (Square) opens the store-address keyboard: arrows move,
Enter types the key, V deletes a character, Escape cancels, O saves. The buttons beneath the picture provide
the same controls. The server binds only to loopback and uses one preview session.

Append `-- --screenshots` to write native-rendered BMP files to `out/` for Library, Detail, Preview,
Artwork, Community, Import, System and the store-address keyboard. Decode uses the SDK's PC texture library; the PS5
app uses the system PNG decoder. USB actions in this preview only display a message; actual USB import runs in
the native app.
