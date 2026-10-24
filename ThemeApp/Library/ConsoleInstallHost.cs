using System;
using System.Collections.Generic;
using SharpProspero.Platform;

namespace ProsperoThemes.App.Library;

/// <summary>
/// <see cref="IInstallHost"/> for the console: ShadowMountPlus's local API over a plain loopback socket and ThemeHelper for
/// the files. Kept out of AppInstall.cs so the PC tests never reference the device SDK.
/// </summary>
public sealed class ConsoleInstallHost : IInstallHost
{
    // ShadowMount answers a small JSON document; anything larger is not its answer.
    private const int MaxAnswerBytes = 64 * 1024;
    private const uint ReplyTimeoutMicroseconds = 2_000_000;

    public IDeviceFiles HelperFiles()
    {
        string? problem = ThemeHelperClient.EnsureRunning();
        if (problem is null) return new InstallFiles();
        // A helper from an earlier build holds the port: it does not know the install folder, and only a restart replaces it.
        throw new AppInstallException(problem.StartsWith(ThemeHelperClient.DifferentHelperPrefix, StringComparison.Ordinal)
            ? AppInstall.RestartOnce
            : problem);
    }

    /// <summary>
    /// <c>POST /api/v1/games/info</c> with the title id, to ShadowMountPlus on 127.0.0.1:10101. Null when it does not answer in
    /// about two seconds or the answer is not a complete 200.
    /// </summary>
    public string? AskShadowMount() => Post(AppInstall.ShadowMountInfoPath, AppInstall.ShadowMountInfoBody);

    public void RequestScan()
    {
        try { Post(AppInstall.ShadowMountScanPath, AppInstall.ShadowMountScanBody); }
        catch { /* the home screen shows the new version after the next scan anyway */ }
    }

    private static string? Post(string path, string body)
    {
        ThemeHelperClient.EnsureNetwork();
        using TcpConnection connection = TcpConnection.Connect(SocketAddress.Loopback(AppInstall.ShadowMountPort));
        connection.SetReceiveTimeout(ReplyTimeoutMicroseconds);
        connection.SendAll(AppInstall.HttpPost("127.0.0.1", AppInstall.ShadowMountPort, path, body));
        var answer = new List<byte>(1024);
        byte[] chunk = new byte[2048];
        try
        {
            // Connection: close, so the end of the answer is the end of the connection.
            while (answer.Count < MaxAnswerBytes)
            {
                int got = connection.Receive(chunk);
                if (got <= 0) break;
                for (int i = 0; i < got; i++) answer.Add(chunk[i]);
            }
        }
        catch (Exception)
        {
            // A timeout after some of the answer: what arrived may still be all of it.
            if (answer.Count == 0) return null;
        }
        return AppInstall.HttpBody(answer.ToArray());
    }
}

/// <summary>
/// File access to the install folder through ThemeHelper, which accepts only the install folder and its .tmp files and gives
/// what it writes there mode 0777.
/// </summary>
internal sealed class InstallFiles : IDeviceFiles
{
    public bool Exists(string path) => ThemeHelperClient.Exists(path);

    public long Length(string path) => ThemeHelperClient.Size(path);

    public byte[] ReadAllBytes(string path) => ThemeHelperClient.Read(path);

    public string Hash(string path) => ThemeHelperClient.Hash(path);

    public void WriteAllBytes(string path, byte[] data) => ThemeHelperClient.Write(path, data);

    public void Move(string from, string to) => ThemeHelperClient.Rename(from, to);

    public void DeleteFile(string path) => throw new InvalidOperationException("the theme helper does not delete files in the install folder");

    public void CreateDirectoryRecursive(string path) => ThemeHelperClient.MakeInstallDirectories(path);

    public void RemountSystemExRw() => throw new InvalidOperationException("an update does not remount");

    public IReadOnlyList<string> ListDirectories(string path) => ThemeHelperClient.List(path);
}
