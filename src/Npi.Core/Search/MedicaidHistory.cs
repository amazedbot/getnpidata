using System.Globalization;
using System.Text.Json.Serialization;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// An enrollment period in a state's Medicaid program from T-MSIS (docs/PRODUCT_PLAN.md step 3.7): active segments joined where
/// they touch. <see cref="From"/> null: the state gave no real start date; <see cref="Until"/> null: still enrolled at the end of
/// the data; <see cref="EndedStatus"/>: the termination that ended it, in T-MSIS's words ("TERM - LICENSE EXPIRED").
/// </summary>
public sealed record MedicaidEnrollmentPeriod(string State, DateOnly? From, DateOnly? Until, string? EndedStatus)
{
    public bool StillEnrolled => Until is null;

    /// <summary>Why the period ended in plain words ("license expired"), from <see cref="EndedStatus"/>; null when none was given.</summary>
    [JsonIgnore]
    public string? EndedReason => EndedStatus is not { Length: > 0 } s ? null
        : (s.StartsWith("TERM - ", StringComparison.OrdinalIgnoreCase) ? s[7..] : s).Trim().ToLowerInvariant() switch
        {
            "" => null,
            "unknown" or "other" => "terminated",
            var r => r.Replace("medicare/medicaid", "Medicare or Medicaid", StringComparison.Ordinal)
                .Replace("medicaid/chip", "Medicaid or CHIP", StringComparison.Ordinal)
                .Replace("medicaid", "Medicaid", StringComparison.Ordinal),
        };
}

/// <summary>An NPI's Medicaid claims in one calendar year: claim lines and the amount Medicaid paid, as billing and as servicing provider.</summary>
/// <param name="ClaimLines">Every claim line with the NPI as billing or servicing provider (a line with both counts once).</param>
public sealed record MedicaidBillingYear(int Year, long ClaimLines, double Paid, long BillingLines, double BillingPaid, long ServicingLines,
    double ServicingPaid);

/// <summary>One of the procedures Medicaid paid an NPI most for in the newest year; the description is Medicare's for the code, when it has one.</summary>
public sealed record MedicaidProcedure(string Hcpcs, string? Description, long ClaimLines, double Paid);

/// <summary>
/// Medicaid in every state from T-MSIS (docs/PRODUCT_PLAN.md step 3.7, HHS open data): the NPI's enrollment periods per state
/// (<see cref="EnrollmentFrom"/> to <see cref="EnrollmentThrough"/>, the months the file covers), its Medicaid billing per
/// calendar year (<see cref="FirstDataYear"/> to <see cref="NewestDataYear"/>; only years with claims listed) and its top procedures
/// in the newest year. History, not a current list: the data ends in December 2024.
/// </summary>
public sealed record MedicaidHistory(DateOnly? EnrollmentFrom, DateOnly? EnrollmentThrough, IReadOnlyList<MedicaidEnrollmentPeriod> Enrollments,
    int? FirstDataYear, int? NewestDataYear, IReadOnlyList<MedicaidBillingYear> Years, IReadOnlyList<MedicaidProcedure> TopProcedures)
{
    /// <summary>The year of <see cref="TopProcedures"/> (the newest data year).</summary>
    public int? TopProceduresYear { get; init; }

    public int? LastBilledYear => Years.Count == 0 ? null : Years.Max(y => y.Year);

    /// <summary>Billed Medicaid in an earlier year but not in the newest one.</summary>
    public bool Stopped => LastBilledYear is { } last && NewestDataYear is { } newest && last < newest;

    /// <summary>The states enrolled in, in order.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> States => Enrollments.Select(e => e.State).Distinct().ToList();

    /// <summary>The years billed as ranges, e.g. "2018–2021, 2023".</summary>
    [JsonIgnore]
    public string Spans => YearSpans.Text(Years.Select(y => y.Year));
}

/// <summary>Years as ranges ("2013–2016, 2019, 2021–2024").</summary>
public static class YearSpans
{
    public static string Text(IEnumerable<int> years)
    {
        var list = years.Distinct().Order().ToList();
        var spans = new List<string>();
        for (var i = 0; i < list.Count;)
        {
            var j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1)
            {
                j++;
            }

            spans.Add(j == i ? list[i].ToString(CultureInfo.InvariantCulture) : $"{list[i]}–{list[j]}");
            i = j + 1;
        }

        return string.Join(", ", spans);
    }
}

/// <summary>Reads <see cref="MedicaidHistory"/> (migration 119's tables; before they exist, or before the first load, nothing).</summary>
public static class MedicaidHistoryReader
{
    private sealed record PeriodRow(string Source, DateTime FirstMonth, DateTime LastMonth);

    private sealed record EnrollmentRow(string State, DateTime? From, DateTime? Until, string? EndedStatus);

    private sealed record YearRow(short Year, long ClaimLines, decimal Paid, long BillingLines, decimal BillingPaid, long ServicingLines, decimal ServicingPaid);

    private sealed record ProcedureRow(short Year, string Hcpcs, string? Description, long ClaimLines, decimal Paid);

    /// <summary>The NPI's Medicaid history; null when neither file lists it or neither is loaded.</summary>
    public static async Task<MedicaidHistory?> ReadAsync(MySqlConnection connection, string npi, CancellationToken ct)
    {
        try
        {
            var periods = (await connection.QueryAsync<PeriodRow>(new CommandDefinition(
                "SELECT source AS Source, first_month AS FirstMonth, last_month AS LastMonth FROM medicaid_data_period", cancellationToken: ct)))
                .ToDictionary(p => p.Source, StringComparer.Ordinal);
            if (periods.Count == 0)
            {
                return null;
            }

            var enrollments = (await connection.QueryAsync<EnrollmentRow>(new CommandDefinition(
                    """
                    SELECT state AS State, enrolled_from AS `From`, enrolled_until AS Until, ended_status AS EndedStatus
                    FROM medicaid_enrollment_period WHERE npi = @npi ORDER BY state, period
                    """, new { npi }, cancellationToken: ct)))
                .Select(e => new MedicaidEnrollmentPeriod(e.State, Date(e.From), Date(e.Until), e.EndedStatus)).ToList();
            var years = (await connection.QueryAsync<YearRow>(new CommandDefinition(
                    """
                    SELECT data_year AS Year, claim_lines AS ClaimLines, paid AS Paid, billing_lines AS BillingLines, billing_paid AS BillingPaid,
                           servicing_lines AS ServicingLines, servicing_paid AS ServicingPaid
                    FROM medicaid_billing_year WHERE npi = @npi ORDER BY data_year
                    """, new { npi }, cancellationToken: ct)))
                .Select(y => new MedicaidBillingYear(y.Year, y.ClaimLines, (double)y.Paid, y.BillingLines, (double)y.BillingPaid, y.ServicingLines,
                    (double)y.ServicingPaid)).ToList();
            var procedures = (await connection.QueryAsync<ProcedureRow>(new CommandDefinition(
                    """
                    SELECT data_year AS Year, hcpcs AS Hcpcs, description AS Description, claim_lines AS ClaimLines, paid AS Paid
                    FROM medicaid_top_procedure WHERE npi = @npi ORDER BY procedure_rank
                    """, new { npi }, cancellationToken: ct))).ToList();
            if (enrollments.Count == 0 && years.Count == 0)
            {
                return null;
            }

            var enrollment = periods.GetValueOrDefault("medicaid_enrollment_segments");
            var spending = periods.GetValueOrDefault("medicaid_spending");
            return new MedicaidHistory(Date(enrollment?.FirstMonth), enrollment is null ? null : MonthEnd(enrollment.LastMonth), enrollments,
                spending?.FirstMonth.Year, spending?.LastMonth.Year, years,
                procedures.Select(p => new MedicaidProcedure(p.Hcpcs, p.Description, p.ClaimLines, (double)p.Paid)).ToList())
            {
                TopProceduresYear = procedures.Count == 0 ? null : procedures[0].Year,
            };
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            return null; // migration 119 not applied yet
        }
    }

    /// <summary>For an organization's NPIs: each one's newest year billed (claim lines, paid), last year billed and states enrolled in.</summary>
    public static async Task<IReadOnlyList<OrganizationMedicaidNpi>> ForNpisAsync(MySqlConnection connection, IReadOnlyList<string> npis,
        IReadOnlyDictionary<string, OrganizationMember> members, CancellationToken ct)
    {
        if (npis.Count == 0)
        {
            return [];
        }

        try
        {
            var newest = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT YEAR(last_month) FROM medicaid_data_period WHERE source = 'medicaid_spending'", cancellationToken: ct));
            var billing = (await connection.QueryAsync<(string Npi, short Year, long ClaimLines, decimal Paid)>(new CommandDefinition(
                "SELECT npi, data_year, claim_lines, paid FROM medicaid_billing_year WHERE npi IN @npis", new { npis }, cancellationToken: ct)))
                .GroupBy(r => r.Npi).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var states = (await connection.QueryAsync<(string Npi, string State)>(new CommandDefinition(
                "SELECT DISTINCT npi, state FROM medicaid_enrollment_period WHERE npi IN @npis ORDER BY npi, state", new { npis }, cancellationToken: ct)))
                .GroupBy(r => r.Npi).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.State).ToList(), StringComparer.Ordinal);
            return [.. npis.Where(n => billing.ContainsKey(n) || states.ContainsKey(n))
                .Select(n =>
                {
                    var years = billing.GetValueOrDefault(n) ?? [];
                    var latest = years.FirstOrDefault(y => y.Year == newest);
                    return new OrganizationMedicaidNpi(n, members.GetValueOrDefault(n)?.Name ?? "", members.GetValueOrDefault(n)?.Role ?? "",
                        newest is { } year && latest.Npi is not null ? year : null, latest.Npi is null ? null : latest.ClaimLines,
                        latest.Npi is null ? null : (double)latest.Paid, years.Count == 0 ? null : years.Max(y => (int)y.Year),
                        states.GetValueOrDefault(n) ?? []);
                })
                .OrderByDescending(m => m.Paid ?? -1).ThenByDescending(m => m.LastBilledYear ?? 0).ThenBy(m => m.Npi, StringComparer.Ordinal)];
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            return [];
        }
    }

    private static DateOnly? Date(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

    private static DateOnly MonthEnd(DateTime month) => new(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
}

/// <summary>One of an organization's NPIs in the Medicaid data: the newest year's claims (null when it billed none then), its last year billed and the states it was enrolled in.</summary>
public sealed record OrganizationMedicaidNpi(string Npi, string Name, string Role, int? Year, long? ClaimLines, double? Paid, int? LastBilledYear,
    IReadOnlyList<string> EnrolledStates);

/// <summary>State Medicaid programs by name, as the pages say them.</summary>
public static class MedicaidNames
{
    /// <summary>"New York Medicaid", "Medi-Cal (California Medicaid)", "Puerto Rico Medicaid".</summary>
    public static string Program(string state) => state.ToUpperInvariant() switch
    {
        "CA" => "Medi-Cal (California Medicaid)",
        var s => GeographyCatalog.StateName(s) + " Medicaid",
    };
}
