using System.Text;
using ProsperoThemes.App.Library;
using SharpProspero.Security;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The in-app update, PC side: ShadowMount's answer, the scan roots, the release ZIP's checks, and the order and safety of the
/// writes, over in-memory files. The console glue (<c>ConsoleInstallHost</c>) is a thin shell around these.
/// </summary>
public class AppInstallTests
{
    private const string Folder = "/data/homebrew/PPSA90210";
    private const string Running = "01.000.026", Next = "01.000.027";

    private const string RealAnswer = """{"path":"/data/homebrew/PPSA90210","runtime_path":"/data/homebrew/PPSA90210","source_type":"folder","image_type":"","title_id":"PPSA90210","version":"01.000.014","image_backed":false,"source_available":true,"status":0}""";

    // ---- the files and the host -----------------------------------------------------------------

    /// <summary>
    /// In-memory files that behave like ThemeHelper: a file written inside an install folder gets mode 0777 (<see cref="Modes"/>),
    /// any other 0666. <see cref="OldHelper"/>: a helper before v9, which refuses every install path.
    /// </summary>
    private sealed class MemoryFiles(Dictionary<string, byte[]> store) : IDeviceFiles
    {
        public Dictionary<string, ushort> Modes { get; } = [];
        public bool OldHelper { get; init; }
        public List<string> Log { get; } = [];
        public HashSet<string> FailWrites { get; } = [];
        public string? CorruptMoveTo { get; set; }

        private void Check()
        {
            if (OldHelper) throw new InvalidOperationException("stat failed: ERR badpath");
        }

        public bool Exists(string path) { Check(); return store.ContainsKey(path); }
        public long Length(string path) { Check(); return store.TryGetValue(path, out byte[]? b) ? b.Length : -1; }
        public byte[] ReadAllBytes(string path) { Check(); return store.TryGetValue(path, out byte[]? b) ? (byte[])b.Clone() : throw new FileNotFoundException(path); }
        public string Hash(string path) { Check(); return Sha256.HashHex(ReadAllBytes(path)); }

        public void WriteAllBytes(string path, byte[] data)
        {
            Check();
            if (FailWrites.Contains(path)) throw new IOException("disk full for " + path);
            Log.Add("write " + path);
            store[path] = (byte[])data.Clone();
            ushort mode = HelperPaths.ModeAfterWrite(Encoding.ASCII.GetBytes(path));
            Modes[path] = mode != 0 ? mode : (ushort)0x1B6;
        }

        public void Move(string from, string to)
        {
            Check();
            if (!store.Remove(from, out byte[]? b)) throw new FileNotFoundException(from);
            Log.Add("move " + from + " " + to);
            if (to == CorruptMoveTo) b = [.. b.Select(x => (byte)(x ^ 0xFF))];
            store[to] = b;
            Modes[to] = Modes.GetValueOrDefault(from);
            Modes.Remove(from);
        }

        public void DeleteFile(string path)
        {
            Check();
            Log.Add("delete " + path);
            store.Remove(path);
        }

        public void CreateDirectoryRecursive(string path) { Check(); Log.Add("mkdir " + path); }
        public void RemountSystemExRw() => throw new NotSupportedException();
        public IReadOnlyList<string> ListDirectories(string path) => [];
    }

    private sealed class FakeHost(Func<IDeviceFiles>? helper = null, string? shadow = null) : IInstallHost
    {
        public int HelperCalls { get; private set; }
        public int Scans { get; private set; }
        public bool ShadowThrows { get; init; }

        public string? AskShadowMount()
        {
            if (ShadowThrows) throw new IOException("connection refused");
            return shadow;
        }

        public IDeviceFiles HelperFiles()
        {
            HelperCalls++;
            return helper is null ? throw new AppInstallException("No ELF loader on port 9021. Start the jailbreak or elfldr first.") : helper();
        }

        public void RequestScan() => Scans++;
    }

    private static byte[] ParamJson(string title = "PPSA90210", string version = Running) =>
        Encoding.UTF8.GetBytes($$"""{"titleId":"{{title}}","contentVersion":"{{version}}"}""");

    /// <summary>A store with this app installed in <paramref name="folder"/> at the running version.</summary>
    private static Dictionary<string, byte[]> Installed(string folder = Folder, string version = Running, string title = "PPSA90210") => new()
    {
        [folder + "/eboot.bin"] = [1, 2, 3],
        [folder + "/sce_sys/param.json"] = ParamJson(title, version),
        [folder + "/sce_sys/icon0.png"] = [9, 9],
        [folder + "/my-notes.txt"] = [7],
    };

    // ---- ShadowMount's answer -------------------------------------------------------------------

    [Fact]
    public void A_folder_install_answer_gives_its_path_and_ignores_the_registered_version()
    {
        var answer = AppInstall.ParseShadowMount(RealAnswer);

        Assert.Equal(AppInstall.ShadowKind.Folder, answer.Kind);
        Assert.Equal(Folder, answer.Path);
    }

    [Theory]
    [InlineData("""{"path":"/data/homebrew/PPSA90210","source_type":"image","image_type":"exfat","title_id":"PPSA90210","image_backed":true,"source_available":true,"status":0}""")]
    [InlineData("""{"path":"/data/homebrew/PPSA90210","source_type":"folder","title_id":"PPSA90210","image_backed":true,"source_available":true,"status":0}""")]
    public void A_disk_image_cannot_be_updated_in_place(string json) =>
        Assert.Equal(AppInstall.ShadowKind.Image, AppInstall.ParseShadowMount(json).Kind);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("[]")]
    [InlineData("""{"status":2,"error":"unknown title"}""")]
    [InlineData("""{"path":"/data/homebrew/PPSA90210","source_type":"folder","title_id":"PPSA90210","image_backed":false,"source_available":false,"status":0}""")]
    [InlineData("""{"path":"/data/homebrew/PPSA90210","source_type":"folder","title_id":"PPSA90210","image_backed":false,"status":0}""")]
    [InlineData("""{"path":"/data/homebrew/PPSA90210","source_type":"other","title_id":"PPSA90210","image_backed":false,"source_available":true,"status":0}""")]
    [InlineData("""{"path":"data/homebrew/PPSA90210","source_type":"folder","title_id":"PPSA90210","image_backed":false,"source_available":true,"status":0}""")]
    [InlineData("""{"path":"/data/homebrew/OTHER","source_type":"folder","title_id":"PPSA90210","image_backed":false,"source_available":true,"status":0}""")]
    [InlineData("""{"path":"/data/../etc/PPSA90210","source_type":"folder","title_id":"PPSA90210","image_backed":false,"source_available":true,"status":0}""")]
    [InlineData("""{"path":"/data/homebrew/PPSA90210","source_type":"folder","title_id":"PPSA00001","image_backed":false,"source_available":true,"status":0}""")]
    public void Anything_else_gives_no_folder(string? json) => Assert.Equal(AppInstall.ShadowKind.Unavailable, AppInstall.ParseShadowMount(json).Kind);

    [Theory]
    [InlineData("/data/homebrew/PPSA90210", true)]
    [InlineData("/mnt/usb3/etaHEN/games/PPSA90210", true)]
    [InlineData("/somewhere/else/PPSA90210", true)]
    [InlineData("/PPSA90210", true)]
    [InlineData("", false)]
    [InlineData("data/homebrew/PPSA90210", false)]
    [InlineData("/data/homebrew/PPSA90210/", false)]
    [InlineData("/data//homebrew/PPSA90210", false)]
    [InlineData("/data/./homebrew/PPSA90210", false)]
    [InlineData("/data/../homebrew/PPSA90210", false)]
    [InlineData("/data/homebrew/PPSA90210/..", false)]
    [InlineData("/data/home brew/PPSA90210", false)]
    [InlineData("/data/home\\brew/PPSA90210", false)]
    [InlineData("/data/homebrew/PPSA902100", false)]
    [InlineData("/data/homebrew/xPPSA90210", false)]
    public void An_install_folder_is_absolute_normalised_and_ends_in_the_title(string path, bool valid) =>
        Assert.Equal(valid, AppInstall.IsValidFolder(path));

    // ---- plain HTTP to ShadowMount --------------------------------------------------------------

    [Fact]
    public void The_question_is_a_json_post_that_closes_the_connection()
    {
        string request = Encoding.ASCII.GetString(AppInstall.HttpPost("127.0.0.1", 10101, AppInstall.ShadowMountInfoPath, AppInstall.ShadowMountInfoBody));

        Assert.StartsWith("POST /api/v1/games/info HTTP/1.1\r\n", request);
        Assert.Contains("Content-Type: application/json\r\n", request);
        Assert.Contains("Connection: close\r\n", request);
        Assert.Contains("Content-Length: 24\r\n", request);
        Assert.EndsWith("\r\n\r\n{\"title_id\":\"PPSA90210\"}", request);
    }

    [Fact]
    public void A_body_is_read_by_content_length_or_by_chunks()
    {
        static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

        Assert.Equal("{\"a\":1}", AppInstall.HttpBody(Bytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\ncontent-length: 7\r\n\r\n{\"a\":1}EXTRA")));
        Assert.Equal("{\"a\":1}", AppInstall.HttpBody(Bytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\n{\"a\r\n4;ext=1\r\n\":1}\r\n0\r\n\r\n")));
        Assert.Equal("{}", AppInstall.HttpBody(Bytes("HTTP/1.0 200 OK\r\n\r\n{}")));
    }

    [Theory]
    [InlineData("HTTP/1.1 404 Not Found\r\nContent-Length: 2\r\n\r\n{}")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 20\r\n\r\n{}")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\n{}")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 2")]
    [InlineData("not http at all")]
    [InlineData("")]
    public void An_incomplete_or_failed_answer_has_no_body(string raw) => Assert.Null(AppInstall.HttpBody(Encoding.ASCII.GetBytes(raw)));

    // ---- the scan roots -------------------------------------------------------------------------

    [Fact]
    public void The_scan_roots_are_the_ones_shadowmount_scans_and_the_helper_accepts_every_one()
    {
        Assert.Equal(32, AppInstall.ScanRoots.Count);
        Assert.Equal(32, AppInstall.ScanRoots.Distinct().Count());
        Assert.Equal("/data/homebrew", AppInstall.ScanRoots[0]);
        Assert.Equal("/data/etaHEN/games", AppInstall.ScanRoots[1]);
        Assert.Contains("/mnt/ext1/etaHEN/games", AppInstall.ScanRoots);
        Assert.Contains("/mnt/usb7/homebrew", AppInstall.ScanRoots);
        Assert.Contains("/mnt/usb0", AppInstall.ScanRoots);
        Assert.Equal("/mnt/ext1", AppInstall.ScanRoots[^1]);
        foreach (string folder in AppInstall.Candidates())
        {
            Assert.True(AppInstall.IsValidFolder(folder), folder);
            Assert.True(HelperPaths.IsInstallFolder(folder), folder);
        }
    }

    // ---- finding the install folder -------------------------------------------------------------

    [Fact]
    public void ShadowMounts_folder_is_used_and_the_files_are_the_helpers()
    {
        var store = Installed();
        var helper = new MemoryFiles(store);
        var host = new FakeHost(() => helper, RealAnswer);

        InstallTarget target = AppInstall.Locate(host, Running);

        Assert.Equal(Folder, target.Folder);
        target.Files.WriteAllBytes(Folder + "/probe.tmp", [1]);
        Assert.Contains("write " + Folder + "/probe.tmp", helper.Log);
    }

    [Fact]
    public void A_disk_image_stops_the_update_with_the_hand_instructions_before_the_helper_is_asked()
    {
        var host = new FakeHost(() => new MemoryFiles(Installed()),
            """{"path":"/data/homebrew/PPSA90210","source_type":"image","image_backed":true,"source_available":true,"title_id":"PPSA90210","status":0}""");

        var ex = Assert.Throws<AppInstallException>(() => AppInstall.Locate(host, Running));

        Assert.Equal("Installed as a disk image: update it by hand.", ex.Message);
        Assert.Equal(0, host.HelperCalls);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    [InlineData(null, true)]
    public void Without_a_usable_shadowmount_answer_one_scan_root_that_matches_decides(string? shadow, bool throws)
    {
        var store = Installed("/mnt/usb2/etaHEN/games/PPSA90210");
        var host = new FakeHost(() => new MemoryFiles(store), shadow) { ShadowThrows = throws };

        Assert.Equal("/mnt/usb2/etaHEN/games/PPSA90210", AppInstall.Locate(host, Running).Folder);
    }

    [Fact]
    public void A_folder_that_shadowmount_names_must_still_be_this_app()
    {
        var store = Installed(Folder, title: "PPSA00001");
        foreach (var pair in Installed("/mnt/ext0/homebrew/PPSA90210")) store[pair.Key] = pair.Value;
        var host = new FakeHost(() => new MemoryFiles(store), RealAnswer);

        Assert.Equal("/mnt/ext0/homebrew/PPSA90210", AppInstall.Locate(host, Running).Folder);
    }

    [Fact]
    public void No_match_a_stale_version_or_two_matches_are_refused()
    {
        var none = new FakeHost(() => new MemoryFiles([]));
        Assert.StartsWith("Can’t find the folder this app is installed in", Assert.Throws<AppInstallException>(() => AppInstall.Locate(none, Running)).Message);

        var stale = new FakeHost(() => new MemoryFiles(Installed(version: "01.000.020")));
        Assert.StartsWith("Can’t find the folder", Assert.Throws<AppInstallException>(() => AppInstall.Locate(stale, Running)).Message);

        var two = Installed("/data/homebrew/PPSA90210");
        foreach (var pair in Installed("/mnt/usb0/homebrew/PPSA90210")) two[pair.Key] = pair.Value;
        string message = Assert.Throws<AppInstallException>(() => AppInstall.Locate(new FakeHost(() => new MemoryFiles(two)), Running)).Message;
        Assert.Contains("more than one place", message);
        Assert.Contains("/data/homebrew/PPSA90210", message);
        Assert.Contains("/mnt/usb0/homebrew/PPSA90210", message);
    }

    [Fact]
    public void An_older_helper_gets_the_restart_instruction_not_an_obscure_error()
    {
        var old = new MemoryFiles(Installed()) { OldHelper = true };

        // ConsoleInstallHost already maps a different build to this text; a helper that answers badpath gets it here
        var scan = Assert.Throws<AppInstallException>(() => AppInstall.Locate(new FakeHost(() => old), Running));
        var shadow = Assert.Throws<AppInstallException>(() => AppInstall.Locate(new FakeHost(() => old, RealAnswer), Running));

        Assert.Equal("Restart the console once, then try the update again.", scan.Message);
        Assert.Equal("Restart the console once, then try the update again.", shadow.Message);
    }

    [Fact]
    public void A_missing_helper_is_named_and_a_folder_the_helper_does_not_accept_is_refused()
    {
        string message = Assert.Throws<AppInstallException>(() => AppInstall.Locate(new FakeHost(), Running)).Message;
        Assert.Contains("No ELF loader", message);

        var elsewhere = """{"path":"/mnt/ext0/games/PPSA90210","source_type":"folder","image_backed":false,"source_available":true,"title_id":"PPSA90210","status":0}""";
        var host = new FakeHost(() => new MemoryFiles([]), elsewhere);
        var refused = Assert.Throws<AppInstallException>(() => AppInstall.Locate(host, Running));
        Assert.Contains("can’t write to /mnt/ext0/games/PPSA90210", refused.Message);
        Assert.Equal(0, host.HelperCalls);
    }

    // ---- the release ZIP ------------------------------------------------------------------------

    private static (byte[] Zip, AppUpdate.ZipInfo Info) Release(params (string Name, string Content)[] extra) =>
        Release(Next, extra, includeDefaults: true);

    private static (byte[] Zip, AppUpdate.ZipInfo Info) Release(string version, (string Name, string Content)[] extra, bool includeDefaults)
    {
        var files = new List<(string, byte[])>();
        if (includeDefaults)
        {
            files.Add(("PPSA90210/eboot.bin", Encoding.ASCII.GetBytes("new program")));
            files.Add(("PPSA90210/sce_sys/param.json", ParamJson(version: version)));
            files.Add(("PPSA90210/sce_sys/prospero-themes/ThemeHelper.elf", Encoding.ASCII.GetBytes("helper 10")));
            files.Add(("PPSA90210/sce_sys/prospero-themes/catalog/13.00.json", Encoding.ASCII.GetBytes("{}")));
            files.Add(("PPSA90210/sce_module/libc.prx", Encoding.ASCII.GetBytes("libc")));
        }
        foreach (var (name, content) in extra) files.Add((name, Encoding.ASCII.GetBytes(content)));
        byte[] zip = StoredZip.Write(files);
        return (zip, new AppUpdate.ZipInfo("/api/app/download/" + version, zip.Length, Sha256.HashHex(zip)));
    }

    [Fact]
    public void A_good_release_opens_and_lists_its_files_with_eboot_and_param_last()
    {
        var (zip, info) = Release(("PPSA90210/", ""), ("PPSA90210/sce_sys/", ""));

        AppInstall.Package package = AppInstall.Open(zip, info, Next);

        Assert.Equal(Next, package.Version);
        Assert.Equal(
            ["sce_module/libc.prx", "sce_sys/prospero-themes/ThemeHelper.elf", "sce_sys/prospero-themes/catalog/13.00.json", "eboot.bin", "sce_sys/param.json"],
            package.Items.Select(i => i.Relative).ToArray());
    }

    [Fact]
    public void A_download_that_does_not_match_the_announced_size_or_hash_is_refused()
    {
        var (zip, info) = Release();

        var wrongHash = Assert.Throws<AppInstallException>(() => AppInstall.Open(zip, info with { Sha256 = new string('0', 64) }, Next));
        var wrongSize = Assert.Throws<AppInstallException>(() => AppInstall.Open(zip, info with { Bytes = info.Bytes + 1 }, Next));
        byte[] flipped = (byte[])zip.Clone();
        flipped[40] ^= 0xFF;
        var changed = Assert.Throws<AppInstallException>(() => AppInstall.Open(flipped, info, Next));

        Assert.Contains("SHA-256", wrongHash.Message);
        Assert.EndsWith("Nothing was changed.", wrongHash.Message);
        Assert.Contains("bytes", wrongSize.Message);
        Assert.Contains("SHA-256", changed.Message);
    }

    [Theory]
    [InlineData("../evil.bin")]
    [InlineData("PPSA90210/../evil.bin")]
    [InlineData("PPSA90210/sce_sys/../../evil.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("PPSA90210\\evil.bin")]
    [InlineData("PPSA90210/sce_sys\\evil.bin")]
    [InlineData("OTHER/evil.bin")]
    [InlineData("evil.bin")]
    [InlineData("PPSA90210x/evil.bin")]
    [InlineData("PPSA90210//evil.bin")]
    [InlineData("PPSA90210/a b.bin")]
    [InlineData("PPSA90210/café.bin")]
    [InlineData("PPSA90210/tab\t.bin")]
    [InlineData("PPSA90210/./evil.bin")]
    [InlineData("PPSA90210/a..b")]
    public void A_name_outside_the_app_folder_or_one_the_helper_would_refuse_stops_the_update(string name)
    {
        var (zip, info) = Release((name, "x"));

        var ex = Assert.Throws<AppInstallException>(() => AppInstall.Open(zip, info, Next));

        Assert.StartsWith("The ZIP holds a file this app won’t write", ex.Message);
        Assert.EndsWith("Nothing was changed.", ex.Message);
    }

    [Fact]
    public void The_zip_needs_eboot_and_param_and_param_has_to_be_this_app_at_the_announced_version()
    {
        var (noEboot, infoA) = Release(Next, [("PPSA90210/sce_sys/param.json", Encoding.ASCII.GetString(ParamJson(version: Next)))], includeDefaults: false);
        var (noParam, infoB) = Release(Next, [("PPSA90210/eboot.bin", "x")], includeDefaults: false);
        var (otherTitle, infoC) = Release(Next, [("PPSA90210/eboot.bin", "x"), ("PPSA90210/sce_sys/param.json", Encoding.ASCII.GetString(ParamJson("PPSA00001", Next)))], false);
        var (otherVersion, infoD) = Release(Next, [("PPSA90210/eboot.bin", "x"), ("PPSA90210/sce_sys/param.json", Encoding.ASCII.GetString(ParamJson(version: "01.000.099")))], false);
        var (garbage, infoE) = Release(Next, [("PPSA90210/eboot.bin", "x"), ("PPSA90210/sce_sys/param.json", "not json")], false);

        Assert.Contains("no PPSA90210/eboot.bin", Assert.Throws<AppInstallException>(() => AppInstall.Open(noEboot, infoA, Next)).Message);
        Assert.Contains("sce_sys/param.json", Assert.Throws<AppInstallException>(() => AppInstall.Open(noParam, infoB, Next)).Message);
        Assert.Contains("is not Prospero Themes", Assert.Throws<AppInstallException>(() => AppInstall.Open(otherTitle, infoC, Next)).Message);
        Assert.Contains("holds version 01.000.099, not 01.000.027", Assert.Throws<AppInstallException>(() => AppInstall.Open(otherVersion, infoD, Next)).Message);
        Assert.Contains("is not Prospero Themes", Assert.Throws<AppInstallException>(() => AppInstall.Open(garbage, infoE, Next)).Message);
    }

    [Fact]
    public void A_name_twice_and_a_zip_that_is_not_a_zip_are_refused()
    {
        var (twice, infoA) = Release(("PPSA90210/eboot.bin", "again"));
        byte[] junk = Encoding.ASCII.GetBytes(new string('x', 300));
        var infoB = new AppUpdate.ZipInfo("/api/app/download/" + Next, junk.Length, Sha256.HashHex(junk));

        Assert.Contains("twice", Assert.Throws<AppInstallException>(() => AppInstall.Open(twice, infoA, Next)).Message);
        Assert.Contains("not a readable ZIP", Assert.Throws<AppInstallException>(() => AppInstall.Open(junk, infoB, Next)).Message);
    }

    // ---- writing --------------------------------------------------------------------------------

    private static InstallTarget TargetOf(MemoryFiles files, string folder = Folder) => new(folder, files);

    [Fact]
    public void Every_file_goes_through_a_tmp_file_and_eboot_then_param_are_written_last()
    {
        var store = Installed();
        var files = new MemoryFiles(store);
        var (zip, info) = Release();
        var package = AppInstall.Open(zip, info, Next);

        var result = AppInstall.Write(package, TargetOf(files));

        Assert.Equal(5, result.Written);
        Assert.Equal(0, result.Unchanged);
        Assert.True(result.HelperChanged);
        string[] moves = files.Log.Where(l => l.StartsWith("move ")).ToArray();
        Assert.Equal(5, moves.Length);
        Assert.Equal($"move {Folder}/eboot.bin.tmp {Folder}/eboot.bin", moves[3]);
        Assert.Equal($"move {Folder}/sce_sys/param.json.tmp {Folder}/sce_sys/param.json", moves[4]);
        // each file is written to its .tmp first and moved straight after
        foreach (string move in moves)
        {
            string tmp = move.Split(' ')[1];
            Assert.True(files.Log.IndexOf("write " + tmp) < files.Log.IndexOf(move));
        }
        Assert.DoesNotContain(store.Keys, k => k.EndsWith(".tmp"));
        Assert.Equal("new program", Encoding.ASCII.GetString(store[Folder + "/eboot.bin"]));
        Assert.Equal(Next, JsonVersion(store[Folder + "/sce_sys/param.json"]));
    }

    private static string JsonVersion(byte[] param) => SharpProspero.Storage.JsonValue.Parse(Encoding.UTF8.GetString(param)).GetString("contentVersion");

    [Fact]
    public void Files_that_are_in_the_folder_but_not_in_the_zip_stay()
    {
        var store = Installed();
        var (zip, info) = Release();

        AppInstall.Write(AppInstall.Open(zip, info, Next), TargetOf(new MemoryFiles(store)));

        Assert.Equal(new byte[] { 7 }, store[Folder + "/my-notes.txt"]);
        Assert.Equal(new byte[] { 9, 9 }, store[Folder + "/sce_sys/icon0.png"]);
    }

    [Fact]
    public void A_file_that_is_identical_already_is_not_rewritten_and_an_unchanged_helper_is_not_news()
    {
        var store = Installed();
        store[Folder + "/sce_sys/prospero-themes/ThemeHelper.elf"] = Encoding.ASCII.GetBytes("helper 10");
        store[Folder + "/sce_module/libc.prx"] = Encoding.ASCII.GetBytes("libc");
        var files = new MemoryFiles(store);
        var (zip, info) = Release();

        var result = AppInstall.Write(AppInstall.Open(zip, info, Next), TargetOf(files));

        Assert.Equal(3, result.Written);
        Assert.Equal(2, result.Unchanged);
        Assert.False(result.HelperChanged);
        Assert.DoesNotContain(files.Log, l => l.Contains("libc.prx") || l.Contains("ThemeHelper.elf"));
        Assert.DoesNotContain("restart the console", AppInstall.DoneText(Next, result));
    }

    [Fact]
    public void A_failed_write_leaves_the_old_program_and_version_in_place()
    {
        var store = Installed();
        var files = new MemoryFiles(store);
        files.FailWrites.Add(Folder + "/sce_sys/prospero-themes/catalog/13.00.json.tmp");
        var (zip, info) = Release();

        var ex = Assert.Throws<AppInstallException>(() => AppInstall.Write(AppInstall.Open(zip, info, Next), TargetOf(files)));

        Assert.StartsWith("The update stopped at sce_sys/prospero-themes/catalog/13.00.json:", ex.Message);
        Assert.Contains("old version still starts", ex.Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, store[Folder + "/eboot.bin"]);
        Assert.Equal(Running, JsonVersion(store[Folder + "/sce_sys/param.json"]));
    }

    [Fact]
    public void A_file_that_does_not_read_back_as_written_stops_the_update()
    {
        var store = Installed();
        var files = new MemoryFiles(store) { CorruptMoveTo = Folder + "/eboot.bin" };
        var (zip, info) = Release();

        var ex = Assert.Throws<AppInstallException>(() => AppInstall.Write(AppInstall.Open(zip, info, Next), TargetOf(files)));

        Assert.Contains("eboot.bin did not read back as written", ex.Message);
        Assert.Equal(Running, JsonVersion(store[Folder + "/sce_sys/param.json"]));   // param.json comes after eboot.bin
    }

    [Fact]
    public void A_name_too_long_for_the_helper_is_refused_when_the_zip_is_checked_not_while_writing()
    {
        string longName = "PPSA90210/" + string.Join("/", Enumerable.Repeat(new string('a', 50), 4)) + "/file.bin";
        var (zip, info) = Release((longName, "x"));

        var ex = Assert.Throws<AppInstallException>(() => AppInstall.Open(zip, info, Next));

        Assert.StartsWith("The ZIP holds a file this app won’t write", ex.Message);
        // the longest name that is accepted still fits the helper's 255 bytes under the longest root
        string fits = string.Join("/", Enumerable.Repeat(new string('a', 40), 4)) + "/file.bin";
        Assert.True(HelperPaths.IsInstallPath("/mnt/usb0/homebrew/PPSA90210/" + fits + ".tmp"));
    }

    [Fact]
    public void Every_file_ends_up_with_mode_0777_and_nothing_is_written_in_place()
    {
        var store = Installed();
        var files = new MemoryFiles(store);
        var (zip, info) = Release();

        AppInstall.Write(AppInstall.Open(zip, info, Next), TargetOf(files));

        foreach (string rel in new[] { "eboot.bin", "sce_sys/param.json", "sce_module/libc.prx", "sce_sys/prospero-themes/ThemeHelper.elf", "sce_sys/prospero-themes/catalog/13.00.json" })
            Assert.Equal((ushort)0x1FF, files.Modes[Folder + "/" + rel]);
        // every write goes to a new .tmp file; the file that is there (the running program) is only replaced by the rename
        Assert.All(files.Log.Where(l => l.StartsWith("write ")), l => Assert.EndsWith(".tmp", l));
    }

    [Fact]
    public void The_helper_sets_the_executable_mode_only_inside_the_install_folder()
    {
        Assert.Equal((ushort)0x1FF, HelperPaths.InstallFileMode);
        Assert.Equal((ushort)0x1FF, HelperPaths.ModeAfterWrite("/data/homebrew/PPSA90210/eboot.bin.tmp"u8));
        Assert.Equal((ushort)0x1FF, HelperPaths.ModeAfterWrite("/mnt/usb1/PPSA90210/sce_module/libc.prx.tmp"u8));
        Assert.Equal((ushort)0, HelperPaths.ModeAfterWrite("/data/prospero-themes/library/x.p5theme"u8));
        Assert.Equal((ushort)0, HelperPaths.ModeAfterWrite("/system_ex/vsh_asset/bg.dds.tmp"u8));
        Assert.Equal((ushort)0, HelperPaths.ModeAfterWrite("/system_data/priv/cache/profile/0x1a2b3c4d/avatar.png.tmp"u8));
        Assert.Equal((ushort)0, HelperPaths.ModeAfterWrite("/data/homebrew/PPSA90211/eboot.bin.tmp"u8));
    }

    [Fact]
    public void Progress_is_reported_for_each_file_and_new_folders_are_created_once()
    {
        var store = Installed();
        var files = new MemoryFiles(store);
        var progress = new RecordingProgress();
        var (zip, info) = Release();

        AppInstall.Write(AppInstall.Open(zip, info, Next), TargetOf(files), progress);

        Assert.Equal(["Installing 1/5", "Installing 2/5", "Installing 3/5", "Installing 4/5", "Installing 5/5"], progress.Steps);
        Assert.Equal(files.Log.Count(l => l.StartsWith("mkdir ")), files.Log.Where(l => l.StartsWith("mkdir ")).Distinct().Count());
        Assert.Contains($"mkdir {Folder}/sce_sys/prospero-themes/catalog", files.Log);
    }

    private sealed class RecordingProgress : IApplyProgress
    {
        public List<string> Steps { get; } = [];
        public void Report(string step, int index, int total) => Steps.Add($"{step} {index}/{total}");
    }

    // ---- the whole thing ------------------------------------------------------------------------

    [Fact]
    public void Find_check_and_write_end_to_end_ends_with_the_new_version_and_the_done_text()
    {
        var store = Installed();
        var host = new FakeHost(() => new MemoryFiles(store), RealAnswer);
        var (zip, info) = Release();

        InstallTarget target = AppInstall.Locate(host, Running);
        var result = AppInstall.Write(AppInstall.Open(zip, info, Next), target);

        Assert.Equal(Next, JsonVersion(store[Folder + "/sce_sys/param.json"]));
        Assert.Equal("Prospero Themes 01.000.027 is installed. Close the app and start it again. This version has a new theme helper: restart the console once as well.",
            AppInstall.DoneText(Next, result));
    }

    [Fact]
    public void The_help_text_the_app_shows_stays_inside_the_font()
    {
        // The atlas covers ASCII, the typographic marks of bake-assets.py and a few accented letters; nothing else may be shown.
        const string extra = "äöüÄÖÜßéèàáçñ…·–×„“”‚‘’";
        string[] shown =
        [
            AppInstall.RestartOnce, AppInstall.DiskImage, AppInstall.DoneText(Next, new AppInstall.InstallResult(1, 0, true)),
            AppUpdate.DialogText(Next, Running, 8_600_000), AppUpdate.Text(Next, Running, true), AppUpdate.RowText(AppUpdate.State.Ready, Next, Running),
            AppUpdate.RowText(AppUpdate.State.Notice, Next, Running), AppUpdate.RowText(AppUpdate.State.Installed, Next, Running),
            AppUpdate.RowText(AppUpdate.State.None, "", Running),
        ];
        foreach (string text in shown)
            foreach (char c in text)
                Assert.True(c is >= ' ' and <= '~' || extra.Contains(c), $"“{c}” in “{text}”");
    }
}
