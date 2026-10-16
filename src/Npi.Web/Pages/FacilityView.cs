using System.Globalization;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>One rating tile: a label, stars (1–5, halves allowed) or a text value, and a note.</summary>
public sealed record RatingTile(string Label, double? Stars, string? Value, string Note, bool Bad = false);

/// <summary>A row of "How it compares": the measure in words, its value and the nation's, CMS's comparison (text and pill class) and the period.</summary>
public sealed record MeasureRow(FacilityMeasure Measure, string Value, string National, string? Comparison, string PillClass, string Period);

/// <summary>
/// What the facility page and an organization's page (docs/PRODUCT_PLAN.md step 3.9) say about one facility: its kind in words, its
/// rating tiles, "How it compares", the dialysis quality incentive and the Medicare volume, with their sources. The partials
/// _FacilityRatings, _FacilityCompares, _FacilityQip and _FacilityVolume render them from a <see cref="FacilitySection"/>.
/// </summary>
public sealed class FacilityView(FacilityProfile profile)
{
    public FacilityProfile Profile { get; } = profile;

    public CertifiedFacility Facility => Profile.Facility;

    public string Name => CityCatalog.TitleCase(Facility.Name);

    /// <summary>"Acute care hospital", "Nursing home", "Home health agency", "Hospice", "Rural health clinic", "Dialysis facility" ….</summary>
    public string KindName => Facility.Kind switch
    {
        "hospital" => Facility.Type is { } type ? (Profile.CertificationOnly ? type : Singular(type)) : "Hospital",
        "nursing_home" => "Nursing home",
        "home_health" => "Home health agency",
        "hospice" => "Hospice",
        FacilityTypes.Dialysis => "Dialysis facility",
        FacilityTypes.SurgeryCenter => "Ambulatory surgery center",
        // A rehabilitation hospital, or a hospital's rehabilitation unit (its CMS number says which).
        FacilityTypes.Rehab => Facility.Type ?? (FacilityTypes.FromCcn(Facility.Ccn) is (FacilityTypes.Rehab, { } unit) ? unit : "Inpatient rehabilitation facility"),
        FacilityTypes.LongTermCare => "Long-term care hospital",
        // Only in the Provider of Services file: the kind its CMS number stands for, else its type.
        _ => Facility.Type ?? FacilityTypes.Get(Facility.Kind).Singular,
    };

    public IEnumerable<RatingTile> Ratings
    {
        get
        {
            var f = Facility;
            switch (f.Kind)
            {
                case "hospital":
                    yield return new RatingTile("Overall", f.OverallRating, f.OverallRating is null ? "Not rated" : null, "CMS overall hospital rating");
                    yield return new RatingTile("Patient survey", f.PatientSurveyRating, f.PatientSurveyRating is null ? "Not rated" : null, "HCAHPS: what patients said");
                    if (f.Outcomes is { } o)
                    {
                        yield return Outcome("Deaths", o.Mortality);
                        yield return Outcome("Safety", o.Safety);
                        yield return Outcome("Readmissions", o.Readmissions);
                    }

                    break;
                case "nursing_home":
                    yield return new RatingTile("Overall", f.OverallRating, f.OverallRating is null ? "Not rated" : null, "CMS overall nursing home rating");
                    yield return new RatingTile("Health inspections", f.InspectionRating, f.InspectionRating is null ? "Not rated" : null, "State inspections");
                    yield return new RatingTile("Staffing", f.StaffingRating, f.StaffingRating is null ? "Not rated" : null, "Nurse staffing hours");
                    yield return new RatingTile("Quality measures", f.QualityRating, f.QualityRating is null ? "Not rated" : null, "Resident care measures");
                    break;
                case "home_health":
                    yield return new RatingTile("Quality of patient care", f.QualityOfCareRating, f.QualityOfCareRating is null ? "Not rated" : null, "CMS star rating");
                    break;
                case "hospice":
                    yield return new RatingTile("Family caregiver survey", f.PatientSurveyRating, f.PatientSurveyRating is null ? "Not rated" : null, "CAHPS Hospice: what families said");
                    break;
                case FacilityTypes.Dialysis:
                    var d = Profile.Dialysis;
                    yield return new RatingTile("Five-star rating", d?.FiveStar, d?.FiveStar is null ? "Not rated" : null,
                        "CMS dialysis facility rating" + (d?.FiveStarFrom is { } from && d.FiveStarTo is { } to ? ", " + Years(from, to) : ""));
                    yield return new RatingTile("Patient survey", d?.PatientSurveyRating, d?.PatientSurveyRating is null ? "Not rated" : null, "ICH CAHPS: what patients said");
                    break;
            }
        }
    }

    /// <summary>"How it compares", by group in the page's order.</summary>
    public IEnumerable<IGrouping<string, MeasureRow>> MeasureGroups => Profile.Measures.Select(Row).GroupBy(r => r.Measure.Group)
        .OrderBy(g => GroupOrder(g.Key));

    private static int GroupOrder(string group)
    {
        for (var i = 0; i < FacilityMeasures.Groups.Count; i++)
        {
            if (FacilityMeasures.Groups[i] == group)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>Where "How it compares" comes from, in the type's own words.</summary>
    public string MeasureSource => Facility.Kind switch
    {
        "hospital" => "Medicare Care Compare: Complications and Deaths, Healthcare-Associated Infections, Unplanned Hospital Visits, Timely and Effective Care and Medicare Spending Per Beneficiary (hospital and national files). Rates are risk-adjusted by CMS; infection ratios compare the infections seen with those predicted (below 1 is fewer). Each measure covers its own period.",
        FacilityTypes.Rehab => "Medicare Care Compare: Inpatient Rehabilitation Facility Provider Data and National Data. Readmission and discharge rates are risk-standardized; infection ratios compare the infections seen with those predicted.",
        FacilityTypes.LongTermCare => "Medicare Care Compare: Long-Term Care Hospital Provider Data and National Data. Readmission and discharge rates are risk-standardized; infection ratios compare the infections seen with those predicted.",
        FacilityTypes.SurgeryCenter => "Medicare Care Compare: Ambulatory Surgical Center Quality Measures (facility and national) and the OAS CAHPS patient survey for ambulatory surgical centers (facility and national). Hospital visit measures are risk-standardized and compared by CMS with the national rate: after colonoscopy a rate per 1,000 colonoscopies, after orthopedic and urology procedures a rate in percent of procedures, after general surgery a ratio (1 = as expected); each covers the years shown. The survey shares aren't compared by CMS.",
        FacilityTypes.Dialysis => "Medicare Care Compare: Dialysis Facility Listing. CMS compares each outcome with what the facility's patients would be expected to have, given their health.",
        _ => "Medicare Care Compare.",
    };

    /// <summary>The ESRD QIP tile's averages: "State average 54, US 54".</summary>
    public string QipAverages => Profile.QualityIncentive is not { } q
        ? ""
        : string.Join(", ", new[] { q.StateAverage is { } s ? $"State average {s}" : null, q.NationalAverage is { } n ? $"US average {n}" : null }.OfType<string>());

    /// <summary>The Medicare volume section's source line, with its data years.</summary>
    public string VolumeSource => "data.cms.gov: Medicare Inpatient Hospitals – by Provider and – by Provider and Service"
        + (Profile.MedicareVolume?.InpatientYear is { } i ? $" ({i})" : "")
        + "; Medicare Outpatient Hospitals – by Provider and Service"
        + (Profile.MedicareVolume?.OutpatientYear is { } o ? $" ({o})" : "")
        + ". Original Medicare (fee-for-service) only, not Medicare Advantage. Outpatient totals add up the service groups CMS lists (those with 11 or more patients).";

    /// <summary>True when the page has something to say about its quality: rating tiles, measures or a quality incentive.</summary>
    public bool HasQuality => !Profile.CertificationOnly && Ratings.Any() || Profile.Measures.Count > 0 || Profile.QualityIncentive is not null;

    private MeasureRow Row(FacilityMeasure m)
    {
        // Dialysis facilities, and the "observed to expected" ratios, are compared with what their patients predict, not with the nation.
        var expected = Facility.Kind == FacilityTypes.Dialysis || m.Id is "ASC_19" or "OP_36";
        var (comparison, pill) = m.Comparison switch
        {
            MeasureComparisons.Better => (expected ? "Better than expected" : "Better than the US", "pill ok"),
            MeasureComparisons.Same => (expected ? "As expected" : "No different from the US", "pill"),
            MeasureComparisons.Worse => (expected ? "Worse than expected" : "Worse than the US", "pill alert"),
            MeasureComparisons.TooFew => ("Too few cases to compare", "pill"),
            _ => ((string?)null, "pill"),
        };
        return new MeasureRow(m, m.Value is { } v ? FacilityMeasures.Format(v, m.Unit) : "—", m.National is { } n ? FacilityMeasures.Format(n, m.Unit) : "—",
            comparison, pill, m.From is { } from && m.To is { } to ? Period(from, to) : "");
    }

    /// <summary>"Jul 2022 – Jun 2025".</summary>
    public static string Period(DateOnly from, DateOnly to) =>
        from.ToString("MMM yyyy", CultureInfo.InvariantCulture) + " – " + to.ToString("MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"2021–2024", or "2024".</summary>
    public static string Years(DateOnly from, DateOnly to) =>
        from.Year == to.Year ? from.Year.ToString(CultureInfo.InvariantCulture) : $"{from.Year}–{to.Year}";

    /// <summary>"0 better, 0 worse of 7" → "Same as the US"; "1 worse" in red.</summary>
    private static RatingTile Outcome(string label, OutcomeCounts c)
    {
        if (c.Measures is not > 0)
        {
            return new RatingTile(label, null, "Not available", "Not enough cases to compare");
        }

        var better = c.Better ?? 0;
        var worse = c.Worse ?? 0;
        var value = (better, worse) switch
        {
            (0, 0) => "Same as the US",
            (_, 0) => $"{better} better than the US",
            (0, _) => $"{worse} worse than the US",
            _ => $"{better} better, {worse} worse",
        };
        return new RatingTile(label, null, value, $"of {c.Measures} measures", worse > 0);
    }

    // "Acute Care Hospitals" → "Acute care hospital".
    private static string Singular(string type)
    {
        var text = type.Trim();
        if (text.EndsWith("Hospitals", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
        }

        return text.Length == 0 ? "Hospital" : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }
}

/// <summary>
/// A facility part rendered by a partial: on the facility page each is a card with an h2 (<see cref="Nested"/> false); on an
/// organization's page they sit inside the facility's card, one heading level down, with ids made unique by the CCN.
/// </summary>
public sealed record FacilitySection(FacilityView View, bool Nested = false)
{
    /// <summary>The level of the part's own heading: h2 on the facility page, h3 inside an organization page's facility card.</summary>
    public int Level => Nested ? 3 : 2;

    /// <summary>An id for the part, unique on the page ("cmp-h", or "cmp-h-330014").</summary>
    public string Id(string name) => Nested ? $"{name}-{View.Facility.Ccn}" : name;
}
