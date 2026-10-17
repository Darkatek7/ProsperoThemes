#!/usr/bin/env python3
"""Build a PS5 theme pack (format 2) from source images.

The console never encodes anything -- no BC7 encoder, no zstd compressor, and a hard 256 MiB heap on the
app side -- so a pack ships payloads in their final on-disk form. This tool does all the encoding, on the
PC, using the pipeline already verified against real hardware.

Two kinds of target, both handled here:

  * **backgrounds** (`bg_*.dds.zst`) -- a Zstandard frame wrapping a 3840x2160 DDS. The image is encoded
    in the target's OWN declared format (15 are BC7_UNORM with a 148-byte DX10 header, one is BC1_UNORM
    with a 128-byte legacy header), the stock asset's own header is kept so only pixels differ, and the
    frame is built exactly like the system's own.

  * **png assets** (button glyphs, trophy plates, activity-card art) -- plain PNGs. The image is resized to
    the stock asset's own dimensions and converted to its colour mode, then shipped as-is. No encoding
    needed, which makes this the cheapest layer to theme.

Everything is recorded in theme.json with its size and SHA-256, so the applier is a copy with backup and
verification rather than a renderer.

    python theme_pack.py list
    python theme_pack.py catalog <stock-dir> <out.json> [--firmware 13.00]
    python theme_pack.py create --stock <dir> --out <pack> [--id ID ...] TARGET=IMAGE [TARGET=IMAGE ...]
    python theme_pack.py inspect <pack>
    python theme_pack.py verify  <pack> [--stock <dir>]

`TARGET` is an asset stem (`bg_hub_default`, `function_button_circle`), a title id (`NPXS40047`), a screen
label (`hub_default`), or a full file name.

Stock layout expected under `--stock`: `*.dds.zst` for backgrounds, and `png/` for the PNG assets. Copy them off
the console (for example over FTP from the paths in docs/13.00-screen-assets.md) into `backups/`.
"""

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
ZSTD_DIR = REPO / "ZstdWrap"
TEXCONV = ZSTD_DIR / "external" / ("texconv.exe" if os.name == "nt" else "texconv")

# Sony's ATRAC9 codec tool for PS4, the encoder there is no open-source equivalent of. It ships inside
# ATRACTool's portable release (the GUI is only a wrapper around it); see ZstdWrap/README.md.
AT9TOOL = ZSTD_DIR / "external" / "at9tool" / ("ps4_at9tool.exe" if os.name == "nt" else "ps4_at9tool")

sys.path.insert(0, str(ZSTD_DIR))
import mkdds      # noqa: E402  - header_length, format_of, replace_pixels
import realzstd   # noqa: E402  - encode_bytes, decode_bytes

ROOT = "vsh_asset"
# The roots a pack may name. Mirrors Shared/AssetRoots.cs, which is the canonical list - change one and the
# other, or Shared.Tests' pin (The_root_table_is_exactly_what_the_applier_mirrors) fails and says so.
ROOTS = ("appmeta", "vsh_asset")
WIDTH, HEIGHT = 3840, 2160
FORMAT_VERSION = 2

# The measured 13.00 backgrounds. `confirmed` marks the screens observed directly on the console; the rest
# are corroborated by the PS5 dev wiki's title-id names and by ps5-wallpaper-modd. See
# docs/13.00-screen-assets.md - an unconfirmed label is a label, not a measurement.
BACKGROUNDS = [
    {"asset": "bg_hub_default", "layer": "background", "screen": "hub_default", "name": "Default app-entry background", "confirmed": True},
    {"asset": "Library_BG_blue_Low_Angle_001", "layer": "background", "screen": "library_bg", "name": "Library background", "confirmed": False},
    {"asset": "bg_image_load_error", "layer": "background", "screen": "load_error", "name": "Image load error", "confirmed": False},
    {"asset": "bg_NPXS40037", "layer": "background", "screen": "ps_plus", "name": "PS Plus", "confirmed": False},
    {"asset": "bg_NPXS40047", "layer": "background", "screen": "ps_store", "name": "PlayStation Store", "confirmed": True},
    {"asset": "bg_NPXS40053", "layer": "background", "screen": "tv_video", "name": "TV & Video", "confirmed": False},
    {"asset": "bg_NPXS40054", "layer": "background", "screen": "music", "name": "Music", "confirmed": False},
    {"asset": "bg_NPXS40056", "layer": "background", "screen": "all_apps", "name": "All Apps", "confirmed": True},
    {"asset": "bg_NPXS40063", "layer": "background", "screen": "explore", "name": "Explore", "confirmed": False},
    {"asset": "bg_NPXS40071", "layer": "background", "screen": "game_library", "name": "Game Library", "confirmed": True},
    {"asset": "bg_NPXS40075", "layer": "background", "screen": "media_gallery", "name": "Media Gallery", "confirmed": True},
    {"asset": "bg_NPXS40080", "layer": "background", "screen": "share_play", "name": "Share Play", "confirmed": False},
    {"asset": "bg_NPXS40110", "layer": "background", "screen": "disc_player", "name": "Disc Player", "confirmed": False},
    {"asset": "bg_NPXS40139", "layer": "background", "screen": "app_library", "name": "App Library", "confirmed": True},
    {"asset": "bg_NPXS40144", "layer": "background", "screen": "unsupported_disc", "name": "Unsupported Disc", "confirmed": False},
    {"asset": "bg_NPXS40154", "layer": "background", "screen": "remote_play", "name": "Remote Play", "confirmed": False},
]

# The PNG layer, measured the same way. Dimensions come from the stock assets and are preserved on
# replacement: 12 of the 18 are the 864x1040 activity-card template, the button glyph is a 120x120 sprite,
# and ScreenShare is a full 4K image.
PNG_ASSETS = [
    {"asset": "function_button_circle.png", "layer": "glyph", "screen": "button_circle", "name": "Button glyph (circle)"},
    {"asset": "function_button_circle_edge.png", "layer": "glyph", "screen": "button_circle_edge", "name": "Button glyph edge (6x12 tile)"},
    {"asset": "TrophyUAM_Platinum.png", "layer": "icon", "screen": "trophy_platinum", "name": "Trophy plate (platinum)"},
    {"asset": "TrophyUAM_Gold.png", "layer": "icon", "screen": "trophy_gold", "name": "Trophy plate (gold)"},
    {"asset": "TrophyUAM_Silver.png", "layer": "icon", "screen": "trophy_silver", "name": "Trophy plate (silver)"},
    {"asset": "TrophyUAM_Bronze.png", "layer": "icon", "screen": "trophy_bronze", "name": "Trophy plate (bronze)"},
    {"asset": "ac_caesar.png", "layer": "icon", "screen": "card_caesar", "name": "Activity card art (caesar)"},
    {"asset": "ac_face_GameHelp.png", "layer": "icon", "screen": "card_gamehelp", "name": "Activity card art (Game Help)"},
    {"asset": "ac_iso_challenges_basemat.png", "layer": "icon", "screen": "card_challenges", "name": "Activity card (challenges)"},
    {"asset": "ac_iso_in_progress_basemat.png", "layer": "icon", "screen": "card_inprogress", "name": "Activity card (in progress)"},
    {"asset": "ac_iso_multi.png", "layer": "icon", "screen": "card_multi", "name": "Activity card (multiplayer)"},
    {"asset": "ac_iso_solo.png", "layer": "icon", "screen": "card_solo", "name": "Activity card (single player)"},
    {"asset": "ac_iso_tournaments_basemat.png", "layer": "icon", "screen": "card_tournaments", "name": "Activity card (tournaments)"},
    {"asset": "ac_consolidated_trophy_neutral.png", "layer": "icon", "screen": "card_trophy_neutral", "name": "Activity card (trophy, neutral)"},
    {"asset": "StoryProgress.png", "layer": "ui", "screen": "story_progress", "name": "Story progress overlay"},
    {"asset": "ScreenShare.png", "layer": "ui", "screen": "screenshare", "name": "Screen share banner (4K)"},
    {"asset": "ScreenShare_Small.png", "layer": "ui", "screen": "screenshare_small", "name": "Screen share banner (small)"},
    {"asset": "gen2vr_power_button.png", "layer": "ui", "screen": "vr_power_button", "name": "VR power button"},
]

# The video layer. Measured with ffprobe: three are HEVC Main / yuv420p (two tagged hvc1, one hev1) and one
# is H.264 with an AAC audio track. The shell's player is specific, so a replacement is re-encoded to match
# its own target's codec, tag, dimensions, frame rate and audio presence rather than guessed at.
VIDEO_ASSETS = [
    {"asset": "psbutton_press.mp4", "layer": "video", "screen": "psbutton_press", "name": "PS button press animation (1000x1000, hev1)"},
    {"asset": "initial_setup_controller.mp4", "layer": "video", "screen": "setup_controller", "name": "Controller setup movie (4K, hvc1)"},
    {"asset": "gen2vr_initial_setup_movie.mp4", "layer": "video", "screen": "vr_setup_movie", "name": "VR setup movie (1080p, hvc1)"},
    {"asset": "A11YStickSetting.mp4", "layer": "video", "screen": "a11y_stick", "name": "Accessibility stick setting (H.264 + AAC)"},
]

# The audio layer. Measured with ffprobe: every one is ATRAC9, 48 kHz, **6 channels (5.1)**, at 240 or
# 300 kbps. Six channels is the constraint that matters — a stereo replacement is refused by the encoder
# and would not match the system's playback path anyway.
AUDIO_ASSETS = [
    {"asset": "sfx_warmboot.at9", "layer": "sound", "screen": "sfx_warmboot", "name": "Warm boot sound"},
    {"asset": "sfx_coldboot.at9", "layer": "sound", "screen": "sfx_coldboot", "name": "Cold boot sound"},
    {"asset": "sfx_initialboot.at9", "layer": "sound", "screen": "sfx_initialboot", "name": "First boot sound"},
    {"asset": "sfx_transition.at9", "layer": "sound", "screen": "sfx_transition", "name": "UI transition sound"},
    {"asset": "bgm_home.at9", "layer": "sound", "screen": "bgm_home", "name": "Home screen music (looped)"},
    {"asset": "bgm_onboarding.at9", "layer": "sound", "screen": "bgm_onboarding", "name": "Onboarding music"},
    {"asset": "bgm_onboarding_vr.at9", "layer": "sound", "screen": "bgm_onboarding_vr", "name": "VR onboarding music"},
]

for _b in BACKGROUNDS:
    _b["target"] = f"{_b['asset']}.dds.zst"
    _b["kind"] = "dds"
for _p in PNG_ASSETS:
    _p["target"] = _p["asset"]
    _p["kind"] = "png"
    _p["confirmed"] = False
for _v in VIDEO_ASSETS:
    _v["target"] = _v["asset"]
    _v["kind"] = "video"
    _v["confirmed"] = False
for _a in AUDIO_ASSETS:
    _a["target"] = _a["asset"]
    _a["kind"] = "audio"
    _a["confirmed"] = False

ASSETS = BACKGROUNDS + PNG_ASSETS + VIDEO_ASSETS + AUDIO_ASSETS
BY_TARGET = {a["target"]: a for a in ASSETS}
BY_TITLE_ID = {a["asset"].replace("bg_", ""): a for a in ASSETS if a["asset"].startswith("bg_NPXS")}


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# Stock lives in a subdirectory per kind, as in backups/.
SUBDIR = {"png": "png", "video": "mp4", "audio": "at9"}

# Per-app entry backgrounds. Not in the catalog, and deliberately so: which titles exist depends on what is
# installed, so the target names the title and the stock is whatever that title has on the console right now.
# The payload is the wallpaper's texture format with the Zstandard wrapper left off - 3840x2160 BC7_UNORM
# behind a 148-byte DX10 header - which is why this needs no encoder of its own.
APP_PIC = "pic0.dds"


def app_background_entry(target: str) -> dict | None:
    """Builds an entry for <c>appmeta/&lt;TITLE_ID&gt;/pic0.dds</c>, or None if that is not the shape."""
    parts = target.split("/")
    if len(parts) != 3 or parts[0] != "appmeta" or parts[2] != APP_PIC or not parts[1]:
        return None

    title_id = parts[1]
    return {
        "root": "appmeta",
        # Relative to the root, like every other entry: _entry prepends the root to build the logical path.
        "target": f"{title_id}/{APP_PIC}",
        "asset": APP_PIC,
        "kind": "app_dds",
        "layer": "background",
        "screen": f"app entry ({title_id})",
        "name": f"App entry background for {title_id}",
        "confirmed": False,
        "title_id": title_id,
    }


def stock_path(stock_dir: Path, entry: dict) -> Path:
    kind = entry["kind"]
    if kind == "app_dds":
        # This title's own copy first, then any other: the format is uniform, so any pic0.dds is a valid
        # header donor, and a title the console has never been asked about still gets a correct header.
        wanted = stock_dir / "appmeta" / entry["title_id"] / APP_PIC
        if wanted.exists():
            return wanted
        found = sorted(stock_dir.rglob(APP_PIC))
        return found[0] if found else wanted
    if kind in SUBDIR:
        return stock_dir / SUBDIR[kind] / entry["asset"]
    return stock_dir / f"{entry['asset']}.dds.zst"


def resolve(name: str) -> dict:
    """Accepts an asset stem, a full file name, a title id, a screen label, or appmeta/<TID>/pic0.dds."""
    app = app_background_entry(name)
    if app is not None:
        return app
    if name in BY_TARGET:
        return BY_TARGET[name]
    for entry in ASSETS:
        stem = entry["asset"].rsplit(".", 1)[0]
        if name in (entry["asset"], stem, entry["screen"]):
            return entry
    if name in BY_TITLE_ID:
        return BY_TITLE_ID[name]
    candidate = f"bg_{name}.dds.zst"
    if candidate in BY_TARGET:
        return BY_TARGET[candidate]
    raise SystemExit(f"unknown target '{name}'. Run 'theme_pack.py list' to see them all.")


def load_stock(stock_dir: Path, entry: dict) -> bytes:
    path = stock_path(stock_dir, entry)
    if not path.exists():
        raise SystemExit(
            f"missing stock asset {path}. Copy the originals off the console first (FTP), "
            f"into backups/ (PNGs under backups/png/). The stock asset is what a replacement "
            f"must match in dimensions, format and header."
        )
    return path.read_bytes()


# ---- builders ---------------------------------------------------------------

def build_background(entry: dict, image: Path, stock_dir: Path, work: Path) -> tuple[bytes, dict]:
    from PIL import Image

    stock_frame = load_stock(stock_dir, entry)
    stock_dds = realzstd.decode_bytes(stock_frame)
    fmt = mkdds.format_of(stock_dds)

    resized = work / f"{entry['asset']}.png"
    with Image.open(image) as im:
        im = im.convert("RGB")
        if im.size != (WIDTH, HEIGHT):
            im = im.resize((WIDTH, HEIGHT), Image.LANCZOS)
        im.save(resized)

    out_dir = work / f"{entry['asset']}-enc"
    out_dir.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        [str(TEXCONV), "-f", fmt, "-m", "1", "-y", "-o", str(out_dir), str(resized)],
        capture_output=True, text=True,
    )
    encoded_path = out_dir / f"{entry['asset']}.dds"
    if result.returncode != 0 or not encoded_path.exists():
        raise SystemExit(f"texconv failed for {image} ({fmt}):\n{result.stdout}\n{result.stderr}")

    final_dds = mkdds.replace_pixels(stock_dds, encoded_path.read_bytes())   # stock header, new pixels
    payload = realzstd.encode_bytes(final_dds)
    return payload, _entry(entry, payload, stock_frame, extra={"format": fmt})


def build_app_background(entry: dict, image: Path, stock_dir: Path, work: Path) -> tuple[bytes, dict]:
    """A per-app entry background: the wallpaper texture, with no Zstandard wrapper around it.

    The stock file is a plain DDS rather than a frame, so there is nothing to decode and nothing to
    compress - which is the whole reason this layer needed no encoder work at all.
    """
    from PIL import Image

    stock_dds = load_stock(stock_dir, entry)
    fmt = mkdds.format_of(stock_dds)
    # DDS_HEADER is dwSize, dwFlags, dwHeight, dwWidth, so height sits at 12 and width at 16 of the file.
    height = int.from_bytes(stock_dds[12:16], "little")
    width = int.from_bytes(stock_dds[16:20], "little")

    resized = work / f"{entry['title_id']}-{APP_PIC}.png"
    with Image.open(image) as im:
        im = im.convert("RGB")
        if im.size != (width, height):
            im = im.resize((width, height), Image.LANCZOS)
        im.save(resized)

    out_dir = work / f"{entry['title_id']}-app-enc"
    out_dir.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        [str(TEXCONV), "-f", fmt, "-m", "1", "-y", "-o", str(out_dir), str(resized)],
        capture_output=True, text=True,
    )
    encoded_path = out_dir / f"{resized.stem}.dds"
    if result.returncode != 0 or not encoded_path.exists():
        raise SystemExit(f"texconv failed for {image} ({fmt}):\n{result.stdout}\n{result.stderr}")

    final_dds = mkdds.replace_pixels(stock_dds, encoded_path.read_bytes())   # stock header, new pixels
    return final_dds, _entry(entry, final_dds, stock_dds,
                             extra={"format": fmt, "dimensions": f"{width}x{height}"})


def build_png(entry: dict, image: Path, stock_dir: Path, work: Path) -> tuple[bytes, dict]:
    from PIL import Image

    stock_bytes = load_stock(stock_dir, entry)
    with Image.open(stock_dir / "png" / entry["asset"]) as stock:
        size, mode = stock.size, stock.mode

    resized = work / entry["asset"]
    with Image.open(image) as im:
        # Convert to the stock asset's own colour mode: a replacement that drops alpha where the shell
        # composites one would show up as an opaque box.
        if mode in ("RGBA", "LA"):
            im = im.convert("RGBA")
        else:
            im = im.convert(mode)
        if im.size != size:
            im = im.resize(size, Image.LANCZOS)
        im.save(resized, format="PNG", optimize=True)

    payload = resized.read_bytes()
    return payload, _entry(entry, payload, stock_bytes, extra={"dimensions": f"{size[0]}x{size[1]}", "mode": mode})


def probe_media(path: Path) -> dict:
    """ffprobe a media file into the parameters a replacement has to match."""
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-of", "json",
         "-show_entries", "stream=codec_type,codec_name,codec_tag_string,profile,width,height,r_frame_rate,pix_fmt",
         str(path)],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        raise SystemExit(f"ffprobe failed on {path}:\n{result.stderr}")

    streams = json.loads(result.stdout).get("streams", [])
    video = next((s for s in streams if s.get("codec_type") == "video"), None)
    if video is None:
        raise SystemExit(f"{path}: no video stream")

    audio = next((s for s in streams if s.get("codec_type") == "audio"), None)
    return {
        "codec": video.get("codec_name", ""),
        "tag": video.get("codec_tag_string", "") or "",
        "profile": (video.get("profile") or "main").lower(),
        "width": int(video.get("width", 0)),
        "height": int(video.get("height", 0)),
        "rate": video.get("r_frame_rate", "30/1"),
        "pix_fmt": video.get("pix_fmt", "yuv420p"),
        "audio": audio.get("codec_name") if audio else None,
    }


ENCODERS = {"hevc": "libx265", "h264": "libx264"}


def build_video(entry: dict, source: Path, stock_dir: Path, work: Path) -> tuple[bytes, dict]:
    """Re-encode a replacement to match the stock clip's own codec, tag, size, rate and audio."""
    stock = stock_path(stock_dir, entry)
    if not stock.exists():
        raise SystemExit(f"missing stock video {stock}")

    info = probe_media(stock)
    stock_bytes = stock.read_bytes()

    encoder = ENCODERS.get(info["codec"])
    if encoder is None:
        raise SystemExit(f"{entry['asset']}: unsupported stock codec '{info['codec']}'")

    out = work / entry["asset"]
    args = [
        "-y", "-i", str(source),
        "-c:v", encoder,
        "-profile:v", info["profile"],
        "-pix_fmt", info["pix_fmt"],
        "-vf", f"scale={info['width']}:{info['height']}",
        "-r", info["rate"],
    ]
    if info["tag"]:
        # hvc1 vs hev1 is not cosmetic: one carries parameter sets in the sample description, the other
        # in-band, and the console's own clips use both tags across different assets.
        args += ["-tag:v", info["tag"]]
    if encoder == "libx265":
        args += ["-x265-params", "log-level=error"]
    args += (["-c:a", "aac", "-profile:a", "aac_low"] if info["audio"] else ["-an"])
    args += ["-movflags", "+faststart", str(out)]

    result = subprocess.run(["ffmpeg", *args], capture_output=True, text=True)
    if result.returncode != 0 or not out.exists():
        raise SystemExit(f"ffmpeg failed for {source}:\n{result.stdout[-2000:]}\n{result.stderr[-2000:]}")

    payload = out.read_bytes()
    detail = f"{info['codec']}/{info['tag']} {info['width']}x{info['height']}@{info['rate']}"
    return payload, _entry(entry, payload, stock_bytes, extra={"video": detail})


def probe_audio(path: Path) -> dict:
    """ffprobe an audio file for the parameters a replacement has to match."""
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-of", "json",
         "-show_entries", "stream=codec_name,sample_rate,channels,bit_rate",
         "-show_entries", "format=bit_rate", str(path)],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        raise SystemExit(f"ffprobe failed on {path}:\n{result.stderr}")

    payload = json.loads(result.stdout)
    streams = payload.get("streams", [])
    audio = next((s for s in streams if s.get("codec_name")), None)
    if audio is None:
        raise SystemExit(f"{path}: no audio stream")

    bit_rate = int(audio.get("bit_rate") or payload.get("format", {}).get("bit_rate") or 0)
    return {
        "codec": audio.get("codec_name", ""),
        "sample_rate": int(audio.get("sample_rate", 48000)),
        "channels": int(audio.get("channels", 2)),
        "bit_rate": bit_rate,
    }


def build_audio(entry: dict, source: Path, stock_dir: Path, work: Path) -> tuple[bytes, dict]:
    """Re-encode a replacement to match the stock clip's own sample rate, channel count and bitrate."""
    stock = stock_path(stock_dir, entry)
    if not stock.exists():
        raise SystemExit(f"missing stock audio {stock}")

    info = probe_audio(stock)
    stock_bytes = stock.read_bytes()

    if info["codec"] != "atrac9":
        raise SystemExit(f"{entry['asset']}: stock codec is '{info['codec']}', not atrac9")
    if not AT9TOOL.exists():
        raise SystemExit(
            f"at9tool not found at {AT9TOOL}. Sony's ATRAC9 codec tool ships inside ATRACTool's portable "
            f"release; see ZstdWrap/README.md."
        )

    # at9tool takes WAV only, at a sample rate and channel count it supports, so the source is converted
    # first rather than assumed to be compatible. Six channels is the usual case here (every stock asset is
    # 5.1), and at9tool refuses a channel count its bitrate does not allow.
    wav = work / f"{entry['asset']}.wav"
    converted = subprocess.run(
        ["ffmpeg", "-y", "-v", "error", "-i", str(source),
         "-ar", str(info["sample_rate"]), "-ac", str(info["channels"]),
         "-c:a", "pcm_s16le", str(wav)],
        capture_output=True, text=True,
    )
    if converted.returncode != 0 or not wav.exists():
        raise SystemExit(f"ffmpeg could not prepare {source}:\n{converted.stderr[-2000:]}")

    out = work / entry["asset"]
    kbps = max(1, round(info["bit_rate"] / 1000)) if info["bit_rate"] else 0
    args = [str(AT9TOOL), "-e"]
    if kbps:
        args += ["-br", str(kbps)]
    args += ["-fs", str(info["sample_rate"]), str(wav), str(out)]

    encoded = subprocess.run(args, capture_output=True, text=True)
    if encoded.returncode != 0 or not out.exists():
        raise SystemExit(f"at9tool failed for {source}:\n{encoded.stdout[-2000:]}\n{encoded.stderr[-2000:]}")

    payload = out.read_bytes()
    detail = f"{info['codec']} {info['sample_rate']}Hz {info['channels']}ch {kbps}kbps"
    return payload, _entry(entry, payload, stock_bytes, extra={"audio": detail})


def _entry(entry: dict, payload: bytes, stock_bytes: bytes, extra: dict | None = None) -> dict:
    record = {
        "path": f"{entry.get('root', ROOT)}/{entry['target']}",
        "layer": entry["layer"],
        "file": f"assets/{entry['target']}",
        "bytes": len(payload),
        "sha256": sha256_hex(payload),
        "screen": entry["screen"],
        "stock_sha256": sha256_hex(stock_bytes),
    }
    if extra:
        record.update(extra)
    return record


# ---- commands ---------------------------------------------------------------

def cmd_list(args: argparse.Namespace) -> int:
    for kind, title in (("dds", "backgrounds"), ("png", "png assets"), ("video", "videos"), ("audio", "audio")):
        rows = [a for a in ASSETS if a["kind"] == kind]
        print(f"\n{title} ({len(rows)}):")
        print(f"  {'target':<40} {'layer':<11} {'screen':<20} name")
        print("  " + "-" * 100)
        for a in rows:
            flag = " *" if a.get("confirmed") else ""
            print(f"  {a['target']:<40} {a['layer']:<11} {a['screen']:<20} {a['name']}{flag}")
    print("\n  * = screen label confirmed by observation on 13.00")
    return 0


def cmd_catalog(args: argparse.Namespace) -> int:
    stock_dir = Path(args.stock_dir)
    assets = []
    for entry in ASSETS:
        path = stock_path(stock_dir, entry)
        if not path.exists():
            print(f"  skipping {entry['asset']}: no stock file", file=sys.stderr)
            continue

        stock_bytes = path.read_bytes()
        row = {
            "path": f"{ROOT}/{entry['target']}",
            "layer": entry["layer"],
            "screen": entry["screen"],
            "name": entry["name"],
            "confirmed": entry.get("confirmed", False),
            "stock_bytes": len(stock_bytes),
            "stock_sha256": sha256_hex(stock_bytes),
        }

        if entry["kind"] == "dds":
            dds = realzstd.decode_bytes(stock_bytes)
            row.update(
                format=mkdds.format_of(dds),
                header_bytes=mkdds.header_length(dds),
                width=int.from_bytes(dds[16:20], "little"),
                height=int.from_bytes(dds[12:16], "little"),
                stock_content_bytes=len(dds),
            )
        elif entry["kind"] == "png":
            from PIL import Image
            with Image.open(path) as im:
                row.update(format="png", width=im.size[0], height=im.size[1], mode=im.mode)
        elif entry["kind"] == "video":
            info = probe_media(path)
            row.update(
                format=f"{info['codec']}/{info['tag']}",
                width=info["width"],
                height=info["height"],
                mode=info["pix_fmt"],
                frame_rate=info["rate"],
                audio=info["audio"] or "none",
            )
        else:
            info = probe_audio(path)
            row.update(
                format=info["codec"],
                channels=info["channels"],
                sample_rate=info["sample_rate"],
                bit_rate=info["bit_rate"],
            )

        assets.append(row)

    catalog = {"firmware": args.firmware, "format": FORMAT_VERSION, "assets": assets}
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")
    by_layer = {}
    for a in assets:
        by_layer[a["layer"]] = by_layer.get(a["layer"], 0) + 1
    print(f"catalog: {len(assets)} assets -> {out}")
    print("  by layer: " + ", ".join(f"{k}={v}" for k, v in sorted(by_layer.items())))
    return 0


def cmd_create(args: argparse.Namespace) -> int:
    stock_dir = Path(args.stock)
    out = Path(args.out)

    requests: list[tuple[dict, Path]] = []
    for pair in args.screens:
        if "=" not in pair:
            raise SystemExit(f"--screen expects TARGET=IMAGE, got '{pair}'")
        name, _, image = pair.partition("=")
        entry = resolve(name)
        image_path = Path(image)
        if not image_path.exists():
            raise SystemExit(f"image not found: {image_path}")
        requests.append((entry, image_path))

    if not requests:
        raise SystemExit("nothing to do: pass at least one TARGET=IMAGE")

    with tempfile.TemporaryDirectory(prefix="theme-pack-") as tmp:
        work = Path(tmp)
        manifest_targets = []
        payloads: list[tuple[str, bytes]] = []

        for entry, image in requests:
            if entry["kind"] == "dds":
                payload, record = build_background(entry, image, stock_dir, work)
            elif entry["kind"] == "app_dds":
                payload, record = build_app_background(entry, image, stock_dir, work)
            elif entry["kind"] == "png":
                payload, record = build_png(entry, image, stock_dir, work)
            elif entry["kind"] == "video":
                payload, record = build_video(entry, image, stock_dir, work)
            else:
                payload, record = build_audio(entry, image, stock_dir, work)

            payloads.append((record["file"], payload))
            manifest_targets.append(record)
            detail = (record.get("format") or record.get("video") or record.get("audio")
                      or f"{record.get('dimensions')} {record.get('mode')}")
            print(f"  {record['path']:<40} {image.name:<24} -> {record['bytes']:>8} bytes  [{detail}]")

        preview_bytes = Path(args.preview).read_bytes() if args.preview else None

        manifest = {
            "format": FORMAT_VERSION,
            "id": args.id,
            "name": args.name,
            "author": args.author,
            "version": args.version,
            "description": args.description,
            "unmapped": args.unmapped,
            "targets": manifest_targets,
        }
        if preview_bytes is not None:
            manifest["preview"] = "preview.png"

        out.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
            zf.writestr("theme.json", json.dumps(manifest, indent=2) + "\n")
            if preview_bytes is not None:
                zf.writestr("preview.png", preview_bytes)
            for name, data in payloads:
                zf.writestr(name, data)

    print(f"\npack: {out}  ({out.stat().st_size} bytes, {len(manifest_targets)} target(s))")
    return 0


def cmd_inspect(args: argparse.Namespace) -> int:
    with zipfile.ZipFile(args.pack) as zf:
        manifest = json.loads(zf.read("theme.json"))
        names = set(zf.namelist())

    print(f"{manifest['name']} v{manifest['version']} by {manifest['author']}  (format {manifest['format']})")
    print(f"id {manifest['id']}  unmapped={manifest.get('unmapped')}")
    print(f"{manifest.get('description', '')}\n")
    print(f"{'target':<42} {'layer':<12} {'bytes':>9}  sha256")
    print("-" * 92)
    for t in manifest["targets"]:
        present = "ok" if t["file"] in names else "MISSING"
        print(f"{t['path']:<42} {t['layer']:<12} {t['bytes']:>9}  {t['sha256'][:16]}...  {present}")
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    """Structural check. Shared.Tests holds the authoritative validator; this is the quick local one."""
    problems = []
    with zipfile.ZipFile(args.pack) as zf:
        names = set(zf.namelist())
        if "theme.json" not in names:
            raise SystemExit("pack has no theme.json")
        manifest = json.loads(zf.read("theme.json"))
        if manifest.get("format") != FORMAT_VERSION:
            problems.append(f"format is {manifest.get('format')}, expected {FORMAT_VERSION}")
        for t in manifest.get("targets", []):
            if t["file"] not in names:
                problems.append(f"{t['path']}: payload {t['file']} missing")
                continue
            data = zf.read(t["file"])
            if len(data) != t["bytes"]:
                problems.append(f"{t['path']}: {len(data)} bytes, manifest says {t['bytes']}")
            if sha256_hex(data) != t["sha256"]:
                problems.append(f"{t['path']}: sha256 mismatch")
            root = t["path"].partition("/")[0]
            if root not in ROOTS or ".." in t["path"]:
                problems.append(f"{t['path']}: path is not under a known root ({', '.join(ROOTS)})")
            if args.stock:
                base = t["path"].split("/", 1)[1]
                candidates = [Path(args.stock) / sub / base for sub in ("png", "mp4", "at9")]
                candidates += [
                    Path(args.stock) / base,
                    Path(args.stock) / f"{base}.dds.zst",
                    # Mirrored layout: appmeta/<TID>/pic0.dds is kept, and pulled into, the same shape.
                    Path(args.stock) / root / base,
                ]
                for candidate in candidates:
                    if candidate.exists() and t.get("stock_sha256"):
                        if sha256_hex(candidate.read_bytes()) != t["stock_sha256"]:
                            problems.append(f"{t['path']}: stock_sha256 does not match {candidate.name}")
                        break

    if problems:
        print("INVALID")
        for p in problems:
            print(f"  - {p}")
        return 1
    print(f"valid: {len(manifest['targets'])} target(s), all payloads present and hashed correctly")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_list = sub.add_parser("list", help="show every themable target")
    p_list.set_defaults(func=cmd_list)

    p_cat = sub.add_parser("catalog", help="write the measured asset catalog")
    p_cat.add_argument("stock_dir")
    p_cat.add_argument("out")
    p_cat.add_argument("--firmware", default="13.00")
    p_cat.set_defaults(func=cmd_catalog)

    p_create = sub.add_parser("create", help="build a pack from source images")
    p_create.add_argument("screens", nargs="*", metavar="TARGET=IMAGE")
    p_create.add_argument("--stock", required=True, help="directory of stock assets (header/dimension donors)")
    p_create.add_argument("--out", required=True)
    p_create.add_argument("--id", default="pack")
    p_create.add_argument("--name", default="Untitled Theme")
    p_create.add_argument("--author", default="unknown")
    p_create.add_argument("--version", default="1.0.0")
    p_create.add_argument("--description", default="")
    p_create.add_argument("--unmapped", default="keep", choices=["keep", "stock"])
    p_create.add_argument("--preview", default=None, help="image used as the pack cover")
    p_create.set_defaults(func=cmd_create)

    p_inspect = sub.add_parser("inspect", help="show a pack's manifest")
    p_inspect.add_argument("pack")
    p_inspect.set_defaults(func=cmd_inspect)

    p_verify = sub.add_parser("verify", help="check a pack locally")
    p_verify.add_argument("pack")
    p_verify.add_argument("--stock", default=None)
    p_verify.set_defaults(func=cmd_verify)

    args = parser.parse_args()

    if args.command == "create":
        kinds = {resolve(p.partition("=")[0])["kind"] for p in args.screens if "=" in p}
        if kinds & {"dds", "app_dds"} and not TEXCONV.exists():
            raise SystemExit(f"texconv not found at {TEXCONV} - see ZstdWrap/README.md")
        if "video" in kinds and shutil.which("ffmpeg") is None:
            raise SystemExit("ffmpeg is required for video targets - install it (e.g. 'scoop install ffmpeg')")
        if "audio" in kinds:
            if not AT9TOOL.exists():
                raise SystemExit(f"at9tool not found at {AT9TOOL} - see ZstdWrap/README.md")
            if shutil.which("ffmpeg") is None:
                raise SystemExit("ffmpeg is required to prepare WAV input for audio targets")

    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
