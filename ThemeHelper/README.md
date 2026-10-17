# ThemeHelper

A restricted privileged helper payload for Prospero Themes. It is a narrowed copy of DevProbe, the author's
research payload (not part of the public repository), with the same low-level idioms and the same
`/system_ex` remount.

## Surface

- **Listener:** `127.0.0.1` only, port `9071`. The socket is created directly in `Program.cs`, because
  `PayloadNetwork.Listen` binds every address.
- **Single instance:** no `SO_REUSEADDR`; a second copy fails to bind and exits.
- **Paths:** every path must lie inside `/system_ex/vsh_asset/`, `/user/appmeta/`, `/mnt/rnps/apps/`, or be exactly `/user/app/<TID>/sce_sys/pic0.dds` (the `AssetRoots`
  mounts) and must not contain `..`, `//` or a backslash. Over-long paths are refused, never truncated.
  Anything else gets `ERR badpath`.
- **Commands:**
  - `PING` returns `OK PONG 9071 v1`.
  - `REMOUNT` remounts `/system_ex` read-write (exfatfs, same options as DevProbe). `EBUSY` counts as success.
  - `STAT <path>` returns `OK size=<n>`.
  - `READ <path>` returns `OK <size>` then the raw bytes, streamed in 32 KiB chunks.
  - `WRITE <path>.tmp <len>` followed by `<len>` raw bytes (at most 64 MiB). Only `.tmp` paths are
    accepted. Streamed in 32 KiB chunks. Replies `WRITE wrote=<n>`.
  - `RENAME <path>.tmp <path>`: only this exact shape, so the helper can replace an asset atomically
    and nothing else.
  - `LIST /user/app/` or `LIST /user/appmeta/` (no other folder) returns `OK`, one `D <name>` per sub-folder, then `END`.
  - `QUIT` closes the connection.
- **Not included:** `LIST`, `MKDIR`, `RMDIR`, `UNLINK`, `SOTEST` and the remaining DevProbe diagnostics.

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
- Built; not yet run on hardware.
