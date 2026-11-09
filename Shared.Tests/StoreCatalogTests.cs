using System.Text;
using ProsperoThemes.App.Library;
using SharpProspero.Platform;
using SharpProspero.Security;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// Stock catalogs from the store (<see cref="StoreCatalog"/>): the list, the checks a catalog has to pass, which catalog
/// wins at start, and when a refresh changes the kept copy. Over in-memory files and a GET that answers from a table.
/// </summary>
public class StoreCatalogTests
{
    private const string Store = "https://store.test/";
    private const string Name = "14.00";
    private const string Hash = "2468d916fd1a4a4a7da924539d0dd25e4cde1ae6b2402b0b703bf76057b011a0";
    private const string ListUrl = "https://store.test/api/catalogs", CatalogUrl = "https://store.test/api/catalogs/14.00";

    private static string CatalogJson(string firmware = Name, string path = "vsh_asset/bg_hub_default.dds.zst", string sha = Hash,
        string container = """{"path":"vsh_asset/Sce.PlayStation.PUI_UI3.rco","stock_bytes":10,"stock_sha256":"ed0e9ef531a816b0b5e01a9fee23022a82b406d878b19205ed95515546354f24","icons":3,"untitled":[1]}""",
        string tiles = """[{"path":"rnps/NPXS40047/appdb/default/icon0.png","exists":true}]""") =>
        $$"""{"firmware":"{{firmware}}","format":2,"assets":[{"path":"{{path}}","stock_bytes":1869327,"stock_sha256":"{{sha}}"}],"icon_container":{{container}},"tile_icons":{{tiles}}}""";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string ListJson(byte[] body, string firmware = Name) =>
        $$"""{"items":[{"firmware":"12.34","sha256":"{{new string('0', 64)}}","updatedAt":"2026-11-01T00:00:00Z"},{"firmware":"{{firmware}}","sha256":"{{Sha256.HashHex(body)}}","updatedAt":"2026-11-09T10:00:00Z"}]}""";

    /// <summary>A GET answered from a table (anything else is a 404), recording each address asked for.</summary>
    private sealed class FakeStore
    {
        public Dictionary<string, HttpResponse> Answers { get; } = [];
        public List<string> Asked { get; } = [];
        public Exception? Throw { get; set; }

        public HttpResponse Get(string url)
        {
            Asked.Add(url);
            if (Throw is not null) throw Throw;
            return Answers.TryGetValue(url, out HttpResponse answer) ? answer : new HttpResponse(404, Bytes("{\"error\":\"not found\"}"));
        }

        public static FakeStore Serving(byte[] body, string? listed = null)
        {
            var store = new FakeStore();
            store.Answers[ListUrl] = new HttpResponse(200, Bytes(listed ?? ListJson(body)));
            store.Answers[CatalogUrl] = new HttpResponse(200, body);
            return store;
        }
    }

    private sealed class MemoryFiles : IDeviceFiles
    {
        public Dictionary<string, byte[]> Store { get; } = new(StringComparer.Ordinal);
        public bool FailWrites { get; set; }
        public bool Exists(string path) => Store.ContainsKey(path);
        public long Length(string path) => Store.TryGetValue(path, out byte[]? b) ? b.Length : -1;
        public byte[] ReadAllBytes(string path) => Store.TryGetValue(path, out byte[]? b) ? (byte[])b.Clone() : throw new FileNotFoundException(path);
        public string Hash(string path) => Sha256.HashHex(ReadAllBytes(path));
        public void WriteAllBytes(string path, byte[] data)
        {
            if (FailWrites) throw new IOException("disk full");
            Store[path] = (byte[])data.Clone();
        }
        public void Move(string from, string to)
        {
            if (!Store.Remove(from, out byte[]? b)) throw new FileNotFoundException(from);
            Store[to] = b;
        }
        public void DeleteFile(string path) => Store.Remove(path);
        public void CreateDirectoryRecursive(string path) { }
        public void RemountSystemExRw() => throw new NotSupportedException();
        public IReadOnlyList<string> ListDirectories(string path) => [];
    }

    private static string CachePath => StoreCatalog.CachePath(Name);

    // ---- addresses and the list -------------------------------------------------------------------

    [Fact]
    public void AddressesAndCacheFollowTheFirmwareName()
    {
        Assert.Equal(ListUrl, StoreCatalog.ListUrl(Store));
        Assert.Equal(CatalogUrl, StoreCatalog.CatalogUrl(Store, Name));
        Assert.Equal("/data/prospero-themes/catalogs/14.00.json", CachePath);
    }

    [Fact]
    public void ListKeepsWellFormedItemsOnly()
    {
        var list = StoreCatalog.ParseList($$"""
            {"items":[
              {"firmware":"13.60","sha256":"{{Hash.ToUpperInvariant()}}","updatedAt":"x"},
              {"firmware":"../x","sha256":"{{Hash}}"},
              {"firmware":"9.60","sha256":"abc"},
              {"firmware":"9.60/","sha256":"{{Hash}}"},
              {"sha256":"{{Hash}}"},
              "nonsense"
            ]}
            """);
        Assert.NotNull(list);
        Assert.Equal(Hash, Assert.Single(list).Value);   // lower case
        Assert.Equal("13.60", list.Keys.Single());
        Assert.Empty(StoreCatalog.ParseList("""{"items":[]}""")!);
        Assert.Null(StoreCatalog.ParseList("""{"catalogs":[]}"""));
        Assert.Null(StoreCatalog.ParseList("[]"));
        Assert.Null(StoreCatalog.ParseList("<html>"));
        Assert.Null(StoreCatalog.ParseList(null));
    }

    // ---- validation -------------------------------------------------------------------------------

    [Fact]
    public void AWellFormedCatalogPasses()
    {
        byte[] body = Bytes(CatalogJson());
        Assert.Null(StoreCatalog.Validate(body, Name, Sha256.HashHex(body)));
        Assert.Null(StoreCatalog.Validate(body, Name, Sha256.HashHex(body).ToUpperInvariant()));
        Assert.Null(StoreCatalog.Validate(body, Name, null));
        // Without tile icons or icon container it is still a catalog.
        Assert.Null(StoreCatalog.Validate(Bytes($$"""{"firmware":"{{Name}}","assets":[{"path":"vsh_asset/a.dds","stock_sha256":"{{Hash}}"}]}"""), Name, null));
    }

    [Fact]
    public void EveryBundledCatalogPassesTheStoreChecks()
    {
        string folder = Path.Combine(RepoRoot(), "catalog");
        string[] files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            byte[] body = File.ReadAllBytes(file);
            string name = Path.GetFileNameWithoutExtension(file);
            Assert.True(StoreCatalog.Validate(body, name, Sha256.HashHex(body)) is null, file + ": " + StoreCatalog.Validate(body, name, null));
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "catalog")) && Directory.Exists(Path.Combine(dir.FullName, "ThemeApp")))
                return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    public static TheoryData<string, string> BadCatalogs() => new()
    {
        { "other firmware", CatalogJson(firmware: "13.60") },
        { "unknown root", CatalogJson(path: "system/common/lib/libc.prx") },
        { "absolute path", CatalogJson(path: "/system_ex/vsh_asset/x.dds") },
        { "escaping path", CatalogJson(path: "vsh_asset/../../etc/passwd") },
        { "short hash", CatalogJson(sha: "abc") },
        { "upper-case hash", CatalogJson(sha: Hash.ToUpperInvariant()) },
        { "non-hex hash", CatalogJson(sha: new string('g', 64)) },
        { "bad container path", CatalogJson(container: $$"""{"path":"tmp/x.rco","stock_sha256":"{{Hash}}"}""") },
        { "bad container hash", CatalogJson(container: """{"path":"vsh_asset/x.rco","stock_sha256":42}""") },
        { "bad tile path", CatalogJson(tiles: """[{"path":"../icon0.png"}]""") },
        { "tiles not a list", CatalogJson(tiles: "{}") },
        { "no assets", $$"""{"firmware":"{{Name}}","assets":[]}""" },
        { "asset not an object", $$"""{"firmware":"{{Name}}","assets":["vsh_asset/a.dds"]}""" },
        { "no stock hashes", $$"""{"firmware":"{{Name}}","assets":[{"path":"vsh_asset/a.dds"}]}""" },
        { "firmware as number", """{"firmware":14.00,"assets":[{"path":"vsh_asset/a.dds","stock_sha256":"2468d916fd1a4a4a7da924539d0dd25e4cde1ae6b2402b0b703bf76057b011a0"}]}""" },
        { "not an object", "[]" },
        { "not json", "<html>Bad gateway</html>" },
        { "empty", "" },
    };

    [Theory, MemberData(nameof(BadCatalogs))]
    public void ABadCatalogIsRejected(string what, string json)
    {
        byte[] body = Bytes(json);
        Assert.True(StoreCatalog.Validate(body, Name, Sha256.HashHex(body)) is not null, what);
    }

    [Fact]
    public void AHashMismatchOrOversizedBodyIsRejected()
    {
        byte[] body = Bytes(CatalogJson());
        Assert.Contains("SHA-256", StoreCatalog.Validate(body, Name, new string('0', 64)));
        byte[] big = Bytes(CatalogJson().Replace("\"format\":2", "\"format\":2,\"pad\":\"" + new string(' ', StoreCatalog.MaxBytes) + "\""));
        Assert.Contains("larger", StoreCatalog.Validate(big, Name, Sha256.HashHex(big)));
        Assert.Equal("not UTF-8", StoreCatalog.Validate([0x7B, 0xFF, 0xFE, 0x7D], Name, null));
        Assert.NotNull(StoreCatalog.Validate(null, Name, null));
    }

    // ---- which catalog is used at start -----------------------------------------------------------

    [Fact]
    public void ABundledCatalogWinsOverTheKeptOne()
    {
        var files = new MemoryFiles();
        files.Store[CachePath] = Bytes(CatalogJson(sha: new string('1', 64)));
        var (catalog, source) = StoreCatalog.Load(Name, CatalogJson(), files);
        Assert.Equal(CatalogSource.Bundled, source);
        Assert.True(catalog.TryStock("vsh_asset/bg_hub_default.dds.zst", out string sha));
        Assert.Equal(Hash, sha);
    }

    [Fact]
    public void WithoutABundledCatalogTheKeptOneIsUsed()
    {
        var files = new MemoryFiles();
        files.Store[CachePath] = Bytes(CatalogJson());
        var (catalog, source) = StoreCatalog.Load(Name, null, files);
        Assert.Equal(CatalogSource.Store, source);
        Assert.Equal(2, catalog.Count);
        Assert.Equal(3, catalog.IconCount);
        // A bundled file that holds no stock hashes is no catalog either.
        Assert.Equal(CatalogSource.Store, StoreCatalog.Load(Name, "{}", files).Source);
    }

    [Fact]
    public void ADamagedOrForeignKeptCopyIsNotUsed()
    {
        var files = new MemoryFiles();
        Assert.Equal(CatalogSource.None, StoreCatalog.Load(Name, null, files).Source);
        files.Store[CachePath] = Bytes(CatalogJson()[..40]);
        var (catalog, source) = StoreCatalog.Load(Name, null, files);
        Assert.Equal(CatalogSource.None, source);
        Assert.Same(StockCatalog.Empty, catalog);
        files.Store[CachePath] = Bytes(CatalogJson(firmware: "13.60"));
        Assert.Equal(CatalogSource.None, StoreCatalog.Load(Name, null, files).Source);
        Assert.Equal(CatalogSource.None, StoreCatalog.Load(null, null, files).Source);
    }

    // ---- refresh ----------------------------------------------------------------------------------

    [Fact]
    public void ARefreshKeepsAValidCatalog()
    {
        byte[] body = Bytes(CatalogJson());
        var store = FakeStore.Serving(body);
        var files = new MemoryFiles();
        var result = StoreCatalog.Refresh(Store, Name, store.Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.Updated, result.Outcome);
        Assert.Equal(2, result.Catalog!.Count);
        Assert.Equal(body, files.Store[CachePath]);
        Assert.False(files.Exists(CachePath + ".tmp"));
        Assert.Equal([ListUrl, CatalogUrl], store.Asked);
        Assert.Equal(CatalogSource.Store, StoreCatalog.Load(Name, null, files).Source);
    }

    [Fact]
    public void ARefreshReplacesAnOlderKeptCopy()
    {
        var files = new MemoryFiles();
        files.Store[CachePath] = Bytes(CatalogJson(sha: new string('1', 64)));
        byte[] body = Bytes(CatalogJson());
        var result = StoreCatalog.Refresh(Store, Name, FakeStore.Serving(body).Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.Updated, result.Outcome);
        Assert.Equal(body, files.Store[CachePath]);
    }

    [Fact]
    public void AKeptCopyWithTheListedHashIsNotDownloadedAgain()
    {
        byte[] body = Bytes(CatalogJson());
        var files = new MemoryFiles();
        files.Store[CachePath] = body;
        var store = FakeStore.Serving(body);
        var result = StoreCatalog.Refresh(Store, Name, store.Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.Unchanged, result.Outcome);
        Assert.Equal(2, result.Catalog!.Count);
        Assert.Equal([ListUrl], store.Asked);
    }

    [Fact]
    public void ARejectedCatalogLeavesTheKeptCopy()
    {
        byte[] old = Bytes(CatalogJson(sha: new string('1', 64)));
        // Listed with its own hash, but naming a path outside the asset roots.
        byte[] bad = Bytes(CatalogJson(path: "system/priv/x"));
        var files = new MemoryFiles();
        files.Store[CachePath] = old;
        var result = StoreCatalog.Refresh(Store, Name, FakeStore.Serving(bad).Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.Rejected, result.Outcome);
        Assert.Null(result.Catalog);
        Assert.Equal(old, files.Store[CachePath]);

        // A body that is not what the list named (changed on the way, or a newer one the list does not know yet).
        byte[] good = Bytes(CatalogJson());
        var store = FakeStore.Serving(good, listed: ListJson(Bytes(CatalogJson(sha: new string('2', 64)))));
        result = StoreCatalog.Refresh(Store, Name, store.Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.Rejected, result.Outcome);
        Assert.Contains("SHA-256", result.Message);
        Assert.Equal(old, files.Store[CachePath]);
    }

    [Fact]
    public void A404OrAMissingListItemLeavesTheKeptCopy()
    {
        byte[] old = Bytes(CatalogJson());
        var files = new MemoryFiles();
        files.Store[CachePath] = old;

        // Listed, but the catalog itself is gone.
        byte[] newer = Bytes(CatalogJson(sha: new string('3', 64)));
        var store = FakeStore.Serving(newer);
        store.Answers.Remove(CatalogUrl);
        var result = StoreCatalog.Refresh(Store, Name, store.Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.NotOffered, result.Outcome);
        Assert.Null(result.Catalog);
        Assert.Equal(old, files.Store[CachePath]);

        // Not listed at all: the catalog is not asked for.
        store = FakeStore.Serving(newer, listed: ListJson(newer, firmware: "13.60"));
        result = StoreCatalog.Refresh(Store, Name, store.Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.NotOffered, result.Outcome);
        Assert.Equal([ListUrl], store.Asked);
        Assert.Equal(old, files.Store[CachePath]);
    }

    [Fact]
    public void NoNetworkOrAStoreErrorLeavesTheKeptCopy()
    {
        byte[] old = Bytes(CatalogJson());
        var files = new MemoryFiles();
        files.Store[CachePath] = old;
        var store = new FakeStore { Throw = new InvalidOperationException("sceHttpSendRequest failed") };
        var result = StoreCatalog.Refresh(Store, Name, store.Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.Failed, result.Outcome);
        Assert.Contains("can’t reach", result.Message);

        store = new FakeStore();
        store.Answers[ListUrl] = new HttpResponse(502, Bytes("<html>"));
        Assert.Equal(StoreCatalog.RefreshOutcome.Failed, StoreCatalog.Refresh(Store, Name, store.Get, files).Outcome);
        store.Answers[ListUrl] = new HttpResponse(200, Bytes("<html>"));
        Assert.Equal(StoreCatalog.RefreshOutcome.Failed, StoreCatalog.Refresh(Store, Name, store.Get, files).Outcome);

        byte[] newer = Bytes(CatalogJson(sha: new string('3', 64)));
        store = FakeStore.Serving(newer);
        store.Answers[CatalogUrl] = new HttpResponse(500, []);
        Assert.Equal(StoreCatalog.RefreshOutcome.Failed, StoreCatalog.Refresh(Store, Name, store.Get, files).Outcome);
        Assert.Equal(old, files.Store[CachePath]);
    }

    [Fact]
    public void ACatalogThatCannotBeKeptIsStillUsed()
    {
        var files = new MemoryFiles { FailWrites = true };
        var result = StoreCatalog.Refresh(Store, Name, FakeStore.Serving(Bytes(CatalogJson())).Get, files);
        Assert.Equal(StoreCatalog.RefreshOutcome.Updated, result.Outcome);
        Assert.NotNull(result.Catalog);
        Assert.Contains("not kept", result.Message);
        Assert.Empty(files.Store);
    }

    [Fact]
    public void ARefreshNeedsAFirmwareName()
    {
        var store = FakeStore.Serving(Bytes(CatalogJson()));
        foreach (string name in new[] { "", "13", "../13.60", "13.60/x", "13.6" })
            Assert.Equal(StoreCatalog.RefreshOutcome.NotOffered, StoreCatalog.Refresh(Store, name, store.Get, new MemoryFiles()).Outcome);
        Assert.Empty(store.Asked);
    }

    // ---- what the System tab says -----------------------------------------------------------------

    [Fact]
    public void TheSystemTabNamesTheSource()
    {
        Assert.Equal("catalog in the app", CatalogSourceText.Info(CatalogSource.Bundled));
        Assert.Equal("catalog from the store", CatalogSourceText.Info(CatalogSource.Store));
        Assert.Equal("no catalog yet", CatalogSourceText.Info(CatalogSource.None));
        Assert.Equal("usual", CatalogSourceText.ReportRow(CatalogSource.Store, "14.00", "usual"));
        Assert.Equal("usual", CatalogSourceText.ReportRow(CatalogSource.None, "", "usual"));
        Assert.Contains("14.00", CatalogSourceText.ReportRow(CatalogSource.None, "14.00", "usual"));
    }
}
