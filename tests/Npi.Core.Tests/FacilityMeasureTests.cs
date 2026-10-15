using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>The facility measures of docs/PRODUCT_PLAN.md step 3.6 part 2: CMS's wording, periods, units and the catalogs.</summary>
public class FacilityMeasureTests
{
    [Theory]
    [InlineData("Better Than the National Rate", MeasureComparisons.Better)]
    [InlineData("Better than the National Benchmark", MeasureComparisons.Better)]
    [InlineData("Better than expected", MeasureComparisons.Better)]
    [InlineData("Better than Expected", MeasureComparisons.Better)]
    [InlineData("Better Than the National Value", MeasureComparisons.Better)]
    [InlineData("No Different Than the National Rate", MeasureComparisons.Same)]
    [InlineData("No Different than National Benchmark", MeasureComparisons.Same)]
    [InlineData("No Different than expected", MeasureComparisons.Same)]
    [InlineData("As Expected", MeasureComparisons.Same)]
    [InlineData("Worse Than the National Rate", MeasureComparisons.Worse)]
    [InlineData("Worse than the National Benchmark", MeasureComparisons.Worse)]
    [InlineData("Worse than Expected", MeasureComparisons.Worse)]
    [InlineData("Number of Cases Too Small", MeasureComparisons.TooFew)]
    [InlineData("Number of cases too small", MeasureComparisons.TooFew)]
    [InlineData("Not Available", null)]
    [InlineData("N/A", null)]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void Cms_comparisons_read_in_four_words(string? text, string? expected) => Assert.Equal(expected, MeasureComparisons.Normalize(text));

    [Fact]
    public void Periods_and_years_read_as_dates()
    {
        Assert.Equal((new DateOnly(2021, 1, 1), new DateOnly(2024, 12, 31)), FacilityMeasures.ParsePeriod("01Jan2021-31Dec2024"));
        Assert.Equal((new DateOnly(2024, 10, 18), new DateOnly(2025, 7, 11)), FacilityMeasures.ParsePeriod(" 18Oct2024-11Jul2025 "));
        Assert.Equal((new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31)), FacilityMeasures.ParsePeriod("2024"));
        Assert.Null(FacilityMeasures.ParsePeriod("Not Available"));
        Assert.Null(FacilityMeasures.ParsePeriod(""));
        Assert.Null(FacilityMeasures.ParsePeriod("01Jan2021"));
    }

    [Fact]
    public void Values_are_written_in_their_unit()
    {
        Assert.Equal("13.5%", FacilityMeasures.Format(13.5, MeasureUnits.Percent));
        Assert.Equal("2.6 per 100", FacilityMeasures.Format(2.6, MeasureUnits.PerHundred));
        Assert.Equal("11.9 per 1,000", FacilityMeasures.Format(11.9, MeasureUnits.PerThousand));
        Assert.Equal("0.99", FacilityMeasures.Format(0.99, MeasureUnits.Ratio));
        Assert.Equal("1,212 minutes", FacilityMeasures.Format(1212, MeasureUnits.Minutes));
    }

    [Fact]
    public void Catalogs_have_unique_ids_in_known_groups_and_units()
    {
        string[] units = [MeasureUnits.Percent, MeasureUnits.PerHundred, MeasureUnits.PerThousand, MeasureUnits.Ratio, MeasureUnits.Minutes];
        foreach (var catalog in new[] { FacilityMeasures.Hospital, [.. FacilityMeasures.PostAcute.Select(m => m.Info)],
                     [.. FacilityMeasures.SurgeryCenter, .. FacilityMeasures.SurgeryCenterSurvey], FacilityMeasures.Dialysis })
        {
            Assert.Equal(catalog.Count, catalog.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(catalog, m => Assert.Contains(m.Group, FacilityMeasures.Groups));
            Assert.All(catalog, m => Assert.Contains(m.Unit, units));
        }

        // The loader puts the IDs into SQL as parameters; they stay plain codes ("MSPB-1" is stored as MSPB_1, as the national file writes it).
        Assert.All(FacilityMeasures.Hospital, m => Assert.Matches("^[A-Za-z0-9_]+$", m.Id));
        Assert.Equal("MSPB_1", FacilityMeasures.FindHospital("MSPB-1")?.Id);
        Assert.Null(FacilityMeasures.FindHospital("EDV"));
    }

    [Fact]
    public void Surgery_center_measures_use_cms_units_and_periods()
    {
        // CMS: ASC-12 per 1,000 colonoscopies; ASC-17 and ASC-18 risk-standardized rates in percent; ASC-19 a ratio.
        Assert.Equal([MeasureUnits.PerThousand, MeasureUnits.Percent, MeasureUnits.Percent, MeasureUnits.Ratio],
            FacilityMeasures.SurgeryCenter.Where(m => m.Id is "ASC_12" or "ASC_17" or "ASC_18" or "ASC_19").Select(m => m.Unit));
        Assert.Equal([3, 2, 2, 2, 1, 1], new[] { "ASC_12", "ASC_17", "ASC_18", "ASC_19", "ASC_9", "ASC_13" }.Select(FacilityMeasures.AscYears));

        // OAS CAHPS: the top box of each of CMS's four questions, in CMS's order.
        Assert.Equal(["OAS_PROFESSIONAL", "OAS_COMMUNICATION", "OAS_RATING", "OAS_RECOMMEND"], FacilityMeasures.SurgeryCenterSurvey.Select(m => m.Id));
        Assert.All(FacilityMeasures.SurgeryCenterSurvey, m => Assert.True(m.HigherIsBetter && m.Group == FacilityMeasures.PatientExperience));
    }

    [Fact]
    public void Post_acute_measures_read_their_own_rows()
    {
        // Every code of a measure starts with its base code, and the IRF ("I_") and LTCH ("L_") files keep to their own.
        foreach (var m in FacilityMeasures.PostAcute)
        {
            Assert.All(new[] { m.ScoreCode, m.CompareCode, m.CasesCode, m.NationalCode }.OfType<string>(), code => Assert.StartsWith(m.Info.Id + "_", code));
        }

        var readmissions = FacilityMeasures.FindPostAcute("I_017_01")!;
        Assert.Equal(("I_017_01_PPR_PD_RSRR", "I_017_01_PPR_PD_COMP_PERF", "I_017_01_PPR_PD_VOLUME", "I_017_01_PPR_PD_NATL_OBS_RATE"),
            (readmissions.ScoreCode, readmissions.CompareCode, readmissions.CasesCode, readmissions.NationalCode));
        Assert.Equal(("L_019_01_MSPB_SCORE", (string?)null, "L_019_01_NATL_MSPB_SCORE"),
            (FacilityMeasures.FindPostAcute("L_019_01")!.ScoreCode, FacilityMeasures.FindPostAcute("L_019_01")!.CompareCode, FacilityMeasures.FindPostAcute("L_019_01")!.NationalCode));
        Assert.True(FacilityMeasures.FindPostAcute("I_019_02")!.Info.HigherIsBetter); // discharged to the community
    }

    [Fact]
    public void Measures_are_listed_by_group_then_in_catalog_order()
    {
        FacilityMeasure M(string id, string group) => new(id, group, id, MeasureUnits.Percent, false, 1, null, null, null, null, null);
        var ordered = FacilityMeasures.Ordered([M("MSPB_1", FacilityMeasures.Spending), M("HAI_2_SIR", FacilityMeasures.Infections),
            M("MORT_30_HF", FacilityMeasures.Deaths), M("MORT_30_AMI", FacilityMeasures.Deaths)], [.. FacilityMeasures.Hospital.Select(m => m.Id)]);
        Assert.Equal(["MORT_30_AMI", "MORT_30_HF", "HAI_2_SIR", "MSPB_1"], ordered.Select(m => m.Id));
    }
}
