using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>Facility owners and nursing home inspections (docs/PRODUCT_PLAN.md step 3.6 part 2): roles, owner kinds, severity codes, filters.</summary>
public class OwnershipTests
{
    [Theory]
    [InlineData("34", OwnerRoles.Owner, "Direct owner (5% or more)")]
    [InlineData("35", OwnerRoles.Owner, "Indirect owner (5% or more)")]
    [InlineData("86", OwnerRoles.Owner, "Indirect owner")]
    [InlineData("43", OwnerRoles.Control, "Operational or managerial control")]
    [InlineData("72", OwnerRoles.Control, "Additional disclosable party")]
    [InlineData("36", OwnerRoles.Lender, "Mortgage holder (5% or more)")]
    [InlineData("41", OwnerRoles.Officer, "Corporate director")]
    [InlineData("42", OwnerRoles.Officer, "Managing employee")]
    public void Role_codes_are_grouped_and_named(string code, string group, string label)
    {
        Assert.Equal(group, OwnerRoles.Group(code));
        Assert.Equal(label, OwnerRoles.Label(code, "WHATEVER CMS WROTE"));
    }

    [Fact]
    public void Unknown_roles_are_officers_named_as_cms_wrote_them()
    {
        Assert.Equal(OwnerRoles.Officer, OwnerRoles.Group("99"));
        Assert.Equal("Some new role", OwnerRoles.Label("99", "SOME NEW ROLE"));
        Assert.Equal("Other", OwnerRoles.Label(null, " "));
        Assert.Equal([0, 1, 2, 3], OwnerRoles.Groups.Select(OwnerRoles.Order));
        // Every ownership and control code is in its group (the loader counts and keeps them by these lists).
        Assert.All(OwnerRoles.OwnershipCodes, c => Assert.Equal(OwnerRoles.Owner, OwnerRoles.Group(c)));
        Assert.All(OwnerRoles.ControlCodes, c => Assert.Equal(OwnerRoles.Control, OwnerRoles.Group(c)));
    }

    [Fact]
    public void Owner_kinds_are_read_in_a_fixed_order_and_unknown_codes_dropped()
    {
        Assert.Equal(["private_equity", "for_profit", "llc"], OwnerKinds.Parse("llc,for_profit,private_equity,something_new"));
        Assert.Empty(OwnerKinds.Parse(null));
        Assert.Empty(OwnerKinds.Parse(""));
        Assert.Equal("Real estate investment trust (REIT)", OwnerKinds.Label("reit"));
        Assert.Null(OwnerKinds.Label("pirate"));
        Assert.Equal(OwnerKinds.All.Count, OwnerKinds.All.Select(k => k.Code).Distinct().Count());
    }

    [Theory]
    [InlineData("A", 1, 0)]
    [InlineData("C", 1, 2)]
    [InlineData("D", 2, 0)]
    [InlineData("G", 3, 0)]
    [InlineData("H", 3, 1)]
    [InlineData("J", 4, 0)]
    [InlineData("L", 4, 2)]
    [InlineData("M", 0, -1)]
    [InlineData("", 0, -1)]
    public void Scope_and_severity_codes_map_to_cms_grid(string code, int level, int scope)
    {
        Assert.Equal(level, InspectionSeverity.Level(code));
        Assert.Equal(scope, InspectionSeverity.Scope(code));
        Assert.Equal(level >= 3, InspectionSeverity.IsHarm(code));
    }

    [Fact]
    public void Severity_reads_in_words()
    {
        Assert.Equal("Actual harm, isolated (G)", InspectionSeverity.Describe("G"));
        Assert.Equal("Immediate jeopardy to health or safety, widespread (L)", InspectionSeverity.Describe("L"));
        Assert.Equal("Z", InspectionSeverity.Describe("Z"));
        Assert.Equal("Fire safety", InspectionSeverity.KindName(InspectionSeverity.FireSafety));
    }

    [Theory]
    [InlineData("0244144871", "0244144871")]
    [InlineData("446168215", "0446168215")]
    [InlineData(" 42127045 ", "0042127045")]
    [InlineData("12345678901", null)]
    [InlineData("12AB", null)]
    [InlineData("", null)]
    public void Associate_ids_are_ten_digits(string id, string? expected) => Assert.Equal(expected, OwnershipService.NormalizeOwnerId(id));

    [Fact]
    public void Owner_filters_are_checked_by_parameter()
    {
        Assert.Empty(OwnershipService.Validate("lifepoint", "REIT", 1, 50));
        var errors = OwnershipService.Validate(new string('x', OwnershipService.MaxQueryLength + 1), "pirate", 0, 500);
        Assert.Equal(["kind", "page", "pageSize", "q"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Versions_and_change_types_read_in_words()
    {
        Assert.Equal(new DateOnly(2026, 9, 30), OwnershipService.UpdatedOn("2026-09-30 NH_HealthCitations_Sep2026_2.csv | 2026-09-30 x.csv"));
        Assert.Null(OwnershipService.UpdatedOn("week 2026-41"));
        Assert.Null(OwnershipService.UpdatedOn(null));
        Assert.Equal("Change of ownership", OwnershipService.ChangeType("CH", "CHANGE OF OWNERSHIP"));
        Assert.Equal("Acquisition or merger", OwnershipService.ChangeType("AM", null));
        Assert.Equal("Consolidation", OwnershipService.ChangeType("CO", "CONSOLIDATION"));
    }

    [Fact]
    public void Penalties_add_up_fines_and_count_payment_denials()
    {
        var inspections = new NursingHomeInspections("335001", null, [], [], [],
            [new NursingHomePenalty(new DateOnly(2025, 4, 1), "Fine", 1000.5, null, null), new NursingHomePenalty(new DateOnly(2025, 5, 1), "Fine", 200, null, null),
             new NursingHomePenalty(new DateOnly(2025, 4, 1), "Payment Denial", null, new DateOnly(2025, 4, 15), 30)]);
        Assert.Equal((1200.5, 1), (inspections.FinesTotal, inspections.PaymentDenials));
    }
}
