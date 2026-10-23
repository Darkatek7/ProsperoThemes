# Prospero Themes

A theme manager for the PS5 home screen, as a native homebrew app (title ID `PPSA90210`). Tested on firmware
13.00.

- **Library:** browse your theme packs (`.p5theme`), look at the details (what the theme changes) and a fullscreen
  preview, apply one and restore the original look at any time. The theme the console shows now is marked "Applied"
  and leads the row; Triangle sorts the rest by name or by newest. When you combine parts of several themes, the
  row ends with "Save current look", which keeps that mix as a theme of its own in your library.
- **Community:** download themes other people published at [themes.darkatek7.com](https://themes.darkatek7.com),
  sort them by top rated, most downloaded or new, and like the ones you enjoy. Themes already in your library carry
  an "In library" badge; when a creator publishes a new version, the card says "Update". A theme you liked shows a
  heart, and Triangle on it removes the like. The Search chip finds themes by name, author or description.
- **USB import:** packs on a USB drive (its root or a `prospero-themes` folder) are checked and copied into the
  library.
- **What a theme can change:** the home-screen background, including behind the system tiles (Game Library,
  Media Gallery, All Apps and the rest), the backgrounds behind app tiles (only apps without their own image, or
  all apps), system icons (top bar and control center), the system tile icons, the UI sounds and the home music.
- **Volume:** the System tab sets how loud a theme's UI sounds (25 % to start) and its home music (10 % to start)
  play. The app writes them again at that volume, from 10 % to 100 %.
- **No console restart:** new icons and sounds show once the home screen restarts. After an apply or restore the
  app offers that; it takes a few seconds and the app stays open. If the home screen does not come back steadily,
  the helper puts the previous files back.

## Requirements

- A PS5 with a running jailbreak chain, tested on firmware 13.00.
- [ShadowMountPlus](https://github.com/drakmor/ShadowMountPlus), which registers apps from `/data/homebrew/`.
- An ELF loader on port **9021** (elfldr). The app hands it **ThemeHelper**, a small payload of its own that
  writes the theme files. It listens only on `127.0.0.1` and only writes inside the theme asset folders.
- A network connection for the Community tab.

## Install

Install it from the homebrew catalog ([homebrew.page](https://homebrew.page/), ProsperoStore), or by hand:

1. Download `PPSA90210.zip` from the [latest release](https://github.com/Darkatek7/ProsperoThemes/releases).
2. Extract it and copy the `PPSA90210` folder to `/data/homebrew/` on the console (FTP or USB).
3. ShadowMountPlus registers it, and Prospero Themes appears on the home screen.

Your library, backups and settings live in `/data/prospero-themes/`, so updating the app keeps them. A release with
a new ThemeHelper (01.000.022 is one) needs one console restart after the update, because a running payload cannot
be replaced; the app says so when it finds the old helper.

## Controls

| Screen | Buttons |
|---|---|
| Every screen | L1 / R1 switch tab (Library, Community, USB Import, System). D-pad or left stick moves. Circle goes back to the Library. |
| Library | X opens the details. Square opens the fullscreen preview. Triangle changes the order. Options reloads the library. On the "Save current look" card at the end, X saves the mix. |
| Details | X applies the theme (it asks first). Square opens the preview. Circle goes back. |
| Apply dialog | D-pad chooses, X confirms, Circle cancels. |
| Preview | X shows or hides the home overlay. Square or Circle goes back. |
| Community | X downloads (or updates). Triangle likes, or removes your like. Square changes the sort. Options refreshes the list. D-pad up from the first row reaches the chips: Left and Right move, X filters, Down goes back to the themes. X on Search opens a keyboard; Options searches, and X on the chip with a term shows every theme again. |
| USB Import | X imports from USB. |
| System | D-pad picks a row, X acts on it. On Sound volume and Music volume, Left and Right change the volume. |

Results and progress ("Applying theme…", "Download failed (404).") appear as a short message at the bottom of the
screen.

**Applying asks first.** A theme with an app background asks where that background goes: "Apps without their own
image" or "All apps" (Cancel is the third choice). The answer is also saved as the App backgrounds setting on the
System tab, where X switches between the two. Themes without an app background get "Apply theme" or "Cancel".

**Restore defaults** is the last row on the System tab. X opens a dialog that asks first. Cancel is selected at the
start, so choose "Restore defaults" and press X to go ahead. It puts every changed file back from the backups the
app made before the first change. When a backup is gone, tile icons come back from the firmware's own copy; any
other file the app can no longer find an original for is left as it is and named in the result. Applying a theme starts from the defaults too: whatever an earlier theme changed
and the new one does not cover goes back to its original first, so themes never stack.

## Other firmware versions

Tested on 13.00, but nothing in the app is tied to it: before anything is written, every file a theme
replaces is compared with the console's own copy (same image size and texture format). What does not fit
this console is skipped and listed, never written. After a system update the app notices files the update
replaced and takes those as the new defaults, so Restore defaults never puts an old firmware's file back.
It then offers to apply your theme again.

On another firmware, please send a report: on the System tab, X on the Firmware report row offers "Save" or
"Save and send". It saves `/data/prospero-themes/reports/firmware-<version>.json` (and a copy on a connected USB
drive); "Save and send" also sends it to the store and shows a short code. It only reads, and describes the files
a theme can change (sizes, hashes, formats, the icon list). Open an issue on this repository with the code, or
attach the file; `ThemePackCreator/theme_pack.py catalog-from-report` turns it into a catalog for that firmware.

## Making themes

- In the browser: the creator at [themes.darkatek7.com](https://themes.darkatek7.com) builds a pack from your
  images and can publish it to the community store.
- On the PC: [`ThemePackCreator/`](ThemePackCreator/README.md) builds packs from the command line, including the
  icon, sound and video layers.

The pack format is described in [`Shared/README.md`](Shared/README.md).

## Repository

| Folder | What it is |
|---|---|
| [`ThemeApp/`](ThemeApp/README.md) | The app: UI, library, apply/restore, community store client, build and release scripts. |
| [`ThemeHelper/`](ThemeHelper/README.md) | The file helper payload the app loads through elfldr. |
| [`Shared/`](Shared/README.md) | The `.p5theme` reader and validator, shared by the app and the tests. |
| [`Shared.Tests/`](Shared.Tests/README.md) | PC unit tests. |
| [`ThemeApp.Preview/`](ThemeApp.Preview/README.md) | Runs the app's UI on the PC in a browser window. |
| [`ThemePackCreator/`](ThemePackCreator/README.md), [`ZstdWrap/`](ZstdWrap/README.md) | PC tools that build packs and handle the console's `.dds.zst` textures. |
| `catalog/`, `docs/` | The measured asset catalog and screen list for firmware 13.00. |

Some developer notes in these READMEs mention DevProbe and other research payloads from the author's private
research repository. They are not part of this repository, and neither building nor using the app needs them.

## Building

You need:
- .NET 10, PowerShell 7 and Python 3 (for the PC tools);
- the [SharpProspero](https://github.com/SvenGDK/SharpProspero) SDK, with `SHARPPROSPERO_ROOT` pointing at it.
  Until [SvenGDK/SharpProspero#6](https://github.com/SvenGDK/SharpProspero/pull/6) is merged, use that
  branch: without it the theme helper payload does not start on firmware 13.40 and later;
- a PS5-format `libc.prx` from a game package in `third_party/modules/` (not included here).

```powershell
$env:SHARPPROSPERO_ROOT = '<path to SharpProspero>'
pwsh -File ThemeApp/build.ps1 -Output Folder         # the app folder in ThemeApp/out/module
pwsh -File ThemeApp/package-release.ps1              # dist/PPSA90210.zip and the catalog record
cd Shared.Tests; dotnet test                         # PC tests
```

`python ThemeApp/deploy-shadowmount.py --deploy --ip <console-ip>` uploads a build over FTP and asks
ShadowMountPlus to register it.

## Privacy

The Community tab talks only to the store address set on the System tab (default
`https://themes.darkatek7.com`). Likes are counted once per console through a random ID that the app creates on
first start (`/data/prospero-themes/settings.json`); each like sends that ID to the store. The ID is anonymous and
not linked to your account. The console also keeps its own list of the themes it liked, in
`/data/prospero-themes/likes.json`, so the hearts show again after a restart. That list stays on the console.

On start the app asks the store for the number of the newest release, to say when an update is out; nothing is
sent with that question. A firmware report goes to the store only when you choose "Save and send".

## License

GPL-3.0, like the SharpProspero SDK it is built with. The Inter font is under the SIL Open Font License
(`ThemeApp/assets/fonts/OFL.txt`).

`Shared/Atrac9Codec.cs` and `Shared/Atrac9Tables.cs` (the home music volume) are a C# port of the ATRAC9 encoder by
[Blaze414](https://github.com/Blaze414/ps5-theme), GPL-3.0-or-later, which in turn follows the MIT-licensed
[LibAtrac9](https://github.com/Thealexbarney/LibAtrac9) by Alex Barney.

The release ZIP contains a `libc.prx`, the system library every SharpProspero app has to carry, because the app
does not start without it. That file is Sony's and is not covered by this repository's license.

## Credits

- [SharpProspero](https://github.com/SvenGDK/SharpProspero) by SvenGDK: the SDK this app is built with.
- [ShadowMountPlus](https://github.com/drakmor/ShadowMountPlus) by drakmor: app registration.
- [Inter](https://github.com/rsms/inter) by Rasmus Andersson: the UI font.
