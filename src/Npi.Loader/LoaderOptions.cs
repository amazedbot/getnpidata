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

    /// <summary>Open Payments metastore (DKAN) listing every dataset; "&lt;year&gt; General Payment Data" entries are used.</summary>
    public string OpenPaymentsCatalogUrl { get; set; } = "https://openpaymentsdata.cms.gov/api/1/metastore/schemas/dataset/items?show-reference-ids=false";

    /// <summary>Census population estimates datasets folder (vintage folders "2020-YYYY/" below it).</summary>
    public string CensusPopulationBaseUrl { get; set; } = "https://www2.census.gov/programs-surveys/popest/datasets/";

    /// <summary>US Census Bureau batch geocoder (Stage 5.5 item 10, map search).</summary>
    public string CensusGeocoderUrl { get; set; } = "https://geocoding.geo.census.gov/geocoder/locations/addressbatch";

    /// <summary>Census geocoder benchmark (address data release).</summary>
    public string GeocodeBenchmark { get; set; } = "Public_AR_Current";

    /// <summary>Addresses per geocoder request (the Census maximum is 10,000).</summary>
    public int GeocodeBatchSize { get; set; } = 10_000;

    /// <summary>Geocoder requests in flight at once.</summary>
    public int GeocodeParallelism { get; set; } = 2;

    /// <summary>
    /// Batches <c>run</c> geocodes at most (new addresses from the weekly files are a few thousand); the
    /// <c>geocode</c> command clears any backlog. 0 = no limit.
    /// </summary>
    public int GeocodeBatchesPerRun { get; set; } = 20;

    /// <summary>S3 listing of Overture Maps releases; the newest "release/&lt;date&gt;.&lt;n&gt;/" folder is used (Stage 5.5 item 10).</summary>
    public string OvertureReleasesUrl { get; set; } = "https://overturemaps-us-west-2.s3.us-west-2.amazonaws.com/?list-type=2&prefix=release/&delimiter=/";

    /// <summary>Overture release folders, read in place with DuckDB (public bucket, no credentials).</summary>
    public string OvertureBaseUrl { get; set; } = "s3://overturemaps-us-west-2/release/";

    /// <summary>Overture places below this confidence (0–1) are ignored.</summary>
    public double OverturePlaceMinConfidence { get; set; } = 0.6;

    public string ResolvedWorkFolder => Resolve(WorkFolder, "work");

    public string ResolvedLogFolder => Resolve(LogFolder, "logs");

    private static string Resolve(string configured, string leaf) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "getnpidata", leaf)
            : Environment.ExpandEnvironmentVariables(configured);
}
