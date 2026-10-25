using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ProsperoThemes.Shared;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// The console's profile folders, through ThemeHelper (v9): <c>/system_data</c> is outside the app's sandbox. The helper
/// only allows the avatar file names (<see cref="HelperPaths.IsAvatarPath"/>), writes only <c>.tmp</c> files and renames
/// them into place.
/// </summary>
public interface IAvatarFiles
{
    /// <summary>Names of the folders in <see cref="AvatarApplier.ProfileRoot"/> ("0x1EA2F4D9", one per user).</summary>
    IReadOnlyList<string> ProfileFolders();

    /// <summary>Names of the files in one profile folder.</summary>
    IReadOnlyList<string> FilesIn(string folder);

    byte[] Read(string path);
    string Hash(string path);

    /// <summary>Writes <paramref name="path"/>, which ends in ".tmp".</summary>
    void Write(string path, byte[] data);

    void Rename(string from, string to);
    void Delete(string path);
}

/// <summary>Outcome of an avatar apply or reset.</summary>
public sealed record AvatarResult(bool Success, string Message);

/// <summary>
/// Sets and resets the profile picture of one user. Measured on 13.00 (docs/plan-avatar.md): the home screen shows
/// <c>avatar64/128/260/440.dds</c> from <c>/system_data/priv/cache/profile/0x&lt;ID&gt;/</c> after it restarts, as long as
/// an <c>online.json</c> is there too (its content does not matter). So an apply writes those four files and creates
/// <c>online.json</c> (as <c>{}</c>) only when it is missing, which leaves a PSN user's names alone.
/// </summary>
/// <remarks>
/// Before the first change the files the apply will replace are copied to <c>/data/prospero-themes/avatar-backup/0x&lt;ID&gt;/</c>
/// with a <c>state.json</c> that says what was there; a reset puts exactly that back. A second apply keeps the first
/// backup, so a reset always returns to the console's own picture. Plain logic over <see cref="IAvatarFiles"/> and
/// <see cref="IDeviceFiles"/>, so the PC tests cover it.
/// </remarks>
public static class AvatarApplier
{
    public const string ProfileRoot = "/system_data/priv/cache/profile";
    public const string OnlineName = "online.json";
    public const string BackupRoot = "/data/prospero-themes/avatar-backup";
    public const string StateName = "state.json";

    /// <summary>What <c>online.json</c> gets when the apply has to create it.</summary>
    private static readonly byte[] EmptyOnline = "{}"u8.ToArray();

    /// <summary>The profile folder name of a user: "0x" and the user id in eight upper-case hex digits, as the console names it.</summary>
    public static string FolderName(int userId) => "0x" + ((uint)userId).ToString("X8");

    public static string BackupFolder(int userId) => BackupRoot + "/" + FolderName(userId);

    /// <summary>What the backup says, or null when there is none (no avatar applied by the app for this user).</summary>
    public sealed record State(string AvatarId, string AvatarName, IReadOnlyList<string> BackedUp, bool CreatedOnline);

    /// <summary>The avatar the app applied for this user, or null.</summary>
    public static State? Current(int userId, IDeviceFiles data)
    {
        string path = BackupFolder(userId) + "/" + StateName;
        try { return data.Exists(path) ? ParseState(Encoding.UTF8.GetString(data.ReadAllBytes(path))) : null; }
        catch { return null; }
    }

    /// <summary>
    /// Writes the pack's picture for <paramref name="userId"/>. The home screen shows it after it restarts; the caller offers
    /// that. On a failure part-way, what was written is put back.
    /// </summary>
    public static AvatarResult Apply(AvatarPack pack, int userId, IAvatarFiles profile, IDeviceFiles data)
    {
        PackReport report = pack.Validate();
        if (report.HasErrors) return new AvatarResult(false, "This avatar can't be used: " + report.Errors.First().Message);

        string? folder = FindFolder(userId, profile, out string? problem);
        if (folder is null) return new AvatarResult(false, problem!);
        IReadOnlyList<string> present = profile.FilesIn(folder);

        // The first apply keeps what is there now; later ones keep that first backup.
        State? state = Current(userId, data);
        try
        {
            if (state is null)
            {
                var backedUp = new List<string>();
                string backup = BackupFolder(userId);
                data.CreateDirectoryRecursive(backup);
                foreach (string name in Managed())
                {
                    if (name == OnlineName || !Contains(present, name)) continue;
                    data.WriteAllBytes(backup + "/" + name, profile.Read(folder + "/" + name));
                    backedUp.Add(name);
                }
                state = new State("", "", backedUp, !Contains(present, OnlineName));
                WriteState(userId, state, data);
            }
        }
        catch (Exception ex)
        {
            return new AvatarResult(false, "Could not back up the current picture: " + ex.Message);
        }

        try
        {
            foreach (int size in AvatarPack.Sizes)
            {
                string name = AvatarPack.FileName(size);
                byte[] bytes = pack.Image(size)!;
                Place(profile, folder + "/" + name, bytes);
            }
            if (!Contains(present, OnlineName)) Place(profile, folder + "/" + OnlineName, EmptyOnline);
            WriteState(userId, state with { AvatarId = pack.Id, AvatarName = pack.Name }, data);
            return new AvatarResult(true, pack.Name + " is your profile picture.");
        }
        catch (Exception ex)
        {
            AvatarResult undo = Reset(userId, profile, data);
            return new AvatarResult(false, "Could not set the picture: " + ex.Message + (undo.Success ? " The old picture is back." : " " + undo.Message));
        }
    }

    /// <summary>Puts back what was there before the first apply and removes the backup.</summary>
    public static AvatarResult Reset(int userId, IAvatarFiles profile, IDeviceFiles data)
    {
        State? state = Current(userId, data);
        if (state is null) return new AvatarResult(true, "The console's own picture is already in place.");
        string? folder = FindFolder(userId, profile, out string? problem);
        if (folder is null) return new AvatarResult(false, problem!);
        IReadOnlyList<string> present = profile.FilesIn(folder);
        string backup = BackupFolder(userId);
        var failed = new List<string>();
        foreach (int size in AvatarPack.Sizes)
        {
            string name = AvatarPack.FileName(size);
            try
            {
                if (Contains(state.BackedUp, name)) Place(profile, folder + "/" + name, data.ReadAllBytes(backup + "/" + name));
                else if (Contains(present, name)) profile.Delete(folder + "/" + name);
            }
            catch { failed.Add(name); }
        }
        if (state.CreatedOnline && Contains(present, OnlineName))
        {
            try { profile.Delete(folder + "/" + OnlineName); }
            catch { failed.Add(OnlineName); }
        }
        if (failed.Count > 0) return new AvatarResult(false, "Could not put back " + string.Join(", ", failed) + ". The backup stays; try again.");

        try
        {
            foreach (string name in state.BackedUp) data.DeleteFile(backup + "/" + name);
            data.DeleteFile(backup + "/" + StateName);
        }
        catch { /* the picture is back; a stale backup only means the next apply keeps it */ }
        return new AvatarResult(true, "Your profile picture is the console's own again.");
    }

    // The files an apply may change: the four avatar files and online.json.
    private static IEnumerable<string> Managed()
    {
        foreach (int size in AvatarPack.Sizes) yield return AvatarPack.FileName(size);
        yield return OnlineName;
    }

    private static string? FindFolder(int userId, IAvatarFiles profile, out string? problem)
    {
        string wanted = FolderName(userId);
        IReadOnlyList<string> folders;
        try { folders = profile.ProfileFolders(); }
        catch (Exception ex) { problem = "Could not read the profile folders: " + ex.Message; return null; }
        foreach (string name in folders)
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) { problem = null; return ProfileRoot + "/" + name; }
        }
        problem = "This user has no profile folder yet (" + wanted + ").";
        return null;
    }

    // Writes a file the helper's way (.tmp, then rename) and reads its hash back.
    private static void Place(IAvatarFiles profile, string path, byte[] bytes)
    {
        profile.Write(path + ".tmp", bytes);
        profile.Rename(path + ".tmp", path);
        if (profile.Hash(path) != ThemePack.Hash(bytes)) throw new InvalidOperationException(path + " reads back different");
    }

    private static bool Contains(IReadOnlyList<string> names, string name)
    {
        foreach (string n in names) if (n == name) return true;
        return false;
    }

    private static void WriteState(int userId, State state, IDeviceFiles data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("avatar", state.AvatarId);
            w.WriteString("name", state.AvatarName);
            w.WriteStartArray("backed_up");
            foreach (string name in state.BackedUp) w.WriteStringValue(name);
            w.WriteEndArray();
            w.WriteBoolean("created_online", state.CreatedOnline);
            w.WriteEndObject();
        }
        string path = BackupFolder(userId) + "/" + StateName;
        data.WriteAllBytes(path + ".tmp", buffer.WrittenSpan.ToArray());
        data.Move(path + ".tmp", path);
    }

    internal static State? ParseState(string json)
    {
        JsonValue root = JsonValue.Parse(json);
        if (root.Type != JsonType.Object) return null;
        var backedUp = new List<string>();
        JsonValue list = root["backed_up"];
        for (int i = 0; list.Type == JsonType.Array && i < list.Count; i++)
        {
            string name = list[i].AsString("");
            // Only names the apply manages, so a changed file can never make a reset write elsewhere.
            foreach (string managed in Managed()) if (name == managed && name != OnlineName) backedUp.Add(name);
        }
        return new State(root.GetString("avatar"), root.GetString("name"), backedUp, root["created_online"].AsBool(false));
    }
}
