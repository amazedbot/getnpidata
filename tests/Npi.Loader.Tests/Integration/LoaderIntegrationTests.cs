using System.IO.Compression;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// End-to-end loads of hand-made NPPES zips into a scratch MySQL database through the real
/// <c>load-file</c> command path. Skipped unless NPI_TEST_MYSQL is set.
/// </summary>
public sealed class LoaderIntegrationTests : IDisposable
{
    private const string A = "1000000004";
    private const string B = "1000000012";
    private const string C = "1000000020";
    private const string D = "1000000038";

    // Values that broke or would break a naive loader.
    private const string LastNameWithCommaAndBraces = "O'Brien, Jr {MD}";               // defect #1 ({ }) and #2 (comma)
    private const string AddressWithCommas = "123 Main St, Suite {5}, Rear";
    private const string CityWithBackslashes = @"C:\new\Nork \N \t";                   // must not be unescaped
    private const string UnicodeLegalName = "Smith, Jones & Co, LLC — Zürich 北京 ☺"; // utf8mb4
    private static readonly string LongLegalName = new('W', 100);                     // V2 width (V1 was 70)

    private readonly string _folder = Directory.CreateTempSubdirectory("npi-it-").FullName;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Monthly_load_keeps_every_value_intact(string lineTerminator)
    {
        await using var db = await TestDatabase.CreateAsync();
        var zip = new NppesZipBuilder()
            .Provider(A,
                ("Entity_Type_Code", "1"),
                ("Provider_Last_Name_Legal_Name", LastNameWithCommaAndBraces),
                ("Provider_First_Name", "José"),
                ("Provider_Middle_Name", @"\N"),
                ("Provider_Credential_Text", "M.D., Ph.D."),
                ("Provider_First_Line_Business_Practice_Location_Address", AddressWithCommas),
                ("Provider_Business_Practice_Location_Address_City_Name", CityWithBackslashes),
                ("Provider_Business_Practice_Location_Address_State_Name", "NY"),
                ("Provider_Gender_Code", "F"),
                ("Provider_Enumeration_Date", "01/02/2003"),
                ("Last_Update_Date", "09/01/2026"),
                ("Healthcare_Provider_Taxonomy_Code_1", "111N00000X"),
                ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"))
            .Provider(B,
                ("Entity_Type_Code", "2"),
                ("Provider_Organization_Name_Legal_Business_Name", UnicodeLegalName),
                ("Provider_Other_Organization_Name", LongLegalName),
                ("Last_Update_Date", "09/02/2026"))
            .Provider(C, ("NPI_Deactivation_Date", "08/01/2020"))
            .OtherName(A, "O'Brien Clinic, P.C.", "3", "09/28/2026")
            .OtherName(A, "{Braces} Inc", "5", "")
            .OtherName(B, "Smith & Jones", "3", "01/15/2025")
            .Location(B, "1 Main St, Bldg {2}", "", "Queen Creek", "AZ", "851426027", "US", "4808471015")
            .Write(_folder, "NPPES_Data_Dissemination_September_2026_V2.zip", lineTerminator);

        Assert.Equal(0, await LoadFileAsync(db, zip));

        var a = (await db.QueryAsync<(string Last, string First, string Middle, string Credential, string Address, string City, string? Org, string Gender, string LoadedFrom)>(
            """
            SELECT Provider_Last_Name_Legal_Name, Provider_First_Name, Provider_Middle_Name, Provider_Credential_Text,
                   Provider_First_Line_Business_Practice_Location_Address, Provider_Business_Practice_Location_Address_City_Name,
                   Provider_Organization_Name_Legal_Business_Name, Provider_Gender_Code, Loaded_From
            FROM npidata WHERE NPI = @A
            """, new { A })).Single();
        Assert.Equal(LastNameWithCommaAndBraces, a.Last);
        Assert.Equal("José", a.First);
        Assert.Equal(@"\N", a.Middle);
        Assert.Equal("M.D., Ph.D.", a.Credential);
        Assert.Equal(AddressWithCommas, a.Address);
        Assert.Equal(CityWithBackslashes, a.City);
        Assert.Null(a.Org); // empty → NULL
        Assert.Equal("F", a.Gender); // V2 "Provider Sex Code"
        Assert.Equal("NPPES_Data_Dissemination_September_2026_V2.zip", a.LoadedFrom);

        Assert.Equal(UnicodeLegalName, await db.ScalarAsync<string>("SELECT Provider_Organization_Name_Legal_Business_Name FROM npidata WHERE NPI = @B", new { B }));
        Assert.Equal(LongLegalName, await db.ScalarAsync<string>("SELECT Provider_Other_Organization_Name FROM npidata WHERE NPI = @B", new { B }));

        var otherNames = await db.QueryAsync<(string Name, DateTime? Created)>(
            "SELECT Provider_Other_Organization_Name, Created_Date FROM other_names WHERE NPI = @A ORDER BY ID", new { A });
        Assert.Equal([("O'Brien Clinic, P.C.", new DateTime(2026, 9, 28)), ("{Braces} Inc", (DateTime?)null)], otherNames);

        Assert.Equal("1 Main St, Bldg {2}", await db.ScalarAsync<string>(
            "SELECT Provider_Secondary_Practice_Location_Address_Line_1 FROM practice_locations WHERE NPI = @B", new { B }));

        Assert.Equal([(A, 0), (B, 0), (C, 1)], await db.QueryAsync<(string, int)>("SELECT NPI, Is_Deactivated FROM npidata ORDER BY NPI"));
        Assert.Equal("Completed", await db.ScalarAsync<string>("SELECT status FROM downlog WHERE filename = 'NPPES_Data_Dissemination_September_2026_V2.zip'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name LIKE '%\\_staging'"));
    }

    // Legacy defect #11: TRUNCATE before reload emptied the table. Now a suspicious monthly is rejected
    // and the current tables stay untouched.
    [Fact]
    public async Task A_monthly_with_too_few_rows_leaves_the_current_data_alone()
    {
        await using var db = await TestDatabase.CreateAsync();
        var full = new NppesZipBuilder();
        for (var i = 0; i < 20; i++)
        {
            full.Provider($"10000001{i:D2}", ("Last_Update_Date", "09/01/2026"));
        }

        Assert.Equal(0, await LoadFileAsync(db, full.Write(_folder, "NPPES_Data_Dissemination_September_2026_V2.zip")));
        var tiny = new NppesZipBuilder().Provider(A, ("Last_Update_Date", "10/01/2026"));

        Assert.Equal(1, await LoadFileAsync(db, tiny.Write(_folder, "NPPES_Data_Dissemination_October_2026_V2.zip")));

        Assert.Equal(20L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM npidata"));
        Assert.Equal("Failed", await db.ScalarAsync<string>("SELECT status FROM downlog WHERE filename = 'NPPES_Data_Dissemination_October_2026_V2.zip'"));
        Assert.Contains("fewer than", await db.ScalarAsync<string>("SELECT error FROM downlog WHERE filename = 'NPPES_Data_Dissemination_October_2026_V2.zip'"));
    }

    // Legacy defects #9 (older weekly overwrote newer data) and #5 (re-runs duplicated other names).
    [Fact]
    public async Task Weeklies_skip_older_rows_and_replace_child_rows_exactly_once()
    {
        await using var db = await TestDatabase.CreateAsync();
        var monthly = new NppesZipBuilder()
            .Provider(A, ("Provider_Last_Name_Legal_Name", "Alpha"), ("Last_Update_Date", "09/10/2026"))
            .Provider(B, ("Provider_Last_Name_Legal_Name", "Bravo"), ("Last_Update_Date", "09/10/2026"))
            .OtherName(A, "A1", "3", "01/01/2020")
            .OtherName(B, "B1", "3", "01/01/2020")
            .OtherName(B, "B2", "3", "")
            .Location(B, "B location");
        Assert.Equal(0, await LoadFileAsync(db, monthly.Write(_folder, "NPPES_Data_Dissemination_September_2026_V2.zip")));

        var weekly = new NppesZipBuilder()
            .Provider(A, ("Provider_Last_Name_Legal_Name", "Stale"), ("Last_Update_Date", "08/01/2026"))
            .Provider(B, ("Provider_Last_Name_Legal_Name", "Bravo-Updated"), ("Last_Update_Date", "09/15/2026"))
            .Provider(D, ("Provider_Last_Name_Legal_Name", "Delta"), ("Last_Update_Date", "09/16/2026"))
            .OtherName(A, "A-stale", "3", "")
            .OtherName(B, "B3", "3", "")
            .OtherName(D, "D1", "3", "")
            .Write(_folder, "NPPES_Data_Dissemination_091426_092026_Weekly_V2.zip");

        Assert.Equal(0, await LoadFileAsync(db, weekly));
        Assert.Equal(0, await LoadFileAsync(db, weekly)); // re-running the same weekly changes nothing

        Assert.Equal([(A, "Alpha"), (B, "Bravo-Updated"), (D, "Delta")],
            await db.QueryAsync<(string, string)>("SELECT NPI, Provider_Last_Name_Legal_Name FROM npidata ORDER BY NPI"));
        Assert.Equal([(A, "A1"), (B, "B3"), (D, "D1")],
            await db.QueryAsync<(string, string)>("SELECT NPI, Provider_Other_Organization_Name FROM other_names ORDER BY NPI, ID"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM practice_locations WHERE NPI = @B", new { B }));
    }

    [Fact]
    public async Task The_deactivation_report_replaces_the_report_table_and_sets_flags()
    {
        await using var db = await TestDatabase.CreateAsync();
        var monthly = new NppesZipBuilder()
            .Provider(A, ("Last_Update_Date", "09/01/2026"))
            .Provider(B, ("NPI_Deactivation_Date", "01/01/2020"), ("NPI_Reactivation_Date", "02/01/2021"), ("Last_Update_Date", "02/01/2021"))
            .Provider(C, ("Last_Update_Date", "09/01/2026"));
        Assert.Equal(0, await LoadFileAsync(db, monthly.Write(_folder, "NPPES_Data_Dissemination_September_2026_V2.zip")));

        Assert.Equal(0, await LoadFileAsync(db, DeactivationZip("NPPES_Deactivated_NPI_Report_091426_V2.zip", (C, "09/14/2026"), (B, "01/01/2020"))));

        Assert.Equal([(A, 0), (B, 0), (C, 1)], await db.QueryAsync<(string, int)>("SELECT NPI, Is_Deactivated FROM npidata ORDER BY NPI"));
        Assert.Equal("09/14/2026", await db.ScalarAsync<string>("SELECT NPI_Deactivation_Date FROM npidata WHERE NPI = @C", new { C }));

        Assert.Equal(0, await LoadFileAsync(db, DeactivationZip("NPPES_Deactivated_NPI_Report_101226_V2.zip", (A, "10/12/2026"), (C, "09/14/2026"))));

        Assert.Equal([A, C], await db.QueryAsync<string>("SELECT NPI FROM nppes_deactivated_npi_report ORDER BY NPI"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT Is_Deactivated FROM npidata WHERE NPI = @A", new { A }));
    }

    [Fact]
    public async Task Migrations_are_recorded_and_not_reapplied()
    {
        await using var db = await TestDatabase.CreateAsync();

        Assert.Equal(0, await new Db.MigrationRunner(db.Database, Logger.None).ApplyAsync(_ct));
        Assert.Equal(Db.MigrationRunner.LoadEmbedded().Count, await db.ScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal("Legacy", await db.ScalarAsync<string>(
            "SELECT column_default FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'downlog' AND column_name = 'status'"));
    }

    private string DeactivationZip(string zipName, params (string Npi, string Date)[] rows)
    {
        var path = Path.Combine(_folder, zipName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var xlsx = XlsxWriter.Write(new object?[][] { ["NPPES Deactivated Records"], ["NPI", "NPPES Deactivation Date"] }
            .Concat(rows.Select(r => new object?[] { r.Npi, r.Date })));
        using var entry = zip.CreateEntry("NPPES Deactivated NPI Report.xlsx").Open();
        entry.Write(xlsx);
        return path;
    }

    private async Task<int> LoadFileAsync(TestDatabase db, string zip)
    {
        using var http = new HttpClient();
        var options = new LoaderOptions { WorkFolder = _folder, LogFolder = _folder };
        return await new LoaderApp(options, db.Database, http, Logger.None).LoadFileAsync(zip, _ct);
    }
}
