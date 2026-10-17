# ZstdWrap

Tools for the Zstandard frames the PS5 stores its system wallpapers in, and for the DDS content those
frames carry.

This folder holds two implementations on purpose:

| | Language | Role |
|---|---|---|
| `ZstdRawFrame.cs` + `Program.cs` | C# | **Dependency-free frame reader and Raw-block writer.** This is the code that can ship inside the PS5 app later, where no zstd library exists. It parses frames and emits frames built from Raw blocks. No compression. |
| `realzstd.py` | Python | **The PC-side authoring path.** Real compression and decompression via `python-zstandard`. Use this to build frames at a sensible size and to decode any frame, including the console's own. |
| `mkdds.py` | Python | Builds DDS wallpapers: clones a template's header, or rewrites it to a different block format. |
| `inventory.py` | Python | Decodes every `*.dds.zst` in a directory and reports what is really inside: dimensions, format, header flavour, byte cost. The assets are **not** uniform, so this is how the real shape is established rather than assumed. |

## What the console actually stores (firmware 13.00, measured)

Decoding the console's own `bg_hub_default.dds.zst` settled this. It is **not** the plain `bg_*.dds` the
older-firmware prior art writes:

| Field | Value |
|---|---|
| Outer container | Zstandard frame, magic `28 B5 2F FD` |
| Frame header | `0x80` — 4-byte content size, no Single_Segment, no checksum, no dictionary |
| Window | descriptor `0x60` → 4 MiB |
| Declared content size | 8,294,548 bytes |
| DDS magic | `DDS ` |
| `dwSize` / `dwFlags` | 124 / `0x00081007` (CAPS\|HEIGHT\|WIDTH\|PIXELFORMAT\|LINEARSIZE) |
| Dimensions | **3840 × 2160** |
| `dwPitchOrLinearSize` | 8,294,400 → **1 byte per pixel** |
| `dwFourCC` | `DX10` |
| **`dxgiFormat`** | **98 = `DXGI_FORMAT_BC7_UNORM`** |
| `resourceDimension` | 3 (`TEXTURE2D`) |
| Header length | **148 bytes** (124-byte `DDS_HEADER` + 20-byte DX10 extension) |

So the wallpaper is a **4K BC7 block-compressed texture**: 8,294,400 pixels at 1 byte each. That is the
single most consequential fact for the theme manager, because BC7 is expensive to encode — see
"Experiment ladder" below.

**But the assets are not uniform, and assuming one format breaks the others.** `inventory.py` over all
16 assets found 15 of them to be BC7_UNORM with a **148-byte** DX10 header, and
`bg_image_load_error` to be **BC1_UNORM with a traditional 128-byte header** (4,147,328 bytes = 128 +
3840×2160/2). Two consequences:

- **The loader honours the declared format**, since the console itself ships two formats in one
  directory. A replacement is therefore free to use a cheaper format than BC7 — which is what makes
  BC1 (half a byte per pixel, trivial to encode) a realistic option for themes.
- **Header flavour must be kept per asset.** The two flavours differ by 20 bytes with an identical
  pixel payload, so `mkdds.py replace` compares *pixel* lengths and keeps the reference's own header.
  Encoding everything as BC7 produced a pixel-size mismatch, and the tool refused it — the right
  failure.

Full measured inventory: [`../docs/13.00-screen-assets.md`](../docs/13.00-screen-assets.md).

## Usage

```bash
python -m pip install zstandard        # required by realzstd.py

# inspect and decode any frame
python realzstd.py info   backups/bg_hub_default.dds.zst
python realzstd.py decode backups/bg_hub_default.dds.zst out.dds

# build a frame
python realzstd.py encode theme.dds theme.dds.zst --level 19

# build DDS variants
python mkdds.py info    theme.dds
python mkdds.py blank   template.dds out.dds              # header copied, pixels zeroed
python mkdds.py bc1     template.dds out.dds --rgb FF00FF # rewrite to BC1_UNORM, solid colour
python mkdds.py replace reference.dds source.dds out.dds  # header from one, pixels from the other
```

## Producing a theme texture: the full pipeline

This is the working end-to-end path, verified at byte level on firmware 13.00. `texconv` is Microsoft's
DirectXTex tool, downloaded from its [releases](https://github.com/microsoft/DirectXTex/releases) into
`external/` (gitignored — third-party binaries are not redistributed here).

```bash
# 1. a 3840x2160 image, any source
python gen_test_image.py wallpaper.png

# 2. encode to BC7 at the console's exact dimensions
external/texconv.exe -f BC7_UNORM -m 1 -y -o . wallpaper.png

# 3. make it a true drop-in: the console's own 148-byte header, our pixels
#    (texconv's header differs in one byte - it also sets DDSD_MIPMAPCOUNT - and this removes that doubt)
python mkdds.py replace backups-decoded/bg_hub_default.dds wallpaper.dds theme.dds

# 4. frame it, then write it to /system_ex/vsh_asset/<screen>.dds.zst
python realzstd.py encode theme.dds theme.dds.zst --level 19
```

Measured on the gradient test image: a 3840×2160 BC7 texture of 8,294,548 bytes framed down to
**48,971 bytes**, with the resulting header byte-identical to the console's own (0 differing bytes of
148). Earlier reference points on the same console: the original wallpaper frames to 1,869,327 bytes,
and re-framing the same pixels with `realzstd.py` gives 1,693,747.

Regenerate `backups-decoded/bg_hub_default.dds` by decoding `backups/bg_hub_default.dds.zst` (see
Usage) — that decoded file is the header donor and the reference to diff against.

The C# side needs the .NET SDK only:

```powershell
dotnet run --project ZstdWrap -- info <frame>       # header and block list
dotnet run --project ZstdWrap -- wrap <in> <out>    # Raw-block frame, no compression
dotnet run --project ZstdWrap -- selftest <in>      # encode+decode and compare
```

## Fixtures

`fixtures/` holds the two frames used for the hardware experiments. Both are tiny because uniform
pixel data compresses enormously, and both decode to a full 3840×2160 texture.

| Fixture | Frame size | Content | What it tests |
|---|---|---|---|
| `stepC-blank-bc7.dds.zst` | 320 B | Header byte-identical to the console's, pixel data zeroed (BC7 all-zero decodes to transparent black) | Whether the console accepts a frame **we** built and honours the pixels, with the format unchanged |
| `stepD-solid-bc1-magenta.dds.zst` | 418 B | Same dimensions, header rewritten to `dxgiFormat 71 = BC1_UNORM`, solid magenta blocks | Whether the texture loader honours the **DDS header's declared format** instead of insisting on BC7 |
| `sample-theme-bc7-gradient.dds.zst` | 48,971 B | A real 3840×2160 BC7 texture of a generated gradient + text, built by the full pipeline above with the console's own header | The end-to-end product path: an arbitrary chosen image in the console's exact format |

That second question matters commercially: BC1 is half a byte per pixel and trivial to encode, so if it
is honoured then themes can be generated cheaply on-console. If the loader is locked to BC7, then BC7
encoding has to happen on the PC and a pack must ship pre-encoded textures.

## Experiment ladder and how to read it

Change one variable at a time, and note that the shell may only read these assets when it starts — a
reboot or shell reload may be needed before any change is visible.

| Written | Expected if the mechanism works | What a different result means |
|---|---|---|
| the original frame bytes (done) | no visible change, wallpaper renders as always | — (this only proves the write path is byte-exact) |
| our re-compressed frame, original pixels (done) | no visible change | the shell rejects frames we produce, or the asset is not re-read |
| `stepD` magenta BC1 (on the console now) | the hub turns solid magenta | unchanged → format not honoured or asset not re-read; broken/blank → header parsed but format rejected |
| `stepC` blank BC7 | the hub turns black/transparent | unchanged → frames we produce are not taken |

These steps ran with the author's research tools, which are not in the public repository; today the app
restores the originals (System → Restore defaults).

## Notes

- **`external/` holds downloaded third-party tools, gitignored.** `texconv.exe` and `texdiag.exe` from
  Microsoft's [DirectXTex](https://github.com/microsoft/DirectXTex/releases) go there for BC7 encoding, and
  `external/at9tool/ps4_at9tool.exe` (with `msvcr71.dll`) for ATRAC9 encoding. The latter is Sony's codec
  tool, which ships inside [ATRACTool](https://github.com/XyLe-GBP/ATRACTool-Reloaded)'s *portable* release
  — that GUI is only a wrapper around `at3tool.exe`/`at9tool.exe`, and no open-source ATRAC9 encoder exists,
  which is why it matters.
- **7-Zip reads Zstandard but cannot write it.** `7z x file.zst` works (and was used to get the original
  DDS out for inspection), but `7z a -tzstd` fails with `Not implemented`. That is why `realzstd.py`
  exists.
- **The frame's declared content size must be truthful.** The console's frames declare one, and a
  decoder is free to pre-size its output buffer from it.
- The C# reader parses compressed and RLE blocks but can only *decode* Raw ones; `DecodeRaw` throws
  rather than silently returning partial data.
