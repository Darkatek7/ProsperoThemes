using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ProsperoThemes.Shared;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The console file operations the applier needs. The production implementation is
/// <see cref="DeviceFiles"/>; tests substitute an in-memory fake. Paths are absolute console paths.
/// </summary>
public interface IDeviceFiles
{
    bool Exists(string path);

    /// <summary>Size of the file in bytes, -1 when there is none.</summary>
    long Length(string path);
    byte[] ReadAllBytes(string path);

    /// <summary>
    /// Lower-case hex SHA-256 of the file, streamed: the file is never held whole. Used to compare and verify
    /// files without a second copy in memory (the icon container is about 100 MB before firmware 13.00).
    /// </summary>
    string Hash(string path);
    void WriteAllBytes(string path, byte[] data);

    /// <summary>Renames <paramref name="from"/> to <paramref name="to"/>, replacing the destination.</summary>
    void Move(string from, string to);
    void DeleteFile(string path);
    void CreateDirectoryRecursive(string path);
    void RemountSystemExRw();

    /// <summary>Names of the sub-folders of <paramref name="path"/> (only /user/app/ and /user/appmeta/ are asked).</summary>
    IReadOnlyList<string> ListDirectories(string path);
}

/// <summary>Outcome of an apply or restore.</summary>
/// <param name="Success">Whether every target was written and verified.</param>
/// <param name="Message">Human-readable summary, or the failure reason including rollback status.</param>
/// <param name="Written">
/// On success, the device paths written. On failure, the device paths that still hold pack (or restored)
/// bytes because they could not be rolled back; empty when nothing was left changed.
/// </param>
public sealed record ApplyResult(bool Success, string Message, IReadOnlyList<string> Written);

/// <summary>
/// What a running apply or restore is doing, for the app's progress notice. Called on the thread the applier runs on,
/// once as each file of a step starts. An interface rather than a delegate, like the other worker hooks.
/// </summary>
public interface IApplyProgress
{
    /// <summary>File <paramref name="index"/> (from 1) of <paramref name="total"/> of <paramref name="step"/> starts.</summary>
    void Report(string step, int index, int total);
}

/// <summary>
/// Applies a theme pack to the console, or restores the on-console originals. Mirrors DevProbe/apply-pack.ps1:
/// validate, resolve, remount, back up once, write <c>.tmp</c>, rename, read back and compare hashes, then
/// record state.
/// </summary>
public sealed class ThemeApplier
{
    /// <summary>Folder under the data root holding the original files, keyed by logical path.</summary>
    public const string BackupFolder = "backup/original";

    /// <summary>
    /// One logical path per line for every file under <see cref="BackupFolder"/>. Kept because an app module
    /// cannot list directories under /data (getdents answers EINVAL), so the backups are found through it.
    /// </summary>
    public const string BackupIndex = "backup/index.txt";

    /// <summary>
    /// "sha256 path" per line: what the app last wrote to each console file. A live file that matches neither
    /// this nor its backup was replaced by someone else, normally a system update (or Sony's servers, which
    /// update the /mnt/rnps bundles on their own); then the backup is out of date, see <see cref="Refreshed"/>.
    /// </summary>
    public const string WrittenRecord = "backup/written.txt";

    /// <summary>
    /// "sha256 logical" per line: the hash of each original the app backed up or put back. It outlives a deleted
    /// backup, so a file that still matches is known to be the original, also one the app wrote itself (a restore
    /// records its write in <see cref="WrittenRecord"/> as well).
    /// </summary>
    public const string OriginalsRecord = "backup/originals.txt";

    /// <summary>Where a backup goes when the system replaced its file: <c>backup/replaced/&lt;time&gt;/&lt;logical&gt;</c>.</summary>
    public const string ReplacedFolder = "backup/replaced";

    private readonly IDeviceFiles _files;
    private readonly string _dataRoot;
    // Seconds since 1970-01-01 UTC. Deliberately not a Func<DateTime>: in the native build that delegate
    // keeps a boxed DateTime and with it the time-zone loader, which needs runtime file calls (getegid,
    // seteuid) the console does not provide, so the app would not link.
    private readonly Func<long> _clock;

    /// <summary>
    /// Whether an <see cref="AssetRoots.AllAppsBackground"/> target reaches titles that ship their own entry
    /// background. Off by default: then a theme's background only shows on titles without one (through
    /// <c>bg_hub_default</c>), and every other title keeps its own artwork. A user setting in the app.
    /// </summary>
    public bool IncludeAllApps { get; init; }

    /// <summary>
    /// Whether an apply keeps what earlier themes changed and this one does not write, so themes combine (a
    /// background theme, then an icon theme). Off: every apply starts from the defaults, so themes replace each
    /// other. Icons in the same container combine too: the new ones are patched onto the container as the app
    /// last wrote it. A user setting in the app, which keeps by default.
    /// </summary>
    public bool KeepEarlierThemes { get; init; }

    /// <summary>
    /// The parts of a theme an apply writes (<see cref="ThemeParts.Of"/>); targets of other parts are left out as if the
    /// pack did not have them. The user picks them when applying; sounds are left out by default when they are turned off.
    /// </summary>
    public ThemePart Parts { get; init; } = ThemePart.All;

    /// <summary>
    /// The console's system software version as it reports it (e.g. "13.000.000"), recorded in state.json.
    /// Null when not known (PC tests). It decides nothing: every check compares the console's own files.
    /// </summary>
    public string? Firmware { get; init; }

    /// <summary>
    /// The library path of the pack an apply or restore works on, recorded in <see cref="Look"/> so the look can be
    /// applied again or saved as a theme. Null when not known (PC tests that do not ask).
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Largest console file the applier reads or writes as a whole: ThemeHelper's write limit (MaxWrite, the same
    /// number). A larger file is left alone without being read; the app cannot have written it, so it still holds
    /// its original. The icon container is about 100 MB on firmware before 13.00: the applier holds one whole copy
    /// of a file at a time and compares and verifies by streamed hashes (<see cref="IDeviceFiles.Hash"/>), because
    /// holding several copies ran the app out of memory.
    /// </summary>
    public long MaxFileBytes { get; init; } = 160L * 1024 * 1024;

    /// <summary>
    /// How loud the theme's UI sounds (<see cref="ThemePack.VagLayer"/> targets) play, as an amplitude from 0 (silent) to 1
    /// (as the theme made them, which is written byte for byte). A lower value decodes each sound, scales it and
    /// encodes it again (<see cref="VagCodec.ScaleVolume"/>) just before it is patched into the container. The
    /// written and verified file is the container, so verification and <see cref="WrittenRecord"/> hold what was
    /// really written. Sounds that are whole files (music) are not changed.
    /// </summary>
    public double UiSoundVolume { get; init; } = 1.0;

    /// <summary>
    /// The stock hashes of this firmware (<see cref="StockCatalog"/>): a file with a catalog hash is the console's
    /// original only when it matches it. Empty: only <see cref="WrittenRecord"/> tells a theme's file from an original.
    /// </summary>
    public StockCatalog Stock { get; init; } = StockCatalog.Empty;

    /// <summary>
    /// How loud the theme's home music (a whole <c>bgm_*.at9</c> file) plays, as an amplitude from 0 to 1 (as the theme
    /// made it, written byte for byte). A lower value decodes the music, scales it and encodes it again
    /// (<see cref="Atrac9Codec.ScaleVolume"/>) just before it is written; that takes seconds per minute of music, reported
    /// as <see cref="StepMusic"/>. Music the codec cannot read is written as it is, with a note.
    /// </summary>
    public double MusicVolume { get; init; } = 1.0;

    /// <summary>
    /// Logical paths of files this run found changed with no original on the console: the backup is gone and the
    /// live file is a theme's (<see cref="WrittenRecord"/> or <see cref="Stock"/> says so), and there is no shipped
    /// copy to take instead. An apply still writes them; a restore leaves them as they are and names them.
    /// </summary>
    public List<string> NoOriginal { get; } = new();

    /// <summary>Told as each file of each step starts (backing up, resetting, writing, restoring). Null: nobody asks.</summary>
    public IApplyProgress? Progress { get; init; }

    /// <summary>Step names <see cref="Progress"/> reports.</summary>
    public const string StepBackup = "Backing up", StepReset = "Resetting", StepWrite = "Writing", StepRestore = "Restoring",
        StepMusic = "Music volume";

    /// <summary>
    /// Targets the last plan could not place: a per-app background for a title without one, a file this console
    /// does not have, or a payload in another size or format than the console's file (another firmware).
    /// </summary>
    public List<string> Skipped { get; } = new();

    /// <summary>
    /// Logical paths whose backup the last operation replaced with the console's current file, because the system
    /// had replaced that file since the app last wrote it. The old backups are kept under <see cref="ReplacedFolder"/>.
    /// </summary>
    public List<string> Refreshed { get; } = new();

    private Dictionary<string, string>? _written;
    private Dictionary<string, string>? _originals;
    // The hashes two or more system tile backgrounds share (StockLook), among the backups and among the live files; found
    // once per operation, the live ones before anything is written.
    private HashSet<string>? _sharedBackupTiles, _sharedLiveTiles;
    // Where each named icon or sound of the current apply sits in its container, found in the stock copy by Prepare.
    // Rate is a sound's sample rate (0 for an icon).
    private readonly Dictionary<(string Device, string Layer, string Resource), (int Offset, int Length, int Rate)> _slots = new();

    public ThemeApplier(IDeviceFiles files, Func<long> unixSecondsClock, string dataRoot = "/data/prospero-themes")
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _dataRoot = dataRoot.TrimEnd('/');
        _clock = unixSecondsClock ?? throw new ArgumentNullException(nameof(unixSecondsClock));
    }

    /// <summary>Replaces every target of <paramref name="pack"/> with its payload.</summary>
    public ApplyResult Apply(ThemePack pack)
    {
        try
        {
            PackReport report = pack.Validate();
            if (report.HasErrors)
                return Fail("pack is invalid, nothing was written: " + string.Join("; ", report.Errors.Select(e => e.Message)));

            if (!TryPlan(pack.Manifest, out List<Plan> plans, out string planError))
                return Fail(planError + ". Nothing was written.");
            Refreshed.Clear();
            NoOriginal.Clear();
            _sharedBackupTiles = _sharedLiveTiles = null;

            // Before anything on the system side is written: every target is backed up and its payload is checked
            // against the console's own file, so a target that does not fit is skipped instead of failing halfway.
            Dictionary<string, byte[]> containers;
            try
            {
                containers = Prepare(pack, plans);
            }
            catch (Exception ex)
            {
                return Fail($"preparing the backups failed, nothing was written: {ex.Message}");
            }
            if (plans.Count == 0)
                return Fail($"nothing in this theme fits this console, so nothing was written. Skipped: {string.Join("; ", Skipped)}");

            try
            {
                _files.RemountSystemExRw();
            }
            catch (Exception ex)
            {
                return Fail($"remount failed, nothing was written: {ex.Message}");
            }

            // Unless earlier themes are kept, every theme starts from the defaults: what an earlier theme changed
            // and this one does not write goes back to its original first, so themes replace each other instead
            // of stacking. Files that already hold their original are left alone, and so are files the system
            // replaced meanwhile.
            var planned = new HashSet<string>(plans.Select(p => p.Device), StringComparer.Ordinal);
            var reset = new List<string>();
            List<(string Device, string Backup, string Logical)> earlier =
                KeepEarlierThemes ? [] : BackedUp().Where(b => !planned.Contains(b.Device)).ToList();
            int resetIndex = 0;
            foreach (var (device, backup, logical) in earlier)
            {
                Progress?.Report(StepReset, ++resetIndex, earlier.Count);
                if (!_files.Exists(device) || _files.Length(device) > MaxFileBytes)
                    continue;
                try
                {
                    // Compared by streamed hashes; only the copy that is written or kept is read whole.
                    string liveHash = _files.Hash(device), originalHash = _files.Hash(backup);
                    if (liveHash == originalHash)
                        continue;
                    if (IsReplaced(device, liveHash))
                    {
                        Refresh(logical, backup, _files.ReadAllBytes(device));
                        continue;
                    }
                    WriteVerified(device, _files.ReadAllBytes(backup), originalHash);
                    reset.Add(device);
                }
                catch (Exception ex)
                {
                    return new ApplyResult(false,
                        $"Could not put {device} back to its default ({ex.Message}), so the theme was not applied. " +
                        $"{reset.Count} other file(s) were already back to default.",
                        reset);
                }
            }

            var touched = new List<Plan>();
            Plan? current = null;
            try
            {
                List<IGrouping<string, Plan>> files = plans.GroupBy(p => p.Device, StringComparer.Ordinal).ToList();
                for (int i = 0; i < files.Count; i++)
                {
                    IGrouping<string, Plan> file = files[i];
                    Progress?.Report(StepWrite, i + 1, files.Count);
                    var patches = file.Where(p => ThemePack.IsPatchLayer(p.Target.Layer)).ToList();
                    foreach (Plan plan in file.Where(p => !ThemePack.IsPatchLayer(p.Target.Layer)))
                    {
                        current = plan;
                        byte[] payload = PayloadFor(pack, plan);
                        byte[] music = MusicAtVolume(plan, payload);
                        WriteVerified(plan.Device, music, plan.Unpack || music != payload ? ThemePack.Hash(music) : plan.Target.Sha256,
                            () => touched.Add(plan));
                    }
                    if (patches.Count == 0)
                        continue;

                    // Named icons and sounds inside one container: patched onto the stock copy from the backup, so a
                    // second theme replaces the first one's, or with KeepEarlierThemes onto the copy the app last
                    // wrote, so they combine. Icons and sounds of one container go in one write, so neither undoes
                    // the other. The slots were found in the stock copy; a patch keeps every slot where it is. Taken
                    // out of the dictionary, so no copy of it outlives this write.
                    current = patches[0];
                    byte[]? container = containers[patches[0].Device];
                    containers.Remove(patches[0].Device);
                    foreach (Plan patch in patches)
                    {
                        (int offset, int length, int rate) = _slots[(patch.Device, patch.Target.Layer, patch.Target.Resource!)];
                        byte[] fitted = patch.Target.Layer == ThemePack.VagLayer
                            ? RcoVag.Fit(SoundAtVolume(pack.Payload(patch.Target)), container.AsSpan(offset, length), rate)
                            : RcoSvg.Fit(pack.Payload(patch.Target), length);
                        fitted.CopyTo(container!, offset);
                    }
                    WriteVerified(patches[0].Device, container!, ThemePack.Hash(container!), () => touched.Add(patches[0]));
                    container = null;
                }
            }
            catch (Exception ex)
            {
                // The rollback reads each original whole: no stock container may still be held next to it.
                containers.Clear();
                return RollBack(current?.Target.Path ?? "(unknown target)", ex.Message, touched);
            }

            var written = plans.Select(p => p.Device).ToList();
            try
            {
                WriteState("apply", PackOf(pack.Manifest), plans.Select(p => (p.Device, p.Backup)));
            }
            catch (Exception ex)
            {
                return new ApplyResult(false,
                    $"Applied and verified {written.Count} target(s), but state.json could not be written: {ex.Message}",
                    written);
            }
            var layer = new LookLayer(SourcePath ?? "", pack.Manifest.Id, pack.Manifest.Name, Parts & ThemeParts.In(pack.Manifest));
            WriteLook(Look.Applied(ReadLook(), layer, combine: KeepEarlierThemes));

            string skipped = Skipped.Count == 0 ? "" : $" Skipped: {string.Join("; ", Skipped)}.";
            string defaults = reset.Count == 0 ? "" : $"Put {reset.Count} earlier change(s) back to default. ";
            return new ApplyResult(true,
                $"{ApplyRestartNote(plans, reset)}{RefreshedNote()}{defaults}Applied and verified {written.Count} target(s).{skipped} {NoOriginalNote()}".TrimEnd(), written);
        }
        catch (Exception ex)
        {
            return Fail($"apply failed: {ex.Message}");
        }
        finally
        {
            _unpacked = null;
        }
    }

    /// <summary>Puts every target of <paramref name="pack"/> back to its on-console original.</summary>
    public ApplyResult Restore(ThemePack pack)
    {
        try
        {
            PackReport report = pack.Validate();
            if (report.HasErrors)
                return Fail("pack is invalid, nothing was written: " + string.Join("; ", report.Errors.Select(e => e.Message)));

            if (!TryPlan(pack.Manifest, out List<Plan> plans, out string planError))
                return Fail(planError + ". Nothing was written.");
            Refreshed.Clear();
            NoOriginal.Clear();
            _sharedBackupTiles = _sharedLiveTiles = null;

            // One plan per file: the icons of one container share it.
            plans = plans.GroupBy(p => p.Device, StringComparer.Ordinal).Select(g => g.First()).ToList();
            foreach (Plan plan in plans)
            {
                if (!_files.Exists(plan.Backup))
                    return Fail($"no on-console backup at {plan.Backup} for {plan.Target.Path}, so nothing was written.");
            }

            try
            {
                _files.RemountSystemExRw();
            }
            catch (Exception ex)
            {
                return Fail($"remount failed, nothing was written: {ex.Message}");
            }

            var restored = new List<string>();
            for (int i = 0; i < plans.Count; i++)
            {
                Plan plan = plans[i];
                Progress?.Report(StepRestore, i + 1, plans.Count);
                try
                {
                    if (_files.Length(plan.Device) > MaxFileBytes) continue;   // never written by the app
                    if (RestoreOne(plan.Device, plan.Backup, plan.Logical))
                        restored.Add(plan.Device);
                }
                catch (Exception ex)
                {
                    return new ApplyResult(false,
                        $"Restore stopped at {plan.Target.Path}: {ex.Message}. " +
                        $"{restored.Count} target(s) were restored before it and are listed in Written; no rollback is attempted.",
                        restored);
                }
            }

            try
            {
                WriteState("restore", PackOf(pack.Manifest), plans.Select(p => (p.Device, p.Backup)));
            }
            catch (Exception ex)
            {
                return new ApplyResult(false,
                    $"Restored and verified {restored.Count} target(s), but state.json could not be written: {ex.Message}",
                    restored);
            }
            if (SourcePath is { Length: > 0 } source && ReadLook() is { } look) WriteLook(Look.WithoutPack(look, source));

            return new ApplyResult(true, $"{RestartNote(restored)}{RefreshedNote()}Restored and verified {restored.Count} target(s).", restored);
        }
        catch (Exception ex)
        {
            return Fail($"restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts back every file that has an on-console backup, whichever pack replaced it. Walks the backup
    /// index rather than one pack's targets, so applying A, then B, then restoring leaves nothing of A.
    /// Backups whose path no longer maps to a theme root are skipped; one failure does not stop the rest.
    /// </summary>
    public ApplyResult RestoreOriginals() => RestoreOriginals(ThemePart.All);

    /// <summary>
    /// Puts back the backed-up files of <paramref name="parts"/> (by <see cref="ThemeParts.OfLogical"/>); with every part
    /// the same as <see cref="RestoreOriginals()"/>. The system icons and the UI sounds share one container, so either of
    /// them brings back both, the music too. Some parts leave the theme applied (state "restore-parts" keeps its pack)
    /// unless the look shows nothing any more.
    /// </summary>
    public ApplyResult RestoreOriginals(ThemePart parts)
    {
        bool all = (parts & ThemePart.All) == ThemePart.All;
        if ((parts & (ThemePart.Icons | ThemePart.Sounds)) != 0) parts |= ThemePart.Icons | ThemePart.Sounds;
        try
        {
            List<(string Device, string Backup, string Logical)> plans = BackedUp();
            List<(string Device, string Logical)> orphans = WithoutBackup(plans);
            if (!all)
            {
                plans = plans.Where(p => (ThemeParts.OfLogical(p.Logical) & parts) != 0).ToList();
                orphans = orphans.Where(p => (ThemeParts.OfLogical(p.Logical) & parts) != 0).ToList();
            }
            if (plans.Count == 0 && orphans.Count == 0)
                return Fail(all ? "there are no on-console backups, so nothing was written."
                    : "nothing of the chosen parts has a backup, so nothing was written.");
            Refreshed.Clear();
            NoOriginal.Clear();
            _sharedBackupTiles = _sharedLiveTiles = null;
            // The pack the state names, read before the state is written again.
            StatePack? kept = all ? null : ReadStatePack();
            List<LookLayer>? look = ReadLook();

            try
            {
                _files.RemountSystemExRw();
            }
            catch (Exception ex)
            {
                return Fail($"remount failed, nothing was written: {ex.Message}");
            }

            var restored = new List<string>();
            var errors = new List<string>();
            int missing = 0;
            int index = 0;
            int total = plans.Count + orphans.Count;
            foreach (var (device, backup, logical) in plans)
            {
                Progress?.Report(StepRestore, ++index, total);
                try
                {
                    // A per-app image of a title that was deleted since: nothing to put back.
                    if (!_files.Exists(device)) { missing++; continue; }
                    if (_files.Length(device) > MaxFileBytes) continue;   // never written by the app
                    if (RestoreOne(device, backup, logical))
                        restored.Add(device);
                }
                catch (Exception ex)
                {
                    errors.Add($"{device}: {ex.Message}");
                }
            }
            foreach (var (device, logical) in orphans)
            {
                Progress?.Report(StepRestore, ++index, total);
                try
                {
                    if (!_files.Exists(device)) { missing++; continue; }
                    if (_files.Length(device) > MaxFileBytes) continue;
                    if (RestoreWithoutBackup(device, logical))
                        restored.Add(device);
                }
                catch (Exception ex)
                {
                    errors.Add($"{device}: {ex.Message}");
                }
            }

            List<LookLayer> left = all || look is null ? new List<LookLayer>() : Look.Without(look, parts);
            try
            {
                // Some parts: the theme stays applied with what is left of it, unless the look shows nothing any more.
                if (all) WriteState("restore-originals", null, plans.Select(p => (p.Device, p.Backup)));
                else WriteState("restore-parts", look is not null && left.Count == 0 ? null : kept, plans.Select(p => (p.Device, p.Backup)));
            }
            catch (Exception ex)
            {
                errors.Add($"state.json: {ex.Message}");
            }
            if (all || look is not null) WriteLook(left);

            string gone = missing == 0 ? "" : $" {missing} file(s) no longer exist on the console and were left out.";
            string which = all ? "original(s)" : $"original(s) of {string.Join(", ", ThemeParts.List(parts & ThemePart.All).ConvertAll(ThemeParts.Label))}";
            return errors.Count == 0
                ? new ApplyResult(true, $"{RestartNote(restored)}{NoOriginalNote()}{RefreshedNote()}Restored and verified {restored.Count} {which}.{gone}", restored)
                : new ApplyResult(false,
                    $"{RestartNote(restored)}{NoOriginalNote()}Restored {restored.Count} of {total} original(s); failed: {string.Join("; ", errors)}.", restored);
        }
        catch (Exception ex)
        {
            return Fail($"restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Which targets of <paramref name="manifest"/> have no file on this console, found the way an apply finds them (the
    /// tile icons of the shipped apps, plain .dds backgrounds, a title's own background) but only by asking whether the
    /// files exist: nothing is read whole and nothing is written. The payloads are not compared with the console's
    /// files, so an apply may still skip a target this lets through. Every part counts, whatever <see cref="Parts"/> says.
    /// The all-apps background without <see cref="IncludeAllApps"/> always fits: it then reaches the titles through
    /// <c>bg_hub_default</c>.
    /// </summary>
    public PackFit CheckFit(ThemePackManifest manifest)
    {
        var fits = new HashSet<ThemePackTarget>(ReferenceEqualityComparer.Instance);
        if (TryPlan(manifest, out List<Plan> plans, out _))
        {
            // Each file is asked once (the icons and sounds of a theme share one container); every question is a
            // round trip to the helper. Length is -1 for a file the console does not have.
            var present = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (Plan plan in plans)
            {
                if (fits.Contains(plan.Target)) continue;
                if (!present.TryGetValue(plan.Device, out bool here))
                    present[plan.Device] = here = _files.Length(plan.Device) is var size && size >= 0 && size <= MaxFileBytes;
                if (here) fits.Add(plan.Target);
            }
        }
        var missing = new List<ThemePackTarget>();
        foreach (ThemePackTarget target in manifest.Targets)
        {
            if (!ThemePackManifest.KnownLayers.Contains(target.Layer) || fits.Contains(target)) continue;
            if (target.Path == AssetRoots.AllAppsBackground && !IncludeAllApps) continue;
            missing.Add(target);
        }
        return new PackFit(missing);
    }

    // ---- planning ---------------------------------------------------------

    /// <summary>
    /// Files the home screen reads only when it starts: the tile icons and the container of the system icons and UI
    /// sounds. A restart of SceShellUI shows them without a restart of the console.
    /// </summary>
    public static bool LoadsWithShell(string device) =>
        device.StartsWith("/mnt/rnps/", StringComparison.Ordinal)
        || device.StartsWith(AssetRoots.ShippedRnpsMount + "/", StringComparison.Ordinal)
        || device.EndsWith(".rco", StringComparison.Ordinal);

    /// <summary>
    /// The backup of each of <paramref name="written"/> that <see cref="LoadsWithShell"/>, as (backup, device): what goes
    /// back when the restarted home screen keeps crashing. Files without a backup are left out.
    /// </summary>
    public IReadOnlyList<(string Backup, string Device)> ShellBackups(IEnumerable<string> written)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string device in written)
            if (LoadsWithShell(device)) wanted.Add(device);
        var pairs = new List<(string Backup, string Device)>();
        if (wanted.Count == 0) return pairs;
        foreach (var (device, backup, _) in BackedUp())
            if (wanted.Contains(device)) pairs.Add((backup, device));
        return pairs;
    }

    /// <summary>
    /// Files the app changed whose backup is gone: named in the backup index or in <see cref="WrittenRecord"/>, without
    /// a backup and not among <paramref name="backedUp"/>.
    /// </summary>
    private List<(string Device, string Logical)> WithoutBackup(List<(string Device, string Backup, string Logical)> backedUp)
    {
        var seen = new HashSet<string>(backedUp.Select(b => b.Logical), StringComparer.Ordinal);
        var found = new List<(string Device, string Logical)>();
        foreach (string logical in ReadIndex())
            if (seen.Add(logical) && AssetRoots.TryResolve(logical, out string device, out _))
                found.Add((OnDevice(_files, logical, device), logical));
        foreach (string device in Written().Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (AssetRoots.TryLogical(device, out string logical) && seen.Add(logical)
                && !_files.Exists($"{_dataRoot}/{BackupFolder}/{logical}"))
                found.Add((device, logical));
        return found;
    }

    /// <summary>
    /// Puts a file whose backup is gone back to its original when the console still has one (<see cref="OriginalSource"/>:
    /// the shipped copy of a tile icon, or the file already is the original); that original becomes its backup again.
    /// Otherwise the file is listed in <see cref="NoOriginal"/> and left as it is.
    /// </summary>
    private bool RestoreWithoutBackup(string device, string logical)
    {
        if (OriginalSource(logical, device, _files.Hash(device), strict: true) is not { } source)
        {
            if (!NoOriginal.Contains(logical)) NoOriginal.Add(logical);
            return false;
        }
        byte[] original = _files.ReadAllBytes(source);
        if (source != device) WriteVerified(device, original, ThemePack.Hash(original));
        WriteBackup(logical, $"{_dataRoot}/{BackupFolder}/{logical}", original);
        AddToIndex(logical);
        return true;
    }

    /// <summary>
    /// Every file with an on-console backup, from the backup index. Backups whose path no longer maps to a
    /// theme root are skipped.
    /// </summary>
    private List<(string Device, string Backup, string Logical)> BackedUp()
    {
        var entries = new List<(string Device, string Backup, string Logical)>();
        foreach (string logical in ReadIndex())
        {
            string backup = $"{_dataRoot}/{BackupFolder}/{logical}";
            if (AssetRoots.TryResolve(logical, out string device, out _) && _files.Exists(backup))
                entries.Add((OnDevice(_files, logical, device), backup, logical));
        }
        return entries;
    }

    /// <summary>
    /// Backs up every planned file and checks each payload against the console's own file (the backup: the live
    /// file may hold an earlier theme). Plans that do not fit are removed and listed in <see cref="Skipped"/>.
    /// Writes only under the data root. Returns the stock copy of each container with named icons or sounds, which
    /// they are patched onto.
    /// </summary>
    private Dictionary<string, byte[]> Prepare(ThemePack pack, List<Plan> plans)
    {
        var containers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var drop = new HashSet<Plan>();
        _slots.Clear();
        List<IGrouping<string, Plan>> files = plans.GroupBy(p => p.Device, StringComparer.Ordinal).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            IGrouping<string, Plan> file = files[i];
            Progress?.Report(StepBackup, i + 1, files.Count);
            Plan first = file.First();
            if (!_files.Exists(first.Device))
            {
                Skipped.Add($"{first.Logical}: this console has no such file");
                drop.UnionWith(file);
                continue;
            }
            if (_files.Length(first.Device) is var size && size > MaxFileBytes)
            {
                Skipped.Add($"{first.Logical}: {size / (1024 * 1024)} MB is too large for the app on this firmware");
                drop.UnionWith(file);
                continue;
            }
            byte[] stock = PrepareBackup(first);
            bool patched = false;
            foreach (Plan plan in file)
            {
                byte[] payload;
                try
                {
                    payload = PayloadFor(pack, plan);
                }
                catch (InvalidDataException ex)
                {
                    Skipped.Add($"{plan.Logical}: the theme's file could not be unpacked ({ex.Message})");
                    drop.Add(plan);
                    continue;
                }
                if (ThemePack.IsPatchLayer(plan.Target.Layer))
                {
                    patched = true;
                    try
                    {
                        if (plan.Target.Layer == ThemePack.VagLayer)
                        {
                            (int offset, int length, int rate) = RcoVag.Find(stock, plan.Target.Resource!);
                            RcoVag.Fit(payload, stock.AsSpan(offset, length), rate);
                            _slots[(plan.Device, plan.Target.Layer, plan.Target.Resource!)] = (offset, length, rate);
                        }
                        else
                        {
                            (int offset, int length) = RcoSvg.Find(stock, plan.Target.Resource!);
                            RcoSvg.Fit(payload, length);
                            _slots[(plan.Device, plan.Target.Layer, plan.Target.Resource!)] = (offset, length, 0);
                        }
                    }
                    catch (ThemePackException ex)
                    {
                        Skipped.Add($"{plan.Target.Resource}: {ex.Message}");
                        drop.Add(plan);
                    }
                }
                else if (PayloadCompat.Mismatch(payload, stock, sameSize: !IsTitleBackground(plan.Target.Path)) is { } mismatch)
                {
                    Skipped.Add($"{plan.Logical}: {mismatch}");
                    drop.Add(plan);
                }
            }
            if (!patched)
                continue;
            if (KeepEarlierThemes && IsLastWritten(first.Device, stock))
            {
                // The stock copy is let go before the live one is read: one whole copy at a time.
                stock = null!;
                containers[first.Device] = _files.ReadAllBytes(first.Device);
            }
            else
            {
                containers[first.Device] = stock;
            }
        }
        plans.RemoveAll(drop.Contains);
        return containers;
    }

    private static bool IsTitleBackground(string targetPath) =>
        targetPath == AssetRoots.AllAppsBackground || AssetRoots.IsAppBackground(targetPath, out _);

    /// <summary>
    /// Returns the console's original of a file: its backup, made now from the live file when there is none yet,
    /// or replaced by the live file when the system replaced it since the app wrote it. An existing backup is
    /// otherwise never overwritten. Holds one whole copy at a time: the live file is compared by a streamed hash.
    /// </summary>
    private byte[] PrepareBackup(Plan plan)
    {
        string liveHash = _files.Hash(plan.Device);
        if (!_files.Exists(plan.Backup))
        {
            // The first apply, or the backup was deleted: the live file is the original only when it is not a theme's.
            if (OriginalSource(plan.Logical, plan.Device, liveHash, strict: false) is not { } source)
            {
                if (!NoOriginal.Contains(plan.Logical)) NoOriginal.Add(plan.Logical);
                return _files.ReadAllBytes(plan.Device);
            }
            byte[] first = _files.ReadAllBytes(source);
            if (source == plan.Device && LooksThemed(plan.Logical, liveHash, () => first, live: true))
            {
                // Another tool's theme, not the original: the app keeps no backup a restore would take for one.
                if (!NoOriginal.Contains(plan.Logical)) NoOriginal.Add(plan.Logical);
                return first;
            }
            WriteBackup(plan.Logical, plan.Backup, first);
            AddToIndex(plan.Logical);
            return first;
        }
        AddToIndex(plan.Logical);   // a backup made before the index existed
        byte[]? stock = _files.ReadAllBytes(plan.Backup);
        string stockHash = ThemePack.Hash(stock);
        bool listed = Stock.TryStock(plan.Logical, out string catalog);
        if (listed ? stockHash == catalog : !LooksThemed(plan.Logical, stockHash, () => stock, live: false))
            RecordOriginal(plan.Logical, stockHash);   // a backup made before the record
        else if (!listed && !NoOriginal.Contains(plan.Logical))
            NoOriginal.Add(plan.Logical);   // a backup made from a theme's file: a restore will not put it back
        // A backup that is not this firmware's original while the live file is: the live file becomes the backup.
        bool wrongBackup = listed && stockHash != catalog && liveHash == catalog;
        if (!wrongBackup && (liveHash == stockHash || !IsReplaced(plan.Device, liveHash)))
            return stock;
        stock = null;
        byte[] live = _files.ReadAllBytes(plan.Device);
        Refresh(plan.Logical, plan.Backup, live, systemUpdate: !wrongBackup);
        return live;
    }

    /// <summary>
    /// Where the original of a file is when its backup is gone: the file itself (<paramref name="liveHash"/>) when it is
    /// not a theme's, else the shipped copy of a tile icon. Null when the console has none. A catalog hash
    /// (<see cref="Stock"/>) and a recorded original (<see cref="OriginalsRecord"/>) decide; then the app's own record
    /// (<see cref="WrittenRecord"/>) says what it wrote there. With nothing known the file counts as original unless
    /// <paramref name="strict"/>: an apply may back it up as before, a restore does not claim it is the default.
    /// </summary>
    private string? OriginalSource(string logical, string device, string liveHash, bool strict)
    {
        bool known = Originals().TryGetValue(logical, out string? recorded);
        if (known && recorded == liveHash) return device;
        if (AssetRoots.Shipped(logical, out _, out string shipped) && shipped != device && _files.Exists(shipped))
        {
            // A tile icon with the firmware's own copy next to it. Sony's servers update the live bundles, so the live
            // file is kept when the system replaced what the app wrote; otherwise the shipped copy is the original.
            if (_files.Hash(shipped) == liveHash) return device;
            return Written().TryGetValue(device, out string? wrote) && wrote != liveHash ? device : shipped;
        }
        if (Stock.TryStock(logical, out string stock)) return liveHash == stock ? device : null;
        if (known || Written().TryGetValue(device, out string? last) && last == liveHash) return null;
        return strict ? null : device;
    }

    /// <summary>
    /// Whether new icons go onto the live container when earlier themes are kept: it differs from the stock copy, is
    /// the app's own (see <see cref="IsReplaced"/>) and as long as the stock copy, so every slot is where the stock
    /// copy has it.
    /// </summary>
    private bool IsLastWritten(string device, byte[] stock)
    {
        string liveHash = _files.Hash(device);
        return liveHash != ThemePack.Hash(stock) && !IsReplaced(device, liveHash) && _files.Length(device) == stock.Length;
    }

    /// <summary>
    /// Whether the live file (already known to differ from its backup) is neither what the app last wrote there.
    /// Files without a record (written by an older build) count as the app's own, as they always were.
    /// </summary>
    private bool IsReplaced(string device, string liveHash) =>
        Written().TryGetValue(device, out string? last) && liveHash != last;

    /// <summary>Keeps the old backup under <see cref="ReplacedFolder"/> and makes the live file the new original.</summary>
    private void Refresh(string logical, string backup, byte[] live, bool systemUpdate = true)
    {
        string stamp = FormatUtc(_clock()).Replace(":", "").Replace("-", "");
        string aside = $"{_dataRoot}/{ReplacedFolder}/{stamp}/{logical}";
        _files.CreateDirectoryRecursive(ParentOf(aside));
        _files.Move(backup, aside);
        WriteBackup(logical, backup, live);
        if (systemUpdate) Refreshed.Add(logical);
    }

    /// <summary>
    /// The home screen reads tile icons (app.db points at them with a cached <c>?ts=</c> stamp), the system symbols and
    /// UI sounds in the icon container, and the music when the console starts, so a change to them only shows after the
    /// next restart. Backgrounds change right away. <paramref name="sounds"/> says UI sounds in the container changed. It
    /// leads the message, because the notice shows two lines at most.
    /// </summary>
    private static string RestartNote(IEnumerable<string> devices, bool sounds = false)
    {
        bool icons = false;
        foreach (string d in devices)
        {
            if (LoadsWithShell(d)) icons = true;
            if (d.EndsWith(".at9", StringComparison.OrdinalIgnoreCase)) sounds = true;
        }
        return (icons, sounds) switch
        {
            (true, true) => "Icons and sounds change after the next restart of the console. ",
            (true, false) => "Icons change after the next restart of the console. ",
            (false, true) => "Sounds change after the next restart of the console. ",
            _ => "",
        };
    }

    /// <summary>The restart note of an apply: a container that only got sounds does not claim its icons changed.</summary>
    private static string ApplyRestartNote(List<Plan> plans, List<string> reset)
    {
        bool sounds = plans.Any(p => p.Target.Layer == ThemePack.VagLayer);
        var devices = plans.Where(p => !(p.Device.EndsWith(".rco", StringComparison.Ordinal)
                && plans.All(q => q.Device != p.Device || q.Target.Layer == ThemePack.VagLayer)))
            .Select(p => p.Device).Concat(reset);
        return RestartNote(devices, sounds);
    }

    private string NoOriginalNote() => NoOriginal.Count == 0 ? "" :
        $"{NoOriginal.Count} changed file(s) have no original on this console any more and stay as they are " +
        $"({string.Join(", ", NoOriginal.Take(2))}{(NoOriginal.Count > 2 ? ", …" : "")}). ";

    private string RefreshedNote() => Refreshed.Count == 0 ? "" :
        $"{Refreshed.Count} file(s) had been replaced by a system update; that version is now their default " +
        $"(the old backups are kept in {ReplacedFolder}). ";

    /// <summary>
    /// Puts one backup back. False when the live file is gone, or was replaced by the system: then the live file
    /// is the new default and becomes the backup instead. Compared by streamed hashes; only the copy that is
    /// written is read whole.
    /// </summary>
    private bool RestoreOne(string device, string backup, string logical)
    {
        if (!_files.Exists(device)) return false;
        string liveHash = _files.Hash(device), originalHash = _files.Hash(backup);
        byte[]? original = null;
        if (LooksThemed(logical, originalHash, () => original ??= _files.ReadAllBytes(backup), live: false))
        {
            // No catalog knows the file, and the backup looks like a theme's (made from another tool's file): it is never
            // put back, nor recorded as the original.
            if (!NoOriginal.Contains(logical)) NoOriginal.Add(logical);
            return false;
        }
        if (Stock.TryStock(logical, out string stock) && originalHash != stock)
        {
            // Not this firmware's original (a backup made from a theme's file): it is never put back. When the live file
            // is the original, it becomes the backup.
            if (liveHash != stock)
            {
                if (!NoOriginal.Contains(logical)) NoOriginal.Add(logical);
                return false;
            }
            Refresh(logical, backup, _files.ReadAllBytes(device), systemUpdate: false);
            return true;
        }
        RecordOriginal(logical, originalHash);
        if (liveHash == originalHash) return true;
        if (IsReplaced(device, liveHash))
        {
            Refresh(logical, backup, _files.ReadAllBytes(device));
            return false;
        }
        WriteVerified(device, original ?? _files.ReadAllBytes(backup), originalHash);
        return true;
    }

    /// <summary>
    /// Whether a file no catalog knows looks like a theme's rather than the console's original (<see cref="StockLook"/>):
    /// an icon container without the titles of the theme icons, or a system tile background another tile has too. With
    /// <paramref name="live"/> the tiles are compared with the live files, else with the backups. Only an icon container
    /// is read (<paramref name="read"/>). A file with a catalog hash is left to the catalog.
    /// </summary>
    private bool LooksThemed(string logical, string sha256, Func<byte[]> read, bool live)
    {
        if (Stock.TryStock(logical, out _)) return false;
        if (logical.EndsWith(".rco", StringComparison.Ordinal)) return StockLook.UntitledThemeIcons(read()).Count > 0;
        if (!StockLook.IsTileBackground(logical)) return false;
        HashSet<string> shared = live ? _sharedLiveTiles ??= SharedTiles(live: true) : _sharedBackupTiles ??= SharedTiles(live: false);
        return shared.Contains(sha256);
    }

    /// <summary>The hashes two or more system tile backgrounds share, among the live files or among their backups.</summary>
    private HashSet<string> SharedTiles(bool live)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string compressed in AssetRoots.SystemTileBackgrounds)
            foreach (string logical in new[] { compressed, AssetRoots.Uncompressed(compressed)! })
            {
                string path = !live ? $"{_dataRoot}/{BackupFolder}/{logical}"
                    : AssetRoots.TryResolve(logical, out string device, out _) ? device : "";
                if (path.Length > 0 && _files.Exists(path)) hashes[logical] = _files.Hash(path);
            }
        return StockLook.SharedTileHashes(hashes);
    }

    // Logical is the <root>/<relative> path the backup and the index use; it differs from Target.Path for the
    // all-apps background, which expands to one plan per title, for the system tiles that take the main background,
    // and for a compressed texture this firmware keeps as plain DDS (Unpack: the payload is written decoded).
    private sealed record Plan(ThemePackTarget Target, string Device, string Backup, string Logical, bool Unpack = false);

    // The last decoded payload and its target path: the main background goes to up to 14 files in a row. Only one
    // is kept, because a decoded background is 8 MB and a theme can carry 16 of them.
    private (string Path, byte[] Data)? _unpacked;

    private byte[] PayloadFor(ThemePack pack, Plan plan)
    {
        if (!plan.Unpack) return pack.Payload(plan.Target);
        if (_unpacked is not { } last || last.Path != plan.Target.Path)
            _unpacked = last = (plan.Target.Path, Zstd.Decompress(pack.Payload(plan.Target)));
        return last.Data;
    }

    /// <summary>
    /// The plan for one logical file. When the console has no <c>X.dds.zst</c> but an <c>X.dds</c> (firmware before
    /// 13.00), it targets that file and the payload is unpacked before it is checked and written.
    /// </summary>
    private Plan Locate(ThemePackTarget target, string logical, string device)
    {
        if (AssetRoots.Uncompressed(logical) is { } plain && !_files.Exists(device)
            && AssetRoots.TryResolve(plain, out string plainDevice, out _) && _files.Exists(plainDevice))
            return new Plan(target, plainDevice, $"{_dataRoot}/{BackupFolder}/{plain}", plain, Unpack: true);
        return new Plan(target, OnDevice(_files, logical, device), $"{_dataRoot}/{BackupFolder}/{logical}", logical);
    }

    /// <summary>
    /// The console file a logical path stands for. A tile icon (<c>rnps/&lt;app&gt;/…</c>) is in the shipped app
    /// (<see cref="AssetRoots.ShippedRnpsMount"/>) when the console has no live copy of that app: 13.60 and 10.01
    /// have none. Every other path is <paramref name="device"/>. The backup keeps the logical path either way.
    /// </summary>
    internal static string OnDevice(IDeviceFiles files, string logical, string device) =>
        AssetRoots.Shipped(logical, out string liveApp, out string shipped) && !files.Exists(liveApp) ? shipped : device;

    private bool TryPlan(ThemePackManifest manifest, out List<Plan> plans, out string error)
    {
        plans = new List<Plan>();
        var perApp = new List<Plan>();
        Skipped.Clear();
        _unpacked = null;
        foreach (ThemePackTarget target in manifest.Targets)
        {
            // An unknown layer is skipped, as ThemePack.Validate's warning promises.
            if (!ThemePackManifest.KnownLayers.Contains(target.Layer))
                continue;
            // A part the user left out is treated as if the pack did not have it.
            if ((ThemeParts.Of(target) & Parts) == 0)
                continue;

            if (target.Path == AssetRoots.AllAppsBackground)
            {
                if (!IncludeAllApps)
                    continue;
                foreach (string logical in AllAppBackgrounds())
                {
                    AssetRoots.TryResolve(logical, out string each, out _);
                    plans.Add(new Plan(target, each, $"{_dataRoot}/{BackupFolder}/{logical}", logical));
                }
                continue;
            }

            if (AssetRoots.IsAppBackground(target.Path, out string titleId))
            {
                string? own = OwnBackground(titleId);
                if (own is null)
                {
                    Skipped.Add($"{titleId}: has no background of its own");
                    continue;
                }
                AssetRoots.TryResolve(own, out string each, out _);
                perApp.Add(new Plan(target, each, $"{_dataRoot}/{BackupFolder}/{own}", own));
                continue;
            }

            if (!AssetRoots.TryResolve(target.Path, out string device, out string reason))
            {
                error = $"{target.Path}: {reason}";
                return false;
            }

            plans.Add(Locate(target, target.Path, device));
        }

        // The system tiles (Media Gallery, All Apps, Game Library…) each show their own stock background, not
        // bg_hub_default: they get the theme's main background too, unless the pack names the file itself.
        if (plans.FirstOrDefault(p => p.Target.Path == AssetRoots.HubBackground) is { } hub)
        {
            foreach (string logical in AssetRoots.SystemTileBackgrounds)
            {
                if (manifest.Targets.Any(t => t.Path == logical)) continue;
                AssetRoots.TryResolve(logical, out string tile, out _);
                Plan plan = Locate(hub.Target, logical, tile);
                if (_files.Exists(plan.Device))
                    plans.Add(plan);
            }
        }

        // A per-app background wins over the all-apps one for its title: drop the all-apps plan for that file.
        foreach (Plan app in perApp)
        {
            plans.RemoveAll(p => p.Device == app.Device && p.Target.Path == AssetRoots.AllAppsBackground);
            plans.Add(app);
        }

        if (plans.Count == 0)
        {
            error = Parts == ThemePart.All ? "pack has no applicable targets" : "the chosen parts of this theme have no applicable targets";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Every title's own entry background that exists on the console: homebrew keeps it in
    /// <c>/user/app/&lt;TID&gt;/sce_sys/pic0.dds</c>, store apps in <c>/user/appmeta/&lt;TID&gt;/pic0.dds</c>.
    /// </summary>
    /// <summary>The logical path of a title's own entry background, or null when it has none.</summary>
    private string? OwnBackground(string titleId)
    {
        if (_files.Exists($"/user/app/{titleId}/sce_sys/pic0.dds")) return $"{AssetRoots.UserApp}/{titleId}/sce_sys/pic0.dds";
        if (_files.Exists($"/user/appmeta/{titleId}/pic0.dds")) return $"{AssetRoots.AppMeta}/{titleId}/pic0.dds";
        return null;
    }

    private IEnumerable<string> AllAppBackgrounds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string tid in _files.ListDirectories("/user/app/").OrderBy(t => t, StringComparer.Ordinal))
            if (_files.Exists($"/user/app/{tid}/sce_sys/pic0.dds") && seen.Add(tid))
                yield return $"{AssetRoots.UserApp}/{tid}/sce_sys/pic0.dds";
        foreach (string tid in _files.ListDirectories("/user/appmeta/").OrderBy(t => t, StringComparer.Ordinal))
            if (!seen.Contains(tid) && _files.Exists($"/user/appmeta/{tid}/pic0.dds"))
                yield return $"{AssetRoots.AppMeta}/{tid}/pic0.dds";
    }

    // ---- device primitives ------------------------------------------------

    /// <summary>Stores <paramref name="live"/> as a backup and reads it back (as a streamed hash).</summary>
    private void WriteBackup(string logical, string backup, byte[] live)
    {
        _files.CreateDirectoryRecursive(ParentOf(backup));

        // Written to a temporary and renamed, so an interrupted copy never becomes the permanent "original".
        string tmp = backup + ".tmp";
        _files.WriteAllBytes(tmp, live);
        _files.Move(tmp, backup);

        string sha256 = ThemePack.Hash(live);
        if (_files.Hash(backup) != sha256)
            throw new InvalidOperationException($"backup {backup} does not match the live file");
        RecordOriginal(logical, sha256);
    }

    // The theme's home music at MusicVolume; the payload itself at full volume, for any other file, or when the codec
    // cannot read it (then Skipped says so and the music plays as the theme made it).
    private byte[] MusicAtVolume(Plan plan, byte[] payload)
    {
        string name = plan.Device[(plan.Device.LastIndexOf('/') + 1)..];
        if (MusicVolume >= 1.0 || double.IsNaN(MusicVolume) || !name.StartsWith("bgm_", StringComparison.Ordinal)
            || !name.EndsWith(".at9", StringComparison.Ordinal))
            return payload;
        try
        {
            int last = 0;
            return Atrac9Codec.ScaleVolume(payload, Math.Max(0, MusicVolume), fraction =>
            {
                int percent = Math.Clamp((int)(fraction * 100), 1, 100);
                if (percent != last) Progress?.Report(StepMusic, last = percent, 100);
            });
        }
        catch (Exception ex) when (ex is ThemePackException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            Skipped.Add($"{plan.Logical}: the music is at its own volume ({ex.Message})");
            return payload;
        }
    }

    // The theme's mono VAG at UiSoundVolume; at full volume the payload itself, not copied or re-encoded.
    private byte[] SoundAtVolume(byte[] vag) =>
        UiSoundVolume >= 1.0 || double.IsNaN(UiSoundVolume) ? vag : VagCodec.ScaleVolume(vag, UiSoundVolume);

    /// <summary>
    /// Writes to <c>{device}.tmp</c>, renames over the device path, then reads back (as a streamed hash) and
    /// compares. The hash is recorded in <see cref="WrittenRecord"/>. <paramref name="renamed"/> runs once the
    /// device file is replaced: a write that fails before that has left the device file as it was.
    /// </summary>
    private void WriteVerified(string device, byte[] data, string expectedSha256, Action? renamed = null)
    {
        string tmp = device + ".tmp";
        _files.WriteAllBytes(tmp, data);
        _files.Move(tmp, device);
        renamed?.Invoke();

        string actual = _files.Hash(device);
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"verification failed for {device}: read back {actual}, expected {expectedSha256}");
        RecordWritten(device, actual);
    }

    private Dictionary<string, string> Written() => _written ??= ReadRecord(WrittenRecord);

    private void RecordWritten(string device, string sha256) => Record(WrittenRecord, Written(), device, sha256);

    private Dictionary<string, string> Originals() => _originals ??= ReadRecord(OriginalsRecord);

    private void RecordOriginal(string logical, string sha256) => Record(OriginalsRecord, Originals(), logical, sha256);

    // A "sha256 path" record (WrittenRecord, OriginalsRecord), by path.
    private Dictionary<string, string> ReadRecord(string name)
    {
        var record = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = $"{_dataRoot}/{name}";
        if (!_files.Exists(path)) return record;
        foreach (string line in Encoding.UTF8.GetString(_files.ReadAllBytes(path)).Split('\n'))
        {
            int space = line.IndexOf(' ');
            if (space == 64) record[line[(space + 1)..].TrimEnd('\r')] = line[..space];
        }
        return record;
    }

    private void Record(string name, Dictionary<string, string> record, string key, string sha256)
    {
        if (record.TryGetValue(key, out string? known) && known == sha256) return;
        record[key] = sha256;
        string path = $"{_dataRoot}/{name}";
        _files.CreateDirectoryRecursive(ParentOf(path));
        string text = string.Concat(record.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Value} {e.Key}\n"));
        _files.WriteAllBytes(path + ".tmp", Encoding.UTF8.GetBytes(text));
        _files.Move(path + ".tmp", path);
    }

    /// <summary>Restores each already-touched target from its backup, newest first.</summary>
    private ApplyResult RollBack(string failedAt, string reason, List<Plan> touched)
    {
        var stuck = new List<string>();
        var errors = new List<string>();

        for (int i = touched.Count - 1; i >= 0; i--)
        {
            Plan plan = touched[i];
            try
            {
                byte[] original = _files.ReadAllBytes(plan.Backup);
                WriteVerified(plan.Device, original, ThemePack.Hash(original));
            }
            catch (Exception ex)
            {
                stuck.Add(plan.Device);
                errors.Add($"{plan.Device}: {ex.Message}");
            }
        }

        string message = $"Apply failed at {failedAt}: {reason}. ";
        if (touched.Count == 0)
            message += "Nothing had been written yet.";
        else if (errors.Count == 0)
            message += $"Rolled back {touched.Count} target(s) to their backups.";
        else
            message += $"Rollback FAILED for {string.Join(", ", stuck)}: {string.Join("; ", errors)}";

        return new ApplyResult(false, message, stuck);
    }

    private List<string> ReadIndex()
    {
        string path = $"{_dataRoot}/{BackupIndex}";
        if (!_files.Exists(path)) return new List<string>();
        return Encoding.UTF8.GetString(_files.ReadAllBytes(path))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    private void AddToIndex(string logicalPath)
    {
        List<string> entries = ReadIndex();
        if (entries.Contains(logicalPath)) return;
        entries.Add(logicalPath);
        entries.Sort(StringComparer.Ordinal);
        string path = $"{_dataRoot}/{BackupIndex}";
        _files.CreateDirectoryRecursive(ParentOf(path));
        _files.WriteAllBytes(path + ".tmp", Encoding.UTF8.GetBytes(string.Join("\n", entries) + "\n"));
        _files.Move(path + ".tmp", path);
    }

    // ---- state ------------------------------------------------------------

    // The pack state.json names: the manifest's, or the one an earlier state named (a restore of some parts keeps it).
    private readonly record struct StatePack(string Id, string Name, string Version);

    private static StatePack PackOf(ThemePackManifest manifest) => new(manifest.Id, manifest.Name, manifest.Version);

    /// <summary>The pack of the theme state.json says is applied (also after a restore of some parts), or null.</summary>
    private StatePack? ReadStatePack()
    {
        try
        {
            string path = _dataRoot + "/state.json";
            if (!_files.Exists(path)) return null;
            string json = Encoding.UTF8.GetString(_files.ReadAllBytes(path));
            if (AppliedTheme.Read(json) is null || !SharpProspero.Storage.JsonValue.TryParse(json, out var root)) return null;
            var pack = root["pack"];
            return new StatePack(pack.GetString("id"), pack.GetString("name"), pack.GetString("version"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The look as the last operation left it; null when there is no look file (one made before 01.000.021).</summary>
    private List<LookLayer>? ReadLook()
    {
        try
        {
            string path = _dataRoot + "/" + Look.FileName;
            return _files.Exists(path) ? Look.Parse(Encoding.UTF8.GetString(_files.ReadAllBytes(path))) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the look. A failure is ignored: the files on the console are what matters, and the look only helps to
    /// apply them again or save them as a theme.
    /// </summary>
    private void WriteLook(IReadOnlyList<LookLayer> look)
    {
        try
        {
            string path = _dataRoot + "/" + Look.FileName;
            _files.CreateDirectoryRecursive(_dataRoot);
            _files.WriteAllBytes(path + ".tmp", Encoding.UTF8.GetBytes(Look.Serialize(look)));
            _files.Move(path + ".tmp", path);
        }
        catch
        {
            // see above
        }
    }

    private void WriteState(string action, StatePack? pack, IEnumerable<(string Device, string Backup)> plans)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();

            if (pack is not { } p)
            {
                writer.WriteNull("pack");
            }
            else
            {
                writer.WriteStartObject("pack");
                writer.WriteString("id", p.Id);
                writer.WriteString("name", p.Name);
                writer.WriteString("version", p.Version);
                writer.WriteEndObject();
            }

            writer.WriteString("action", action);
            writer.WriteString("when_utc", FormatUtc(_clock()));
            if (Firmware is not null)
                writer.WriteString("firmware", Firmware);

            writer.WriteStartArray("targets");
            foreach (var (device, backup) in plans)
            {
                writer.WriteStartObject();
                writer.WriteString("path", device);
                writer.WriteString("backup", backup);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        _files.CreateDirectoryRecursive(_dataRoot);
        string statePath = _dataRoot + "/state.json";
        string tmp = statePath + ".tmp";
        _files.WriteAllBytes(tmp, buffer.WrittenSpan.ToArray());
        _files.Move(tmp, statePath);
    }

    /// <summary>ISO 8601 UTC for Unix seconds, by the days-to-civil conversion rather than DateTime (see _clock).</summary>
    public static string FormatUtc(long unixSeconds)
    {
        long days = Math.DivRem(unixSeconds, 86400, out long second);
        if (second < 0) { second += 86400; days--; }
        days += 719468;
        long era = (days >= 0 ? days : days - 146096) / 146097;
        long dayOfEra = days - era * 146097;
        long yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36524 - dayOfEra / 146096) / 365;
        long dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
        long mp = (5 * dayOfYear + 2) / 153;
        long day = dayOfYear - (153 * mp + 2) / 5 + 1;
        long month = mp < 10 ? mp + 3 : mp - 9;
        long year = yearOfEra + era * 400 + (month <= 2 ? 1 : 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{year:D4}-{month:D2}-{day:D2}T{second / 3600:D2}:{second / 60 % 60:D2}:{second % 60:D2}Z");
    }

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static ApplyResult Fail(string message) => new(false, message, Array.Empty<string>());
}
