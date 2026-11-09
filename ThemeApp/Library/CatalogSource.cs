namespace ProsperoThemes.App.Library;

/// <summary>
/// Where the stock catalog of this console's firmware comes from (<see cref="StockCatalog"/>): the app carries it
/// (Bundled), the store had it and the app keeps a checked copy (Store, see <c>StoreCatalog</c>), or there is none (None).
/// The System tab says which.
/// </summary>
public enum CatalogSource { None, Bundled, Store }

/// <summary>The System tab's texts for a <see cref="CatalogSource"/>.</summary>
public static class CatalogSourceText
{
    /// <summary>What follows the firmware in the System tab's info panel ("13.60 · catalog from the store").</summary>
    public static string Info(CatalogSource source) => source switch
    {
        CatalogSource.Bundled => "catalog in the app",
        CatalogSource.Store => "catalog from the store",
        _ => "no catalog yet",
    };

    /// <summary>
    /// The Firmware report row's description: without a catalog for a known firmware it says that a report is how one
    /// comes; otherwise the row's usual text (<paramref name="usual"/>).
    /// </summary>
    public static string ReportRow(CatalogSource source, string firmwareName, string usual) =>
        source == CatalogSource.None && firmwareName.Length > 0
            ? $"No catalog for {firmwareName} yet: a report from this console adds one"
            : usual;
}
