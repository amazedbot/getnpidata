using Npi.Core.Search;
using Npi.Loader.Csv;
using Npi.Loader.Datasets;

namespace Npi.Loader.Tests;

/// <summary>
/// The facility owner, ownership change and nursing home inspection sources (docs/PRODUCT_PLAN.md step 3.6 part 2) without a database:
/// catalog discovery and that every column they map is in CMS's layouts (the fixtures copy each file's header).
/// </summary>
public class OwnershipSourceTests
{
    private static CmsCatalog Catalog()
    {
        using var stream = File.OpenRead(Fixtures.Path("datasets/cms_catalog_sample.json"));
        return CmsCatalog.Parse(stream);
    }

    private static HashSet<string> Header(string fixture)
    {
        using var stream = File.OpenRead(Fixtures.Path("datasets/" + fixture));
        var header = CsvHeader.Read(stream);
        Assert.Equal("\r\n", header.LineTerminator); // CMS writes these files with CRLF
        return header.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_owner_and_change_file_is_found_in_the_catalog_by_its_newest_period()
    {
        var catalog = Catalog();
        foreach (var (title, _) in FacilityOwnerSource.Files.Concat(OwnershipChangeSource.ChangeFiles).Concat(OwnershipChangeSource.OwnerFiles))
        {
            Assert.NotNull(catalog.FindLatest(title));
        }

        // The hospital file is published monthly, the newest an "_update" of the month before.
        Assert.Equal("2026-08-31 Hospital_All_Owners_2026.07.31_update.csv", catalog.FindLatest("Hospital All Owners")!.Version);
        // "… Change of Ownership" and "… Change of Ownership - Owner Information" are different datasets.
        Assert.Equal("2026-06-30 SNF_CHOW_2026.07.17.csv", catalog.FindLatest("Skilled Nursing Facility Change of Ownership")!.Version);
    }

    [Theory]
    [InlineData("hospital_all_owners_sample.csv", 15)]
    [InlineData("snf_all_owners_sample.csv", 17)]
    [InlineData("hha_all_owners_sample.csv", 15)]
    [InlineData("hospital_chow_owners_sample.csv", 12)]
    public void Owner_files_have_the_common_columns_and_some_owner_kinds(string fixture, int kinds)
    {
        var header = Header(fixture);
        Assert.All(FacilityOwnerSource.OwnerColumns, c => Assert.Contains(c.Header, header));
        Assert.Equal(kinds, FacilityOwnerSource.KindColumns.Count(k => header.Contains(k.Header)));
    }

    [Fact]
    public void Owner_kind_columns_and_codes_match_the_sites_list()
    {
        Assert.Equal(OwnerKinds.All.Select(k => k.Code).Order(StringComparer.Ordinal), FacilityOwnerSource.KindColumns.Select(k => k.Code).Order(StringComparer.Ordinal));
        var sql = FacilityOwnerSource.KindsSql();
        Assert.All(FacilityOwnerSource.KindColumns, k => Assert.Contains($"IF(`f_{k.Code}` = 1, '{k.Code}', NULL)", sql, StringComparison.Ordinal));
        Assert.Equal("'34', '35'", FacilityOwnerSource.CodeList(["34", "35"]));
    }

    [Theory]
    [InlineData("hospital_chow_sample.csv")]
    [InlineData("snf_chow_sample.csv")]
    public void Change_files_have_buyer_and_seller(string fixture)
    {
        var header = Header(fixture);
        foreach (var side in new[] { "BUYER", "SELLER" })
        {
            foreach (var column in new[] { "ENROLLMENT ID", "CCN", "NPI", "ASSOCIATE ID", "ORGANIZATION NAME", "DOING BUSINESS AS NAME" })
            {
                Assert.Contains($"{column} - {side}", header);
            }
        }

        Assert.Contains("EFFECTIVE DATE", header);
        Assert.Contains("CHOW TYPE CODE", header);
    }

    [Fact]
    public void Nursing_home_files_have_the_columns_the_summaries_read()
    {
        foreach (var fixture in new[] { "nh_health_deficiencies_sample.csv", "nh_fire_safety_deficiencies_sample.csv" })
        {
            var header = Header(fixture);
            foreach (var column in new[] { "CMS Certification Number (CCN)", "Survey Date", "Deficiency Prefix", "Deficiency Category", "Deficiency Tag Number",
                         "Deficiency Description", "Scope Severity Code", "Deficiency Corrected", "Correction Date", "Inspection Cycle", "Complaint Deficiency",
                         "Infection Control Inspection Deficiency" })
            {
                Assert.Contains(column, header);
            }
        }

        Assert.Contains("Payment Denial Length in Days", Header("nh_penalties_sample.csv"));
        Assert.Equal("cc_nursing_home_inspections", new NursingHomeInspectionSource().Name);
    }

    [Fact]
    public void The_enrollments_version_says_what_it_keeps()
    {
        // A new marker reloads unchanged files once (the enrollment and associate IDs were added for the owners).
        Assert.Equal("enrollments", FacilityEnrollmentSource.Layout);
    }
}
