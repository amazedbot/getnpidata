namespace Npi.Loader;

/// <summary>Loader settings (CLAUDE.md §5). Secrets come from user-secrets or environment variables.</summary>
public sealed class LoaderOptions
{
    /// <summary>Downloads and temporary files. Default: %ProgramData%\getnpidata\work.</summary>
    public string WorkFolder { get; set; } = "";

    /// <summary>Rolling log files. Default: %ProgramData%\getnpidata\logs.</summary>
    public string LogFolder { get; set; } = "";

    /// <summary>Keep the most recently loaded zip in the work folder (older ones are deleted).</summary>
    public bool KeepLastZip { get; set; } = true;

    public string NppesPageUrl { get; set; } = "https://download.cms.gov/nppes/NPI_Files.html";

    /// <summary>A full replace (monthly, deactivation report) must have at least this share of the current rows.</summary>
    public double MinRowRatio { get; set; } = 0.95;

    public int DownloadAttempts { get; set; } = 4;

    /// <summary>NUCC taxonomy CSV page; the newest nucc_taxonomy_&lt;ver&gt;.csv link on it is loaded.</summary>
    public string NuccPageUrl { get; set; } = "https://www.nucc.org/index.php/code-sets-mainmenu-41/provider-taxonomy-mainmenu-40/csv-mainmenu-57";

    /// <summary>A NUCC release with fewer codes than this is rejected as broken (26.1 has 883).</summary>
    public int MinTaxonomyCodes { get; set; } = 500;

    /// <summary>HUD USPS crosswalk API; type=2 is ZIP → county.</summary>
    public string HudApiUrl { get; set; } = "https://www.huduser.gov/hudapi/public/usps?type=2&query=All";

    /// <summary>HUD API token (secret: user-secrets or environment only).</summary>
    public string HudApiToken { get; set; } = "";

    /// <summary>How often <c>run</c> downloads the HUD crosswalk to see whether a new quarter is out.</summary>
    public int HudRefreshDays { get; set; } = 14;

    /// <summary>Census Gazetteer directory; the newest &lt;year&gt;_Gazetteer folder is used.</summary>
    public string GazetteerBaseUrl { get; set; } = "https://www2.census.gov/geo/docs/maps-data/data/gazetteer/";

    /// <summary>Census 2020 county codes; fallback names for county FIPS the Gazetteer lacks (territories).</summary>
    public string CountyCodes2020Url { get; set; } = "https://www2.census.gov/geo/docs/reference/codes2020/national_county2020.txt";

    /// <summary>data.cms.gov DCAT catalog listing every CMS dataset release (Stage 5.5).</summary>
    public string CmsCatalogUrl { get; set; } = "https://data.cms.gov/data.json";

    /// <summary>Provider Data Catalog (Care Compare) metastore; a dataset id is appended.</summary>
    public string ProviderDataMetastoreUrl { get; set; } = "https://data.cms.gov/provider-data/api/1/metastore/schemas/dataset/items/";

    /// <summary>HHS-OIG LEIE full list of excluded individuals and entities.</summary>
    public string LeieUrl { get; set; } = "https://oig.hhs.gov/exclusions/downloadables/UPDATED.csv";

    /// <summary>HRSA HPSA detail files; {discipline} is PC, DH or MH.</summary>
    public string HrsaHpsaUrlTemplate { get; set; } = "https://data.hrsa.gov/DataDownload/DD_Files/BCD_HPSA_FCT_DET_{discipline}.csv";

    /// <summary>Census population estimates datasets folder (vintage folders "2020-YYYY/" below it).</summary>
    public string CensusPopulationBaseUrl { get; set; } = "https://www2.census.gov/programs-surveys/popest/datasets/";

    public string ResolvedWorkFolder => Resolve(WorkFolder, "work");

    public string ResolvedLogFolder => Resolve(LogFolder, "logs");

    private static string Resolve(string configured, string leaf) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "getnpidata", leaf)
            : Environment.ExpandEnvironmentVariables(configured);
}
