using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
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
    byte[] ReadAllBytes(string path);
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

    /// <summary>Targets the last plan could not place, e.g. a per-app background for a title without one.</summary>
    public List<string> Skipped { get; } = new();

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

            try
            {
                _files.RemountSystemExRw();
            }
            catch (Exception ex)
            {
                return Fail($"remount failed, nothing was written: {ex.Message}");
            }

            // Every theme starts from the defaults: what an earlier theme changed and this one does not write
            // goes back to its original first, so themes replace each other instead of stacking. Files that
            // already hold their original are left alone.
            var planned = new HashSet<string>(plans.Select(p => p.Device), StringComparer.Ordinal);
            var reset = new List<string>();
            foreach (var (device, backup) in BackedUp())
            {
                if (planned.Contains(device) || !_files.Exists(device))
                    continue;
                try
                {
                    byte[] original = _files.ReadAllBytes(backup);
                    if (_files.ReadAllBytes(device).AsSpan().SequenceEqual(original))
                        continue;
                    WriteVerified(device, original, ThemePack.Hash(original));
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
                    var icons = file.Where(p => p.Target.Layer == ThemePack.SvgLayer).ToList();
                    foreach (Plan plan in file.Where(p => p.Target.Layer != ThemePack.SvgLayer))
                    {
                        current = plan;
                        EnsureBackup(plan);
                        byte[] payload = pack.Payload(plan.Target);
                        touched.Add(plan);
                        WriteVerified(plan.Device, payload, plan.Target.Sha256);
                    }
                    if (icons.Count == 0)
                        continue;

                    // Named icons inside one container: always patched onto the stock copy from the backup, so
                    // a second theme replaces the first one's icons instead of stacking on top of them.
                    current = icons[0];
                    EnsureBackup(icons[0]);
                    byte[] container = _files.ReadAllBytes(icons[0].Backup);
                    foreach (Plan icon in icons)
                        RcoSvg.Replace(container, icon.Target.Resource!, pack.Payload(icon.Target));
                    touched.Add(icons[0]);
                    WriteVerified(icons[0].Device, container, ThemePack.Hash(container));
                }
            }
            catch (Exception ex)
            {
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
            return new ApplyResult(true, $"{defaults}Applied and verified {written.Count} target(s).{skipped}", written);
        }
        catch (Exception ex)
        {
            return Fail($"apply failed: {ex.Message}");
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
                    byte[] source = _files.ReadAllBytes(plan.Backup);
                    WriteVerified(plan.Device, source, ThemePack.Hash(source));
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

            return new ApplyResult(true, $"Restored and verified {restored.Count} target(s).", restored);
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
            List<(string Device, string Backup)> plans = BackedUp();
            if (plans.Count == 0)
                return Fail("there are no on-console backups, so nothing was written.");

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
            foreach (var (device, backup) in plans)
            {
                try
                {
                    byte[] source = _files.ReadAllBytes(backup);
                    WriteVerified(device, source, ThemePack.Hash(source));
                    restored.Add(device);
                }
                catch (Exception ex)
                {
                    errors.Add($"{device}: {ex.Message}");
                }
            }

            try
            {
                WriteState("restore-originals", null, plans);
            }
            catch (Exception ex)
            {
                errors.Add($"state.json: {ex.Message}");
            }

            return errors.Count == 0
                ? new ApplyResult(true, $"Restored and verified {restored.Count} original(s).", restored)
                : new ApplyResult(false, $"Restored {restored.Count} of {plans.Count} original(s); failed: {string.Join("; ", errors)}", restored);
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
    private List<(string Device, string Backup)> BackedUp()
    {
        var entries = new List<(string Device, string Backup)>();
        foreach (string logical in ReadIndex())
        {
            string backup = $"{_dataRoot}/{BackupFolder}/{logical}";
            if (AssetRoots.TryResolve(logical, out string device, out _) && _files.Exists(backup))
                entries.Add((device, backup));
        }
        return entries;
    }

    // Logical is the <root>/<relative> path the backup and the index use; it differs from Target.Path
    // only for the all-apps background, which expands to one plan per title.
    private sealed record Plan(ThemePackTarget Target, string Device, string Backup, string Logical);

    private bool TryPlan(ThemePack pack, out List<Plan> plans, out string error)
    {
        plans = new List<Plan>();
        var perApp = new List<Plan>();
        Skipped.Clear();
        foreach (ThemePackTarget target in pack.Manifest.Targets)
        {
            // An unknown layer is skipped, as ThemePack.Validate's warning promises.
            if (!ThemePackManifest.KnownLayers.Contains(target.Layer))
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

            plans.Add(new Plan(target, device, $"{_dataRoot}/{BackupFolder}/{target.Path}", target.Path));
        }

        // The system tiles (Media Gallery, All Apps, Game Library…) each show their own stock background, not
        // bg_hub_default: they get the theme's main background too, unless the pack names the file itself.
        if (plans.FirstOrDefault(p => p.Target.Path == AssetRoots.HubBackground) is { } hub)
        {
            foreach (string logical in AssetRoots.SystemTileBackgrounds)
            {
                if (pack.Manifest.Targets.Any(t => t.Path == logical)) continue;
                AssetRoots.TryResolve(logical, out string tile, out _);
                if (_files.Exists(tile))
                    plans.Add(new Plan(hub.Target, tile, $"{_dataRoot}/{BackupFolder}/{logical}", logical));
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
            error = "pack has no applicable targets";
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

    /// <summary>Captures the live file once. An existing backup is never overwritten.</summary>
    private void EnsureBackup(Plan plan)
    {
        if (_files.Exists(plan.Backup))
        {
            AddToIndex(plan.Logical);   // a backup made before the index existed
            return;
        }

        if (!_files.Exists(plan.Device))
            throw new InvalidOperationException($"live file {plan.Device} does not exist, so it cannot be backed up");

        byte[] live = _files.ReadAllBytes(plan.Device);
        _files.CreateDirectoryRecursive(ParentOf(plan.Backup));

        // Written to a temporary and renamed, so an interrupted copy never becomes the permanent "original".
        string tmp = plan.Backup + ".tmp";
        _files.WriteAllBytes(tmp, live);
        _files.Move(tmp, plan.Backup);

        byte[] readBack = _files.ReadAllBytes(plan.Backup);
        if (!readBack.AsSpan().SequenceEqual(live))
            throw new InvalidOperationException($"backup {plan.Backup} does not match the live file {plan.Device}");
        AddToIndex(plan.Logical);
    }

    /// <summary>Writes to <c>{device}.tmp</c>, renames over the device path, then reads back and compares.</summary>
    private void WriteVerified(string device, byte[] data, string expectedSha256)
    {
        string tmp = device + ".tmp";
        _files.WriteAllBytes(tmp, data);
        _files.Move(tmp, device);

        string actual = ThemePack.Hash(_files.ReadAllBytes(device));
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"verification failed for {device}: read back {actual}, expected {expectedSha256}");
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
