using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>One page per organization (docs/PRODUCT_PLAN.md step 3.9): merging clinicians and owners, and choosing the page a list links to.</summary>
public class OrganizationPageTests
{
    private static OrganizationClinicians.Entry Clinician(string npi, string name, ClinicianSource source, string? specialty = "Cardiologist",
        string? state = "NY", string? city = "JAMAICA") => new(npi, name, specialty, city, state, source);

    [Fact]
    public void A_clinician_in_several_sources_is_listed_once_with_every_source()
    {
        var merged = OrganizationClinicians.Merge(
        [
            Clinician("1000000004", "DOE, JANE", ClinicianSource.FacilityAffiliation),
            Clinician("1000000004", "DOE, JANE", ClinicianSource.Reassignment),
            Clinician("1000000012", "ROE, RICHARD", ClinicianSource.CareCompareGroup),
            Clinician("1000000004", "DOE, JANE", ClinicianSource.CareCompareGroup),
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(ClinicianSource.FacilityAffiliation | ClinicianSource.Reassignment | ClinicianSource.CareCompareGroup,
            merged.Single(c => c.Npi == "1000000004").Sources);

        var summary = OrganizationClinicians.Summarize(merged);
        Assert.Equal((2, 1, 1, 2, 1), (summary.Total, summary.FacilityAffiliations, summary.Reassignments, summary.CareCompareGroups, summary.InSeveral));
        Assert.Equal(("Cardiologist", 2), (summary.BySpecialty[0].Specialty, summary.BySpecialty[0].Clinicians));
    }

    [Fact]
    public void Clinicians_sort_by_name_specialty_location_or_ties()
    {
        var entries = new[]
        {
            Clinician("1000000020", "BROWN, ANN", ClinicianSource.Reassignment, "Pediatrician", "NY", "QUEENS"),
            Clinician("1000000038", "ADAMS, BEN", ClinicianSource.FacilityAffiliation | ClinicianSource.Reassignment, null, "CT", "STAMFORD"),
            Clinician("1000000046", "CLARK, CAL", ClinicianSource.FacilityAffiliation, "Cardiologist", null, null),
        };

        Assert.Equal(["ADAMS, BEN", "BROWN, ANN", "CLARK, CAL"], OrganizationClinicians.Sort(entries, null).Select(e => e.SortName));
        Assert.Equal(["CLARK, CAL", "BROWN, ANN", "ADAMS, BEN"], OrganizationClinicians.Sort(entries, "specialty").Select(e => e.SortName));
        Assert.Equal(["ADAMS, BEN", "BROWN, ANN", "CLARK, CAL"], OrganizationClinicians.Sort(entries, "LOCATION").Select(e => e.SortName));
        Assert.Equal("ADAMS, BEN", OrganizationClinicians.Sort(entries, "source")[0].SortName);
        Assert.Equal("name", OrganizationClinicians.NormalizeSort("drop table"));
    }

    [Fact]
    public void Clinician_sources_read_in_words_and_codes()
    {
        var both = ClinicianSource.FacilityAffiliation | ClinicianSource.Reassignment;
        Assert.Equal(["facilityAffiliation", "reassignment"], ClinicianSources.Codes(both));
        Assert.Equal("Works at its facility (Care Compare); bills Medicare through its group", ClinicianSources.Text(both));
        Assert.Equal("In its group (Care Compare)", ClinicianSources.TextOf("careCompareGroup"));
        Assert.Null(ClinicianSources.TextOf("unknown"));
    }

    private static FacilityOwner Owner(string? id, string name, string roleCode, string role, string group, double? percentage = null,
        DateOnly? since = null, string? title = null) =>
        new(id, id is not null, name, null, title, roleCode, role, group, percentage, since, [], null, null);

    private static FacilityOwnership Ownership(string ccn, params FacilityOwner[] owners) =>
        new(ccn, "O" + ccn, "LEGAL " + ccn, null, new DateOnly(2026, 9, 1), owners, null, []);

    [Fact]
    public void An_owner_of_several_facilities_is_listed_once_with_each_facility()
    {
        var merged = OrganizationOwnershipMerge.Merge(
        [
            ("330014", "JAMAICA HOSPITAL", Ownership("330014",
                Owner("1234567890", "MEDISYS HEALTH NETWORK", "34", "5% or more direct ownership", "owner", 100, new DateOnly(2001, 1, 1)),
                Owner(null, "Jane Roe", "40", "Officer", "officer", title: "Director"))),
            ("33S014", "JAMAICA HOSPITAL PSYCHIATRIC UNIT", Ownership("33S014",
                Owner("1234567890", "MEDISYS HEALTH NETWORK", "34", "5% or more direct ownership", "owner", 100, new DateOnly(1999, 6, 1)),
                Owner(null, "JANE ROE ", "41", "Director", "officer"))),
            ("335001", "TRUMP PAVILION", Ownership("335001")),
        ])!;

        Assert.Equal(2, merged.Owners.Count);
        var network = merged.Owners[0];
        Assert.Equal(("MEDISYS HEALTH NETWORK", 100.0, new DateOnly(1999, 6, 1)), (network.Name, network.Percentage!.Value, network.Since!.Value));
        Assert.Equal(["330014", "33S014"], network.Facilities.Select(f => f.Ccn));

        // A person: once by name across facilities, with every role and the title, and no ID.
        var person = merged.Owners[1];
        Assert.Null(person.OwnerId);
        Assert.Equal(["Officer", "Director"], person.Roles);
        Assert.Equal("Director", person.Title);
        Assert.Equal(["330014", "33S014"], person.Facilities.Select(f => f.Ccn));
        Assert.Equal(3, merged.Enrollments.Count);
    }

    [Fact]
    public void No_owners_and_no_changes_is_no_ownership_section() =>
        Assert.Null(OrganizationOwnershipMerge.Merge([("335001", "TRUMP PAVILION", Ownership("335001"))]));

    [Fact]
    public void A_facility_links_to_the_organization_covering_most_of_its_npis()
    {
        var pages = new Dictionary<string, ProviderOrganization>
        {
            ["1000000004"] = new("1000000004", "BIG HOSPITAL", 12),
            ["1000000012"] = new("1000000004", "BIG HOSPITAL", 12),
            ["1000000020"] = new("1000000020", "SMALL CLINIC", 1),
        };
        var picked = OrganizationGroups.PickPages(
        [
            ("330014", "1000000004"), ("330014", "1000000012"), ("330014", "1000000020"),
            ("335001", "1000000020"),
            ("339999", "1000000038"), // an NPI without a page: no link
        ], pages);

        Assert.Equal("1000000004", picked["330014"].MainNpi);
        Assert.Equal("1000000020", picked["335001"].MainNpi);
        Assert.False(picked.ContainsKey("339999"));
    }
}
