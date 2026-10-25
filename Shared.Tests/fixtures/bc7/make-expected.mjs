// Writes modes.bc7 (a synthetic 64x64 BC7 image: 31 random blocks of every mode, then 8 of mode 0 with the first byte
// zeroed = reserved) and expected.json (what the store's TypeScript decoder, texture/src/bc7-decode.ts in the store repository, makes of it
// and of the real blocks in ../zstd/bc7-l19.zst). Build the store's texture package first (npm run build), then:
//   node make-expected.mjs <path to texture/dist/bc7-decode.js> <bc7-l19 raw blocks file> [name=file.dds ...]
// The raw file is the zstd fixture decoded (python -c "import zstandard;..."). Each name=file adds the 3840x2160 DDS of a
// fixture pack's hub background (decompressed) under that name, with the hash of the half-size image.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const { decodeBc7 } = await import(pathToFileURL(process.argv[2]).href);
const real = new Uint8Array(readFileSync(process.argv[3]));

// xorshift32, so the fixture is the same on every run.
let state = 0x1234abcd;
const next = () => { state ^= state << 13; state >>>= 0; state ^= state >>> 17; state ^= state << 5; state >>>= 0; return state; };

const modes = new Uint8Array(256 * 16);
for (let b = 0; b < 256; b++) {
  for (let i = 0; i < 16; i++) modes[b * 16 + i] = next() & 0xff;
  if (b < 248) {
    const m = b % 8;
    modes[b * 16] = (modes[b * 16] & ~((1 << (m + 1)) - 1)) | (1 << m); // m zero bits, then a one
  } else {
    modes[b * 16] = 0; // reserved
  }
}
writeFileSync('modes.bc7', modes);

const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
function half(rgba, w, h) {
  const out = new Uint8Array((w >> 1) * (h >> 1) * 4);
  for (let y = 0; y < h >> 1; y++) for (let x = 0; x < w >> 1; x++) for (let c = 0; c < 4; c++) {
    const at = (y * 2 * w + x * 2) * 4 + c;
    out[(y * (w >> 1) + x) * 4 + c] = (rgba[at] + rgba[at + 4] + rgba[at + w * 4] + rgba[at + w * 4 + 4] + 2) >> 2;
  }
  return out;
}
function entry(blocks, w, h, samples) {
  const rgba = decodeBc7(blocks, w, h);
  const o = { width: w, height: h, sha256: sha(rgba), pixels: {} };
  for (const [x, y] of samples) o.pixels[`${x},${y}`] = [...rgba.subarray((y * w + x) * 4, (y * w + x) * 4 + 4)];
  if (w % 4 === 0 && h % 4 === 0) o.halfSha256 = sha(half(rgba, w, h));
  return o;
}
const expected = {
  modes: entry(modes, 64, 64, [[0, 0], [5, 3], [31, 17], [63, 63], [40, 12]]),
  real: entry(real, 800, 400, [[0, 0], [123, 45], [400, 200], [799, 399]]),
  // cropped from whole blocks
  realCropped: entry(real, 797, 391, [[0, 0], [796, 390], [500, 300]]),
};
// The first block of every mode, to name the mode a failure is in.
expected.modeBlocks = {};
for (let m = 0; m < 8; m++) expected.modeBlocks[m] = sha(decodeBc7(modes.subarray(m * 16, m * 16 + 16), 4, 4));
const { decodeDdsBc7 } = await import(pathToFileURL(process.argv[2]).href);
for (const arg of process.argv.slice(4)) {
  const [name, file] = arg.split('=');
  const { width, height, rgba } = decodeDdsBc7(new Uint8Array(readFileSync(file)));
  const o = { width, height, sha256: sha(rgba), halfSha256: sha(half(rgba, width, height)), pixels: {} };
  for (const [x, y] of [[0, 0], [1919, 1079], [3839, 2159], [2000, 1500]]) o.pixels[`${x},${y}`] = [...rgba.subarray((y * width + x) * 4, (y * width + x) * 4 + 4)];
  expected[name] = o;
}
writeFileSync('expected.json', JSON.stringify(expected, null, 2) + '\n');
