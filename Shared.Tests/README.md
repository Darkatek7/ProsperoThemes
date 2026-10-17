# Shared.Tests

PC unit tests for `../Shared`, the theme pack engine. Everything here runs offline with no console
attached — which is the point of keeping the format in dependency-free managed C#.

```powershell
$env:SHARPPROSPERO_ROOT = '<path to SharpProspero>'
dotnet test
```

The engine sources are pulled in with `<Compile Include="..\Shared\*.cs" />` rather than a project
reference, matching the SDK's own test projects, so the app can later compile the same files in without a
versioned assembly in between.

## What is covered

- **The happy path**, for both containers: a zip pack and a hand-built ustar pack, so the tar path is
  tested rather than assumed.
- **Refusals**: unknown roots, eight classes of unsafe path (`..`, absolute, backslash, empty segments, a
  bare root, an empty string), missing payloads, size mismatch, hash mismatch, duplicate targets, an empty
  target list, and a wrong format version.
- **Forward compatibility**: an unknown `layer` warns and is *skipped* rather than refused, so a newer pack
  still applies on an older app. This is asserted explicitly, because it is the behaviour that lets the
  format grow.
- **Structural failure**: not an archive, no `theme.json`, malformed JSON — each must raise
  `ThemePackException` rather than return a half-read pack.
- **`fixtures/two-screen.p5theme`**, a real pack produced by `ThemePackCreator`, so the C# reader is
  checked against the Python writer. Two implementations of one format drift unless something does this.

`ThemePackManifest` holds an `IReadOnlyList`, so its record equality compares that by reference; the
round-trip test therefore compares field by field. Worth knowing before writing another equality assertion
against it.
