# ThemeApp

ProsperoThemes as a native **application module**, with a controller-driven theme library, pack details,
fullscreen home preview and USB import.

**2026-10-13 milestone:** the native UI and interactive PC preview share the same framebuffer renderer.
The managed build and 63 tests pass. The full PS5 build uses the SDK's managed SHA-256 implementation;
the platform SHA-256 implementation would import unavailable OpenSSL symbols. Hardware deployment and
launch evidence are recorded separately below.

## Library, details and preview

- Library navigation uses the d-pad or left stick. X opens details; Square opens fullscreen preview.
- X in preview toggles the simulated home overlay; Circle returns to the screen that opened it.
- L1/R1 switch between the Library, Community, USB Import and System tabs. Options reloads the library.
- **Apply starts from the defaults:** before a theme is written, every file an earlier theme changed and
  the new one does not write goes back to its backup (files already at their original are not rewritten).
  So themes replace each other instead of stacking. If one of them cannot be put back, the theme is not
  applied. **Restore defaults** (System tab, X) puts every backed-up file back.
- The on-screen text is English (US spelling). The Inter font atlases cover ASCII plus the characters in
  `CHARS` of `tools/bake-assets.py` (`… · – × “ ” ‘ ’` and some accented Latin letters); keep new UI text
  inside that set.
- **Store address** (System tab): Square opens an on-screen keyboard that the app draws itself, so no system
  keyboard library is needed. The d-pad moves over the keys, X types the focused key (the bottom row has
  `http://`, `https://`, `:8080`, "Default" for the public store and "Clear all"), Square deletes the last character, Options saves
  and Circle discards. L1/R1 do nothing while typing. The address must start with `http://` or `https://`,
  contain a host, no spaces and at most 200 characters; trailing slashes are dropped. An unusable address
  keeps the keyboard open with the reason. Saving writes `store_url` in
  `/data/prospero-themes/settings.json` and clears the Community list, which loads from the new store on the
  next visit. An empty address removes the store. Editing `store_url` in the file still works.
  Without a `store_url` in the file (a first start writes it), the app uses the public store
  `https://themes.darkatek7.com`.
- **Home background of the app's own tile:** the app ships no `pic0` of its own, so the shell shows the
  theme's background behind its tile (`bg_hub_default`), like every other app without its own image. A
  per-focus rotation (the BgRotator payload) existed until 2026-10-17 and was removed: it only served this
  one tile.
- **Community HTTP** goes through `Library/StoreHttpClient.cs`, which loads libSceHttp and libSceSsl at run
  time. Linked as need entries (the SDK's `HttpClient`) they kept the app from starting on FW 13.00.
  `build.ps1` checks the need entries of `eboot.bin` with `tools/check-needed-modules.ps1` and stops if
  either library is back.
- **"In library" badge** (Community tab): a store theme whose pack is in the library carries a badge on its
  card. The library names packs `<sha256>.p5theme`; the app records the hash of every download in
  `/data/prospero-themes/community.json` (store id → hash), and uses the store's `sha256` per item instead
  when the list carries one. A pack removed from the library loses its badge.
- The bundled **Blue Hour** theme is a real format-2 pack carrying a ready-to-write BC7/zstd background
  for `bg_hub_default`, plus its PNG cover. It is not marked active merely because it is bundled.
- USB import checks mounted `/mnt/usb0` through `/mnt/usb7`, each root and its `prospero-themes` folder.
  It validates before copying, uses the archive's SHA-256 as its filename, verifies the copied temporary
  file, then renames it. Manifest IDs cannot cause an existing theme to be overwritten.
- Imported archives live under `/data/prospero-themes/library`. Invalid files are skipped with a readable
  message. Limits: 48 MiB archive, 64 MiB declared expanded content, 256 archive entries, 128 library
  packs, 16 MiB retained cover bytes, five decoded cover images. PNG covers are at most 1920×1080/4 MiB.

App resources ship at `/app0/sce_sys/prospero-themes/`. The pinned SDK gathers only known source trees,
and its packager omits arbitrary top-level asset directories; `build.ps1` stages the runtime fonts,
font licence, bundled pack and ThemeHelper inside the metadata tree **before** packaging. Original artwork
and font
source files stay in the repository and are excluded from the runtime artifact.

## PC preview

```powershell
dotnet run --project ThemeApp.Preview/ThemeApp.Preview.csproj -p:SharpProsperoRoot=$env:SHARPPROSPERO_ROOT
```

Open `http://127.0.0.1:5185`. Arrow keys navigate, Enter is X, Escape is Circle, V is Square and Q/E are
L1/R1. This host invokes the same `ThemeBrowser` and `ThemeRenderer` as the PS5; only file/image decoding
and input transport differ. `--screenshots` renders every screen (Library, Detail, Preview, Artwork, Community, Import, System and
the store-address keyboard) to `ThemeApp.Preview/out/`.

## ShadowMount deployment

```powershell
# Review the local file plan first (no network writes).
python ThemeApp/deploy-shadowmount.py
# Upload a verified staging folder and request ShadowMount's scan (console address as --ip).
python ThemeApp/deploy-shadowmount.py --deploy --ip <console-ip> --api-port 10101
```

The deployer preserves an existing app in a dated backup, verifies every upload by reading it back,
then atomically publishes `/data/homebrew/PPSA90210`. It uses ShadowMount's `/api/v1/scan` request, rather
than the old direct `/user/app` registration experiment described below. On 2026-10-13 the console
reported ShadowMount+ `1.7-beta5fix1` and API port `10101`.

## What it is

`Program.cs` hosts the `ProsperoApp` frame loop, handles controller input and hands worker results back
to the frame thread. File scanning and import live in `Library/`; rendering and navigation live in `Ui/`.
The application delivery route differs from payloads:

| | Payload | Application module |
|---|---|---|
| Container | `.elf`, unsigned | `.pkg`, signed |
| Loaded by | the exploit's loader, into a host process | the console's own dynamic linker |
| Runs as | a thread inside someone else's process | its own process with its own services |
| Carries | nothing | its own copies of modules the system does not publish |

The pack format and PC apply/restore path already work. Background replacement is proven on hardware;
other pack layers remain subject to the evidence in the repository README.

## Game, not Media

The shell sorts an application into **Games** or **Media** by fields in `sce_sys/param.json`, not by
anything in the binary:

| | `applicationCategoryType` | `contentBadgeType` | `gameIntent` |
|---|---:|---:|---|
| **Games** | **`0`** | **`1`** | **present, permitting `launchActivity`** |
| Media | `65536` | `2` | absent |

`sce_sys/param.json` here uses the Games row. The SDK's metadata step reports the kind it read:

```
== Metadata ==
  kind                           Game (0)
```

Why the Games row:

- SharpProspero's pinned `docs/param-json.md` says an application built here uses `0`: "That is the kind
  for any title that ships a module of its own, whatever the module goes on to do". This project ships
  exactly that, an application module with its own `eboot.bin` and `libc.prx`.
- The same document says declaring a media kind does not give a title the media services, only the
  handling that assumes them, and that media kinds do not carry `gameIntent`.
- `launchActivity` is the intent the home screen uses to start a title.

Two SharpProspero apps installed on this console (firmware 13.00) follow the same pattern. Their
`param.json` files were read back over FTP from `/data/homebrew/<TID>/sce_sys/param.json`, and their source
repositories (SvenGDK/Prospero-Explorer, Prospero-Multi-Tools, Prospero-Vibrate) set the same values:

| Installed app | `applicationCategoryType` | `contentBadgeType` | `gameIntent` |
|---|---:|---:|---|
| Prospero Explorer (`PPSA99109`) | `0` | `1` | `launchActivity` |
| Prospero Multi Tools (`PPSA99110`) | `0` | `1` | `launchActivity` |

**History.** An earlier revision of this project declared the Media row. That choice was based on two
Sony system apps, YouTube and Internet Browser, which rely on the console's media service frameworks. A
native app that does not use those services gains nothing from the Media row, so the choice is superseded.
Their readings are kept here for reference:

The values are not a guess. Both were read back off this console from those applications:

| Installed app | `applicationCategoryType` | `contentBadgeType` |
|---|---:|---:|
| YouTube (`PPSA01650`), `/user/appmeta/PPSA01650/param.json` | `65536` | `2` |
| Internet Browser (`MOUU12023`), `/user/appmeta/MOUU12023/param.json` | `65536` | `2` |

Both files were read back from the console (`/user/appmeta/<TID>/param.json`), which is how the values
above were obtained rather than taken from documentation. The same Internet Browser entry also
carries `"hubAppUri": "psmediahub:main?titleId=MOUU12023"`, which is how a *web-backed* media hub is
opened. That is part of the media-hub setup the Media row relied on; a native app that draws its own
screen does not need it, so this project sets none.

**Status.** The package now declares the Game kind. That it launches on hardware with this classification
is not yet verified, and the launch evidence is still open.

The built package's identity:

| Field | Value |
|---|---|
| `titleId` | `PPSA90210` |
| `contentId` | `UP0000-PPSA90210_00-0000000000000000` |
| `conceptId` | `90210` |

## The `libc.prx` it has to carry

An application module is loaded by the console's linker, so it must ship the modules the system does not
publish. The one it needs is `libc.prx`, and the SDK ships none — its `runtime/` folder holds only import
lists. Without it the build stops with:

```
This application names 1 module(s) that it has to carry with them, and they are not present:
  libc.prx
```

Supply one from a game package and point the build at it:

```powershell
# third_party/modules/libc.prx  (gitignored - it is Sony's binary, not ours)
.\build.ps1
```

`build.ps1` looks in `third_party/modules` (and one level up, at this collection's root) or takes
`-Modules`. `PROSPERO_MODULES` still overrides everything, as it does for the SDK's own build.

**The honest caveat.** The copy used here is `libc.prx` out of a DREDGE backport folder — version `05.00`,
taken out of an installed application rather than from the SDK. The SDK's own warning is that a copy
"lifted out of an installed application" was built against whichever system that application shipped
against, and that a mismatch "hangs the console at launch, with nothing written to the log". The build
raises the package's required system version to match it:

```
== System version ==
  eboot.bin                        02.00
  libc.prx                         05.00
Result:   05.00  0x0500000000000000
```

`05.00` is comfortably below firmware 13.00, and backward compatibility is the norm — but this is the
one part of the package that is not ours and not verified, and it is a live suspect for the launch
failure below.

**The container format decides whether the app starts.** The PS5 loader only takes a `libc.prx` in the PS5
container (first bytes `54 14 F5 EE`). A copy in the PS4 container (`4F 15 3D 1D`) is "already wrapped" for the
SDK, which then copies it unchanged; the app stops before `Main` with "The data is corrupted", and klog shows
`Lack of a .prx file in /app0/sce_module is detected` and `PrxModuleLoadError`. That is what broke the
Community build on 2026-10-13, not its libSceHttp/libSceSsl need entries (proven 2026-10-15: the same build
with a PS5-container libc.prx starts and reaches the store). `tools/check-needed-modules.ps1` now fails the
build for a carried module without the PS5 magic. The working copy is 1,284,674 bytes (the one in
`/data/prospero-themes/app-backup/PPSA90210-20261013-152942/sce_module`).

## Building it

```powershell
pwsh -File .\ThemeApp\build.ps1 -SdkRoot <sdk> -Output Folder     # out/module: the app folder
pwsh -File .\ThemeApp\package-release.ps1                          # dist/PPSA90210.zip + catalog record
```

`-Output Folder` stops at the app folder (`out/module`), which ShadowMount installs from
`/data/homebrew/PPSA90210`. Without it the SDK also writes a `.pkg`. `package-release.ps1` zips that folder
as `PPSA90210/` and writes the record for the homebrew catalog.

Use PowerShell 7. The SDK's `build-app.ps1` merges native stderr into the pipeline under
`$ErrorActionPreference='Stop'`, which Windows PowerShell 5.1 treats as fatal.

## Files

| File | Purpose |
|---|---|
| `Program.cs` | The app: a `ProsperoApp` subclass, one screen. |
| `ThemeApp.csproj` | Application-module project; imports the SDK's `Prospero.App.props/targets`. |
| `sce_sys/param.json` | Identity and the Game classification. |
| `sce_sys/icon0.png` | 512×512 source icon; the build converts it to `icon0.dds`. |
| `build.ps1` | Wraps the SDK's `build-app.ps1`, locates `libc.prx`, builds ThemeHelper, stages the assets. |
| `package-release.ps1` | Release ZIP (`PPSA90210/` at the top) and the homebrew catalog record. |
| `deploy-shadowmount.py` | Uploads the app folder to `/data/homebrew/PPSA90210` over FTP and asks ShadowMount to register it. |
| `third_party/modules/` | Gitignored. Where `libc.prx` goes. |
