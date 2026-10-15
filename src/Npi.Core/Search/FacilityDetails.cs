using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// The facility page's sections from docs/PRODUCT_PLAN.md step 3.6 part 2, read for one CCN: "How it compares" (the type's main Care Compare
/// measures, <see cref="FacilityMeasures"/>), a hospital's Medicare volume, a dialysis facility's listing details, and when a rehabilitation
/// facility or long-term care hospital was first certified.
/// </summary>
internal static class FacilityDetails
{
    internal sealed record Result(IReadOnlyList<FacilityMeasure> Measures, HospitalVolume? Volume, DialysisFacts? Dialysis, DateOnly? CertifiedSince,
        DialysisQualityIncentive? QualityIncentive);

    private sealed class SurveyRow
    {
        public string Ccn { get; set; } = "";

        public short? Professional { get; set; }

        public short? Communication { get; set; }

        public short? Recommend { get; set; }

        public short? Rating { get; set; }

        public int? Surveys { get; set; }

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }
    }

    private sealed class QipRow
    {
        public short PaymentYear { get; set; }

        public short? TotalScore { get; set; }

        public short? StateAverage { get; set; }

        public short? NationalAverage { get; set; }

        public double? PaymentReduction { get; set; }
    }

    // Read into classes by property name (the columns' own types, as MySqlConnector returns them).
    private sealed class MeasureRow
    {
        public string Ccn { get; set; } = "";

        public string MeasureId { get; set; } = "";

        public double? Score { get; set; }

        public string? Compared { get; set; }

        public int? Cases { get; set; }

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }
    }

    private sealed class AscRow
    {
        public string Ccn { get; set; } = "";

        public short? DataYear { get; set; }

        public double? Asc9Rate { get; set; }

        public double? Asc13Rate { get; set; }

        public string? Asc12Category { get; set; }

        public double? Asc12Rate { get; set; }

        public int? Asc12Cases { get; set; }

        public string? Asc17Category { get; set; }

        public double? Asc17Rate { get; set; }

        public int? Asc17Cases { get; set; }

        public string? Asc18Category { get; set; }

        public double? Asc18Rate { get; set; }

        public int? Asc18Cases { get; set; }

        public string? Asc19Category { get; set; }

        public double? Asc19Rate { get; set; }

        public int? Asc19Cases { get; set; }

        public int TotalCases => (Asc12Cases ?? 0) + (Asc17Cases ?? 0) + (Asc18Cases ?? 0) + (Asc19Cases ?? 0);
    }

    private sealed class DialysisRow
    {
        public short? Stations { get; set; }

        public string? Ownership { get; set; }

        public string? Chain { get; set; }

        public sbyte? InCenter { get; set; }

        public sbyte? Peritoneal { get; set; }

        public sbyte? HomeHemodialysis { get; set; }

        public double? FiveStar { get; set; }

        public string? FiveStarPeriod { get; set; }

        public sbyte? SurveyRating { get; set; }

        public string? Survival { get; set; }

        public string? SurvivalPeriod { get; set; }

        public string? Hospitalization { get; set; }

        public string? HospitalizationPeriod { get; set; }

        public string? Readmission { get; set; }

        public string? ReadmissionPeriod { get; set; }

        public string? EdVisits { get; set; }

        public string? EdVisitsPeriod { get; set; }

        public string? Infection { get; set; }

        public string? InfectionPeriod { get; set; }

        public string? Transfusion { get; set; }

        public string? TransfusionPeriod { get; set; }

        public string? Waitlist { get; set; }

        public string? WaitlistPeriod { get; set; }
    }

    private sealed class VolumeRow
    {
        public short? InpatientYear { get; set; }

        public int? Discharges { get; set; }

        public int? Beneficiaries { get; set; }

        public int? CoveredDays { get; set; }

        public double? MedicarePayments { get; set; }

        public double? TotalPayments { get; set; }

        public short? OutpatientYear { get; set; }

        public int? OutpatientServices { get; set; }

        public double? OutpatientPayments { get; set; }
    }

    private sealed class ServiceRow
    {
        public string Setting { get; set; } = "";

        public string Code { get; set; } = "";

        public string? Description { get; set; }

        public int Cases { get; set; }

        public int? Beneficiaries { get; set; }

        public double? AvgMedicarePayment { get; set; }
    }

    public static async Task<Result> ReadAsync(MySqlConnection connection, string ccn, string kind, CancellationToken ct)
    {
        IReadOnlyList<FacilityMeasure> measures = [];
        DialysisFacts? dialysis = null;
        DateOnly? certifiedSince = null;
        DialysisQualityIncentive? incentive = null;
        switch (kind)
        {
            case FacilityTypes.Hospital:
                measures = Combine(await MeasureRowsAsync(connection, "cms_hospital_measure", ccn, ct), ccn, FacilityMeasures.Hospital);
                break;
            case FacilityTypes.Rehab or FacilityTypes.LongTermCare:
                var prefix = kind == FacilityTypes.Rehab ? "I_" : "L_";
                measures = Combine(await MeasureRowsAsync(connection, "cms_post_acute_measure", ccn, ct), ccn,
                    [.. FacilityMeasures.PostAcute.Select(m => m.Info).Where(m => m.Id.StartsWith(prefix, StringComparison.Ordinal))]);
                certifiedSince = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                    "SELECT certified_since FROM cms_post_acute WHERE ccn = @ccn", new { ccn }, cancellationToken: ct)) is { } since
                    ? DateOnly.FromDateTime(since)
                    : null;
                break;
            case FacilityTypes.SurgeryCenter:
                measures = SurgeryCenterMeasures((await connection.QueryAsync<AscRow>(new CommandDefinition(
                    """
                    SELECT ccn AS Ccn, data_year AS DataYear, asc9_rate AS Asc9Rate, asc13_rate AS Asc13Rate,
                           asc12_category AS Asc12Category, asc12_rate AS Asc12Rate, asc12_cases AS Asc12Cases,
                           asc17_category AS Asc17Category, asc17_rate AS Asc17Rate, asc17_cases AS Asc17Cases,
                           asc18_category AS Asc18Category, asc18_rate AS Asc18Rate, asc18_cases AS Asc18Cases,
                           asc19_category AS Asc19Category, asc19_rate AS Asc19Rate, asc19_cases AS Asc19Cases
                    FROM cms_asc WHERE ccn IN (@ccn, @nation) ORDER BY npi
                    """, new { ccn, nation = FacilityMeasures.National }, cancellationToken: ct))).ToList(), ccn);
                var surveys = (await connection.QueryAsync<SurveyRow>(new CommandDefinition(
                    """
                    SELECT ccn AS Ccn, professional AS Professional, communication AS Communication, recommend AS Recommend, rating AS Rating, surveys AS Surveys, start_date AS StartDate, end_date AS EndDate
                    FROM cms_asc_survey WHERE ccn IN (@ccn, @nation)
                    """, new { ccn, nation = FacilityMeasures.National }, cancellationToken: ct))).ToList();
                measures = [.. FacilityMeasures.Ordered([.. measures, .. SurveyMeasures(surveys, ccn)],
                    [.. FacilityMeasures.SurgeryCenter.Concat(FacilityMeasures.SurgeryCenterSurvey).Select(m => m.Id)])];
                break;
            case FacilityTypes.Dialysis:
                var row = await connection.QueryFirstOrDefaultAsync<DialysisRow>(new CommandDefinition(
                    """
                    SELECT stations AS Stations, ownership AS Ownership, chain AS Chain, in_center AS InCenter, peritoneal AS Peritoneal,
                           home_hemodialysis AS HomeHemodialysis, five_star AS FiveStar, five_star_period AS FiveStarPeriod, survey_rating AS SurveyRating,
                           survival AS Survival, survival_period AS SurvivalPeriod, hospitalization AS Hospitalization, hospitalization_period AS HospitalizationPeriod,
                           readmission AS Readmission, readmission_period AS ReadmissionPeriod, ed_visits AS EdVisits, ed_visits_period AS EdVisitsPeriod,
                           infection AS Infection, infection_period AS InfectionPeriod, transfusion AS Transfusion, transfusion_period AS TransfusionPeriod,
                           waitlist AS Waitlist, waitlist_period AS WaitlistPeriod
                    FROM cms_dialysis WHERE ccn = @ccn
                    """, new { ccn }, cancellationToken: ct));
                if (row is not null)
                {
                    var star = FacilityMeasures.ParsePeriod(row.FiveStarPeriod);
                    dialysis = new DialysisFacts(row.Stations, row.Ownership, row.Chain, Flag(row.InCenter), Flag(row.Peritoneal), Flag(row.HomeHemodialysis),
                        row.FiveStar is >= 1 and <= 5 ? row.FiveStar : null, star?.From, star?.To, row.SurveyRating is >= 1 and <= 5 ? row.SurveyRating : null);
                    measures = DialysisMeasures(row);
                }

                var qip = await connection.QueryFirstOrDefaultAsync<QipRow>(new CommandDefinition(
                    """
                    SELECT payment_year AS PaymentYear, total_score AS TotalScore, state_average AS StateAverage, national_average AS NationalAverage,
                           payment_reduction AS PaymentReduction
                    FROM cms_dialysis_qip WHERE ccn = @ccn
                    """, new { ccn }, cancellationToken: ct));
                incentive = qip is null
                    ? null
                    : new DialysisQualityIncentive(qip.PaymentYear, qip.TotalScore, qip.StateAverage, qip.NationalAverage, qip.PaymentReduction);
                break;
        }

        return new Result(measures, await VolumeAsync(connection, ccn, ct), dialysis, certifiedSince, incentive);
    }

    /// <summary>The surgery center's OAS CAHPS answers, with the national shares (CMS doesn't compare them).</summary>
    private static IEnumerable<FacilityMeasure> SurveyMeasures(List<SurveyRow> rows, string ccn)
    {
        var own = rows.FirstOrDefault(r => string.Equals(r.Ccn, ccn, StringComparison.OrdinalIgnoreCase));
        if (own is null)
        {
            yield break;
        }

        var nation = rows.FirstOrDefault(r => r.Ccn == FacilityMeasures.National);
        foreach (var m in FacilityMeasures.SurgeryCenterSurvey)
        {
            var (value, national) = m.Id switch
            {
                "OAS_PROFESSIONAL" => (own.Professional, nation?.Professional),
                "OAS_COMMUNICATION" => (own.Communication, nation?.Communication),
                "OAS_RECOMMEND" => (own.Recommend, nation?.Recommend),
                _ => (own.Rating, nation?.Rating),
            };
            if (value is not null)
            {
                yield return new FacilityMeasure(m.Id, m.Group, m.Name, m.Unit, m.HigherIsBetter, value, national, null, own.Surveys, Date(own.StartDate),
                    Date(own.EndDate));
            }
        }
    }

    private static bool? Flag(sbyte? value) => value is null ? null : value != 0;

    private static async Task<List<MeasureRow>> MeasureRowsAsync(MySqlConnection connection, string table, string ccn, CancellationToken ct) =>
        (await connection.QueryAsync<MeasureRow>(new CommandDefinition(
            $"""
            SELECT ccn AS Ccn, measure_id AS MeasureId, score AS Score, compared AS Compared, cases AS Cases, start_date AS StartDate, end_date AS EndDate
            FROM {(table is "cms_hospital_measure" or "cms_post_acute_measure" ? table : throw new ArgumentException("Unknown measure table.", nameof(table)))}
            WHERE ccn IN (@ccn, @nation)
            """, new { ccn, nation = FacilityMeasures.National }, cancellationToken: ct))).ToList();

    /// <summary>The catalog's measures the facility has a value or a comparison for, each with the national value.</summary>
    private static List<FacilityMeasure> Combine(List<MeasureRow> rows, string ccn, IReadOnlyList<FacilityMeasureInfo> catalog)
    {
        var own = rows.Where(r => string.Equals(r.Ccn, ccn, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.MeasureId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var nation = rows.Where(r => r.Ccn == FacilityMeasures.National)
            .GroupBy(r => r.MeasureId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<FacilityMeasure>();
        foreach (var m in catalog)
        {
            if (!own.TryGetValue(m.Id, out var r))
            {
                continue;
            }

            var comparison = MeasureComparisons.Normalize(r.Compared);
            if (r.Score is null && comparison is null)
            {
                continue;
            }

            result.Add(new FacilityMeasure(m.Id, m.Group, m.Name, m.Unit, m.HigherIsBetter, r.Score, nation.GetValueOrDefault(m.Id)?.Score, comparison, r.Cases,
                Date(r.StartDate), Date(r.EndDate)));
        }

        return [.. FacilityMeasures.Ordered(result, [.. catalog.Select(c => c.Id)])];
    }

    private static List<FacilityMeasure> SurgeryCenterMeasures(List<AscRow> rows, string ccn)
    {
        // The file lists a surgery center once per NPI it reports under, sometimes with different values: the one with the most cases.
        var own = rows.Where(r => string.Equals(r.Ccn, ccn, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.TotalCases).FirstOrDefault();
        if (own is null)
        {
            return [];
        }

        var nation = rows.FirstOrDefault(r => r.Ccn == FacilityMeasures.National);
        var values = new Dictionary<string, (double? Value, string? Category, int? Cases, double? National)>
        {
            ["ASC_12"] = (own.Asc12Rate, own.Asc12Category, own.Asc12Cases, nation?.Asc12Rate),
            ["ASC_17"] = (own.Asc17Rate, own.Asc17Category, own.Asc17Cases, nation?.Asc17Rate),
            ["ASC_18"] = (own.Asc18Rate, own.Asc18Category, own.Asc18Cases, nation?.Asc18Rate),
            ["ASC_19"] = (own.Asc19Rate, own.Asc19Category, own.Asc19Cases, nation?.Asc19Rate),
            ["ASC_9"] = (own.Asc9Rate, null, null, nation?.Asc9Rate),
            ["ASC_13"] = (own.Asc13Rate, null, null, nation?.Asc13Rate),
        };
        var result = new List<FacilityMeasure>();
        foreach (var m in FacilityMeasures.SurgeryCenter)
        {
            var (value, category, cases, national) = values[m.Id];
            var comparison = MeasureComparisons.Normalize(category);
            if (value is null && comparison is null)
            {
                continue;
            }

            DateOnly? from = own.DataYear is { } year ? new DateOnly(year - FacilityMeasures.AscYears(m.Id) + 1, 1, 1) : null;
            DateOnly? to = own.DataYear is { } last ? new DateOnly(last, 12, 31) : null;
            result.Add(new FacilityMeasure(m.Id, m.Group, m.Name, m.Unit, m.HigherIsBetter, value, national, comparison, cases, from, to));
        }

        return [.. FacilityMeasures.Ordered(result, [.. FacilityMeasures.SurgeryCenter.Select(c => c.Id)])];
    }

    private static List<FacilityMeasure> DialysisMeasures(DialysisRow row)
    {
        var values = new Dictionary<string, (string? Category, string? Period)>
        {
            ["survival"] = (row.Survival, row.SurvivalPeriod),
            ["hospitalization"] = (row.Hospitalization, row.HospitalizationPeriod),
            ["readmission"] = (row.Readmission, row.ReadmissionPeriod),
            ["ed_visits"] = (row.EdVisits, row.EdVisitsPeriod),
            ["infection"] = (row.Infection, row.InfectionPeriod),
            ["transfusion"] = (row.Transfusion, row.TransfusionPeriod),
            ["waitlist"] = (row.Waitlist, row.WaitlistPeriod),
        };
        var result = new List<FacilityMeasure>();
        foreach (var m in FacilityMeasures.Dialysis)
        {
            var (category, periodText) = values[m.Id];
            if (MeasureComparisons.Normalize(category) is not { } comparison)
            {
                continue;
            }

            var period = FacilityMeasures.ParsePeriod(periodText);
            result.Add(new FacilityMeasure(m.Id, m.Group, m.Name, m.Unit, m.HigherIsBetter, null, null, comparison, null, period?.From, period?.To));
        }

        return [.. FacilityMeasures.Ordered(result, [.. FacilityMeasures.Dialysis.Select(c => c.Id)])];
    }

    private static async Task<HospitalVolume?> VolumeAsync(MySqlConnection connection, string ccn, CancellationToken ct)
    {
        var v = await connection.QueryFirstOrDefaultAsync<VolumeRow>(new CommandDefinition(
            """
            SELECT inpatient_year AS InpatientYear, discharges AS Discharges, beneficiaries AS Beneficiaries, covered_days AS CoveredDays,
                   medicare_payments AS MedicarePayments, total_payments AS TotalPayments, outpatient_year AS OutpatientYear,
                   outpatient_services AS OutpatientServices, outpatient_payments AS OutpatientPayments
            FROM cms_hospital_volume WHERE ccn = @ccn
            """, new { ccn }, cancellationToken: ct));
        if (v is null)
        {
            return null;
        }

        var services = (await connection.QueryAsync<ServiceRow>(new CommandDefinition(
            """
            SELECT setting AS Setting, code AS Code, description AS Description, cases AS Cases, beneficiaries AS Beneficiaries,
                   avg_medicare_payment AS AvgMedicarePayment
            FROM cms_hospital_service WHERE ccn = @ccn ORDER BY setting, service_rank
            """, new { ccn }, cancellationToken: ct))).ToList();
        HospitalService Service(ServiceRow s) => new(s.Code, s.Description, s.Cases, s.Beneficiaries, s.AvgMedicarePayment);
        return new HospitalVolume(v.InpatientYear, v.Discharges, v.Beneficiaries, v.CoveredDays, v.MedicarePayments, v.TotalPayments, v.OutpatientYear,
            v.OutpatientServices, v.OutpatientPayments, [.. services.Where(s => s.Setting == "I").Select(Service)],
            [.. services.Where(s => s.Setting == "O").Select(Service)]);
    }

    private static DateOnly? Date(DateTime? value) => value is { } d ? DateOnly.FromDateTime(d) : null;
}
