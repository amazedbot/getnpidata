namespace Npi.Loader;

/// <summary>Loader settings (CLAUDE.md §5). Secrets come from user-secrets or environment variables.</summary>
public sealed class LoaderOptions
{
    /// <summary>Downloads and temporary files. Default: <see cref="DefaultRoot"/>/work.</summary>
    public string WorkFolder { get; set; } = "";

    /// <summary>Rolling log files. Default: <see cref="DefaultRoot"/>/logs.</summary>
    public string LogFolder { get; set; } = "";

    /// <summary>Keep the most recently loaded zip in the work folder (older ones are deleted).</summary>
    public bool KeepLastZip { get; set; } = true;

    public string NppesPageUrl { get; set; } = "https://download.cms.gov/nppes/NPI_Files.html";

    /// <summary>A full replace (monthly, deactivation report) must have at least this share of the current rows.</summary>
    public double MinRowRatio { get; set; } = 0.95;

    public int DownloadAttempts { get; set; } = 4;

    /// <summary>A file of at least <see cref="DownloadPartMinBytes"/> is fetched in this many byte ranges at once, when the server allows it.</summary>
    public int DownloadParts { get; set; } = 4;

    public long DownloadPartMinBytes { get; set; } = Datasets.FileDownloader.DefaultPartMinBytes;

    /// <summary>
    /// Stage 5.5 datasets <c>run</c> and <c>datasets</c> check and load at once (speed item 7); the order between those that
    /// read another's tables is kept (<see cref="Datasets.DatasetSource.After"/>), and at most one heavy load runs at a time
    /// (<see cref="Datasets.DatasetSource.Heavy"/>). 1 = one after another.
    /// </summary>
    public int ParallelSources { get; set; } = 3;

    /// <summary>
    /// States whose license files are downloaded and filtered at once (speed item 7); matching and replacing their rows stays
    /// one state at a time. 1 = one state after another.
    /// </summary>
    public int ParallelStates { get; set; } = 3;

    /// <summary>Requests in flight to a state board's paged feed (Vermont's roster, 100 records a request); answers are kept in order.</summary>
    public int StateFeedParallelRequests { get; set; } = 4;

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

    /// <summary>openFDA bulk download index; the drug and device enforcement (recall) files are loaded (Stage 5.5 item 17).</summary>
    public string FdaDownloadIndexUrl { get; set; } = "https://api.fda.gov/download.json";

    /// <summary>SEC EDGAR registrants with tickers.</summary>
    public string SecCompanyTickersUrl { get; set; } = "https://www.sec.gov/files/company_tickers_exchange.json";

    /// <summary>
    /// User-Agent for SEC EDGAR, which asks for a contact e-mail ("getnpidata name@example.com"). Private: user-secrets only.
    /// Empty: the SEC source is skipped.
    /// </summary>
    public string SecUserAgent { get; set; } = "";

    /// <summary>HHS-OIG's list of Corporate Integrity Agreements (pages ?page=2, …).</summary>
    public string OigCiaUrl { get; set; } = "https://oig.hhs.gov/compliance/corporate-integrity-agreements/browse-cias/";

    /// <summary>data.medicaid.gov metastore; the newest "NADAC (National Average Drug Acquisition Cost) &lt;year&gt;" file is loaded (item 19, part 3).</summary>
    public string MedicaidCatalogUrl { get; set; } = "https://data.medicaid.gov/api/1/metastore/schemas/dataset/items?show-reference-ids=false";

    /// <summary>data.chhs.ca.gov's CKAN package of the enrolled Medi-Cal fee-for-service providers (state Medicaid lists, Phase 3).</summary>
    public string MediCalPackageUrl { get; set; } = "https://data.chhs.ca.gov/api/3/action/package_show?id=profile-of-enrolled-medi-cal-fee-for-service-ffs-providers";

    /// <summary>openFDA's API (adverse event counts).</summary>
    public string OpenFdaApiUrl { get; set; } = "https://api.fda.gov/";

    /// <summary>An optional openFDA API key (free; private: user-secrets). Without it openFDA allows 1,000 requests a day.</summary>
    public string OpenFdaApiKey { get; set; } = "";

    /// <summary>openFDA requests per run without a key (one per product).</summary>
    public int OpenFdaRequestsPerRun { get; set; } = 900;

    /// <summary>openFDA requests per run with a key.</summary>
    public int OpenFdaRequestsPerRunWithKey { get; set; } = 20_000;

    /// <summary>With the key: one openFDA request started every this often (about 230 a minute; openFDA allows 240).</summary>
    public TimeSpan OpenFdaKeyedRequestInterval { get; set; } = TimeSpan.FromMilliseconds(260);

    /// <summary>With the key: openFDA requests in flight at once.</summary>
    public int OpenFdaParallelRequests { get; set; } = 4;

    /// <summary>ClinicalTrials.gov's study API.</summary>
    public string ClinicalTrialsApiUrl { get; set; } = "https://clinicaltrials.gov/api/v2/studies";

    /// <summary>Products asked about per run (two requests each).</summary>
    public int ClinicalTrialsPerRun { get; set; } = 600;

    /// <summary>
    /// ClinicalTrials.gov products asked at once (speed item 11). 1 = one request at a time with <see cref="ApiRequestDelay"/>
    /// after each, as before.
    /// </summary>
    public int ClinicalTrialsParallelRequests { get; set; } = 4;

    /// <summary>
    /// With more than one ClinicalTrials.gov product at once: one request started every this often, 5 a second at most.
    /// NLM publishes no rate limit for API v2 (its about-api page and the OpenAPI document at /api/oas/v2 have none, checked
    /// 2026-10-15), so this stays modest: an answer takes ~0.1 s, so the requests in flight only cover slow answers and the
    /// interval sets the pace; about twice the rate of the one-at-a-time loop (~2.4 a second), which never saw a 429; a 429
    /// stops the run.
    /// </summary>
    public TimeSpan ClinicalTrialsRequestInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Pause between requests to the public APIs (openFDA allows 240 a minute).</summary>
    public TimeSpan ApiRequestDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The API caches retry a server error (5xx) or a dropped connection after this, then after twice this, then skip the product.</summary>
    public TimeSpan ApiRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The API caches stop a run early (not as a failure) after this many products in a row kept failing.</summary>
    public int ApiFailuresInARow { get; set; } = 5;

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

    /// <summary>S3 listing of the FEMA USA Structures GeoParquet files on Source Cooperative (Stage 5.5 item 18).</summary>
    public string FemaStructuresListingUrl { get; set; } = "https://data.source.coop/wherobots?list-type=2&prefix=usa-structures/geoparquet/";

    /// <summary>Prefix of the listed keys' download URLs.</summary>
    public string FemaStructuresBaseUrl { get; set; } = "https://data.source.coop/wherobots/";

    /// <summary>
    /// Where DuckDB reads the listed files in place when only new addresses are matched (speed item 4); null = the same as
    /// <see cref="FemaStructuresBaseUrl"/>. A local folder works too (the tests).
    /// </summary>
    public string? FemaStructuresReadBaseUrl { get; set; }

    /// <summary>Overture places below this confidence (0–1) are ignored.</summary>
    public double OverturePlaceMinConfidence { get; set; } = 0.6;

    /// <summary>
    /// <c>geocode</c> and <c>overture</c> skip rebuilding the map tables (a 20-minute step), for scripts that run several of
    /// them and rebuild once at the end (environment variable NPI_SkipMapBuild=true).
    /// </summary>
    public bool SkipMapBuild { get; set; }

    /// <summary>
    /// State license files the owner downloads from boards that require a sign-in, one subfolder per state (FL), Stage 5.5
    /// item 15. Default: <see cref="DefaultRoot"/>/state-files.
    /// </summary>
    public string StateLicenseFilesFolder { get; set; } = "";

    public string ResolvedStateLicenseFilesFolder => Resolve(StateLicenseFilesFolder, "state-files");

    /// <summary>
    /// The AHRQ Compendium of U.S. Health Systems CSV files the owner downloads from <see cref="AhrqCompendiumPageUrl"/> (the
    /// systems file and the hospital, nursing home and home health linkage files; docs/PRODUCT_PLAN.md step 3.6 part 2):
    /// ahrq.gov answers programs with a browser check, so the loader reads them from here. Default: <see cref="DefaultRoot"/>/ahrq-compendium.
    /// </summary>
    public string AhrqCompendiumFolder { get; set; } = "";

    public string ResolvedAhrqCompendiumFolder => Resolve(AhrqCompendiumFolder, "ahrq-compendium");

    /// <summary>Where the Compendium files are published (recorded as the source, and named in the doctor's advice).</summary>
    public string AhrqCompendiumPageUrl { get; set; } = "https://www.ahrq.gov/chsp/data-resources/compendium.html";

    /// <summary>
    /// HRSA's 340B OPAIS covered entity export the owner downloads (the "Covered Entity Daily Export" JSON as its .zip,
    /// 340B_CoveredEntityJson_Daily_&lt;yyyyMMdd_HHmmss&gt;.zip, or the .json in it, or the "Covered Entity Daily Report" .xlsx;
    /// docs/PRODUCT_PLAN.md step 3.6 part 2): OPAIS has no download URL or API, so the loader reads the newest file here. Default:
    /// <see cref="DefaultRoot"/>/hrsa-340b.
    /// </summary>
    public string Hrsa340bFolder { get; set; } = "";

    public string ResolvedHrsa340bFolder => Resolve(Hrsa340bFolder, "hrsa-340b");

    /// <summary>OPAIS's page with the daily reports (Reports/Files).</summary>
    public string Hrsa340bPageUrl { get; set; } = "https://340bopais.hrsa.gov/Reports";

    /// <summary>
    /// HRSA's Health Center Service Delivery and Look-Alike Sites file, listed on data.hrsa.gov's data download page under the
    /// same name every day (docs/PRODUCT_PLAN.md step 3.6 part 2); its Last-Modified and size are the version.
    /// </summary>
    public string HrsaHealthCenterSitesUrl { get; set; } = "https://data.hrsa.gov/DataDownload/DD_Files/Health_Center_Service_Delivery_and_LookAlike_Sites.csv";

    public string ResolvedWorkFolder => Resolve(WorkFolder, "work");

    /// <summary>
    /// The ready-made file of every active provider (owner, 2026-10-14), rebuilt by <c>run</c> after a load changed its data and
    /// served by the site for an export with no filter. The site reads it from the same folder (its <c>FullExport:Folder</c>).
    /// Default: <see cref="DefaultRoot"/>/exports.
    /// </summary>
    public string ExportFolder { get; set; } = "";

    public string ResolvedExportFolder => Resolve(ExportFolder, "exports");

    /// <summary>How many NPI ranges the full export reads at once.</summary>
    public int ExportParts { get; set; } = 8;

    /// <summary>
    /// How many of the builders after the projection that read none of each other's tables (standardized credentials, practice
    /// address types, area counts) run at once, each on its own MySQL connection (speed item 13); 1 = one after another.
    /// </summary>
    public int ParallelBuilders { get; set; } = 3;

    /// <summary>Where CMS serves its monthly NPPES files, listed or not (the history, docs/PRODUCT_PLAN.md step 3.4).</summary>
    public string HistoryCmsBaseUrl { get; set; } = "https://download.cms.gov/nppes/";

    /// <summary>NBER's copies of the older monthly NPPES files (https://doi.org/10.60592/bjwk-8n62).</summary>
    public string HistoryNberBaseUrl { get; set; } = "https://data.nber.org/";

    /// <summary>Download attempts per archived file: data.nber.org answers 503/504 about half the time.</summary>
    public int HistoryDownloadAttempts { get; set; } = 8;

    /// <summary>Keep each archived zip after it is read (otherwise deleted at once: the archive is ~137 GB).</summary>
    public bool HistoryKeepZips { get; set; }

    /// <summary>Download the next archived file while the current one is read (two zips on disk at most, ~2.5 GB).</summary>
    public bool HistoryPrefetch { get; set; } = true;

    public string ResolvedHistoryFolder => Path.Combine(ResolvedWorkFolder, "history");

    public string ResolvedLogFolder => Resolve(LogFolder, "logs");

    /// <summary>
    /// Where the work, log and state-file folders go unless configured: %ProgramData%\getnpidata on Windows; elsewhere
    /// $XDG_DATA_HOME/getnpidata, else ~/.local/share/getnpidata (the shared data folder there, /usr/share, isn't writable).
    /// </summary>
    public static string DefaultRoot => Npi.Core.DataFolders.DefaultRoot;

    internal static string DefaultRootFor(bool windows, string? xdgDataHome, string programData, string home) =>
        Npi.Core.DataFolders.DefaultRootFor(windows, xdgDataHome, programData, home);

    private static string Resolve(string configured, string leaf) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(DefaultRoot, leaf)
            : Environment.ExpandEnvironmentVariables(configured);
}
