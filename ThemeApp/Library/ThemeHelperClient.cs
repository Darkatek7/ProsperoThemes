using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SharpProspero.Interop.Kernel;
using SharpProspero.Interop.Net;
using SharpProspero.Platform;
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
    public const int ExpectedTag = 4;
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
        return "The theme helper was sent but did not respond.";
    }

    private static string? CheckTag(string reply) =>
        reply == $"OK PONG {Port} v{ExpectedTag}"
            ? null
            : $"A different theme helper is already running ({reply}). Please restart the console.";

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

    public static byte[] Read(string path)
    {
        byte[]? data = null;
        string reply = Request("READ " + path, null, out data);
        if (data is null) throw new InvalidOperationException($"read {path} failed: {reply}");
        return data;
    }

    public static void Write(string path, byte[] data)
    {
        string reply = Request($"WRITE {path} {data.Length}", data, out _);
        if (reply != "WRITE wrote=" + data.Length) throw new InvalidOperationException($"write {path} failed: {reply}");
    }

    /// <summary>Sub-folder names of /user/app/ or /user/appmeta/ (the only folders the helper lists).</summary>
    public static IReadOnlyList<string> List(string path)
    {
        using var connection = TcpConnection.Connect(SocketAddress.Loopback(Port));
        connection.SetReceiveTimeout(ReplyTimeoutMicroseconds);
        connection.SendAll(Encoding.ASCII.GetBytes("LIST " + path + "\n"));
        string reply = ReadLine(connection);
        if (reply != "OK") throw new InvalidOperationException($"list {path} failed: {reply}");
        var names = new List<string>();
        for (string line = ReadLine(connection); line != "END" && line.Length > 0; line = ReadLine(connection))
            if (line.StartsWith("D ", StringComparison.Ordinal)) names.Add(line[2..]);
        connection.SendAll("QUIT\n"u8);
        return names;
    }

    public static void Rename(string from, string to)
    {
        string reply = Request($"RENAME {from} {to}", null, out _);
        if (reply != "RENAME rc=0") throw new InvalidOperationException($"rename {from} failed: {reply}");
    }

    /// <summary>Sends one command (plus an optional body), reads the status line and, for READ, the data.</summary>
    private static string Request(string command, byte[]? body, out byte[]? readData)
    {
        readData = null;
        using var connection = TcpConnection.Connect(SocketAddress.Loopback(Port));
        connection.SetReceiveTimeout(ReplyTimeoutMicroseconds);
        connection.SendAll(Encoding.ASCII.GetBytes(command + "\n"));
        if (body is not null) connection.SendAll(body);
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

    private static bool IsThemeRoot(string path) =>
        path.StartsWith("/system_ex/vsh_asset/", StringComparison.Ordinal) ||
        path.StartsWith("/user/appmeta/", StringComparison.Ordinal) ||
        path.StartsWith("/mnt/rnps/apps/", StringComparison.Ordinal) ||
        path.StartsWith("/user/app/", StringComparison.Ordinal);

    public bool Exists(string path) => IsThemeRoot(path) ? ThemeHelperClient.Exists(path) : _local.Exists(path);

    public byte[] ReadAllBytes(string path) => IsThemeRoot(path) ? ThemeHelperClient.Read(path) : _local.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] data)
    {
        if (IsThemeRoot(path)) ThemeHelperClient.Write(path, data);
        else _local.WriteAllBytes(path, data);
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
