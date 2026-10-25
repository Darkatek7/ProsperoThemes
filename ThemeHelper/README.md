# ThemeHelper

A restricted privileged helper payload for Prospero Themes. It is a narrowed copy of DevProbe, the author's
research payload (not part of the public repository), with the same low-level idioms and the same
`/system_ex` remount.

## Surface

- **Listener:** `127.0.0.1` only, port `9071`. The socket is created directly in `Program.cs`, because
  `PayloadNetwork.Listen` binds every address.
- **Single instance:** no `SO_REUSEADDR`; a second copy fails to bind and exits.
- **Paths:** every path must lie inside `/system_ex/vsh_asset/`, `/user/appmeta/`, `/mnt/rnps/apps/`, or be exactly `/user/app/<TID>/sce_sys/pic0.dds` (the `AssetRoots`
  mounts) or a tile icon `/system_ex/rnps/apps/…/icon0.png` (v7, for firmware without the `/mnt/rnps` copies), or inside the app's own folder `/data/prospero-themes/`, and must not contain `..`, `//` or a
  backslash. Over-long paths are refused, never truncated. Anything else gets `ERR badpath`.
- **The app's folder (v6):** some jailbreaks start the app without `/data` in its sandbox (10.01 with OnionHEN,
  ProsperoThemes issue #7). The app then keeps its library, backups and settings through the helper
  (`AppData` in `ThemeApp/Library/DeviceFiles.cs`). Inside `/data/prospero-themes/` only, `WRITE` and
  `RENAME` take any name, and `MKDIR`, `UNLINK` and `LIST` with files are allowed.
- **The app's install folder (v9):** the app updates itself in place. Inside `<root>/PPSA90210`, where `<root>` is exactly one
  of ShadowMountPlus's scan roots (`/data/homebrew`, `/data/etaHEN/games`, `/mnt/ext0` and `/mnt/ext1`, `/mnt/usb0` to
  `/mnt/usb7`, each bare or followed by `/homebrew` or `/etaHEN/games`), `STAT`, `READ`, `LIST` and `MKDIR` work, `WRITE` only
  creates `<file>.tmp` (at most 160 MiB) and `RENAME` only moves `<file>.tmp` onto `<file>`. Names may use letters, digits and
  `. _ -` only; no `..`, `//`, `.` segment, backslash, space or path of 256 bytes or more. After a write the helper sets mode
  **0777** on the file (created with that mode, then the raw `chmod` syscall; the console does not start an app whose
  `eboot.bin` is not executable). A failed chmod is accepted when the file has its owner execute bit anyway (a filesystem
  without chmod); otherwise the answer is `WRITE chmod=<error>` instead of `WRITE wrote=<n>`.
  The rules live in `Shared/HelperPaths.cs`, which the helper, the app and `Shared.Tests` compile.
- **Avatars (v9):** inside `/system_data/priv/cache/profile/0x<8 hex digits, either case>/` only the files `avatar64.dds`,
  `avatar128.dds`, `avatar260.dds`, `avatar440.dds`, `picture64.dds`, `picture128.dds`, `picture260.dds`, `picture440.dds`,
  `avatar.png`, `picture.png` and `online.json` (and their `.tmp`; never `version.json`): `STAT`, `READ`, `LIST`, `WRITE` of
  the `.tmp` (at most 1 MiB), `RENAME` and `DELETE`. `LIST /system_data/priv/cache/profile` (the folder of all profiles)
  answers only `D <name>` lines, so the app can find the `0x...` folder. Measured on 13.00 with an offline user: writing that
  set and restarting `SceShellUI` changes the profile picture; deleting it restores the default. The app feature comes later;
  the helper ships the capability now so users restart the console once.
- **Commands:**
  - `PING` returns `OK PONG 9071 v<tag>`, the build tag (`BuildTag` in `Program.cs`, `ExpectedTag` in the app).
  - `REMOUNT` remounts `/system_ex` read-write (exfatfs, same options as DevProbe). `EBUSY` counts as success.
  - `STAT <path>` returns `OK size=<n>`.
  - `FREE <path>` (v10; any confined path) returns `OK free=<KiB>`, the space left for an unprivileged writer on the
    filesystem of the path's folder (`statfs`), or `FREE rc=<n>`. The app checks it before each write (issue #13).
  - `READ <path>` returns `OK <size>` then the raw bytes, streamed in 32 KiB chunks.
  - `WRITE <path>.tmp <len>` followed by `<len>` raw bytes (at most 160 MiB: the icon container is about
    100 MB before firmware 13.00). In a theme root only `.tmp` paths are accepted. Streamed in 32 KiB chunks. Replies
    `WRITE wrote=<n>`, or `WRITE rc=<error>` when the file cannot be opened. A refused header (`ERR badpath`)
    or an incomplete write closes the connection, so the body is never read as commands; after an open or
    write failure the rest of the body is read and dropped first, so the client still gets the answer.
  - `RENAME <path>.tmp <path>`: in a theme root only this exact shape, so the helper can replace an asset
    atomically and nothing else. Inside the app's folder any rename within it.
  - `LIST /user/app/` or `LIST /user/appmeta/` returns `OK`, one `D <name>` per sub-folder, then `END`. A
    folder in the app's folder also lists its files as `F <name>`. A folder that cannot be opened answers
    `ERR opendir`.
  - `MKDIR <path>` (the app's folder or a folder in it) replies `MKDIR rc=<n>`; "already exists" is
    `0x80020011` as a signed number.
  - `UNLINK <path>` (a file in the app's folder, or since v10 a `<file>.tmp` anywhere `WRITE` may create one: the
    leftover of a write that failed half-way) replies `UNLINK rc=<n>`.
  - `DELETE <path>` (v9; an avatar file or its `.tmp`, nothing else) replies `DELETE rc=<n>`.
  - `RESTARTSHELL <n>` (v8), then `n` lines `<backup> <target>` (at most 64): ends `SceShellUI`, the home screen,
    and nothing else, so it reads the system icons, tile icons and UI sounds again. Each backup must lie in the app's
    folder and exist, each target in a theme root (not a `.tmp`); one bad pair refuses the whole request and closes
    the connection. Then the helper watches the home screen for 30 seconds. When it starts 3 times or more, or stays
    away for 15 seconds, the helper copies each backup back over its target (`.tmp`, then rename). The watch
    and the copies finish even when the client is gone. Replies `RESTARTSHELL restarts=<n> restored=<k>`, or
    `RESTARTSHELL rc=<error>` when the home screen is not found or cannot be ended.
  - `HEN` (v9, no argument) replies `OK HEN onionhen` when the folder `/system_tmp/onionhen/ready` exists, otherwise
    `OK HEN none`. OnionHEN creates that folder (and `/system_tmp` is cleared at boot), so it means OnionHEN runs now.
    Read-only, one fixed `stat` with the calls the helper already imports. The app cannot see `/system_tmp`, so it asks
    here; it does not restart the home screen under OnionHEN. etaHEN is not reported.
  - `QUIT` closes the connection.
- **Not included:** `RMDIR`, `SOTEST`, a general `KILL` and the remaining DevProbe diagnostics.

## Home screen restart (measured on 13.00, 2026-10-23)

- After `kill -9` the system starts `SceShellUI` again about a second later, as exactly one new process, and it
  then runs steadily. Three kills four seconds apart gave three new processes.
- The new home screen shows the icon container and tile icons written before it started. The theme app keeps
  running in the background; its tile on the Media tab brings it back as it was.
- Repeated crashes make the system stop starting the home screen after about ten. The guard acts at three.
- Tested with three manual kills during the watch: the helper put the backups back, and the app then recorded
  the restore.
- OnionHEN puts its Toolbox into a new `SceShellUI` about 14 s after a restart. Measured on 13.00 (2026-10-25): in 1 of 3
  tries that failed and the new home screen crashed (`CE-108262-9`); the system recovered by itself. So the app asks
  `HEN` and, under OnionHEN, never sends `RESTARTSHELL` (the client refuses it too).
- The process table comes from the raw `__sysctl` syscall, and `kill` and `nanosleep` are raw syscalls too, so v8
  imports nothing new. Every client socket gets `SO_NOSIGPIPE`, because the answer to `RESTARTSHELL` may reach a
  client that went away.

## Notes

- A payload cannot be unloaded. Port `9071` is fixed, so a rebuilt helper cannot take over the port
  from a running instance. Reboot the console between builds, or check `PING` for the build tag.
- Requests are `\n`-terminated ASCII. `ReadLine` consumes exactly one byte past the newline, so binary
  payloads after a `WRITE` header stay intact.
- The payload heap is 16 MB. Files are never buffered whole.
- Build with `SHARPPROSPERO_ROOT` set, as for DevProbe.
- The app starts it on its own (`ThemeApp/Library/ThemeHelperClient.cs`): it pings `127.0.0.1:9071`,
  and only if nothing listens there sends the bundled `sce_sys/prospero-themes/ThemeHelper.elf` to the
  ELF loader on `127.0.0.1:9021`. `ThemeApp/build.ps1` builds and bundles it.
