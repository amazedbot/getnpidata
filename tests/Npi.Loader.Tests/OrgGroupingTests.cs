using Npi.Core.Search;
using Npi.Loader.Projection;

namespace Npi.Loader.Tests;

/// <summary>Grouping organization NPIs into organizations (docs/PRODUCT_PLAN.md step 3.8), in memory.</summary>
public class OrgGroupingTests
{
    private static readonly DateTime Old = new(2012, 1, 1);
    private static readonly DateTime New = new(2025, 1, 1);

    private static OrgGroupInput Org(string npi, string name, string zip, string address, string taxonomy = "282N00000X", DateTime? updated = null) =>
        OrgGroupInput.Create(npi, name, zip, address, taxonomy, updated ?? Old);

    private static Dictionary<string, OrgGroupMember> Group(params OrgGroupInput[] orgs) =>
        OrgGrouping.Build(orgs).Members.ToDictionary(m => m.Npi, StringComparer.Ordinal);

    [Theory]
    [InlineData("8900 Van Wyck Expwy", "8900", "VAN WYCK EXPY")]
    [InlineData("89-00 VAN WYCK EXPRESS WAY", "8900", "VAN WYCK EXPY")]
    [InlineData("101 Nicolls Rd, Stes 102, 201", "101", "NICOLLS RD")]
    [InlineData("NICOLLS ROAD", "", "NICOLLS RD")]
    public void Addresses_are_normalized(string address, string number, string street)
    {
        var (n, s) = OrgGrouping.Address(address);
        Assert.Equal(number, n);
        Assert.Equal(street, s);
        Assert.Equal((null, null), OrgGrouping.Address("  "));
    }

    [Fact]
    public void The_same_name_at_the_same_address_is_one_organization()
    {
        var groups = Group(
            Org("1000000001", "Jamaica Hospital Medical Center", "11418", "8900 Van Wyck Expwy"),
            Org("1000000002", "JAMAICA HOSPITAL MEDICAL CENTER", "11418", "89-00 Van Wyck Expressway Ste 2", "3336C0003X"),
            Org("1000000003", "Jamaica Hospital Medical Center", "11418", "134-20 Jamaica Ave"),   // same name, same city, another address
            Org("1000000004", "Jamaica Hospital Medical Centre", "11418", "8900 Van Wyck Expwy"));  // a similar name only
        Assert.Equal(["1000000001", "1000000002"], groups.Keys.Order(StringComparer.Ordinal));
        Assert.All(groups.Values, m => Assert.Equal(OrganizationTie.NameAddress, m.JoinedBy));
        Assert.All(groups.Values, m => Assert.Equal(2, m.Members));
    }

    [Fact]
    public void An_address_without_a_number_joins_its_streets_only_number()
    {
        var groups = Group(
            Org("1000000001", "Stony Brook University Hospital", "11794", "101 Nicolls Rd"),
            Org("1000000002", "Stony Brook University Hospital", "11794", "Nicolls Road"));
        Assert.Equal(2, groups.Count);

        // Two numbers on the street: the address without one stays alone.
        Assert.Empty(Group(
            Org("1000000001", "Stony Brook University Hospital", "11794", "101 Nicolls Rd"),
            Org("1000000002", "Stony Brook University Hospital", "11794", "205 Nicolls Rd"),
            Org("1000000003", "Stony Brook University Hospital", "11794", "Nicolls Road")));
    }

    [Fact]
    public void A_shared_ccn_joins_in_one_zip_code_and_a_unit_counts_as_its_hospital()
    {
        var hospital = Org("1000000001", "Good Samaritan Hospital", "11795", "1000 Montauk Hwy") with { Ccns = [("330286", FacilityTypes.Hospital)] };
        var unit = Org("1000000002", "Good Samaritan Psychiatric Unit", "11795", "1000 Montauk Highway Fl 3") with { Ccns = [("33S286", FacilityTypes.Hospital)] };
        var away = Org("1000000003", "Good Samaritan Clinic", "11701", "200 Broadway") with { Ccns = [("330286", FacilityTypes.Hospital)] };
        var groups = Group(hospital, unit, away);

        Assert.Equal(["1000000001", "1000000002"], groups.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(OrganizationTie.Ccn, groups["1000000002"].JoinedBy);
        Assert.Equal("33S286", groups["1000000002"].Ccn);
        Assert.Equal("1000000001", groups["1000000002"].MainNpi); // the hospital's own CCN wins
    }

    [Fact]
    public void A_pac_id_joins_in_one_zip_code_but_not_a_chains_stores()
    {
        var clinic = Org("1000000001", "North Shore Medical Group", "11030", "1 Main St", "261QM1300X") with { PacIds = ["1234567890"] };
        var lab = Org("1000000002", "North Shore Lab Services", "11030", "9 Elm St", "291U00000X") with { PacIds = ["1234567890"] };
        var store1 = Org("1000000003", "Corner Pharmacy 1", "11030", "10 Main St", "3336C0003X") with { PacIds = ["5555555555"] };
        var store2 = Org("1000000004", "Corner Pharmacy 2", "11030", "99 Elm St", "3336C0003X") with { PacIds = ["5555555555"] };
        var faraway = Org("1000000005", "North Shore Imaging", "10001", "5 Fifth Ave", "261QR0200X") with { PacIds = ["1234567890"] };
        var groups = Group(clinic, lab, store1, store2, faraway);

        Assert.Equal(["1000000001", "1000000002"], groups.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(OrganizationTie.MedicareEnrollment, groups["1000000001"].JoinedBy);
    }

    [Fact]
    public void A_parent_joins_its_subparts_in_one_zip_code_and_a_chains_tax_id_doesnt_cross_zip_codes()
    {
        var parent = Org("1000000001", "Mercy Health System", "19104", "1 Mercy Plaza", "261QM1300X");
        var sub = Org("1000000002", "Mercy Imaging Center", "19104", "5 Mercy Plaza", "261QR0200X") with { ParentKey = OrgGrouping.NameKey("MERCY HEALTH SYSTEM") };
        var tin1 = Org("1000000003", "Drugstore 101", "19104", "10 Market St", "3336C0003X") with { ParentTin = "123456789" };
        var tin2 = Org("1000000004", "Drugstore 102", "10001", "10 Broadway", "3336C0003X") with { ParentTin = "123456789" };
        var groups = Group(parent, sub, tin1, tin2);

        Assert.Equal(["1000000001", "1000000002"], groups.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(OrganizationTie.Parent, groups["1000000002"].JoinedBy);
    }

    [Fact]
    public void The_main_npi_is_the_hospital_then_any_ccn_then_medicare_then_practice_then_the_newest()
    {
        OrgGroupInput Member(string npi) => Org(npi, "Saint Example Hospital", "11418", "1 Hospital Way");

        // A hospital's own CCN beats a unit's and Medicare billing.
        var groups = Group(
            Member("1000000001") with { MedicareBilling = true },
            Member("1000000002") with { Ccns = [("33S014", FacilityTypes.Hospital)] },
            Member("1000000003") with { Ccns = [("330014", FacilityTypes.Hospital)] });
        Assert.All(groups.Values, m => Assert.Equal("1000000003", m.MainNpi));
        Assert.True(groups["1000000003"].IsMain);

        // Any CCN beats Medicare enrollment, which beats another sign of practice, which beats the newest update.
        Assert.Equal("1000000002", Group(Member("1000000001") with { MedicareEnrolled = true },
            Member("1000000002") with { Ccns = [("335001", FacilityTypes.NursingHome)] })["1000000001"].MainNpi);
        Assert.Equal("1000000001", Group(Member("1000000001") with { PacIds = ["1"] }, Member("1000000002") with { Active = true })["1000000002"].MainNpi);
        Assert.Equal("1000000002", Group(Member("1000000001") with { LastUpdate = New }, Member("1000000002") with { Active = true })["1000000001"].MainNpi);
        Assert.Equal("1000000002", Group(Member("1000000001"), Member("1000000002") with { LastUpdate = New })["1000000001"].MainNpi);
        Assert.Equal("1000000001", Group(Member("1000000002"), Member("1000000001"))["1000000002"].MainNpi); // lowest NPI last
    }

    [Fact]
    public void Without_a_hospital_ccn_a_hospital_npi_with_a_sign_of_practice_beats_another_facility()
    {
        // A hospital whose CCN CMS lists under an NPI outside the group: its dialysis center holds the group's only CCN.
        OrgGroupInput Member(string npi, string taxonomy) => Org(npi, "Brookhaven Memorial Hospital", "11772", "101 Hospital Rd", taxonomy);
        var dialysis = Member("1000000001", "261QE0700X") with { Ccns = [("332332", FacilityTypes.Dialysis)], Active = true };

        // The hospital NPI enrolled in Medicare is the main NPI, before the dialysis center and the hospital NPI with only a listing.
        var groups = Group(dialysis, Member("1000000002", "282N00000X") with { Active = true }, Member("1000000003", "282N00000X") with { MedicareEnrolled = true });
        Assert.All(groups.Values, m => Assert.Equal("1000000003", m.MainNpi));

        // A hospital NPI with no sign of practice doesn't: it would be made stale by "another NPI of this organization is current".
        Assert.Equal("1000000001", Group(dialysis, Member("1000000002", "282N00000X"))["1000000002"].MainNpi);

        // A hospital's own CCN still comes first; a unit's taxonomy (27) isn't a hospital's.
        Assert.Equal("1000000002", Group(Member("1000000001", "282N00000X") with { MedicareEnrolled = true },
            Member("1000000002", "261QE0700X") with { Ccns = [("330141", FacilityTypes.Hospital)] })["1000000001"].MainNpi);
        Assert.Equal("1000000001", Group(dialysis, Member("1000000002", "273R00000X") with { MedicareEnrolled = true })["1000000002"].MainNpi);
    }

    [Fact]
    public void The_stats_count_each_stage_and_size()
    {
        var (members, stats) = OrgGrouping.Build([
            Org("1000000001", "A Hospital", "11418", "1 Main St"),
            Org("1000000002", "A Hospital", "11418", "1 Main St"),
            Org("1000000003", "B Clinic", "11418", "2 Main St", "261QM1300X") with { Ccns = [("330014", FacilityTypes.Hospital)] },
            Org("1000000004", "B Clinic East", "11418", "3 Main St", "261QM1300X") with { Ccns = [("33T014", FacilityTypes.Hospital)] },
            Org("1000000005", "Alone", "11418", "4 Main St"),
        ]);
        Assert.Equal(4, members.Count);
        Assert.Equal(new OrgGroupStage("same name and address", 1, 2), stats.Stages[0]);
        Assert.Equal(new OrgGroupStage("+ shared CCN", 2, 4), stats.Stages[1]);
        Assert.Equal(2, stats.Sizes["2"]);
    }

    [Fact]
    public void Changed_npis_are_every_member_of_a_group_that_changed()
    {
        (string, string)[] before = [("1", "1"), ("2", "1"), ("3", "3"), ("4", "3"), ("5", "5"), ("6", "5")];
        (string, string)[] after = [("1", "1"), ("2", "1"), ("3", "3"), ("4", "3"), ("7", "3"), ("5", "6"), ("6", "6")];
        // 1–2 unchanged; 3–4 gained 7; 5–6 changed their main NPI.
        Assert.Equal(["3", "4", "5", "6", "7"], OrgGroupBuilder.ChangedNpis(before, after));
        Assert.Equal(["1", "2"], OrgGroupBuilder.ChangedNpis([("1", "1"), ("2", "1")], []));
        Assert.Empty(OrgGroupBuilder.ChangedNpis(before, before));
    }
}
