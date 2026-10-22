"""PS-ADPCM (VAG) for the console's UI sounds.

The UI sounds (focus move, enter, cancel, ...) are not files of their own: they are stereo VAG clips inside
vsh_asset/Sce.PlayStation.PUI_UI3.rco. A pack carries a replacement as a mono VAG file; the app writes it to both
channels of the clip, keeps the clip's size, and cuts or pads it with silence (Shared/RcoVag.cs). The stock clips are
HEVAG, whose first five predictors are standard VAG's, so the plain encoder here (filters 0 to 4) is enough.
"""

import struct

# Standard VAG predictors, in 1/64.
COEF = [(0, 0), (60, 0), (115, -52), (98, -55), (122, -60)]
HEADER = 0x30
FRAME = 16


def encode(samples: list[int]) -> bytes:
    """Mono 16-bit samples to PS-ADPCM frames (28 samples each). The best filter and shift is searched per frame
    while tracking the decoder's own state, so rounding never drifts. No flags are set."""
    out = bytearray()
    s1 = s2 = 0
    for start in range(0, len(samples), 28):
        block = samples[start:start + 28]
        block += [0] * (28 - len(block))
        best = None
        for filt in range(5):
            c1, c2 = COEF[filt]
            for shift in range(13):
                a1, a2, nibbles, err = s1, s2, [], 0
                for x in block:
                    p = (a1 * c1 + a2 * c2 + 32) >> 6
                    q = max(-8, min(7, round(((x - p) << shift) / 4096)))
                    v = max(-32768, min(32767, ((q << 12) >> shift) + p))
                    err += (v - x) ** 2
                    a2, a1 = a1, v
                    nibbles.append(q & 15)
                    if best is not None and err >= best[0]:
                        break
                else:
                    if best is None or err < best[0]:
                        best = (err, filt, shift, nibbles, a1, a2)
        _, filt, shift, nibbles, s1, s2 = best
        out += bytes([(filt << 4) | shift, 0]) + bytes(nibbles[k] | (nibbles[k + 1] << 4) for k in range(0, 28, 2))
    return bytes(out)


def decode(frames: bytes) -> list[int]:
    """PS-ADPCM frames back to samples (standard predictors only), for checks."""
    out, s1, s2 = [], 0, 0
    for i in range(0, len(frames) - FRAME + 1, FRAME):
        shift, filt = frames[i] & 15, min(frames[i] >> 4, 4)
        if frames[i + 1] & 7 == 7:
            break
        c1, c2 = COEF[filt]
        for b in frames[i + 2:i + FRAME]:
            for n in (b & 15, b >> 4):
                n = n - 16 if n >= 8 else n
                v = max(-32768, min(32767, ((n << 12) >> shift) + ((s1 * c1 + s2 * c2 + 32) >> 6)))
                s2, s1 = s1, v
                out.append(v)
    return out


def make_vag(samples: list[int], rate: int, name: str) -> bytes:
    """A mono VAG file: the header, the sound frames (the last one flagged as the end) and an end-marker frame."""
    frames = bytearray(encode(samples) or bytes(FRAME))
    frames[len(frames) - FRAME + 1] = 1
    frames += bytes([0, 7]) + bytes(14)
    header = b"VAGp" + struct.pack(">IIII", 0x20, 0, len(frames), rate) + bytes(10) + bytes([0, 1])
    header += name.encode("ascii", "replace")[:16].ljust(16, b"\0")
    assert len(header) == HEADER
    return bytes(header) + bytes(frames)


def find_clip(container: bytes, name: str) -> tuple[int, int, int]:
    """(offset of the frames, their length, sample rate) of the one clip named `name` (cut to 16 characters, as the
    container keeps it). Raises ValueError when no clip or more than one has that name."""
    key = name[:16].encode("ascii")
    hits = []
    at = container.find(b"VAGp")
    while at >= 0 and at + HEADER <= len(container):
        if container[at + 0x20:at + 0x30].split(b"\0")[0] == key:
            size, rate = struct.unpack(">II", container[at + 0x0C:at + 0x14])
            hits.append((at + HEADER, size, rate))
        at = container.find(b"VAGp", at + 4)
    if len(hits) != 1:
        raise ValueError(f"sound '{name}' matches {len(hits)} clips in the container")
    return hits[0]


def list_clips(container: bytes) -> list[tuple[str, int, int]]:
    """(name, length of the frames, sample rate) of every clip in the container, in order."""
    clips = []
    at = container.find(b"VAGp")
    while at >= 0 and at + HEADER <= len(container):
        size, rate = struct.unpack(">II", container[at + 0x0C:at + 0x14])
        clips.append((container[at + 0x20:at + 0x30].split(b"\0")[0].decode("ascii", "replace"), size, rate))
        at = container.find(b"VAGp", at + 4)
    return clips


def clip_samples(length: int) -> int:
    """How many samples of sound a clip of `length` bytes of stereo frames holds (without its first and end pair)."""
    return (length // (2 * FRAME) - 2) * 28
