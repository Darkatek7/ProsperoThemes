# Shared — theme pack engine

The C# half of the theme engine, and the authoritative reader for the pack format. It is deliberately
**pure computation with no console or service dependency**, so the whole format is testable offline on
the PC — which is what lets an applier refuse a bad pack before it writes anything.

## Why it is shaped this way

The pack format is **target-driven, not feature-driven**. A pack lists files to replace and tags each with
a layer for the UI; it never encodes how a background is rendered or what an icon means. Two consequences,
both deliberate:

- **Every layer rides the same engine.** The 16 backgrounds, 18 PNGs, 7 sounds, 4 videos, 14 GNFs and
  8 RCOs found on 13.00 are all the same kind of entry. Adding a layer is adding entries, not changing the
  engine.
- **A target nobody has seen before still works**, provided its root is allowlisted. The asset set is
  firmware-dependent and only 13.00 has been measured, so the format must not hard-code it.

Payloads ship **ready to write** — final on-disk bytes, including the zstd frame for a background. The
console has no BC7 encoder, no zstd compressor, and the app runs under a 256 MiB heap, so all encoding
happens on the PC. Anything the PC can produce, the format can already carry.

## Files

| File | Responsibility |
|---|---|
| `AssetRoots.cs` | The root allowlist and path safety. `vsh_asset` maps to `/system_ex/vsh_asset`; a pack never names an absolute path. **This is the format's main safety boundary.** |
| `ThemePackManifest.cs` | The `theme.json` model, parse and write. Uses the SDK's `JsonValue` rather than `System.Text.Json` so it stays ahead-of-time safe when compiled into the app. |
| `ThemePack.cs` | Reads a zip or tar archive (sniffed by magic, not extension), exposes payloads, and validates a pack end to end. |
| `PackReport.cs` | Issues and severities, so callers get a reason rather than a boolean. |
| `Zstd.cs`, `Bc7.cs` | Decoders for the preview and for consoles before 13.00: zstd, and BC7 (all modes, also straight to half size) with the DDS header. Tested against the store's TypeScript decoder (`Shared.Tests/fixtures/bc7`). |

## Validation rules

Refused outright:

- an unknown asset root — it could name a path outside the assets we understand
- a path with `..`, an absolute path, a backslash, an empty segment, or a NUL
- a missing payload, or one whose size or SHA-256 disagrees with the manifest
- a payload over the size cap (32 MiB default), because `/system_ex` is a system partition
- a duplicate target, an empty target list, or an unsupported `format`

Warned and **skipped**, not refused:

- an unknown `layer`. This is what lets a newer pack degrade gracefully on an older app instead of failing
  wholesale — the entry is ignored and everything else still applies.

```csharp
ThemePack pack = ThemePack.OpenFile("theme.p5theme");
PackReport report = pack.Validate();
if (report.HasErrors)
{
    foreach (PackIssue issue in report.Errors)
        Log.Error(issue.Message);
    return;
}

foreach (ThemePackTarget target in pack.ApplicableTargets())
{
    byte[] payload = pack.Payload(target);   // write these bytes verbatim, atomically
}
```

Sources are consumed by the test and app projects via `<Compile Include>`, matching the SDK's own
convention, so there is no assembly to version.

## Not here

Nothing writes to the console. This is the reader and the rules; the applier is the app
(`ThemeApp/Library/ThemeApplier.cs`).
