# ThemeApp

ProsperoThemes as a native **application module**, with a controller-driven theme library, pack details,
fullscreen home preview and USB import.

**2026-10-13 milestone:** the native UI and interactive PC preview share the same framebuffer renderer.
The managed build and 63 tests pass. The full PS5 build uses the SDK's managed SHA-256 implementation;
the platform SHA-256 implementation would import unavailable OpenSSL symbols. Hardware deployment and
launch evidence are recorded separately below.

## Library, details and preview

- Library navigation uses the d-pad or left stick. X opens details; Square opens fullscreen preview; Options
  reloads the library. Left and Right move along the theme row; Down from it reaches the avatar row (see "Avatars").
- **Preview** is an interactive stand-in for the console's home screen, made of what the pack brings (see
  "The preview" below). D-pad Left/Right moves the focus along the row of tiles (or along the control centre's
  buttons), L1/R1 switch Games and Media, Triangle goes between the home screen and the control centre, X hides the
  overlay (and brings it back), Square plays or stops the theme's music excerpt, Circle goes back (out of the
  control centre first, then out of the preview, to the screen that opened it). The footer lists at most four of
  these; the d-pad needs no hint.
- L1/R1 switch between the Library, Themes, Avatars, USB Import and System tabs. Circle goes back to the Library from
  any tab.
- **Questions first:** X on a theme's detail opens an Apply dialog; X on System's "Restore defaults" row opens a
  Restore dialog (focus starts on Cancel); X on a Themes card opens the Download dialog (focus on Download, see "Download
  question" below). In a dialog the d-pad moves the focus, X takes the focused option, Circle
  closes it; every other button is ignored. While the app is busy neither of these two opens ("Please wait…"); the Download question can (a download is not a console change).
- **Notices:** results and progress ("Applying theme…", "Download failed (404).") appear as one toast at the bottom
  of the screen: in the footer band, or just above it when the text is long. It stays while the app is busy and fades
  about six seconds after it was posted.
- **Applied badge:** the library card and the detail cover of the theme the console shows now carry an
  "Applied" badge (read from `state.json`; a restore removes it).
- **What a pack changes** (Library line, Detail list, store cards) is written by `PackSummary` with the store's
  counting rules: "Background · App background · 16 icons · 9 tile icons". The Detail screen lists the same groups
  under "What it changes" (Background, App background, App images, Icons, Tile icons, Sounds, Videos, and Other files
  for files that belong to none of them), with the number where there is more than one.
- **Apply starts from the defaults:** before a theme is written, every file an earlier theme changed and
  the new one does not write goes back to its backup (files already at their original are not rewritten).
  So themes replace each other instead of stacking. If one of them cannot be put back, the theme is not
  applied. **Restore defaults** (System tab: X, then Restore defaults in the dialog) puts every backed-up file back.
- **Where an app background goes:** applying a pack with an all-apps background asks in the Apply dialog:
  "Apps without their own image", "All apps" or "Cancel" (focus starts on the saved scope; the d-pad picks, X
  confirms, Circle cancels). The answer is saved as the background scope (the same setting as the App backgrounds
  row on the System tab). Packs without one get the plain "Apply theme" / "Cancel" dialog.
- **Other firmware, system updates:** nothing in the app is tied to 13.00; it compares the console's own
  files instead. Before the first system write every target is backed up and its payload checked against
  the console's file (its backup when there is one): same DDS size and DXGI format, same PNG size, same
  zstd decoded size (a title's own `pic0.dds` may differ in size, the shell scales it). A file the
  console does not have, or does not match, is skipped and listed; so is an icon the RCO does not hold.
  The untitled search icon (`#875`) is used only when it is the 13.00 element (length and hash).
  `backup/written.txt` records what the app last wrote to each file: a live file that matches neither
  that nor its backup was replaced by the system (an update, or Sony's servers for `/mnt/rnps`); its old
  backup moves to `backup/replaced/<time>/` and the live file becomes the new default, so Restore
  defaults never writes an old firmware's file back.
  A file whose backup is gone (deleted from `/data`) is never taken for an original when it is a theme's:
  `backup/originals.txt` keeps the hash of every original the app saved or put back, and the bundled
  `catalog/<firmware>.json` (StockCatalog: 9.60, 10.01, 10.60, 11.20, 11.60, 12.40, 12.70, 13.00, 13.20, 13.40, 13.42, 13.60) has the stock hash of each asset
  and of the icon container. A tile icon then comes back from the firmware's own copy in
  `/system_ex/rnps/apps` (on 13.00, 8 of the 9 icons are identical to it; Sony's servers updated NPXS40305).
  Any other such file has no original left on the console: Restore defaults leaves it and names it. A backup that
  does not match the catalog is never written back. On firmware without a catalog (one no report came from yet) the look
  decides (`Shared/StockLook.cs`): a stock icon container titles every theme icon (home, settings, power, ps_store and
  the rest), and the system tiles' backgrounds all differ; reports from 9.60 to 13.60 agree. A backup that fails this
  (made from another tool's theme, as on a 12.40 console whose 13 tile backgrounds were one picture) is never written
  back nor recorded as an original, and a first apply keeps no backup of such a live file; both are named. The System tab shows the firmware
  (`SystemInfo.SystemSoftwareVersion`, "13.000.040" on the test console); `state.json` records it.
- **Room before each write** (issue #13, helper v10): every console file is written as `<file>.tmp` and renamed over the
  old one, so its filesystem needs room for the whole new file first. A 12.40 console with a full `/system_ex` was left
  with half an icon container (`Sce.PlayStation.PUI_UI3.rco.tmp`, 52 MB of 106 MB) that the next boot kept. Now the app
  asks ThemeHelper for the free space (`FREE`) before an apply writes anything and again before each write, refuses with
  "not enough space for <file>: X MiB needed, Y MiB free" (1 MiB margin), deletes a `.tmp` left by an earlier write
  first, and deletes its own `.tmp` when a write fails (`UNLINK` of a `.tmp`).
- **In place when there is no room** (helper v11): the icon container is the same size themed and stock (icons and sounds
  patch it same-size), so when a write would fail the room check, the target exists with exactly the new data's size and
  the helper can patch, `ThemeApplier.WriteInPlace` compares the live file with the new data in 1 MiB pieces and overwrites
  only the pieces that differ (`PATCH <path> <offset> <len>`, neighbouring pieces together, at most 8 MiB per patch), then
  verifies by the streamed hash as always. Restore defaults and apply both use it, and only as the fallback: with room the
  `.tmp` and rename path is unchanged. The live file is read as `FileBytes` (outside the managed heap). A pass that stops
  half-way leaves old and new ranges mixed; the app first drops the file's `written.txt` entry, so the mix is not taken
  for a file the system replaced, and running the same restore or apply again finishes it. Another size, or a helper
  without `PATCH`, keeps the "not enough space" message. Unlike a rename this writes into the file the home screen may
  have open; the home screen reads the container when it starts (see `LoadsWithShell`), so the change shows after its restart. Measured on 13.00 (2026-10-25, helper v11): with 20 MB free on `/system_ex`, applying a theme with a sound in the 39 MB container patched it in place (no `.tmp`), and the home screen restart loaded it and stayed stable; Restore defaults (icons and sounds) then put it back in place with 12 MB free, byte for byte the catalog's stock SHA-256, and the restart was stable again. The raw `lseek` syscall works in the payload.
- **Originals from USB** (issue #13): the USB import also reads `prospero-themes/originals/<logical path>` (the same tree as
  `backup/original` on the same firmware) and takes a file as the backup only when the catalog lists it with that size and
  SHA-256, and it resolves to a console file (`ThemeApplier.ImportOriginal`). A different backup moves to
  `backup/replaced/<time>/`. A stock backup is then put back, and kept by an apply, whatever `backup/written.txt` says:
  that record may predate it. Checked on 13.00 (2026-10-25): with themed backups of `bg_NPXS40053.dds.zst` and the 39 MB
  icon container, Restore defaults left both themed; after the import ("2 original(s) imported") it wrote both back,
  and the live hashes matched the catalog; a second import said "2 already there".
- **Large files stay out of the managed heap:** the applier reads whole target files as `FileBytes`; from 16 MiB on
  (`LargeFile`) they sit in flexible memory. Before 13.00 the icon container is about 100 MB, and the 256 MiB heap could
  not take it: 12.40 failed with "preparing the backups failed, nothing was written: Arg_OutOfMemoryException" (issue
  #13). Measured on 13.00 (2026-10-25): with the app idle the heap held 36 MiB live and 68 MiB committed; two ~100 MB
  arrays in a row took it to 153 MiB committed, and after them and an apply it stayed at 233 MiB, while about 1.6 GiB of
  flexible memory was free. On the PC under the same heap limits, a second ~100 MB array in a row (the kept-themes path:
  stock copy, then the live one) failed at about 110 MiB live.
- **Firmware report** (System tab, Firmware report row): writes `/data/prospero-themes/reports/firmware-<version>.json`
  (and a copy to `/mnt/usb0` when a drive is connected): for every file in the bundled
  `catalog/13.00.json`, the system tile backgrounds and tile icons and the icon container, whether it
  exists, its size, hash and format, plus the RCO's icon list. Copies that look like a theme's (see above) are listed
  in `looks_themed`, and the app then asks for a report from a console that never had a theme; `catalog-from-report`
  leaves those files out (for older reports it applies the same rules). Only reads. On the PC,
  `python ThemePackCreator/theme_pack.py catalog-from-report <report> catalog/<fw>.json` turns it into a
  catalog and lists what differs from 13.00. The 13.00 console's own report reproduces
  `catalog/13.00.json` exactly.
- The on-screen text is English (US spelling). The Inter font atlases cover ASCII plus the characters in
  `CHARS` of `tools/bake-assets.py` (`… · – × “ ” ‘ ’` and some accented Latin letters); keep new UI text
  inside that set.
- **App update** (System tab, the row before Restore defaults; see "In-app update" below).
- **System tab:** a focusable list (d-pad up/down, X): App backgrounds (X switches between "Without own image" and
  "All apps"), Store address (X opens the keyboard), Firmware report (X saves the report), Restore defaults (X asks
  first). Circle goes back to the Library. The first time System opens on a firmware without a bundled catalog
  (`/app0/sce_sys/prospero-themes/catalog/<fw>.json` missing, firmware known), a "Help support <fw>" question offers
  "Send report" (the same as Firmware report, Save and send) or "Not now" (Circle too); the firmware is kept as
  `report_asked` in `settings.json`, so it is not asked again for it.
- **Store address** (System tab, Store address row): X opens an on-screen keyboard that the app draws itself, so no system
  keyboard library is needed. The d-pad moves over the keys, X types the focused key (the bottom row has
  `http://`, `https://`, `:8080`, "Default" for the public store and "Clear all"), Square deletes the last character, Options saves
  and Circle discards. L1/R1 do nothing while typing. The address must start with `http://` or `https://`,
  contain a host, no spaces and at most 200 characters; trailing slashes are dropped. An unusable address
  keeps the keyboard open with the reason. Saving writes `store_url` in
  `/data/prospero-themes/settings.json` and clears the Themes and Avatars lists, which load from the new store on the
  next visit. An empty address removes the store. Editing `store_url` in the file still works.
  Without a `store_url` in the file (a first start writes it), the app uses the public store
  `https://themes.darkatek7.com`.
- **Home background of the app's own tile:** the app ships no `pic0` of its own, so the shell shows the
  theme's background behind its tile (`bg_hub_default`), like every other app without its own image. A
  per-focus rotation (the BgRotator payload) existed until 2026-10-17 and was removed: it only served this
  one tile.
- **Community HTTP** goes through `Library/StoreHttpClient.cs`, over the SDK's libSceHttp and libSceSsl imports,
  which are linked as need entries (`sceKernelDlsym` finds no export in an app, so they are not resolved at run
  time; the module service only has to load them before the first call). They were once blamed for the app not
  starting on FW 13.00; the cause was a `libc.prx` in the PS4 container (see below), and the app starts with them
  (2026-10-15). `build.ps1` checks the need entries of `eboot.bin` with `tools/check-needed-modules.ps1`: it stops
  for a carried module that is not in the PS5 container and reports every need entry that no hardware-tested build
  has had yet.
- **"In library" badge** (Themes tab; the Avatars tab has the same badge, see "Avatars"): a store theme whose pack is in the library carries a badge on its
  card. The library names packs `<sha256>.p5theme`; the app records the hash of every download in
  `/data/prospero-themes/community.json` (store id → hash), and uses the store's `sha256` per item instead
  when the list carries one. A pack removed from the library loses its badge. The ids of the themes this console
  liked are kept next to `community.json`, in `/data/prospero-themes/likes.json` (see Likes below).
- **Theme updates** (Themes tab): cards show the store's version. When the store lists a hash that is not in
  the library while the pack recorded in `community.json` is, the card carries an orange "Update" badge and the footer and
  the question (X) say Update. The download moves the old pack to `/data/prospero-themes/attic/` (kept, not deleted); an
  applied theme changes on the console only after it is applied again.
- **Themes controls** (Themes tab): the d-pad moves between the cards, X asks what to do with the theme (see below),
  Square previews it, Triangle likes, Options refreshes the list, Circle goes back to the Library. The footer lists
  Download (Update), Preview, Like (Unlike) and Refresh. The sort (Top rated, Most downloaded, New) is Square on the chips
  (and with no cards, when there is nothing to preview); its hint sits beside the pills only while the chips have the focus.
- **Download question** (X on a card): "Download" / "Download and apply" / "Cancel" ("Update" / "Update and apply" for a
  card with a newer version). Focus starts on "Download", so a plain download is X, X. The X that opens the question never
  answers it; Circle or Cancel closes it. A download is not a console change, so the question also works while card pictures
  load (the store queue runs it afterwards). A list that arrives under the question closes it, because the card under the
  cursor may be another theme. "Download and apply" runs the same download job, and the library scan that follows
  (`_applyAfterScan`, the downloaded pack's hash) selects the pack named after that hash and goes to its Detail, where the
  Apply question opens on the next frame exactly as X on the Detail opens it (`ThemeBrowser.ApplyAfterDownload` and the
  shared `AskApply`): the parts to tick, the app background scope question, or the plain question. Nothing is applied before
  that question is confirmed, and none opens while the app is busy. A failed download never goes on; if the user has left the
  Themes tab (or another question is open) when the scan ends, the download notice is all there is.
- **Preview before download** (Square on a card): fetches the pack without keeping it and opens the same fullscreen preview the
  library has (home row with the pack's tile icons, background following the focus, control centre, UI sounds, Square for the
  music, Circle back to the card). The pack is downloaded with `GetWithRetry` on the store queue (so card pictures stop for it
  like for a download), checked like a download (`ThemeLibraryEntry.Inspect`, 48 MiB at most) and held in memory only:
  `PreviewController.ShowStorePack` keeps the bytes (the worker captures its own reference) and lets them go when the preview
  closes. Nothing is written to `/data`, the library or `community.json`, and the library's reader is never used for it. A
  failure is a notice ("Preview failed (404)."), and a pack that arrives after the user left the tab is dropped. The request
  carries `?preview=1`, which the store does not count as a download (`web/server`; an older store ignores the flag).
  While a store preview is open the library's size does not matter (`ThemeBrowser.PreviewOfStore`).
- **Category filter** (Themes tab): a row of chips to the right of the sort pills, "All" and the store's twelve
  categories (Anime, Games, Movies & TV, Cartoon, Nature, Space, Fantasy, Sci-Fi, Pixel art, Minimal, Abstract,
  Seasonal). D-pad up from the cards' top row focuses the row (with no cards, Up and Down toggle between it and the
  cards, so another category can always be picked); Left and Right move between the chips, X chooses the focused chip
  (the list reloads with `?category=<id>`, the cursor goes back to the first card), Down returns to the cards. Square
  (sort) works on the chips only (on a card Square previews it), Options (refresh) from either row; Triangle, X and Square-as-preview keep their meaning on the cards only. The chosen
  chip is white, the focused one carries the focus ring, and the row scrolls to keep the focused (else the chosen) chip
  in view. The count line names what the list on screen was loaded with ("24 themes · Anime · Top rated"), and an
  empty filter says "No themes in this category yet". A card lists its categories as small capsules along the bottom of
  its picture. A press while the app is busy moves the chip but starts no load; the list is loaded as soon as the job
  ends (the same rule as the sort). The category ids and labels in `CommunityCategories` (`Library/CommunityList.cs`)
  must match `web/shared/src/categories.ts`.
- **AI filter** (Themes tab and System tab): the chip row starts with Search, No AI and All. X on No AI turns the
  filter on or off; while it is on, the list loads with `&ai=hide`, so the store leaves out every theme it counts as
  AI-generated (the author said so, the automatic check found it, or a moderator decided). The System tab's "AI
  themes" row (Show / Hide) is the same switch; both write `hide_ai` (`on` / `off`, off at first) in
  `/data/prospero-themes/settings.json`. A card of an AI theme carries an "AI" badge (next to the heart when liked).
- **Likes** (Themes tab): Triangle likes the selected theme. On a theme this console already liked, Triangle removes
  the like (the footer says "Unlike" there). A liked theme shows a heart on its card. The request is a POST to
  the store's `/api/themes/<id>/like` (a DELETE for an unlike), with the console's device ID in an `X-Device-Id`
  header, so the store counts one like per console. The heart and the footer follow the store's answer; a refused
  request leaves the card unchanged and says why. The liked ids are written to `/data/prospero-themes/likes.json` (a
  sorted JSON array) after each confirmed change, and read back at start, so the hearts survive a restart. If that
  file cannot be written, the hearts last until the app quits.
- The bundled **Blue Hour** theme is a real format-2 pack carrying a ready-to-write BC7/zstd background
  for `bg_hub_default`, plus its PNG cover. It is not marked active merely because it is bundled.
- USB import checks mounted `/mnt/usb0` through `/mnt/usb7`, each root and its `prospero-themes` folder. `.p5avatar` files
  go the same way into the avatars (see "Avatars").
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

## The preview

The full-screen preview shows a theme the way the console would, using the pieces of the pack:

- **Home row.** The system tiles (Store, Plus, Game Library, App Library, Welcome in Games; Discover, Music Home, All
  Apps, Live TV in Media) with the pack's own 512x512 PNG tile icons (`rnps/...` targets) scaled down once; a tile the
  pack does not dress is drawn as a neutral stand-in. After them come the apps the pack gives a background of its
  own (`previews.apps`, and `apps/<TID>/pic0.dds` or `appmeta/<TID>/pic0.dds` targets; their tile shows the app's
  picture cropped square; at most 12) and a few neutral drawn demo apps. The focused tile's name is under it. The
  row slides (eased) when the focus moves and scrolls when it is longer than the screen.
- **Background follows focus**, as on the console: a focused app with its own picture (`previews.apps[TID]`) shows
  it, otherwise an app (not a system tile) shows the all-apps picture (`previews.allApps`), otherwise the theme's
  cover (`preview`). A pack without previews shows its cover behind everything. A picture change cross-fades in six
  steps over a quarter of a second (`FadeSteps` and `FadeSeconds` in `ThemeRenderer.Preview.cs`).
- **Control centre** (Triangle): Home, Switcher, Notifications, Game Base, Music, Downloads, Share Screen, Sound,
  Mic, Accessories, Profile and Power, with the pack's 128x128 PNG renders (`previews.icons[slot]`, white on
  transparent; the app cannot draw SVG) or a neutral drawn stand-in where the pack has none.
- **Sounds.** Moving the focus plays the pack's focus-move clip (`vag` target with `resource` `psfx_focus_move`),
  X the confirm clip (`psfx_enter`), Circle the back clip (`psfx_cancel`), and the end of a row `psfx_cannot_move`;
  a clip the pack lacks stays silent. Square plays `previews.music` (an MP3 excerpt) in a loop.

How it is made cheap enough for the console: the pack is read on a worker when the preview opens (only the files
`Library/PreviewModel.cs` names; a pack's big backgrounds are never inflated); tile art and icons are scaled once;
a picture is decoded and scaled to the screen once, on a worker, together with the two washed bands of the overlay
(`PreviewPicture`); the cover and four more are kept (the least recently used of the four goes first). A frame at rest is three copies of those
plus the row's tiles and text; the cross-fade mixes the two pictures into one buffer per step and the frames
between steps copy it. Everything is released when the preview closes. `ThemeApp.Preview --bench` prints the cost
of each of these states.

**Sound** is `Library/ConsolePreviewAudio.cs`: the SDK's `AudioOutDevice` (libSceAudioOut) fed by two
`AudioMixer`s (clips, music) on a thread of its own, the MP3 decoded by `AudioDecoder` (libSceAudiodec, whose module
is loaded with `SystemModule.Load(SystemModuleId.AudioDec)` first) in one go to 48 kHz PCM. The decoder does not
report an MP3's rate or channel count, so `Library/Mp3Frames.cs` reads them from the first frame header. The output
port is opened by the audio thread when the first sound is wanted and closed when the preview closes, after the clips
that are still playing (the back clip of leaving) have ended. Any failure leaves the preview silent. **Both libraries
are static imports, so they are new need entries of the app** (like libSceHttp and libSceSsl they are linked, not
resolved at run time: `sceKernelDlsym` finds no export in an app). They are not in the tested list of
`tools/check-needed-modules.ps1`, so the build warns about them until a console has started the app with them.
If the app stops starting, create `NullPreviewAudio` instead of `ConsolePreviewAudio` in `Program.cs`: the class is
then never used, and the trimmed build has no audio need entries.

## Avatars

Avatars are profile pictures for the account that starts the app. They are kept apart from themes: a `.p5avatar` never
carries a theme file, and a theme never carries an avatar.

- **Pack** (`Shared/AvatarPack.cs`): a ZIP with `avatar.json` (format, id, name, author, version, and the size and SHA-256
  of each file), the four DXT5 pictures `avatar440.dds`, `avatar260.dds`, `avatar128.dds`, `avatar64.dds` and `preview.jpg`
  for the tiles. The reader checks the DDS headers, the sizes and the hashes, and nothing else in the pack is used.
- **Library** (`Library/AvatarLibrary.cs`): packs live in `/data/prospero-themes/avatars/`. The Library's second row,
  "Your avatars", is read with every library scan, with "Console picture" first. Down from the themes reaches the row;
  Left and Right move along it; Up or Circle goes back. X on an avatar asks "Set as profile picture"; X on Console picture
  asks "Use console picture" (focus on Cancel); Triangle asks "Remove avatar" (focus on Cancel) and moves the pack to
  `/data/prospero-themes/attic/`. Options reloads the library. The avatar in use is marked "In use".
- **Avatars tab** (`Library/AvatarStore.cs`, `ThemeRenderer.AvatarStore.cs`): the store's list, `GET /api/avatars` with the
  sort, page and page size, in six columns. The list and its pictures load in a job of their own, so the Themes tab's list
  is not touched. Square cycles the sort (Top rated, Most downloaded, New); Options loads the list again. X asks "Download" (focus),
  "Download and set" or "Cancel"; a download goes through `AvatarLibrary.Add`, which checks it like an import, and
  "Download and set" sets the picture once the pack is in the library. Triangle likes or unlikes: POST or DELETE on
  `/api/avatars/<id>/like` with the console's `X-Device-Id`, and the liked ids are kept in
  `/data/prospero-themes/avatar-likes.json`. Cards of packs already in the library carry "In library".
- **Setting a picture** (`Library/AvatarApplier.cs`): the user's profile folder is found through ThemeHelper (`LIST`, the
  `0x<ID>` folder under `/system_data/priv/cache/profile/`), and the user is the one who started the app. The first apply
  copies the files it will replace to `/data/prospero-themes/avatar-backup/0x<ID>/` with a `state.json`; the four
  `avatar*.dds` are then written through ThemeHelper (`.tmp`, then renamed), and `online.json` (`{}`) is created only when
  it is missing, so a PSN user's name stays. Measured on 13.00: the home screen shows the four pictures once `online.json`
  is there, after a restart of the home screen (the app offers it; under OnionHEN only "Later"). Reset works per file:
  a file the backup holds goes back, a file that was not there before is deleted, and an `online.json` the app created
  is removed; an `online.json` the app did not create is left alone.

## PC preview

```powershell
dotnet run --project ThemeApp.Preview/ThemeApp.Preview.csproj -p:SharpProsperoRoot=$env:SHARPPROSPERO_ROOT
```

Open `http://127.0.0.1:5185`. Arrow keys navigate, Enter is X, Escape is Circle, V is Square, Q/E are
L1/R1, T is Triangle (like; the control centre in the preview) and O is Options. This host invokes the same
`ThemeBrowser`, `PreviewController` and `ThemeRenderer` as the PS5; only file/image decoding (it has a small JPEG
decoder of its own, since the SDK's PC texture library reads PNG only), the sound (none) and input transport differ.
The last pack in its library is made in code (`PreviewDemoPack.cs`): tile icons, pictures, music and sounds for the
preview to show; it is reached with End in the Library. `--screenshots` renders every screen (Library, Detail, Preview, Artwork, the demo pack's preview in each of its
scenes, Themes, Avatars, Import, System and the store-address keyboard) and the new states (the Apply, scope and Restore dialogs, each System row focused, the
three kinds of toast, the Applied badge) to `ThemeApp.Preview/out/`. The Themes shots (`community-*` file names) include the preview of a store card
(`community-preview*`), the Download and Update questions (`dialog-community-download`, `dialog-community-update`) and the apply
question that "Download and apply" leads to (`dialog-community-download-apply`); the PC preview treats a card's pack as the library
pack it was made from, so nothing is fetched or written there. The avatar shots (`library-avatars-*`, `avatars-tab*`,
`dialog-avatar-*`) cover the Library's avatar row and the Avatars tab with its questions.

## Home screen restart

New system icons, tile icons and UI sounds show once the home screen (`SceShellUI`) restarts. After an apply or restore
that wrote one of them the app asks "Show the new look now?" (Show now, Later). Show now has ThemeHelper end the home
screen and watch it for 30 seconds; when it keeps crashing, the helper puts the backups back (`ThemeHelper/README.md`).

**Under OnionHEN the app does not restart the home screen.** OnionHEN puts its Toolbox into the new home screen about 14 s
after a restart, and in 1 of 3 tries on 13.00 (2026-10-25) that failed and the home screen crashed (`CE-108262-9`; the system
recovered by itself). The app asks the helper (`HEN`, answered from `/system_tmp/onionhen/ready`; the app's sandbox cannot see
`/system_tmp`) after a job that wrote icons or sounds. When OnionHEN runs, the question has the option Later only
(`DialogKind.RestartShellBlocked`) and says the changes show after the next restart of the console. The client refuses
`RESTARTSHELL` under OnionHEN as well. An answer from the helper is kept for the session; a helper that does not answer, or
an older one without the command, leaves the question as it was. Only OnionHEN changes the behaviour (etaHEN is not detected).

## In-app update

The System tab's **App update** row (ten rows now: 44 px high with 4 px gaps, the same 476 px that nine of 48 took, so the
info panel still fits above the footer) shows what `AppUpdate.State` says: None (X asks the store again and answers with a
notice), Notice (a later release without a ZIP: "update it where you installed it from"), Ready (the version in the accent
colour; X opens the "Update Prospero Themes?" question, "Update now" / "Cancel", focus on Update now; busy apps answer "Please
wait"), Installed (this session replaced the files; "Restart"). The start-up notice says "System, App update installs it" when
the store's answer (`GET /api/app/latest`) carries `zip: {url, bytes, sha256}`; `AppUpdate.Parse` accepts a ZIP only with a
size up to 64 MiB, 64 hex digits and a store path under `/api/app/download/` (no `..`, `//`, `?`, backslash or other host).

"Update now" runs as a job (`StartHelperWork`, result held on screen) in this order, all in `Library/AppInstall.cs`:

1. **Where.** ShadowMountPlus first: `POST http://127.0.0.1:10101/api/v1/games/info` with `{"title_id":"PPSA90210"}` over a plain
   loopback socket (`ConsoleInstallHost`, 2 s receive timeout, `Connection: close`). The answer's `path` counts only for
   `source_type` "folder", not image backed, `source_available` true and a valid path (absolute, normalised, ends in
   `/PPSA90210`); its `version` is the registration's and can be stale, so it is not read. An image gives "Installed as a disk
   image: update it by hand." Without a usable answer the scan roots (`AppInstall.ScanRoots`: `/data/homebrew`,
   `/data/etaHEN/games`, `/mnt/ext0..1`, `/mnt/usb0..7`, bare and with `/homebrew` or `/etaHEN/games`) are probed for
   `<root>/PPSA90210/sce_sys/param.json`; a candidate counts when its `titleId` is PPSA90210 and its `contentVersion` is the one
   the app started with (`/app0/sce_sys/param.json` read at start). None or two matches refuse with the places named.
2. **Download and check** (`AppInstall.Open`): byte count and SHA-256 (the SDK's managed `Sha256`) against `zip`, then the SDK's
   `ZipArchive`. An entry must lie under `PPSA90210/`, with no `..`, backslash, space, control or non-ASCII character, and
   pass the helper's own path rules (`HelperPaths.IsInstallRelative`); folder entries are skipped; at most 256 entries, 64 MiB per
   file, 160 MiB in all; `eboot.bin` and `sce_sys/param.json` must be there, the param.json naming PPSA90210 at the announced
   `contentVersion`. Nothing is written before all of this passes.
3. **Write** (`AppInstall.Write`): everything but `eboot.bin` and `sce_sys/param.json` first (by name), then `eboot.bin`, then
   `param.json`. Every file goes to a new `<file>.tmp`, is renamed onto the file and read back (SHA-256); a file identical to what
   is there is skipped (which also tells whether `ThemeHelper.elf` changed: then the message adds "restart the console once as
   well"). Files in the folder that the ZIP does not name stay. Afterwards the app asks ShadowMount to scan
   (`POST /api/v1/scan`, failures ignored) so the home screen's version refreshes; untested on the console, as is the refresh of
   the console's own copy of `sce_sys` (`/user/appmeta/PPSA90210`).

**Why every write goes through ThemeHelper (v9).** The console refuses to start an app whose `eboot.bin` or modules are not
executable: another app's updater wrote files with mode 0644 and got "Can't start the game or app" (errno 13). The SDK's file
API creates files with mode 0666 (less the umask), so the helper creates each file in the install folder with mode 0777 and then
sets it with the raw `chmod` syscall (`HelperPaths.ModeAfterWrite`). A filesystem that refuses chmod (an exFAT drive, untested)
is accepted when the file has its owner execute bit anyway; otherwise the answer is `WRITE chmod=<error>` and the app does not
rename the file. Local writes by the app are not used, so there is one tested path; the app's sandbox may also not reach `/data`
at all (OnionHEN). A helper from before v9 answers `ERR badpath` for these paths, and a
different build holding the port is caught by the build tag: both give "Restart the console once, then try the update again."
After a successful update further helper jobs are refused until the app is restarted, because `/app0` is a live view of the
install folder and a helper started now would come from the new ELF.

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

A deferred scan (`scan_deferred`, reason `game_active` or `runtime_prepared`) leaves the app mounted from the folder
that was published at the last scan that ran, which the next deploy has renamed into
`/data/prospero-themes/app-backup/`: the console then keeps starting that older build (seen 2026-10-25, the deferral held
even with the app closed). Copying the new `eboot.bin` into that folder, with the app closed, ran the new build.

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
| `Library/Preview*.cs`, `Ui/Preview*.cs`, `Library/Mp3Frames.cs`, `Library/ImageHeader.cs` | The preview: its model, the pack's files, the controller and its worker, the visuals. |
| `Library/ConsolePreviewAudio.cs` | The preview's sound; the only file that links the audio libraries. |
| `ThemeApp.csproj` | Application-module project; imports the SDK's `Prospero.App.props/targets`. |
| `sce_sys/param.json` | Identity and the Game classification. |
| `sce_sys/icon0.png` | 512×512 source icon; the build converts it to `icon0.dds`. |
| `build.ps1` | Wraps the SDK's `build-app.ps1`, locates `libc.prx`, builds ThemeHelper, stages the assets. |
| `package-release.ps1` | Release ZIP (`PPSA90210/` at the top) and the homebrew catalog record. |
| `deploy-shadowmount.py` | Uploads the app folder to `/data/homebrew/PPSA90210` over FTP and asks ShadowMount to register it. |
| `third_party/modules/` | Gitignored. Where `libc.prx` goes. |
