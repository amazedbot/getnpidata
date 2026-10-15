using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Npi.Core.Search;
using Npi.Web.Pages;

namespace Npi.Web.Tests;

/// <summary>The facility page's sections from docs/PRODUCT_PLAN.md step 3.6 part 2: ratings, "How it compares" and Medicare volume.</summary>
public class FacilityPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ClinicianList NoClinicians = new(0, [], [], 1, OrganizationService.PageSize);

    private static FacilityModel Page(CertifiedFacility facility, Func<FacilityProfile, FacilityProfile>? with = null)
    {
        var profile = new FacilityProfile(facility, "1 MAIN ST", "11201", null, null, [], NoClinicians);
        return new FacilityModel(null!, null!, Options.Create(new MapOptions())) { Profile = with is null ? profile : with(profile) };
    }

    private static CertifiedFacility Facility(string ccn, string kind, int? overall = null, int? survey = null) =>
        new(ccn, kind, "BROOKLYN KIDNEY CENTER", null, "Profit", "BROOKLYN", "NY", "7185550100", null, null, overall, null, null, null, 0)
        {
            PatientSurveyRating = survey,
        };

    [Fact]
    public void A_dialysis_facility_shows_its_stars_and_outcomes_against_what_is_expected()
    {
        var model = Page(Facility("332301", FacilityTypes.Dialysis, 4, 3), p => p with
        {
            Dialysis = new DialysisFacts(28, "Profit", "DaVita", true, false, true, 4, new DateOnly(2021, 1, 1), new DateOnly(2024, 12, 31), 3),
            Measures =
            [
                new("survival", FacilityMeasures.Outcomes, "Patient survival", MeasureUnits.Ratio, true, null, null, MeasureComparisons.Better, null,
                    new DateOnly(2021, 1, 1), new DateOnly(2024, 12, 31)),
                new("hospitalization", FacilityMeasures.Readmissions, "Hospital admissions", MeasureUnits.Ratio, false, null, null, MeasureComparisons.Worse, null, null, null),
            ],
        });

        Assert.Equal("Dialysis facility", model.KindName);
        Assert.Equal("Dialysis facility in Brooklyn, NY, 28 dialysis stations.", model.Summary);
        Assert.Equal([("Five-star rating", (double?)4, "CMS dialysis facility rating, 2021–2024"), ("Patient survey", 3, "ICH CAHPS: what patients said")],
            model.Ratings.Select(r => (r.Label, r.Stars, r.Note)));

        var groups = model.MeasureGroups.ToList();
        Assert.Equal([FacilityMeasures.Readmissions, FacilityMeasures.Outcomes], groups.Select(g => g.Key)); // in the catalog's group order, whatever order the measures came in
        var survival = groups[1].Single();
        Assert.Equal(("—", "Better than expected", "pill ok", "Jan 2021 – Dec 2024"), (survival.Value, survival.Comparison, survival.PillClass, survival.Period));
        Assert.Equal(("Worse than expected", "pill alert", ""), (groups[0].Single().Comparison, groups[0].Single().PillClass, groups[0].Single().Period));
        Assert.Contains("expected", model.MeasureSource, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hospital_compares_with_the_nation_and_shows_its_medicare_volume()
    {
        var model = Page(Facility("330045", FacilityTypes.Hospital, 3), p => p with
        {
            Measures =
            [
                new("MORT_30_AMI", FacilityMeasures.Deaths, "Heart attack patients who died within 30 days", MeasureUnits.Percent, false, 13.5, 11.9, null, 234,
                    new DateOnly(2022, 7, 1), new DateOnly(2025, 6, 30)),
                new("HAI_1_SIR", FacilityMeasures.Infections, "Central line bloodstream infections (CLABSI)", MeasureUnits.Ratio, false, 0.302, 1, MeasureComparisons.Same,
                    null, null, null),
                new("OP_36", FacilityMeasures.Readmissions, "Hospital visits within 7 days of outpatient surgery", MeasureUnits.Ratio, false, 0.9, null,
                    MeasureComparisons.Better, null, null, null),
            ],
            MedicareVolume = new HospitalVolume(2024, 4299, 3177, 23739, 52228590, 65257547, 2024, 1200, 450000.5,
                [new HospitalService("470", "MAJOR HIP AND KNEE JOINT REPLACEMENT", 300, null, 12000)], []),
        });

        Assert.Equal("Hospital", model.KindName);
        var rows = model.MeasureGroups.SelectMany(g => g).ToList();
        Assert.Equal(("13.5%", "11.9%", (string?)null, "Jul 2022 – Jun 2025"), (rows[0].Value, rows[0].National, rows[0].Comparison, rows[0].Period));
        Assert.Equal(("0.30", "1.00", "No different from the US"), (rows[1].Value, rows[1].National, rows[1].Comparison));
        Assert.Equal("Better than expected", rows[2].Comparison); // an observed-to-expected ratio
        Assert.Equal("data.cms.gov: Medicare Inpatient Hospitals – by Provider and – by Provider and Service (2024); Medicare Outpatient Hospitals – by Provider and Service (2024). Original Medicare (fee-for-service) only, not Medicare Advantage. Outpatient totals add up the service groups CMS lists (those with 11 or more patients).",
            model.VolumeSource);
    }

    [Fact]
    public void A_dialysis_facility_shows_its_quality_incentive_and_a_surgery_center_its_survey()
    {
        var dialysis = Page(Facility("332301", FacilityTypes.Dialysis), p => p with { QualityIncentive = new DialysisQualityIncentive(2026, 39, 55, 54, 1.5) });
        Assert.Equal("State average 55, US average 54", dialysis.QipAverages);
        Assert.Equal("", Page(Facility("332302", FacilityTypes.Dialysis)).QipAverages);

        var asc = Page(Facility("33C0001234", FacilityTypes.SurgeryCenter), p => p with
        {
            Measures = [new("OAS_RECOMMEND", FacilityMeasures.PatientExperience, "Would recommend", MeasureUnits.Percent, true, 92, 87, null, 217, null, null)],
        });
        var row = asc.MeasureGroups.Single().Single();
        Assert.Equal((FacilityMeasures.PatientExperience, "92%", "87%", (string?)null), (row.Measure.Group, row.Value, row.National, row.Comparison));
        Assert.Contains("OAS CAHPS", asc.MeasureSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("33T045", FacilityTypes.Rehab, "Rehabilitation unit of a hospital")]
    [InlineData("333025", FacilityTypes.Rehab, "Rehabilitation hospital")]
    [InlineData("332001", FacilityTypes.LongTermCare, "Long-term care hospital")]
    [InlineData("33C0001234", FacilityTypes.SurgeryCenter, "Ambulatory surgery center")]
    public void New_types_have_plain_names_and_no_star_tiles(string ccn, string kind, string name)
    {
        var model = Page(Facility(ccn, kind));
        Assert.Equal(name, model.KindName);
        Assert.Empty(model.Ratings);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
        }
    }

    [Theory]
    [InlineData("/api/v1/facilities/not-a-ccn")]
    [InlineData("/api/v1/facilities/12")]
    public async Task An_api_request_for_something_that_is_no_ccn_is_404(string url)
    {
        // Answered without the database: OrganizationService.IsCcn refuses it first.
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
