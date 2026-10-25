# ThemePackCreator

Builds PS5 theme packs (format 2) from source images, and captures the stock asset catalog.

All encoding happens here, on the PC, because the console cannot do it: no BC7 encoder in the SDK, no
zstd compressor a payload can reach, and a hard 256 MiB heap on the app side. A pack therefore ships
**ready-to-write bytes**, and the applier is a copy with backup and verification rather than a renderer.

## The pipeline

Per target, using nothing that has not been verified on real hardware.

**Backgrounds** (`bg_*.dds.zst`) — a Zstandard frame wrapping a 3840×2160 DDS:

```
source image
  -> resized to 3840x2160
  -> encoded in the target's OWN declared format        (texconv: BC7_UNORM or BC1_UNORM)
  -> the stock asset's own DDS header, so only pixels differ
  -> zstd-framed exactly like the system's own wallpapers
  -> recorded in theme.json with its size and SHA-256
```

**PNG assets** (glyphs, trophy plates, activity-card art, UI banners) — plain PNGs, so no encoding at all.
This is the cheapest layer to theme:

```
source image
  -> resized to the STOCK asset's own dimensions
  -> converted to the STOCK asset's own colour mode      (dropping alpha where the shell composites none)
  -> saved as PNG and shipped as-is
  -> recorded in theme.json with its size and SHA-256
```

Measured dimensions on 13.00, which replacements are matched to: 12 of the 18 PNGs are the **864×1040**
activity-card/trophy template, `function_button_circle.png` is a **120×120** glyph sprite,
`function_button_circle_edge.png` a **6×12** tile, `ScreenShare.png` a full **3840×2160** banner, and
`StoryProgress.png` **740×284**.

**Video assets** (`.mp4`) — re-encoded to match the target exactly, because a mismatch is a clip that will
not play:

```
source video
  -> ffprobe the stock clip for codec, tag, profile, dimensions, frame rate, pixel format, audio
  -> re-encode with ffmpeg to every one of those      (libx265 or libx264, tagged hvc1 or hev1)
  -> -movflags +faststart, so moov precedes mdat as the console's own clips do
  -> recorded in theme.json with its size and SHA-256
```

Measured: `psbutton_press.mp4` HEVC/`hev1` 1000×1000@59.94, `initial_setup_controller.mp4` HEVC/`hvc1`
3840×2160@30, `gen2vr_initial_setup_movie.mp4` HEVC/`hvc1` 1920×1080@59.94, and `A11YStickSetting.mp4`
H.264/`avc1` 1392×784@60 **with an AAC track** — the only one carrying audio, which is why audio presence
is matched rather than always kept or always dropped.

**Audio assets** (`.at9`) — ATRAC9, for which Sony's own tool is the only encoder:

```
source audio
  -> ffprobe the stock clip for sample rate, channel count and bitrate
  -> ffmpeg converts the source to WAV at those parameters   (at9tool takes WAV only)
  -> Sony's at9tool -e -br <kbps> -fs <hz>                    (ps4_at9tool.exe)
  -> recorded in theme.json with its size and SHA-256
```

Measured: every one of the seven is ATRAC9, **48 kHz, 6 channels (5.1)**, at 240 or 300 kbps. Six channels
is the constraint that bites — at9tool refuses a channel count its bitrate does not allow, so the source is
converted rather than assumed compatible. Our output matches the stock files apart from the RIFF size
field: the ATRAC9 config block is byte-identical.

`ps4_at9tool.exe` ships inside [ATRACTool](https://github.com/XyLe-GBP/ATRACTool-Reloaded)'s portable
release, whose GUI is only a wrapper around it; it lives, gitignored, at `ZstdWrap/external/at9tool/`.

Two details that matter and were learned the hard way:

- **The target's format and header flavour must be respected.** 15 of the 16 backgrounds are BC7_UNORM with
  a 148-byte DX10 header; `bg_image_load_error` is BC1_UNORM with a 128-byte legacy header. The two differ
  by 20 bytes over an identical pixel payload, so pixels are compared by *pixel* length and the stock
  header is kept verbatim.
- **The stock asset is needed as a header donor.** It is also what makes `stock_sha256` possible, which is
  how an applier can tell "still stock" from "already changed by something else". Capture the originals
  first (for example over FTP from the paths in `docs/13.00-screen-assets.md`) and keep them in `backups/`.

## Commands

```bash
python -m pip install zstandard pillow     # once
# texconv.exe from https://github.com/microsoft/DirectXTex/releases into ZstdWrap/external/
#   needed only for background targets; the PNG layer needs no encoder
# ffmpeg + ffprobe on PATH                  (e.g. scoop install ffmpeg)
#   needed only for video targets

# see every themable target: 16 backgrounds, 2 glyphs, 12 icons, 4 UI, 4 videos
python theme_pack.py list

# capture the measured catalog for a firmware (a product asset, not just docs)
python theme_pack.py catalog ../backups ../catalog/13.00.json --firmware 13.00

# build a pack: TARGET=IMAGE, where TARGET is an asset stem, a title id, a screen label or a file name
python theme_pack.py create \
    --stock ../backups --out ./midnight.p5theme \
    --id midnight-neon --name "Midnight Neon" --author "Your Name" --version 1.0.0 \
    --description "Deep blue hub, neon store, custom glyphs" \
    --preview ./cover.png \
    bg_hub_default=hub.png medium=store.png NPXS40075=gallery.png \
    function_button_circle=glyph.png TrophyUAM_Gold=trophy.png

# inspect and check
python theme_pack.py inspect ./midnight.p5theme
python theme_pack.py verify  ./midnight.p5theme --stock ../backups
```

The stock layout expected under `--stock`: backgrounds as `<stock>/<name>.dds.zst`, PNG assets under
`<stock>/png/`.

`create` accepts `bg_hub_default`, `hub_default`, `NPXS40047` or `ps_store` for the same target; the
mapping is in `BACKGROUNDS` at the top of the script and is annotated with which labels are **confirmed**
by observation on 13.00 versus predicted from the title-id names.

## Website previews

A browser can show neither a BC7 background nor ATRAC9 music, so `create` also writes what the store website
shows instead, and lists it in `theme.json` under `previews`. They are never targets and nothing of them is written to the console.

- **A per-app background** (`appmeta/<TID>/pic0.dds=image`) gets `previews/apps/<TID>.jpg`: the source image fitted
  into 1280×720 as a JPEG (Pillow), at the best quality that stays under 300 KB.
- **The home music** (`bgm_home=audio`) gets `previews/music.mp3`: the first minute as a 128 kbps MP3 (ffmpeg, under
  2 MiB). Without ffmpeg on `PATH` the pack is built without it.
- **A system icon** (`svg:<slot>=file.svg`, e.g. `svg:iconid_search=search.svg`) gets `previews/icons/<slot>.png`: a
  128×128 PNG render, white on transparent, for the console app, which has no SVG renderer. It needs `cairosvg`
  (`pip install cairosvg`) or `resvg` or `rsvg-convert` on `PATH`; without one the pack is built without it and a note
  says so.
- `--no-previews` leaves all of them out.

There is no all-apps target in this tool (`allapps/pic0.dds` comes from the web creator), so no `allApps` preview.
`inspect` lists the previews and `verify` warns about one that is missing, too large or not a JPEG, PNG or MP3; a bad
preview never makes a pack invalid.

## Applying a pack

Copy the `.p5theme` to a USB drive (its root or a `prospero-themes` folder), import it in Prospero Themes on
the USB Import tab and apply it from the Library. The app validates the pack, backs up the originals on the
console and can restore them at any time (System → Restore defaults). Packs can also be published to the
community store and downloaded on the Themes tab.

## Notes

- `verify` here is the quick local structural check. The authoritative validator is the C# library in
  `../Shared`, and `../Shared.Tests` validates packs produced by this script — two implementations of one
  format drift unless something checks them against each other. Both a background pack and a PNG-layer pack
  are committed there as fixtures.
- **A brand-new layer needs no engine change.** The PNG layer was added by extending this script's target
  table and nothing else: `Shared/`, the validator and the applier were untouched, and the applier applied
  it on the first try. That is the format being target-driven rather than feature-driven, and it is why the
  remaining layers (sounds, video, GNF, RCO) are additive work.
- `bg_image_load_error` has a legacy 128-byte header and a BC1 payload; it is the useful test case for the
  format-flexible path.
- All 16 backgrounds are 3840×2160, so one generated image size serves every screen. PNG dimensions differ
  per asset and are read from the stock file.
