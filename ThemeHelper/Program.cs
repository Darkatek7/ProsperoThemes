// ThemeHelper - the restricted privileged helper for Prospero Themes.
//
// A restricted copy of DevProbe. It exists so the theme app never needs the general-purpose probe:
//
//   - LOOPBACK ONLY. The listener is bound to 127.0.0.1, never INADDR_ANY, so nothing off the console
//     can reach it. PayloadNetwork.Listen binds every address, so the socket is created here directly.
//   - FIXED PORT 9071, SINGLE INSTANCE. No SO_REUSEADDR, so a second copy fails to bind and exits
//     instead of silently sharing the port; the app pings first and only loads the helper when absent.
//   - CONFINED PATHS. Every path must lie inside one of the theme roots, /system_ex/vsh_asset/,
//     /user/appmeta/ or /mnt/rnps/apps/ (the mounts in Shared/AssetRoots.cs), the tile icons in
//     /system_ex/rnps/apps/ (firmware without the /mnt/rnps copies) or the app's own folder
//     /data/prospero-themes, with no "..", "//" or backslash. In a theme root WRITE may only create
//     "<target>.tmp", and RENAME may only move "<target>.tmp" onto "<target>", so the helper can replace a
//     theme asset atomically and do nothing else there.
//   - THE APP'S FOLDER. Where the app's sandbox has no /data (10.01 with OnionHEN), the app keeps its library,
//     backups and settings through the helper: inside /data/prospero-themes WRITE and RENAME take any name, and
//     MKDIR, UNLINK and a LIST of files and folders are allowed. Nowhere else.
//   - NARROW COMMAND SET. PING, REMOUNT, STAT, READ, WRITE, RENAME, LIST, MKDIR, UNLINK, QUIT. There is no
//     RMDIR, SOTEST or any other diagnostic command.
//
// Requests are ASCII lines terminated by '\n'. ReadLine consumes exactly one byte past the newline and
// never reads ahead, so a WRITE command's binary payload that follows its header line stays intact.
// Whole files are never buffered: READ and WRITE stream through one 32 KiB chunk, because the payload
// heap is only 16 MB.
//
// Diagnostics go over the socket, not to klog (see DevProbe's note: kern.msgbuf is not readable here).

using System;
using System.Runtime.InteropServices;
using PayloadCommon;
using SharpProspero.Interop.Kernel;
using SharpProspero.Interop.Posix;
using SharpProspero.Payload;
using SharpProspero.Payload.IO;
using SharpProspero.Payload.Net;

namespace ThemeHelper;

internal static unsafe class Program
{
    private const ushort Port = 9071;
    private const int Backlog = 4;
    private const int MaxLine = 512;
    private const int MaxPath = 256;
    private const int StreamChunk = 32768;

    // 127.0.0.1 as the uint PosixSocket expects: address octets in network order, first octet in the
    // lowest byte. Matches PayloadNetwork.Connect's packing of (a, b, c, d).
    private const uint LoopbackAddress = 127u | (0u << 8) | (0u << 16) | (1u << 24);

    // The only trees the helper may touch, mirroring AssetRoots' mounts.
    private const string VshAssetPrefix = "/system_ex/vsh_asset/";
    private const string AppMetaPrefix = "/user/appmeta/";
    private const string RnpsPrefix = "/mnt/rnps/apps/";
    // The shipped React Native apps (AssetRoots.ShippedRnpsMount): only their icon0.png, see IsShippedTileIcon.
    private const string ShippedRnpsPrefix = "/system_ex/rnps/apps/";
    private const string UserAppPrefix = "/user/app/";
    // The app's own folder; the helper manages it only for an app that cannot reach /data itself.
    private const string DataRoot = "/data/prospero-themes";
    private const string DataPrefix = DataRoot + "/";
    // The largest file the helper writes: the icon container (Sce.PlayStation.PUI_UI3.rco), 39 MB on 13.00 and
    // about 100 MB before it, with room. WRITE streams through one chunk, so this is not a memory limit. The
    // app's ThemeApplier.MaxFileBytes is the same number.
    private const int MaxWrite = 160 * 1024 * 1024;

    // open(2) flags, as the unjail sample uses them on this platform.
    private const int OpenReadOnly = 0x0000;
    private const int OpenReadWrite = 0x0002;
    private const int OpenCreate = 0x0200;
    private const int OpenTruncate = 0x0400;
    private const ushort Mode666 = 0x1B6;

    // Syscall number not exposed by PayloadCrt on this revision (nmount), per the unjail sample.
    private const int SysNmount = 378;

    // Build tag announced on PING, so a client can tell which helper revision it is talking to.
    private const int BuildTag = 7;

    // /system_ex is remounted once per boot. A second MNT_UPDATE on the already writable mount was the
    // last thing before a console hang (2026-10-13), so it is not repeated: not within this helper's
    // lifetime, and not after another payload (DevProbe, an earlier helper) has remounted it.
    private static bool _remounted;

    [UnmanagedCallersOnly(EntryPoint = "__managed__Main")]
    public static int Main(void* args)
    {
        PayloadCrt.Klog("sp:themehelper:start\n\0"u8);

        int listener = ListenLoopback(Port, Backlog);
        if (listener < 0)
            return -1;

        // Allocated once: stackalloc inside a loop is not released until the method returns.
        byte* line = stackalloc byte[MaxLine + 1];
        byte* scratch = stackalloc byte[StreamChunk];

        while (true)
        {
            int client = PayloadNetwork.Accept(listener);
            if (client < 0)
                continue;
            ClientTimeout.Apply(client);
            Serve(client, line, scratch);
            PayloadTcpServer.Close(client);
        }
    }

    /// <summary>
    /// Opens a listening socket bound to 127.0.0.1 only. Returns the descriptor, or -1 on failure.
    /// Mirrors PayloadNetwork.Listen apart from the bind address.
    /// </summary>
    private static int ListenLoopback(ushort port, int backlog)
    {
        int socket = PosixSocket.socket(PosixSocket.AfInet, PosixSocket.SockStream, 0);
        if (socket < 0)
            return -1;

        var address = new SockaddrIn
        {
            Length = 16,
            Family = (byte)PosixSocket.AfInet,
            Port = PosixSocket.HostToNetwork(port),
            Address = LoopbackAddress,
        };
        if (PosixSocket.bind(socket, &address, 16) < 0)
        {
            PosixSocket.close(socket);
            return -1;
        }
        if (PosixSocket.listen(socket, backlog) < 0)
        {
            PosixSocket.close(socket);
            return -1;
        }
        return socket;
    }

    private static void Serve(int client, byte* line, byte* scratch)
    {
        while (true)
        {
            int length = ReadLine(client, line, MaxLine);
            if (length <= 0)
                return;
            line[length] = 0;
            Trace(line, scratch, "sp:themehelper:> "u8);

            if (StartsWith(line, "PING"))
            {
                Send(client, "OK PONG "u8);
                SendInt(client, Port);
                Send(client, " v"u8);
                SendInt(client, BuildTag);
                Send(client, "\n"u8);
                continue;
            }

            if (StartsWith(line, "REMOUNT"))
            {
                int rc = 0;
                if (!_remounted && SystemExIsWritable())
                {
                    PayloadCrt.Klog("sp:themehelper:remount skipped, already rw\n\0"u8);
                    _remounted = true;
                }
                else if (!_remounted)
                {
                    rc = RemountSystemEx();
                    if (rc == 0) _remounted = true;
                }
                else PayloadCrt.Klog("sp:themehelper:remount skipped\n\0"u8);
                SendResult(client, "REMOUNT", "rc="u8, rc);
                continue;
            }

            if (StartsWith(line, "STAT "))
            {
                byte* path = ConfinedPathFrom(line + 5, scratch);
                if (path == null) { Send(client, "ERR badpath\n"u8); continue; }

                SceKernelStat info = default;
                int rc = KernelFile.stat(path, &info);
                if (rc != 0)
                    rc = (int)PayloadCrt.Syscall(PayloadCrt.SYS_stat, (long)(nint)path, (long)(nint)(&info));
                if (rc != 0) { SendResult(client, "STAT", "rc="u8, rc); continue; }

                Send(client, "OK size="u8);
                SendInt(client, (int)info.Size);
                Send(client, "\n"u8);
                continue;
            }

            if (StartsWith(line, "READ "))
            {
                byte* path = ConfinedPathFrom(line + 5, scratch);
                if (path == null) { Send(client, "ERR badpath\n"u8); continue; }
                ServeRead(client, path, scratch);
                continue;
            }

            // A WRITE that is refused or does not complete ends the connection: its binary body follows the
            // header and must never be parsed as command lines.
            if (StartsWith(line, "WRITE "))
            {
                // header is "WRITE <path> <len>"
                byte* cursor = line + 6;
                while (*cursor != 0 && *cursor != (byte)' ') cursor++;
                if (*cursor != (byte)' ') { Send(client, "ERR badpath\n"u8); return; }
                *cursor = 0;
                int wanted = ParseInt(cursor + 1);
                byte* path = ConfinedPathFrom(line + 6, scratch);
                if (path == null || !(EndsWithTmp(path) || IsDataPath(path)) || wanted < 0 || wanted > MaxWrite)
                {
                    Send(client, "ERR badpath\n"u8);
                    return;
                }
                if (!ServeWrite(client, path, wanted, scratch))
                    return;
                continue;
            }

            // RENAME: "RENAME <from> <to>". Both paths must be confined. The from-path is terminated in
            // place inside the line buffer and the to-path is copied to scratch, which is what makes an
            // atomic tmp -> rename replacement possible.
            if (StartsWith(line, "RENAME "))
            {
                byte* cursor = line + 7;
                while (*cursor != 0 && *cursor != (byte)' ') cursor++;
                if (*cursor != (byte)' ') { Send(client, "ERR badpath\n"u8); continue; }
                *cursor = 0;
                byte* fromPath = line + 7;
                if (!IsConfined(fromPath)) { Send(client, "ERR badpath\n"u8); continue; }
                byte* toPath = ConfinedPathFrom(cursor + 1, scratch);
                bool inData = toPath != null && IsDataPath(fromPath) && IsDataPath(toPath);
                if (toPath == null || !(inData || IsTmpOf(fromPath, toPath))) { Send(client, "ERR badpath\n"u8); continue; }

                int rc = KernelFile.sceKernelRename(fromPath, toPath);
                if (rc != 0)
                    rc = (int)PayloadCrt.Syscall(PayloadCrt.SYS_rename, (long)(nint)fromPath, (long)(nint)toPath);
                SendResult(client, "RENAME", "rc="u8, rc);
                continue;
            }

            if (StartsWith(line, "LIST "))
            {
                ServeList(client, line + 5);
                continue;
            }

            // MKDIR and UNLINK: only in the app's own folder (MKDIR also for the folder itself).
            if (StartsWith(line, "MKDIR "))
            {
                byte* path = PathFrom(line + 6, scratch);
                if (path == null || !(IsDataPath(path) || Equals(path, DataRoot))) { Send(client, "ERR badpath\n"u8); continue; }
                int rc = KernelFile.sceKernelMkdir(path, 0x1FF);
                if (rc != 0 && rc != AlreadyExists)
                    rc = (int)PayloadCrt.Syscall(PayloadCrt.SYS_mkdir, (long)(nint)path, 0x1FF);
                if (rc == -EExist) rc = AlreadyExists;
                SendResult(client, "MKDIR", "rc="u8, rc);
                continue;
            }

            if (StartsWith(line, "UNLINK "))
            {
                byte* path = PathFrom(line + 7, scratch);
                if (path == null || !IsDataPath(path)) { Send(client, "ERR badpath\n"u8); continue; }
                int rc = KernelFile.sceKernelUnlink(path);
                if (rc != 0)
                    rc = (int)PayloadCrt.Syscall(PayloadCrt.SYS_unlink, (long)(nint)path);
                SendResult(client, "UNLINK", "rc="u8, rc);
                continue;
            }

            if (StartsWith(line, "QUIT"))
                return;

            Send(client, "ERR unknown\n"u8);
        }
    }

    private static void ServeRead(int client, byte* path, byte* scratch)
    {
        int fd = KernelFile.sceKernelOpen(path, OpenReadOnly, 0);
        if (fd < 0)
            fd = (int)PayloadCrt.Syscall(PayloadCrt.SYS_open, (long)(nint)path, OpenReadOnly, 0);
        if (fd < 0) { SendResult(client, "READ", "rc="u8, fd); return; }

        SceKernelStat info = default;
        if (KernelFile.stat(path, &info) != 0)
            PayloadCrt.Syscall(PayloadCrt.SYS_stat, (long)(nint)path, (long)(nint)(&info));

        Send(client, "OK "u8);
        SendInt(client, (int)info.Size);
        Send(client, "\n"u8);

        long remaining = info.Size;
        while (remaining > 0)
        {
            int want = remaining > StreamChunk ? StreamChunk : (int)remaining;
            long got = KernelFile.sceKernelRead(fd, scratch, (nuint)want);
            if (got < 0)
                got = PayloadCrt.Syscall(PayloadCrt.SYS_read, fd, (long)(nint)scratch, want);
            if (got <= 0) break;
            if (!PayloadTcpServer.SendAll(client, scratch, (int)got)) break;
            remaining -= got;
        }

        KernelFile.sceKernelClose(fd);
    }

    /// <summary>
    /// Streams the body into the file; true when all of it was written. When the open or a write fails, the
    /// rest of the body (its length is checked against MaxWrite) is read and dropped, so the client can finish
    /// sending and still gets its answer. On false the caller ends the connection.
    /// </summary>
    private static bool ServeWrite(int client, byte* path, int wanted, byte* scratch)
    {
        int flags = OpenReadWrite | OpenCreate;
        if (wanted > 0) flags |= OpenTruncate;

        int fd = KernelFile.sceKernelOpen(path, flags, Mode666);
        if (fd < 0)
            fd = (int)PayloadCrt.Syscall(PayloadCrt.SYS_open, (long)(nint)path, flags, Mode666);
        if (fd < 0)
        {
            Drain(client, wanted, scratch);
            SendResult(client, "WRITE", "rc="u8, fd);
            return false;
        }

        // The path lives in scratch, but open has already consumed it, so scratch can now carry data.
        int done = 0, received = 0;
        while (done < wanted)
        {
            int want = wanted - done > StreamChunk ? StreamChunk : wanted - done;
            int got = ReadExact(client, scratch, want);
            if (got <= 0) break;
            received += got;

            long wrote = KernelFile.sceKernelWrite(fd, scratch, (nuint)got);
            if (wrote < 0)
                wrote = PayloadCrt.Syscall(PayloadCrt.SYS_write, fd, (long)(nint)scratch, got);
            if (wrote <= 0) break;
            done += (int)wrote;
            if (wrote < got) break;
        }

        KernelFile.sceKernelClose(fd);
        if (received < wanted)
            Drain(client, wanted - received, scratch);
        SendResult(client, "WRITE", "wrote="u8, done);
        return done == wanted;
    }

    /// <summary>Reads and drops up to <paramref name="length"/> bytes, stopping early when the client does.</summary>
    private static void Drain(int client, int length, byte* scratch)
    {
        while (length > 0)
        {
            int got = ReadExact(client, scratch, length > StreamChunk ? StreamChunk : length);
            if (got <= 0) return;
            length -= got;
        }
    }

    // MNT_RDONLY in statfs.f_flags.
    private const ulong MntReadOnly = 0x1;

    /// <summary>True when /system_ex is already mounted read-write; a failed statfs counts as not writable.</summary>
    private static bool SystemExIsWritable()
    {
        byte* path = stackalloc byte[] {
            (byte)'/', (byte)'s', (byte)'y', (byte)'s', (byte)'t', (byte)'e', (byte)'m', (byte)'_', (byte)'e', (byte)'x', 0 };
        FreeBsdStatfs info = default;
        return PayloadMount.statfs(path, &info) == 0 && (info.f_flags & MntReadOnly) == 0;
    }

    /// <summary>
    /// Remounts /system_ex read-write with the exfatfs option set the design specifies. Copied from
    /// DevProbe's RemountSystemEx: seven options as fourteen iovec entries, with async and ignoreacl as
    /// flag-style entries.
    /// </summary>
    private static int RemountSystemEx()
    {
        FreeBsdIovec* iov = stackalloc FreeBsdIovec[14];

        byte* kFstype = stackalloc byte[] { (byte)'f', (byte)'s', (byte)'t', (byte)'y', (byte)'p', (byte)'e', 0 };
        byte* vExfatfs = stackalloc byte[] { (byte)'e', (byte)'x', (byte)'f', (byte)'a', (byte)'t', (byte)'f', (byte)'s', 0 };
        byte* kFrom = stackalloc byte[] { (byte)'f', (byte)'r', (byte)'o', (byte)'m', 0 };
        byte* vDevice = stackalloc byte[] {
            (byte)'/', (byte)'d', (byte)'e', (byte)'v', (byte)'/', (byte)'s', (byte)'s', (byte)'d', (byte)'0',
            (byte)'.', (byte)'s', (byte)'y', (byte)'s', (byte)'t', (byte)'e', (byte)'m', (byte)'_', (byte)'e', (byte)'x', 0 };
        byte* kFspath = stackalloc byte[] { (byte)'f', (byte)'s', (byte)'p', (byte)'a', (byte)'t', (byte)'h', 0 };
        byte* vSystemEx = stackalloc byte[] {
            (byte)'/', (byte)'s', (byte)'y', (byte)'s', (byte)'t', (byte)'e', (byte)'m', (byte)'_', (byte)'e', (byte)'x', 0 };
        byte* kLarge = stackalloc byte[] { (byte)'l', (byte)'a', (byte)'r', (byte)'g', (byte)'e', 0 };
        byte* vYes = stackalloc byte[] { (byte)'y', (byte)'e', (byte)'s', 0 };
        byte* kTimezone = stackalloc byte[] { (byte)'t', (byte)'i', (byte)'m', (byte)'e', (byte)'z', (byte)'o', (byte)'n', (byte)'e', 0 };
        byte* vStatic = stackalloc byte[] { (byte)'s', (byte)'t', (byte)'a', (byte)'t', (byte)'i', (byte)'c', 0 };
        byte* kAsync = stackalloc byte[] { (byte)'a', (byte)'s', (byte)'y', (byte)'n', (byte)'c', 0 };
        byte* kIgnoreacl = stackalloc byte[] { (byte)'i', (byte)'g', (byte)'n', (byte)'o', (byte)'r', (byte)'e', (byte)'a', (byte)'c', (byte)'l', 0 };

        PayloadMount.SetIovecPair(&iov[0], kFstype, 7, vExfatfs, 8);
        PayloadMount.SetIovecPair(&iov[2], kFrom, 5, vDevice, 20);
        PayloadMount.SetIovecPair(&iov[4], kFspath, 7, vSystemEx, 11);
        PayloadMount.SetIovecPair(&iov[6], kLarge, 6, vYes, 4);
        PayloadMount.SetIovecPair(&iov[8], kTimezone, 9, vStatic, 7);
        PayloadMount.SetIovecFlag(&iov[10], kAsync, 6);
        PayloadMount.SetIovecFlag(&iov[12], kIgnoreacl, 10);

        int rc = PayloadMount.nmount(iov, 14, PayloadMount.MntUpdate);
        if (rc != 0)
            rc = (int)PayloadCrt.Syscall(SysNmount, (long)(nint)iov, 14, PayloadMount.MntUpdate);

        // EBUSY means the mount already carries exactly these parameters, which is the desired state.
        if (rc == -16) rc = 0;
        return rc;
    }

    /// <summary>Writes "prefix + line" to the kernel log, so a hang shows which request was in flight.</summary>
    private static void Trace(byte* line, byte* scratch, ReadOnlySpan<byte> prefix)
    {
        int n = 0;
        foreach (byte b in prefix) scratch[n++] = b;
        for (int i = 0; line[i] != 0 && n < 200; i++) scratch[n++] = line[i];
        scratch[n++] = (byte)'\n';
        scratch[n] = 0;
        PayloadCrt.Klog(scratch);
    }

    // ---- confinement ----

    /// <summary>
    /// True when a NUL-terminated path lies strictly inside one of the theme roots and contains no "..",
    /// "//" or backslash.
    /// </summary>
    private static bool IsConfined(byte* path)
    {
        if (IsDataPath(path)) return true;
        int prefix = StartsWith(path, VshAssetPrefix) ? VshAssetPrefix.Length
            : StartsWith(path, AppMetaPrefix) ? AppMetaPrefix.Length
            : StartsWith(path, RnpsPrefix) ? RnpsPrefix.Length
            : StartsWith(path, ShippedRnpsPrefix) && IsShippedTileIcon(path) ? ShippedRnpsPrefix.Length
            : StartsWith(path, UserAppPrefix) && IsUserAppBackground(path) ? UserAppPrefix.Length : -1;
        if (prefix < 0 || path[prefix] == 0) return false;
        for (int i = 0; path[i] != 0; i++)
        {
            if (i >= MaxPath - 1) return false;
            if (path[i] == (byte)'\\') return false;
            if (path[i] == (byte)'.' && path[i + 1] == (byte)'.') return false;
            if (path[i] == (byte)'/' && path[i + 1] == (byte)'/') return false;
        }
        return true;
    }

    // sceKernelMkdir's "already exists", and EEXIST from the raw syscall.
    private const int AlreadyExists = unchecked((int)0x80020011);
    private const int EExist = 17;

    /// <summary>
    /// True when a NUL-terminated path lies strictly inside the app's own folder and contains no "..", "//" or
    /// backslash.
    /// </summary>
    private static bool IsDataPath(byte* path)
    {
        if (!StartsWith(path, DataPrefix) || path[DataPrefix.Length] == 0) return false;
        for (int i = 0; path[i] != 0; i++)
        {
            if (i >= MaxPath - 1) return false;
            if (path[i] == (byte)'\\') return false;
            if (path[i] == (byte)'.' && path[i + 1] == (byte)'.') return false;
            if (path[i] == (byte)'/' && path[i + 1] == (byte)'/') return false;
        }
        return true;
    }

    /// <summary>
    /// Under /user/app the helper touches one file per title and nothing else: exactly
    /// "/user/app/&lt;TID&gt;/sce_sys/pic0.dds" (or its ".tmp"), the title's own entry background.
    /// </summary>
    private static bool IsUserAppBackground(byte* path)
    {
        int i = UserAppPrefix.Length, start = i;
        while (path[i] != 0 && path[i] != (byte)'/') i++;
        if (i == start || i - start > 16 || path[i] != (byte)'/') return false;
        ReadOnlySpan<byte> tail = "/sce_sys/pic0.dds"u8;
        for (int k = 0; k < tail.Length; k++)
            if (path[i + k] != tail[k]) return false;
        int end = i + tail.Length;
        if (path[end] == 0) return true;
        return path[end] == (byte)'.' && path[end + 1] == (byte)'t' && path[end + 2] == (byte)'m'
            && path[end + 3] == (byte)'p' && path[end + 4] == 0;
    }

    /// <summary>
    /// Under /system_ex/rnps/apps, next to the firmware's signed app bundles, the helper touches only tile icons: paths
    /// that end in "/icon0.png" (or its ".tmp").
    /// </summary>
    private static bool IsShippedTileIcon(byte* path)
    {
        int end = Length(path);
        if (end > 4 && path[end - 4] == (byte)'.' && path[end - 3] == (byte)'t' && path[end - 2] == (byte)'m'
            && path[end - 1] == (byte)'p')
            end -= 4;
        ReadOnlySpan<byte> tail = "/icon0.png"u8;
        if (end < ShippedRnpsPrefix.Length + tail.Length) return false;
        for (int k = 0; k < tail.Length; k++)
            if (path[end - tail.Length + k] != tail[k]) return false;
        return true;
    }

    /// <summary>
    /// LIST is allowed for two theme-root folders, where it only reports the sub-folder names (the title ids that
    /// may carry an entry background), and for folders in the app's own folder, where it reports sub-folders as
    /// "D name" and files as "F name".
    /// </summary>
    private static void ServeList(int client, byte* path)
    {
        bool data = IsDataPath(path) || Equals(path, DataRoot);
        if (!(data || Equals(path, UserAppPrefix) || Equals(path, AppMetaPrefix))) { Send(client, "ERR badpath\n"u8); return; }
        void* dir = PayloadFileSystem.opendir(path);
        if (dir == null) { Send(client, "ERR opendir\n"u8); return; }
        Send(client, "OK\n"u8);
        int emptyRun = 0;   // a missing folder can yield empty names forever; see DevProbe's ServeList
        while (true)
        {
            FreeBsdDirent* entry = PayloadFileSystem.readdir(dir);
            if (entry == null) break;
            byte namlen = entry->d_namlen;
            if (namlen == 0) { if (++emptyRun > 8) break; continue; }
            emptyRun = 0;
            if (entry->d_name[0] == (byte)'.') continue;
            if (entry->d_type == PayloadFileSystem.DT_DIR) Send(client, "D "u8);
            else if (data && entry->d_type == PayloadFileSystem.DT_REG) Send(client, "F "u8);
            else continue;
            PayloadTcpServer.SendAll(client, entry->d_name, namlen);
            Send(client, "\n"u8);
        }
        PayloadFileSystem.closedir(dir);
        Send(client, "END\n"u8);
    }

    private static bool Equals(byte* text, string expected)
    {
        int i = 0;
        for (; i < expected.Length; i++)
            if (text[i] != (byte)expected[i]) return false;
        return text[i] == 0;
    }

    private static int Length(byte* text)
    {
        int n = 0;
        while (text[n] != 0) n++;
        return n;
    }

    /// <summary>True when the path ends in ".tmp" with something before it.</summary>
    private static bool EndsWithTmp(byte* path)
    {
        int n = Length(path);
        return n > 4 && path[n - 4] == (byte)'.' && path[n - 3] == (byte)'t' && path[n - 2] == (byte)'m' && path[n - 1] == (byte)'p';
    }

    /// <summary>True when <paramref name="from"/> is exactly <paramref name="to"/> followed by ".tmp".</summary>
    private static bool IsTmpOf(byte* from, byte* to)
    {
        int n = Length(to);
        if (Length(from) != n + 4 || !EndsWithTmp(from)) return false;
        for (int i = 0; i < n; i++)
            if (from[i] != to[i]) return false;
        return true;
    }

    /// <summary>
    /// Copies a NUL-terminated path out of the line into scratch and returns it only if it is confined.
    /// </summary>
    private static byte* ConfinedPathFrom(byte* source, byte* scratch)
    {
        byte* path = PathFrom(source, scratch);
        if (path == null) return null;
        return IsConfined(path) ? path : null;
    }

    // ---- socket helpers ----

    /// <summary>Reads one '\n'-terminated line, consuming exactly one byte past the newline.</summary>
    private static int ReadLine(int client, byte* buffer, int max)
    {
        int count = 0;
        while (count < max)
        {
            byte one;
            long got = PayloadNetwork.Receive(client, new Span<byte>(&one, 1));
            if (got <= 0) return count;
            if (one == (byte)'\n') return count;
            if (one == (byte)'\r') continue;
            buffer[count++] = one;
        }
        return count;
    }

    private static int ReadExact(int client, byte* buffer, int length)
    {
        int total = 0;
        while (total < length)
        {
            long got = PayloadNetwork.Receive(client, new Span<byte>(buffer + total, length - total));
            if (got <= 0) break;
            total += (int)got;
        }
        return total;
    }

    private static void Send(int client, ReadOnlySpan<byte> data) => PayloadTcpServer.SendAll(client, data);

    private static void SendInt(int client, int value)
    {
        byte* digits = stackalloc byte[12];
        int count = 0;
        bool negative = value < 0;
        if (negative) value = -value;
        if (value == 0) digits[count++] = (byte)'0';
        else while (value > 0) { digits[count++] = (byte)('0' + (value % 10)); value /= 10; }
        if (negative) Send(client, "-"u8);
        for (int i = count - 1; i >= 0; i--)
            PayloadTcpServer.SendAll(client, digits + i, 1);
    }

    private static void SendResult(int client, string name, ReadOnlySpan<byte> field, int value)
    {
        foreach (char c in name) { byte b = (byte)c; PayloadTcpServer.SendAll(client, &b, 1); }
        Send(client, " "u8);
        Send(client, field);
        SendInt(client, value);
        Send(client, "\n"u8);
    }

    // ---- text helpers ----

    private static bool StartsWith(byte* line, string prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
        {
            if (line[i] != (byte)prefix[i]) return false;
        }
        return true;
    }

    /// <summary>Copies a NUL-terminated path out of the line into a scratch buffer.</summary>
    private static byte* PathFrom(byte* source, byte* scratch)
    {
        if (*source == 0) return null;
        int i = 0;
        while (source[i] != 0)
        {
            if (i >= MaxPath - 1) return null;   // too long: refuse rather than truncate
            scratch[i] = source[i];
            i++;
        }
        scratch[i] = 0;
        return i == 0 ? null : scratch;
    }

    private static int ParseInt(byte* text)
    {
        int value = 0;
        bool any = false;
        while (*text >= (byte)'0' && *text <= (byte)'9')
        {
            if (value > (int.MaxValue - 9) / 10) return -1;   // too large: refuse rather than wrap
            value = value * 10 + (*text - (byte)'0');
            text++;
            any = true;
        }
        return any ? value : -1;
    }
}
