using System.Globalization;

namespace Npi.Core.Search;

/// <summary>How a facility measure's value is written: a percent, a rate per 100 or 1,000, a ratio (1 = as predicted or the national median) or minutes.</summary>
public static class MeasureUnits
{
    public const string Percent = "percent";
    public const string PerHundred = "per100";
    public const string PerThousand = "per1000";
    public const string Ratio = "ratio";
    public const string Minutes = "minutes";
}

/// <summary>How CMS compares a facility with the nation (or with what its patients predict): CMS's own wording, in four words.</summary>
public static class MeasureComparisons
{
    public const string Better = "better";
    public const string Same = "same";
    public const string Worse = "worse";
    public const string TooFew = "too_few";

    /// <summary>
    /// "Better Than the National Rate", "Better than the National Benchmark", "Better than expected" → better; "No Different Than the National
    /// Rate", "As Expected" → same; "Worse …" → worse; "Number of Cases Too Small" → too_few; "Not Available", "N/A", blank → null.
    /// </summary>
    public static string? Normalize(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        if (t.Contains("too small", StringComparison.OrdinalIgnoreCase) || t.Contains("too few", StringComparison.OrdinalIgnoreCase))
        {
            return TooFew;
        }

        if (t.Contains("no different", StringComparison.OrdinalIgnoreCase) || t.Equals("As Expected", StringComparison.OrdinalIgnoreCase))
        {
            return Same;
        }

        if (t.StartsWith("better", StringComparison.OrdinalIgnoreCase))
        {
            return Better;
        }

        return t.StartsWith("worse", StringComparison.OrdinalIgnoreCase) ? Worse : null;
    }
}

/// <summary>
/// One of a facility type's main measures as the facility page shows it (docs/PRODUCT_PLAN.md step 3.6 part 2): CMS's measure ID (or ours,
/// for the dialysis and surgery center columns), the group it is listed under, plain words, its unit and whether a higher value is better.
/// </summary>
public sealed record FacilityMeasureInfo(string Id, string Group, string Name, string Unit, bool HigherIsBetter = false);

/// <summary>
/// An inpatient rehabilitation facility's or long-term care hospital's measure in CMS's long files ("Provider Data", "National Data"): the
/// row codes that carry its value, CMS's comparison, the number of patients or stays, and the national value.
/// </summary>
public sealed record PostAcuteMeasureInfo(FacilityMeasureInfo Info, string ScoreCode, string? CompareCode, string? CasesCode, string NationalCode);

/// <summary>
/// One measure of a facility, with the national value and CMS's comparison where CMS publishes them, and the period it covers.
/// <see cref="Comparison"/> is <see cref="MeasureComparisons"/>' better / same / worse / too_few, or null when CMS doesn't compare it.
/// </summary>
public sealed record FacilityMeasure(string Id, string Group, string Name, string Unit, bool HigherIsBetter, double? Value, double? National, string? Comparison,
    int? Cases, DateOnly? From, DateOnly? To);

/// <summary>A dialysis facility's Care Compare listing beyond its name and address: stations, services, chain and its star ratings.</summary>
public sealed record DialysisFacts(int? Stations, string? ProfitStatus, string? Chain, bool? InCenterHemodialysis, bool? PeritonealDialysis,
    bool? HomeHemodialysisTraining, double? FiveStar, DateOnly? FiveStarFrom, DateOnly? FiveStarTo, int? PatientSurveyRating);

/// <summary>
/// A dialysis facility's result in Medicare's ESRD Quality Incentive Program for one payment year (Provider Data Catalog "ESRD QIP - Total
/// Performance Scores - Payment Year …"): its total performance score (0–100; null when CMS gave it no score), the state and national
/// averages, and the cut to its Medicare payments for that year, in percent (0 = none; CMS cuts by up to 2%).
/// </summary>
public sealed record DialysisQualityIncentive(int PaymentYear, int? TotalPerformanceScore, int? StateAverage, int? NationalAverage, double? PaymentReduction);

/// <summary>One of a hospital's most frequent Medicare services: a DRG (inpatient stays) or an APC (outpatient services).</summary>
public sealed record HospitalService(string Code, string? Description, int Cases, int? Beneficiaries, double? AverageMedicarePayment);

/// <summary>
/// A hospital's Medicare fee-for-service volume (data.cms.gov "Medicare Inpatient Hospitals - by Provider" and "… by Provider and Service",
/// "Medicare Outpatient Hospitals - by Provider and Service"): inpatient discharges, patients, covered days and payments, outpatient services and
/// payments (summed over the service groups CMS lists, those with 11 or more patients), and the top DRGs and APCs.
/// </summary>
public sealed record HospitalVolume(int? InpatientYear, int? Discharges, int? Beneficiaries, int? CoveredDays, double? MedicarePayments, double? TotalPayments,
    int? OutpatientYear, int? OutpatientServices, double? OutpatientPayments, IReadOnlyList<HospitalService> TopInpatient, IReadOnlyList<HospitalService> TopOutpatient);

/// <summary>
/// The measures the facility pages show per type (docs/PRODUCT_PLAN.md step 3.6 part 2), kept compact: deaths, complications, infections,
/// readmissions, emergency department and spending measures for hospitals (Care Compare's Complications and Deaths, Healthcare-Associated
/// Infections, Unplanned Hospital Visits, Timely and Effective Care, Medicare Spending Per Beneficiary); infections, readmissions, discharge
/// to the community and spending for inpatient rehabilitation facilities and long-term care hospitals; hospital visits after surgery for
/// surgery centers; Care Compare's outcome categories for dialysis facilities. The loader keeps only these rows.
/// </summary>
public static class FacilityMeasures
{
    public const string Deaths = "Deaths";
    public const string Complications = "Complications & safety";
    public const string Infections = "Infections";
    public const string Readmissions = "Readmissions & hospital visits";
    public const string Emergency = "Emergency & timely care";
    public const string Outcomes = "Outcomes";
    public const string PatientExperience = "Patient experience";
    public const string Spending = "Spending";

    /// <summary>The row the national values are stored under (CMS's own national files call it so).</summary>
    public const string National = "NATION";

    /// <summary>The groups in the order the page lists them.</summary>
    public static readonly IReadOnlyList<string> Groups = [Deaths, Complications, Infections, Readmissions, Outcomes, PatientExperience, Emergency, Spending];

    /// <summary>
    /// Surgery centers' patient survey (OAS CAHPS, Provider Data Catalog "… for ambulatory surgical centers - Facility" and "- National"): the
    /// top-box share of each question in CMS's wording and order (facilities and staff, communication about your procedure, rating of the
    /// facility, recommending the facility); the completed surveys as the cases. CMS doesn't compare them with the nation, so the page shows
    /// the national value only.
    /// </summary>
    public static readonly IReadOnlyList<FacilityMeasureInfo> SurgeryCenterSurvey =
    [
        new("OAS_PROFESSIONAL", PatientExperience, "Patients who reported that staff definitely gave care in a professional way and the facility was clean",
            MeasureUnits.Percent, HigherIsBetter: true),
        new("OAS_COMMUNICATION", PatientExperience, "Patients who reported that staff definitely communicated about what to expect during and after the procedure",
            MeasureUnits.Percent, HigherIsBetter: true),
        new("OAS_RATING", PatientExperience, "Patients who gave the facility a rating of 9 or 10 on a scale from 0 (lowest) to 10 (highest)", MeasureUnits.Percent,
            HigherIsBetter: true),
        new("OAS_RECOMMEND", PatientExperience, "Patients who reported YES they would DEFINITELY recommend the facility to family or friends", MeasureUnits.Percent,
            HigherIsBetter: true),
    ];

    /// <summary>Hospitals, by CMS measure ID ("MSPB-1" is stored as MSPB_1, as the national file writes it).</summary>
    public static readonly IReadOnlyList<FacilityMeasureInfo> Hospital =
    [
        new("MORT_30_AMI", Deaths, "Heart attack patients who died within 30 days", MeasureUnits.Percent),
        new("MORT_30_HF", Deaths, "Heart failure patients who died within 30 days", MeasureUnits.Percent),
        new("MORT_30_PN", Deaths, "Pneumonia patients who died within 30 days", MeasureUnits.Percent),
        new("MORT_30_COPD", Deaths, "COPD patients who died within 30 days", MeasureUnits.Percent),
        new("MORT_30_STK", Deaths, "Stroke patients who died within 30 days", MeasureUnits.Percent),
        new("MORT_30_CABG", Deaths, "Bypass surgery (CABG) patients who died within 30 days", MeasureUnits.Percent),
        new("Hybrid_HWM", Deaths, "Deaths within 30 days, all patients (hospital-wide)", MeasureUnits.Percent),
        new("PSI_04", Deaths, "Surgical patients who died after a serious treatable complication", MeasureUnits.PerThousand),
        new("COMP_HIP_KNEE", Complications, "Complications after hip or knee replacement", MeasureUnits.Percent),
        new("PSI_90", Complications, "Patient safety and adverse events (PSI 90 composite)", MeasureUnits.Ratio),
        new("PSI_03", Complications, "Pressure ulcers acquired in the hospital", MeasureUnits.PerThousand),
        new("HAI_1_SIR", Infections, "Central line bloodstream infections (CLABSI)", MeasureUnits.Ratio),
        new("HAI_2_SIR", Infections, "Catheter urinary tract infections (CAUTI)", MeasureUnits.Ratio),
        new("HAI_3_SIR", Infections, "Surgical site infections after colon surgery", MeasureUnits.Ratio),
        new("HAI_4_SIR", Infections, "Surgical site infections after abdominal hysterectomy", MeasureUnits.Ratio),
        new("HAI_5_SIR", Infections, "MRSA bloodstream infections", MeasureUnits.Ratio),
        new("HAI_6_SIR", Infections, "C. difficile intestinal infections", MeasureUnits.Ratio),
        new("Hybrid_HWR", Readmissions, "Readmissions within 30 days, all patients (hospital-wide)", MeasureUnits.Percent),
        new("READM_30_AMI", Readmissions, "Heart attack patients readmitted within 30 days", MeasureUnits.Percent),
        new("READM_30_HF", Readmissions, "Heart failure patients readmitted within 30 days", MeasureUnits.Percent),
        new("READM_30_PN", Readmissions, "Pneumonia patients readmitted within 30 days", MeasureUnits.Percent),
        new("READM_30_COPD", Readmissions, "COPD patients readmitted within 30 days", MeasureUnits.Percent),
        new("READM_30_CABG", Readmissions, "Bypass surgery (CABG) patients readmitted within 30 days", MeasureUnits.Percent),
        new("READM_30_HIP_KNEE", Readmissions, "Hip or knee replacement patients readmitted within 30 days", MeasureUnits.Percent),
        new("OP_32", Readmissions, "Hospital visits within 7 days of an outpatient colonoscopy", MeasureUnits.PerThousand),
        new("OP_36", Readmissions, "Hospital visits within 7 days of outpatient surgery", MeasureUnits.Ratio),
        new("OP_18b", Emergency, "Median time in the emergency department (patients sent home)", MeasureUnits.Minutes),
        new("OP_22", Emergency, "Patients who left the emergency department before being seen", MeasureUnits.Percent),
        new("SEP_1", Emergency, "Sepsis and septic shock care given as recommended", MeasureUnits.Percent, HigherIsBetter: true),
        new("IMM_3", Emergency, "Staff vaccinated against the flu", MeasureUnits.Percent, HigherIsBetter: true),
        new("MSPB_1", Spending, "Medicare spending per patient, compared with the national median", MeasureUnits.Ratio),
    ];

    /// <summary>Inpatient rehabilitation facilities ("I_…") and long-term care hospitals ("L_…"), stored under the base code.</summary>
    public static readonly IReadOnlyList<PostAcuteMeasureInfo> PostAcute =
    [
        Sir("I_006_01", "Catheter urinary tract infections (CAUTI)"),
        Sir("I_015_01", "C. difficile intestinal infections"),
        Readmission("I_017_01", "PPR_PD", "Potentially preventable readmissions within 30 days after discharge"),
        Readmission("I_018_01", "PPR_WI", "Potentially preventable readmissions during the stay"),
        Community("I_019_02"),
        Spend("I_020_01"),
        Sir("L_006_01", "Catheter urinary tract infections (CAUTI)"),
        Sir("L_007_01", "Central line bloodstream infections (CLABSI)"),
        Sir("L_014_01", "C. difficile intestinal infections"),
        Readmission("L_017_01", "PPR_PD", "Potentially preventable readmissions within 30 days after discharge"),
        Community("L_018_02"),
        Spend("L_019_01"),
    ];

    /// <summary>
    /// Ambulatory surgery centers: the columns of Care Compare's ASC file the loader keeps, by our ID (CMS's measure, "-" as "_"), in CMS's
    /// units: ASC-12 is a risk-standardized hospital visit rate per 1,000 colonoscopies; ASC-17 and ASC-18 are risk-standardized hospital
    /// visit rates (RSHVR) in percent of procedures (CMS's measure technical report: "a median RSHVR of 2.5%"); ASC-19 is a
    /// risk-standardized ratio (1 = as expected). Each claims measure covers several years ending with the file's year (<see cref="AscYears"/>).
    /// </summary>
    public static readonly IReadOnlyList<FacilityMeasureInfo> SurgeryCenter =
    [
        new("ASC_12", Readmissions, "Facility 7-day risk-standardized hospital visit rate after outpatient colonoscopy", MeasureUnits.PerThousand),
        new("ASC_17", Readmissions, "Hospital visits after orthopedic ambulatory surgical center procedures (7-day risk-standardized rate)", MeasureUnits.Percent),
        new("ASC_18", Readmissions, "Hospital visits after urology ambulatory surgical center procedures (7-day risk-standardized rate)", MeasureUnits.Percent),
        new("ASC_19", Readmissions, "Facility-level 7-day hospital visits after general surgery procedures (risk-standardized ratio)", MeasureUnits.Ratio),
        new("ASC_9", Outcomes, "Normal colonoscopies with the recommended follow-up interval", MeasureUnits.Percent, HigherIsBetter: true),
        new("ASC_13", Outcomes, "Patients kept at a normal body temperature during anesthesia", MeasureUnits.Percent, HigherIsBetter: true),
    ];

    /// <summary>
    /// How many calendar years an ASC measure covers, ending with the file's year: ASC-12 three, ASC-17, 18 and 19 two (CMS's ASC public
    /// reporting guide: January 2022 / January 2023 through December 2024 for the 2024 file); the chart-abstracted ones one.
    /// </summary>
    public static int AscYears(string id) => id switch
    {
        "ASC_12" => 3,
        "ASC_17" or "ASC_18" or "ASC_19" => 2,
        _ => 1,
    };

    /// <summary>Dialysis facilities: Care Compare's outcome categories (as expected, better or worse than expected), by our ID.</summary>
    public static readonly IReadOnlyList<FacilityMeasureInfo> Dialysis =
    [
        new("survival", Outcomes, "Patient survival", MeasureUnits.Ratio, HigherIsBetter: true),
        new("hospitalization", Readmissions, "Hospital admissions", MeasureUnits.Ratio),
        new("readmission", Readmissions, "Readmissions after a hospital stay", MeasureUnits.Ratio),
        new("ed_visits", Readmissions, "Emergency department visits", MeasureUnits.Ratio),
        new("infection", Infections, "Bloodstream infections", MeasureUnits.Ratio),
        new("transfusion", Complications, "Blood transfusions", MeasureUnits.Ratio),
        new("waitlist", Outcomes, "Patients put on the kidney transplant waitlist in their first year", MeasureUnits.Ratio, HigherIsBetter: true),
    ];

    public static FacilityMeasureInfo? FindHospital(string? id) =>
        Hospital.FirstOrDefault(m => string.Equals(m.Id, id?.Trim().Replace('-', '_'), StringComparison.OrdinalIgnoreCase));

    public static PostAcuteMeasureInfo? FindPostAcute(string? id) =>
        PostAcute.FirstOrDefault(m => string.Equals(m.Info.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Care Compare's period text "01Jan2021-31Dec2024" (dialysis) or a measure year ("2024", surgery centers) as dates; null when it isn't one.
    /// </summary>
    public static (DateOnly From, DateOnly To)? ParsePeriod(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        if (t.Length == 4 && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is > 1900 and < 3000)
        {
            return (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        }

        var parts = t.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && DateOnly.TryParseExact(parts[0], "ddMMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
               && DateOnly.TryParseExact(parts[1], "ddMMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            ? (from, to)
            : null;
    }

    /// <summary>"13.5%", "2.6 per 100", "11.9 per 1,000", "0.85", "212 minutes".</summary>
    public static string Format(double value, string unit) => unit switch
    {
        MeasureUnits.Percent => value.ToString("0.#", CultureInfo.InvariantCulture) + "%",
        MeasureUnits.PerHundred => value.ToString("0.#", CultureInfo.InvariantCulture) + " per 100",
        MeasureUnits.PerThousand => value.ToString("0.#", CultureInfo.InvariantCulture) + " per 1,000",
        MeasureUnits.Minutes => value.ToString("N0", CultureInfo.InvariantCulture) + " minutes",
        _ => value.ToString("0.00", CultureInfo.InvariantCulture),
    };

    /// <summary>The measures in group order, then in the catalog's order.</summary>
    public static IReadOnlyList<FacilityMeasure> Ordered(IEnumerable<FacilityMeasure> measures, IReadOnlyList<string> catalogOrder) =>
        [.. measures.OrderBy(m => Position(Groups, m.Group) is var g and >= 0 ? g : Groups.Count).ThenBy(m => Position(catalogOrder, m.Id) is var i and >= 0 ? i : int.MaxValue)];

    private static int Position(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static PostAcuteMeasureInfo Sir(string code, string name) =>
        new(new FacilityMeasureInfo(code, Infections, name, MeasureUnits.Ratio), code + "_SIR", code + "_COMP_PERF", null, code + "_NATL_SIR");

    private static PostAcuteMeasureInfo Readmission(string code, string part, string name) =>
        new(new FacilityMeasureInfo(code, Readmissions, name, MeasureUnits.Percent), $"{code}_{part}_RSRR", $"{code}_{part}_COMP_PERF", $"{code}_{part}_VOLUME",
            $"{code}_{part}_NATL_OBS_RATE");

    private static PostAcuteMeasureInfo Community(string code) =>
        new(new FacilityMeasureInfo(code, Outcomes, "Patients discharged to the community (home) who stayed out of the hospital", MeasureUnits.Percent, HigherIsBetter: true),
            code + "_DTC_RS_RATE", code + "_DTC_COMP_PERF", code + "_DTC_VOLUME", code + "_DTC_NATL_OBS_RATE");

    private static PostAcuteMeasureInfo Spend(string code) =>
        new(new FacilityMeasureInfo(code, Spending, "Medicare spending per patient, compared with the national median", MeasureUnits.Ratio), code + "_MSPB_SCORE", null,
            code + "_MSPB_NUMB", code + "_NATL_MSPB_SCORE");
}
