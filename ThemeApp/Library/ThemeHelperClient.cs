using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using ProsperoThemes.Shared;
using SharpProspero.Interop.Kernel;
using SharpProspero.Interop.Net;
using SharpProspero.Platform;
using SharpProspero.Security;
using SharpProspero.Storage;

namespace ProsperoThemes.App.Library;

/// <summary>
/// Talks to ThemeHelper, the small privileged payload that writes theme assets. An app module cannot
/// remount /system_ex itself, so the helper does it; the app starts the helper on its own the first time
/// it is needed by handing the bundled ELF to the resident ELF loader. Each request opens its own loopback
/// connection, because the helper serves one client at a time.
/// </summary>
public static class ThemeHelperClient
{
    public const int Port = 9071;
    public const int LoaderPort = 9021;
    /// <summary>The build tag this app expects; ThemeHelper/Program.cs BuildTag.</summary>
    public const int ExpectedTag = 9;
    /// <summary>The start of the problem <see cref="EnsureRunning"/> reports when another build of the helper holds the port.</summary>
    public const string DifferentHelperPrefix = "A different theme helper is already running";
    public const string ElfPath = DeviceThemeLibrary.AssetRoot + "/ThemeHelper.elf";

    private const uint ReplyTimeoutMicroseconds = 15_000_000;
    private static bool _netReady;

    /// <summary>
    /// Makes sure the expected helper is running, loading it if nothing listens on its port. Returns null
    /// when it is ready, otherwise a message for the user. Blocks; call it from a worker thread.
    /// </summary>
    public static string? EnsureRunning()
    {
        if (!_netReady) { NetPool.sceNetInit(); _netReady = true; }

        string? reply = TryPing(out bool listening);
        if (reply is not null) return CheckTag(reply);
        // Something accepted the connection but did not answer: a busy helper looks like this, so a
        // second copy is not loaded on top of it.
        if (listening) return "The theme helper isn’t responding. Please restart the console.";

        byte[] elf;
        try { elf = FileSystem.ReadAllBytes(ElfPath); }
        catch (Exception ex) { return "The theme helper is missing from the app package: " + ex.Message; }

        try
        {
            using var loader = TcpConnection.Connect(SocketAddress.Loopback(LoaderPort));
            loader.SendAll(elf);
        }
        catch (Exception)
        {
            return "No ELF loader on port " + LoaderPort + ". Start the jailbreak or elfldr first.";
        }

        for (int attempt = 0; attempt < 50; attempt++)
        {
            Thread.Sleep(200);
            reply = TryPing(out _);
            if (reply is not null) return CheckTag(reply);
        }
        return "The theme helper was sent to the ELF loader on port " + LoaderPort + " but did not start. " +
            "Please report your jailbreak and loader at github.com/Darkatek7/ProsperoThemes/issues " +
            "with a firmware report (System tab, Firmware report).";
    }

    /// <summary>
    /// Whether the expected helper already answers. Never starts it: for checks that may only read when it runs
    /// anyway. Blocks while it asks; call it from a worker thread.
    /// </summary>
    public static bool IsRunning()
    {
        if (!_netReady) { NetPool.sceNetInit(); _netReady = true; }
        return TryPing(out _) is { } reply && CheckTag(reply) is null;
    }

    private static string? CheckTag(string reply) =>
        reply == $"OK PONG {Port} v{ExpectedTag}"
            ? null
            : $"{DifferentHelperPrefix} ({reply}). Please restart the console.";

    /// <summary>Brings the network up once (a plain socket needs it before the first connect).</summary>
    public static void EnsureNetwork()
    {
        if (!_netReady) { NetPool.sceNetInit(); _netReady = true; }
    }

    private static string? TryPing(out bool listening)
    {
        listening = false;
        TcpConnection connection;
        try { connection = TcpConnection.Connect(SocketAddress.Loopback(Port)); }
        catch (Exception) { return null; }
        listening = true;
        using (connection)
        {
            try
            {
                connection.SetReceiveTimeout(3_000_000);
                connection.SendAll("PING\n"u8);
                return ReadLine(connection);
            }
            catch (Exception) { return null; }
        }
    }

    // ---- requests ----------------------------------------------------------

    public static void Remount()
    {
        string reply = Request("REMOUNT", null, out _);
        if (reply != "REMOUNT rc=0") throw new InvalidOperationException("remount refused: " + reply);
    }

    /// <summary>
    /// True for "OK size=", false for a failed stat ("STAT rc="). Any other answer (a path the helper refuses, no
    /// answer at all) throws, so a helper problem is never taken for a missing file.
    /// </summary>
    public static bool Exists(string path)
    {
        string reply = Request("STAT " + path, null, out _);
        if (reply.StartsWith("OK size=", StringComparison.Ordinal)) return true;
        if (reply.StartsWith("STAT rc=", StringComparison.Ordinal)) return false;
        throw new InvalidOperationException($"stat {path} failed: {(reply.Length == 0 ? "no answer" : reply)}");
    }

    /// <summary>The file's size from STAT, -1 for a failed stat; any other answer throws, as in <see cref="Exists"/>.</summary>
    public static long Size(string path)
    {
        string reply = Request("STAT " + path, null, out _);
        if (reply.StartsWith("OK size=", StringComparison.Ordinal) && long.TryParse(reply.AsSpan(8), out long size)) return size;
        if (reply.StartsWith("STAT rc=", StringComparison.Ordinal)) return -1;
        throw new InvalidOperationException($"stat {path} failed: {(reply.Length == 0 ? "no answer" : reply)}");
    }

    public static byte[] Read(string path)
    {
        byte[]? data = null;
        string reply = Request("READ " + path, null, out data);
        if (data is null) throw new InvalidOperationException($"read {path} failed: {reply}");
        return data;
    }

    /// <summary>
    /// A whole file as <see cref="FileBytes"/>, received straight into them: a large file outside the managed heap
    /// (<see cref="LargeFile"/>).
    /// </summary>
    public static FileBytes ReadFile(string path)
    {
        using var connection = TcpConnection.Connect(SocketAddress.Loopback(Port));
        connection.SetReceiveTimeout(ReplyTimeoutMicroseconds);
        connection.SendAll(Encoding.ASCII.GetBytes("READ " + path + "\n"));
        string reply = ReadLine(connection);
        if (!reply.StartsWith("OK ", StringComparison.Ordinal) || !int.TryParse(reply.AsSpan(3), out int size))
            throw new InvalidOperationException($"read {path} failed: {reply}");
        FileBytes data = LargeFile.Allocate(size);
        try
        {
            Span<byte> span = data.Span;
            int done = 0;
            while (done < size)
            {
                int got = connection.Receive(span[done..]);
                if (got <= 0) throw new InvalidOperationException($"read {path} ended after {done} of {size} bytes");
                done += got;
            }
            connection.SendAll("QUIT\n"u8);
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lower-case hex SHA-256 of a file, streamed through READ, so a large file is never held whole (the icon
    /// container is about 100 MB before firmware 13.00).
    /// </summary>
    public static string Hash(string path)
    {
        using var connection = TcpConnection.Connect(SocketAddress.Loopback(Port));
        connection.SetReceiveTimeout(ReplyTimeoutMicroseconds);
        connection.SendAll(Encoding.ASCII.GetBytes("READ " + path + "\n"));
        string reply = ReadLine(connection);
        if (!reply.StartsWith("OK ", StringComparison.Ordinal) || !long.TryParse(reply.AsSpan(3), out long size))
            throw new InvalidOperationException($"read {path} failed: {reply}");
        var digest = new Sha256();
        byte[] buffer = new byte[64 * 1024];
        long done = 0;
        while (done < size)
        {
            int got = connection.Receive(buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - done)));
            if (got <= 0) throw new InvalidOperationException($"read {path} ended after {done} of {size} bytes");
            digest.Update(buffer.AsSpan(0, got));
            done += got;
        }
        connection.SendAll("QUIT\n"u8);
        return digest.FinishHex();
    }

    /// <summary>Writes a whole file from any memory (also a <see cref="FileBytes"/> outside the managed heap).</summary>
    public static void Write(string path, ReadOnlySpan<byte> data)
    {
        string reply = Request($"WRITE {path} {data.Length}", data, out _);
        if (reply != "WRITE wrote=" + data.Length) throw new InvalidOperationException($"write {path} failed: {reply}");
    }

    /// <summary>Sub-folder names of /user/app/, /user/appmeta/ or a folder in the app's own folder.</summary>
    public static IReadOnlyList<string> List(string path) => ListEntries(path).Folders;

    /// <summary>
    /// Sub-folders and files of a folder in the app's own folder (the helper lists files nowhere else). A folder
    /// that does not exist lists as empty.
    /// </summary>
    public static (IReadOnlyList<string> Folders, IReadOnlyList<string> Files) ListEntries(string path)
    {
        using var connection = TcpConnection.Connect(SocketAddress.Loopback(Port));
        connection.SetReceiveTimeout(ReplyTimeoutMicroseconds);
        connection.SendAll(Encoding.ASCII.GetBytes("LIST " + path + "\n"));
        string reply = ReadLine(connection);
        if (reply == "ERR opendir") return ([], []);
        if (reply != "OK") throw new InvalidOperationException($"list {path} failed: {reply}");
        var folders = new List<string>();
        var files = new List<string>();
        for (string line = ReadLine(connection); line != "END" && line.Length > 0; line = ReadLine(connection))
        {
            if (line.StartsWith("D ", StringComparison.Ordinal)) folders.Add(line[2..]);
            else if (line.StartsWith("F ", StringComparison.Ordinal)) files.Add(line[2..]);
        }
        connection.SendAll("QUIT\n"u8);
        return (folders, files);
    }

    /// <summary>
    /// Creates a folder in the app's own folder, and the ones above it up to /data/prospero-themes. "Already exists"
    /// counts as done.
    /// </summary>
    public static void MakeDirectories(string path)
    {
        string folder = path.TrimEnd('/');
        if (folder != AppData.Root && !folder.StartsWith(AppData.Root + "/", StringComparison.Ordinal))
            throw new InvalidOperationException($"the theme helper only creates folders in {AppData.Root}, not {folder}");
        var steps = new List<string> { AppData.Root };
        if (folder != AppData.Root) AddSteps(steps, folder, AppData.Root);
        foreach (string step in steps) Mkdir(step);
    }

    /// <summary>
    /// Creates a folder inside the app's install folder (<see cref="HelperPaths"/>), and the ones above it up to the install
    /// folder itself, which has to exist already. "Already exists" counts as done.
    /// </summary>
    public static void MakeInstallDirectories(string path)
    {
        string folder = path.TrimEnd('/');
        string root = HelperPaths.InstallFolderOf(folder)
            ?? throw new InvalidOperationException($"the theme helper only creates folders in the app's install folder, not {folder}");
        if (folder == root) return;
        var steps = new List<string>();
        AddSteps(steps, folder, root);
        foreach (string step in steps) Mkdir(step);
    }

    // Every folder from the one below <root> down to <folder>.
    private static void AddSteps(List<string> steps, string folder, string root)
    {
        for (int slash = folder.IndexOf('/', root.Length + 1); slash > 0; slash = folder.IndexOf('/', slash + 1))
            steps.Add(folder[..slash]);
        steps.Add(folder);
    }

    private static void Mkdir(string step)
    {
        string reply = Request("MKDIR " + step, null, out _);
        if (reply != "MKDIR rc=0" && reply != "MKDIR rc=" + unchecked((int)0x80020011))
            throw new InvalidOperationException($"mkdir {step} failed: {reply}");
    }

    /// <summary>Deletes an avatar file (or its .tmp) in a profile folder; the helper refuses every other path (v9).</summary>
    public static void Delete(string path)
    {
        string reply = Request("DELETE " + path, null, out _);
        if (reply != "DELETE rc=0") throw new InvalidOperationException($"delete {path} failed: {reply}");
    }

    public static void Unlink(string path)
    {
        string reply = Request("UNLINK " + path, null, out _);
        if (reply != "UNLINK rc=0") throw new InvalidOperationException($"delete {path} failed: {reply}");
    }

    public static void Rename(string from, string to)
    {
        string reply = Request($"RENAME {from} {to}", null, out _);
        if (reply != "RENAME rc=0") throw new InvalidOperationException($"rename {from} failed: {reply}");
    }

    private static HenKind _hen = HenKind.Unknown;

    /// <summary>
    /// Which HEN runs, from the helper's HEN command (the app's sandbox cannot see /system_tmp). The answer is kept for the
    /// session once the helper gave one; a helper that cannot be reached, or an older one that does not know the command,
    /// gives <see cref="HenKind.Unknown"/>, which is asked again next time. Never throws. Blocks; call it from a worker thread.
    /// </summary>
    public static HenKind QueryHen()
    {
        if (_hen != HenKind.Unknown) return _hen;
        try
        {
            EnsureNetwork();
            HenKind answer = RunningHen.Parse(Request(RunningHen.Command, null, out _));
            _hen = answer;
            return answer;
        }
        catch (Exception) { return HenKind.Unknown; }
    }

    /// <summary>
    /// Restarts the home screen (SceShellUI) so the icons and sounds just written show, and waits while the helper
    /// watches it for about 30 seconds. Under OnionHEN (<see cref="QueryHen"/>) it throws without asking the helper: a restart
    /// there can crash the new home screen. When the restarted home screen keeps crashing, the helper copies each backup
    /// of <paramref name="backups"/> over its file. Returns how many times the home screen started and how many
    /// files went back; throws when the helper refuses or does not answer.
    /// </summary>
    public static (int Restarts, int Restored) RestartShell(IReadOnlyList<(string Backup, string Device)> backups)
    {
        if (RunningHen.ShellRestartRefusal(QueryHen()) is { } refusal) throw new InvalidOperationException(refusal);
        var request = new StringBuilder("RESTARTSHELL ").Append(backups.Count).Append('\n');
        foreach (var (backup, device) in backups) request.Append(backup).Append(' ').Append(device).Append('\n');
        using var connection = TcpConnection.Connect(SocketAddress.Loopback(Port));
        connection.SetReceiveTimeout(RestartTimeoutMicroseconds);
        connection.SendAll(Encoding.ASCII.GetBytes(request.ToString()));
        string reply = ReadLine(connection);
        const string prefix = "RESTARTSHELL restarts=", restoredField = " restored=";
        int split = reply.IndexOf(restoredField, StringComparison.Ordinal);
        if (!reply.StartsWith(prefix, StringComparison.Ordinal) || split < 0
            || !int.TryParse(reply.AsSpan(prefix.Length, split - prefix.Length), out int restarts)
            || !int.TryParse(reply.AsSpan(split + restoredField.Length), out int restored))
            throw new InvalidOperationException("restarting the home screen failed: " + (reply.Length == 0 ? "no answer" : reply));
        return (restarts, restored);
    }

    /// <summary>ThemeHelper's LoopRestarts: this many starts within its watch count as a home screen that keeps crashing.</summary>
    public const int ShellLoopRestarts = 3;

    // The helper watches the restarted home screen for 30 seconds, and may then copy the icon container back.
    private const uint RestartTimeoutMicroseconds = 120_000_000;

    /// <summary>Sends one command (plus an optional body), reads the status line and, for READ, the data.</summary>
    private static string Request(string command, ReadOnlySpan<byte> body, out byte[]? readData)
    {
        readData = null;
        using var connection = TcpConnection.Connect(SocketAddress.Loopback(Port));
        connection.SetReceiveTimeout(ReplyTimeoutMicroseconds);
        connection.SendAll(Encoding.ASCII.GetBytes(command + "\n"));
        if (!body.IsEmpty)
        {
            try { connection.SendAll(body); }
            catch (Exception ex)
            {
                // The helper ends a refused write early; its answer, when it sent one, says why.
                string answer;
                try { answer = ReadLine(connection); }
                catch (Exception) { answer = ""; }
                throw new InvalidOperationException(
                    $"{command}: the helper closed the connection ({(answer.Length > 0 ? answer : ex.Message)})");
            }
        }
        string reply = ReadLine(connection);
        if (command.StartsWith("READ ", StringComparison.Ordinal) && reply.StartsWith("OK ", StringComparison.Ordinal))
        {
            int size = int.Parse(reply.AsSpan(3));
            var data = new byte[size];
            int done = 0;
            while (done < size)
            {
                int got = connection.Receive(data.AsSpan(done));
                if (got <= 0) throw new InvalidOperationException($"read ended after {done} of {size} bytes");
                done += got;
            }
            readData = data;
        }
        // A WRITE that did not complete ends the connection on the helper's side: nothing more to send.
        if (body.IsEmpty || reply == "WRITE wrote=" + body.Length)
            connection.SendAll("QUIT\n"u8);
        return reply;
    }

    private static string ReadLine(TcpConnection connection)
    {
        var bytes = new List<byte>(64);
        Span<byte> one = stackalloc byte[1];
        while (bytes.Count < 512)
        {
            if (connection.Receive(one) <= 0) break;
            if (one[0] == (byte)'\n') break;
            bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}

/// <summary>
/// <see cref="IDeviceFiles"/> for the device: theme-root paths go through ThemeHelper, everything else
/// (backups and state under /data) through the app's own file access.
/// </summary>
public sealed class HelperDeviceFiles : IDeviceFiles
{
    /// <summary>Seconds since 1970 from the kernel's wall clock, for <see cref="ThemeApplier"/>.</summary>
    public static unsafe long UnixSeconds()
    {
        KernelTimespec time;
        return KernelClock.sceKernelClockGettime(KernelClock.ClockRealtime, &time) == 0 ? time.Seconds : 0;
    }

    private readonly DeviceFiles _local = new();

    internal static bool IsThemeRoot(string path) =>
        path.StartsWith("/system_ex/vsh_asset/", StringComparison.Ordinal) ||
        path.StartsWith("/user/appmeta/", StringComparison.Ordinal) ||
        path.StartsWith("/mnt/rnps/apps/", StringComparison.Ordinal) ||
        path.StartsWith(AssetRoots.ShippedRnpsMount + "/", StringComparison.Ordinal) ||
        path.StartsWith("/user/app/", StringComparison.Ordinal);

    public bool Exists(string path) => IsThemeRoot(path) ? ThemeHelperClient.Exists(path) : _local.Exists(path);

    public long Length(string path) => IsThemeRoot(path) ? ThemeHelperClient.Size(path) : _local.Length(path);

    public byte[] ReadAllBytes(string path) => IsThemeRoot(path) ? ThemeHelperClient.Read(path) : _local.ReadAllBytes(path);

    public FileBytes ReadFile(string path) => IsThemeRoot(path) ? ThemeHelperClient.ReadFile(path) : _local.ReadFile(path);

    public string Hash(string path) => IsThemeRoot(path) ? ThemeHelperClient.Hash(path) : _local.Hash(path);

    public void WriteAllBytes(string path, byte[] data) => WriteFile(path, data);

    public void WriteFile(string path, ReadOnlySpan<byte> data)
    {
        if (IsThemeRoot(path)) ThemeHelperClient.Write(path, data);
        else _local.WriteFile(path, data);
    }

    public void Move(string from, string to)
    {
        if (IsThemeRoot(from) != IsThemeRoot(to)) throw new InvalidOperationException($"cannot move {from} across roots to {to}");
        if (IsThemeRoot(to)) ThemeHelperClient.Rename(from, to);
        else _local.Move(from, to);
    }

    public void DeleteFile(string path)
    {
        if (IsThemeRoot(path)) throw new InvalidOperationException("the theme helper does not delete files");
        _local.DeleteFile(path);
    }

    public void CreateDirectoryRecursive(string path)
    {
        if (IsThemeRoot(path)) throw new InvalidOperationException("the theme helper does not create folders");
        _local.CreateDirectoryRecursive(path);
    }

    public void RemountSystemExRw() => ThemeHelperClient.Remount();

    public IReadOnlyList<string> ListDirectories(string path) => ThemeHelperClient.List(path);
}

/// <summary>
/// Read-only <see cref="IDeviceFiles"/> for the firmware report: the app's own file access first, the helper
/// for theme-root files the app may not read (it refuses every other path, so the app's own error is kept), and no helper at all when it did not start (the report is how a console
/// where it does not start gets diagnosed). Counts which side answered.
/// </summary>
public sealed class ReportDeviceFiles : IDeviceFiles
{
    private readonly DeviceFiles _local = new();
    private readonly bool _helper;

    public ReportDeviceFiles(bool helperRunning) => _helper = helperRunning;

    public int AppReads { get; private set; }
    public int HelperReads { get; private set; }

    /// <summary>
    /// The app may not see the theme roots at all (on 13.00 every one of them reads through the helper), so
    /// without the helper a theme-root file is reported as unreadable, never as missing.
    /// </summary>
    public bool Exists(string path)
    {
        bool themeRoot = HelperDeviceFiles.IsThemeRoot(path);
        try { if (_local.Exists(path)) return true; }
        catch when (themeRoot) { /* not readable for the app: ask the helper */ }
        if (!themeRoot) return false;
        if (!_helper) throw new InvalidOperationException("not readable for the app without the theme helper");
        return ThemeHelperClient.Exists(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        try
        {
            byte[] data = _local.ReadAllBytes(path);
            AppReads++;
            return data;
        }
        catch when (_helper && HelperDeviceFiles.IsThemeRoot(path))
        {
            byte[] data = ThemeHelperClient.Read(path);
            HelperReads++;
            return data;
        }
    }

    public string Hash(string path)
    {
        try
        {
            string digest = _local.Hash(path);
            AppReads++;
            return digest;
        }
        catch when (_helper && HelperDeviceFiles.IsThemeRoot(path))
        {
            string digest = ThemeHelperClient.Hash(path);
            HelperReads++;
            return digest;
        }
    }

    /// <summary>A theme-root file the app does not see reads as -1 locally, so the helper is asked then too.</summary>
    public long Length(string path)
    {
        bool themeRoot = HelperDeviceFiles.IsThemeRoot(path);
        try
        {
            long length = _local.Length(path);
            if (length >= 0 || !themeRoot || !_helper) return length;
        }
        catch when (_helper && themeRoot) { /* not readable for the app: ask the helper */ }
        return ThemeHelperClient.Size(path);
    }

    public void WriteAllBytes(string path, byte[] data) => throw new InvalidOperationException("the report only reads");
    public void Move(string from, string to) => throw new InvalidOperationException("the report only reads");
    public void DeleteFile(string path) => throw new InvalidOperationException("the report only reads");
    public void CreateDirectoryRecursive(string path) => throw new InvalidOperationException("the report only reads");
    public void RemountSystemExRw() => throw new InvalidOperationException("the report only reads");
    public IReadOnlyList<string> ListDirectories(string path) => Array.Empty<string>();
}

/// <summary>
/// <see cref="IAvatarFiles"/> for the device: the profile folders through ThemeHelper (v9), which allows only the avatar file
/// names there (<see cref="HelperPaths.IsAvatarPath"/>).
/// </summary>
public sealed class HelperAvatarFiles : IAvatarFiles
{
    public IReadOnlyList<string> ProfileFolders()
    {
        // The helper answers a refused or unreadable folder with an empty list; no folder at all is never a real answer.
        IReadOnlyList<string> folders = ThemeHelperClient.List(AvatarApplier.ProfileRoot);
        if (folders.Count == 0) throw new InvalidOperationException("the theme helper lists no folders in " + AvatarApplier.ProfileRoot);
        return folders;
    }

    public IReadOnlyList<string> FilesIn(string folder) => ThemeHelperClient.ListEntries(folder).Files;
    public byte[] Read(string path) => ThemeHelperClient.Read(path);
    public string Hash(string path) => ThemeHelperClient.Hash(path);
    public void Write(string path, byte[] data) => ThemeHelperClient.Write(path, data);
    public void Rename(string from, string to) => ThemeHelperClient.Rename(from, to);
    public void Delete(string path) => ThemeHelperClient.Delete(path);
}
