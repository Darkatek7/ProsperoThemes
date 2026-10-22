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
    /// Largest console file the applier reads or writes as a whole: ThemeHelper's write limit (MaxWrite, the same
    /// number). A larger file is left alone without being read; the app cannot have written it, so it still holds
    /// its original. The icon container is about 100 MB on firmware before 13.00: the applier holds one whole copy
    /// of a file at a time and compares and verifies by streamed hashes (<see cref="IDeviceFiles.Hash"/>), because
    /// holding several copies ran the app out of memory.
    /// </summary>
    public long MaxFileBytes { get; init; } = 160L * 1024 * 1024;

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

            if (!TryPlan(pack, out List<Plan> plans, out string planError))
                return Fail(planError + ". Nothing was written.");
            Refreshed.Clear();

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
            foreach (var (device, backup, logical) in KeepEarlierThemes ? [] : BackedUp())
            {
                if (planned.Contains(device) || !_files.Exists(device) || _files.Length(device) > MaxFileBytes)
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
                foreach (IGrouping<string, Plan> file in plans.GroupBy(p => p.Device, StringComparer.Ordinal))
                {
                    var patches = file.Where(p => ThemePack.IsPatchLayer(p.Target.Layer)).ToList();
                    foreach (Plan plan in file.Where(p => !ThemePack.IsPatchLayer(p.Target.Layer)))
                    {
                        current = plan;
                        byte[] payload = PayloadFor(pack, plan);
                        WriteVerified(plan.Device, payload, plan.Unpack ? ThemePack.Hash(payload) : plan.Target.Sha256,
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
                            ? RcoVag.Fit(pack.Payload(patch.Target), container.AsSpan(offset, length), rate)
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
                WriteState("apply", pack.Manifest, plans.Select(p => (p.Device, p.Backup)));
            }
            catch (Exception ex)
            {
                return new ApplyResult(false,
                    $"Applied and verified {written.Count} target(s), but state.json could not be written: {ex.Message}",
                    written);
            }

            string skipped = Skipped.Count == 0 ? "" : $" Skipped: {string.Join("; ", Skipped)}.";
            string defaults = reset.Count == 0 ? "" : $"Put {reset.Count} earlier change(s) back to default. ";
            return new ApplyResult(true,
                $"{ApplyRestartNote(plans, reset)}{RefreshedNote()}{defaults}Applied and verified {written.Count} target(s).{skipped}", written);
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

            if (!TryPlan(pack, out List<Plan> plans, out string planError))
                return Fail(planError + ". Nothing was written.");
            Refreshed.Clear();

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
            foreach (Plan plan in plans)
            {
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
                WriteState("restore", pack.Manifest, plans.Select(p => (p.Device, p.Backup)));
            }
            catch (Exception ex)
            {
                return new ApplyResult(false,
                    $"Restored and verified {restored.Count} target(s), but state.json could not be written: {ex.Message}",
                    restored);
            }

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
    public ApplyResult RestoreOriginals()
    {
        try
        {
            List<(string Device, string Backup, string Logical)> plans = BackedUp();
            if (plans.Count == 0)
                return Fail("there are no on-console backups, so nothing was written.");
            Refreshed.Clear();

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
            foreach (var (device, backup, logical) in plans)
            {
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

            try
            {
                WriteState("restore-originals", null, plans.Select(p => (p.Device, p.Backup)));
            }
            catch (Exception ex)
            {
                errors.Add($"state.json: {ex.Message}");
            }

            string gone = missing == 0 ? "" : $" {missing} file(s) no longer exist on the console and were left out.";
            return errors.Count == 0
                ? new ApplyResult(true, $"{RestartNote(restored)}{RefreshedNote()}Restored and verified {restored.Count} original(s).{gone}", restored)
                : new ApplyResult(false,
                    $"{RestartNote(restored)}Restored {restored.Count} of {plans.Count} original(s); failed: {string.Join("; ", errors)}.", restored);
        }
        catch (Exception ex)
        {
            return Fail($"restore failed: {ex.Message}");
        }
    }

    // ---- planning ---------------------------------------------------------

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
                entries.Add((device, backup, logical));
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
        foreach (IGrouping<string, Plan> file in plans.GroupBy(p => p.Device, StringComparer.Ordinal).ToList())
        {
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
        if (!_files.Exists(plan.Backup))
        {
            byte[] first = _files.ReadAllBytes(plan.Device);
            WriteBackup(plan.Backup, first);
            AddToIndex(plan.Logical);
            return first;
        }
        AddToIndex(plan.Logical);   // a backup made before the index existed
        string liveHash = _files.Hash(plan.Device);
        byte[]? stock = _files.ReadAllBytes(plan.Backup);
        if (liveHash == ThemePack.Hash(stock) || !IsReplaced(plan.Device, liveHash))
            return stock;
        stock = null;
        byte[] live = _files.ReadAllBytes(plan.Device);
        Refresh(plan.Logical, plan.Backup, live);
        return live;
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
    private void Refresh(string logical, string backup, byte[] live)
    {
        string stamp = FormatUtc(_clock()).Replace(":", "").Replace("-", "");
        string aside = $"{_dataRoot}/{ReplacedFolder}/{stamp}/{logical}";
        _files.CreateDirectoryRecursive(ParentOf(aside));
        _files.Move(backup, aside);
        WriteBackup(backup, live);
        Refreshed.Add(logical);
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
            if (d.StartsWith("/mnt/rnps/", StringComparison.Ordinal) || d.EndsWith(".rco", StringComparison.Ordinal)) icons = true;
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
        if (liveHash == originalHash) return true;
        if (IsReplaced(device, liveHash))
        {
            Refresh(logical, backup, _files.ReadAllBytes(device));
            return false;
        }
        WriteVerified(device, _files.ReadAllBytes(backup), originalHash);
        return true;
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
        return new Plan(target, device, $"{_dataRoot}/{BackupFolder}/{logical}", logical);
    }

    private bool TryPlan(ThemePack pack, out List<Plan> plans, out string error)
    {
        plans = new List<Plan>();
        var perApp = new List<Plan>();
        Skipped.Clear();
        _unpacked = null;
        foreach (ThemePackTarget target in pack.Manifest.Targets)
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
                if (pack.Manifest.Targets.Any(t => t.Path == logical)) continue;
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
    private void WriteBackup(string backup, byte[] live)
    {
        _files.CreateDirectoryRecursive(ParentOf(backup));

        // Written to a temporary and renamed, so an interrupted copy never becomes the permanent "original".
        string tmp = backup + ".tmp";
        _files.WriteAllBytes(tmp, live);
        _files.Move(tmp, backup);

        if (_files.Hash(backup) != ThemePack.Hash(live))
            throw new InvalidOperationException($"backup {backup} does not match the live file");
    }

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

    private Dictionary<string, string> Written()
    {
        if (_written is not null) return _written;
        _written = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = $"{_dataRoot}/{WrittenRecord}";
        if (!_files.Exists(path)) return _written;
        foreach (string line in Encoding.UTF8.GetString(_files.ReadAllBytes(path)).Split('\n'))
        {
            int space = line.IndexOf(' ');
            if (space == 64) _written[line[(space + 1)..].TrimEnd('\r')] = line[..space];
        }
        return _written;
    }

    private void RecordWritten(string device, string sha256)
    {
        Dictionary<string, string> written = Written();
        if (written.TryGetValue(device, out string? known) && known == sha256) return;
        written[device] = sha256;
        string path = $"{_dataRoot}/{WrittenRecord}";
        _files.CreateDirectoryRecursive(ParentOf(path));
        string text = string.Concat(written.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Value} {e.Key}\n"));
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

    private void WriteState(string action, ThemePackManifest? manifest, IEnumerable<(string Device, string Backup)> plans)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();

            if (manifest is null)
            {
                writer.WriteNull("pack");
            }
            else
            {
                writer.WriteStartObject("pack");
                writer.WriteString("id", manifest.Id);
                writer.WriteString("name", manifest.Name);
                writer.WriteString("version", manifest.Version);
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
