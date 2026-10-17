using System.Globalization;
using System.Runtime.InteropServices;
using Npi.Core;

namespace Npi.Loader.Datasets;

/// <summary>An NPI's Medicaid claims in one calendar year (<c>medicaid_billing_year</c>); the totals count a cell with the NPI as both once.</summary>
public sealed record MedicaidBillingRow(string Npi, int Year, long ClaimLines, decimal Paid, long BillingLines, decimal BillingPaid,
    long ServicingLines, decimal ServicingPaid);

/// <summary>One of an NPI's procedures Medicaid paid most for in the newest year (<c>medicaid_top_procedure</c>).</summary>
public sealed record MedicaidProcedureRow(string Npi, int Rank, int Year, string Hcpcs, long ClaimLines, decimal Paid);

/// <summary>What <see cref="MedicaidSpending.Summarize"/> read.</summary>
public sealed record MedicaidSpendingSummary(DateOnly FirstMonth, DateOnly LastMonth, long Lines, long SkippedNpis, long SkippedAmounts, long NpiYears,
    long Npis, IReadOnlyDictionary<int, (long Npis, decimal Paid)> ByYear);

/// <summary>
/// HHS's "Medicaid Provider Spending by HCPCS" (T-MSIS; docs/PRODUCT_PLAN.md step 3.7): one CSV row per billing NPI, servicing NPI,
/// procedure code and month (BILLING_PROVIDER_NPI_NUM, SERVICING_PROVIDER_NPI_NUM, HCPCS_CODE, CLAIM_FROM_MONTH yyyy-MM,
/// TOTAL_PATIENTS, TOTAL_CLAIM_LINES, TOTAL_PAID), 2018–2024, ~230M rows in 11.6 GB; HHS leaves out cells under 12 claim lines or 12
/// patients. Summarized in memory per NPI and calendar year (claim lines and amount paid, as billing and as servicing provider) and,
/// for the newest year, per NPI and procedure (its top <see cref="TopProcedures"/>). Patients aren't kept: a cell counts them per
/// procedure and month, so adding cells would count a patient many times. An NPI that isn't a valid NPI (blank, the check digit)
/// isn't counted for that role; a cell paying more than <see cref="MaxCellPaid"/> is a data error (T-MSIS has cells of trillions of
/// dollars under invalid NPIs) and is skipped.
/// </summary>
public static class MedicaidSpending
{
    public const int TopProcedures = 5;

    /// <summary>No real cell (one NPI pair, procedure and month) comes near this; larger ones are data errors.</summary>
    public const decimal MaxCellPaid = 1_000_000_000m;

    private const int YearBase = 2000; // years 2000–2063 in 6 bits

    private struct Year
    {
        public long BillingLines, ServicingLines, BothLines;
        public double BillingPaid, ServicingPaid, BothPaid; // doubles keep the table at ~64 bytes an entry; cents are exact well past $1B
    }

    private struct Procedure
    {
        public long Lines;
        public double Paid;
    }

    /// <summary>Reads the CSV (header first) and hands the per-year rows to <paramref name="years"/>, the top procedures to <paramref name="procedures"/>.</summary>
    /// <param name="capacity">
    /// The NPI-years and newest-year NPI-procedures expected, so the tables don't grow by doubling (the 2026 release: ~7.4M and ~7M;
    /// 0 for small files).
    /// </param>
    public static MedicaidSpendingSummary Summarize(TextReader reader, Action<MedicaidBillingRow> years, Action<MedicaidProcedureRow> procedures,
        int capacity = 0)
    {
        var header = new List<string>();
        HhsOpenData.Split(reader.ReadLine() ?? throw new InvalidDataException("The Medicaid spending file is empty."), header);
        const string what = "The Medicaid spending file";
        int cBilling = HhsOpenData.Column(header, "BILLING_PROVIDER_NPI_NUM", what), cServicing = HhsOpenData.Column(header, "SERVICING_PROVIDER_NPI_NUM", what),
            cCode = HhsOpenData.Column(header, "HCPCS_CODE", what), cMonth = HhsOpenData.Column(header, "CLAIM_FROM_MONTH", what),
            cLines = HhsOpenData.Column(header, "TOTAL_CLAIM_LINES", what), cPaid = HhsOpenData.Column(header, "TOTAL_PAID", what);
        var needed = new[] { cBilling, cServicing, cCode, cMonth, cLines, cPaid }.Max() + 1;

        var byYear = new Dictionary<long, Year>(capacity);
        var byProcedure = new Dictionary<long, Procedure>(capacity);
        var codes = new List<string>();
        var codeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var fields = new List<string>();
        long lines = 0, skippedNpis = 0, skippedAmounts = 0;
        int minMonth = int.MaxValue, maxMonth = 0, topYear = 0;
        while (reader.ReadLine() is { } line)
        {
            lines++;
            HhsOpenData.Split(line, fields);
            if (fields.Count < needed)
            {
                throw new InvalidDataException($"{what}: line {lines + 1:N0} has {fields.Count} fields, not {header.Count}.");
            }

            var month = fields[cMonth];
            if (month.Length < 7 || !int.TryParse(month.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                || !int.TryParse(month.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mm) || year < YearBase || year >= YearBase + 64
                || mm is < 1 or > 12)
            {
                throw new InvalidDataException($"{what}: line {lines + 1:N0} has no month yyyy-MM (\"{month}\").");
            }

            if (!double.TryParse(fields[cPaid], NumberStyles.Float, CultureInfo.InvariantCulture, out var paid)
                || !long.TryParse(fields[cLines], NumberStyles.Integer, CultureInfo.InvariantCulture, out var claimLines))
            {
                throw new InvalidDataException($"{what}: line {lines + 1:N0} has no number of claim lines or amount paid.");
            }

            var billing = Npi(fields[cBilling]);
            var servicing = Npi(fields[cServicing]);
            if (billing < 0 && servicing < 0)
            {
                skippedNpis++;
                continue;
            }

            if (Math.Abs(paid) > (double)MaxCellPaid)
            {
                skippedAmounts++;
                continue;
            }

            minMonth = Math.Min(minMonth, year * 12 + mm - 1);
            maxMonth = Math.Max(maxMonth, year * 12 + mm - 1);
            var y = year - YearBase;
            if (billing >= 0)
            {
                ref var a = ref CollectionsMarshal.GetValueRefOrAddDefault(byYear, billing << 6 | (long)y, out _);
                a.BillingLines += claimLines;
                a.BillingPaid += paid;
                if (servicing == billing)
                {
                    a.ServicingLines += claimLines;
                    a.ServicingPaid += paid;
                    a.BothLines += claimLines;
                    a.BothPaid += paid;
                }
            }

            if (servicing >= 0 && servicing != billing)
            {
                ref var a = ref CollectionsMarshal.GetValueRefOrAddDefault(byYear, servicing << 6 | (long)y, out _);
                a.ServicingLines += claimLines;
                a.ServicingPaid += paid;
            }

            // The newest year's procedures: a newer year seen drops what was gathered for an older one.
            if (year < topYear)
            {
                continue;
            }

            if (year > topYear)
            {
                byProcedure.Clear();
                topYear = year;
            }

            var code = fields[cCode].Trim().ToUpperInvariant();
            if (code.Length == 0)
            {
                continue;
            }

            if (!codeIndex.TryGetValue(code, out var c))
            {
                if (codes.Count == 1 << 20)
                {
                    throw new InvalidDataException($"{what} has more than {1 << 20:N0} procedure codes.");
                }

                codeIndex[code] = c = codes.Count;
                codes.Add(code);
            }

            if (billing >= 0)
            {
                ref var p = ref CollectionsMarshal.GetValueRefOrAddDefault(byProcedure, billing << 20 | (long)c, out _);
                p.Lines += claimLines;
                p.Paid += paid;
            }

            if (servicing >= 0 && servicing != billing)
            {
                ref var p = ref CollectionsMarshal.GetValueRefOrAddDefault(byProcedure, servicing << 20 | (long)c, out _);
                p.Lines += claimLines;
                p.Paid += paid;
            }
        }

        if (maxMonth == 0)
        {
            throw new InvalidDataException($"{what} has no rows with a valid NPI.");
        }

        // Per NPI and year, in NPI order (only the keys are sorted, to keep memory down).
        var perYear = new Dictionary<int, (long Npis, decimal Paid)>();
        long npis = 0, lastNpi = -1;
        var yearKeys = byYear.Keys.ToArray();
        Array.Sort(yearKeys);
        foreach (var key in yearKeys)
        {
            var a = byYear[key];
            var year = (int)(key & 63) + YearBase;
            var npi = key >> 6;
            if (npi != lastNpi)
            {
                npis++;
                lastNpi = npi;
            }

            var total = a.BillingPaid + a.ServicingPaid - a.BothPaid;
            years(new MedicaidBillingRow(Text(npi), year, a.BillingLines + a.ServicingLines - a.BothLines, Money(total),
                a.BillingLines, Money(a.BillingPaid), a.ServicingLines, Money(a.ServicingPaid)));
            var (n, p) = perYear.GetValueOrDefault(year);
            perYear[year] = (n + 1, p + Money(total));
        }

        var npiYears = byYear.Count;
        yearKeys = [];
        byYear.Clear();
        byYear.TrimExcess();

        // Each NPI's top procedures of the newest year, by amount paid, then claim lines, then code.
        var procedureKeys = byProcedure.Keys.ToArray();
        Array.Sort(procedureKeys);
        var top = new List<(long Key, Procedure P)>();
        void Flush()
        {
            if (top.Count == 0)
            {
                return;
            }

            var rank = 0;
            foreach (var (key, p) in top.OrderByDescending(e => e.P.Paid).ThenByDescending(e => e.P.Lines)
                         .ThenBy(e => codes[(int)(e.Key & ((1 << 20) - 1))], StringComparer.Ordinal).Take(TopProcedures))
            {
                procedures(new MedicaidProcedureRow(Text(key >> 20), ++rank, topYear, codes[(int)(key & ((1 << 20) - 1))], p.Lines, Money(p.Paid)));
            }

            top.Clear();
        }

        foreach (var key in procedureKeys)
        {
            if (top.Count > 0 && top[0].Key >> 20 != key >> 20)
            {
                Flush();
            }

            top.Add((key, byProcedure[key]));
        }

        Flush();
        return new MedicaidSpendingSummary(new DateOnly(minMonth / 12, minMonth % 12 + 1, 1), new DateOnly(maxMonth / 12, maxMonth % 12 + 1, 1), lines,
            skippedNpis, skippedAmounts, npiYears, npis, perYear);
    }

    // A valid NPI as a number (less 1,000,000,000, so it fits 31 bits), or -1.
    private static long Npi(string value)
    {
        var v = value.Trim();
        return InputFormats.HasValidCheckDigit(v) && v[0] is '1' or '2' ? long.Parse(v, CultureInfo.InvariantCulture) - 1_000_000_000L : -1;
    }

    private static decimal Money(double value) => Math.Round((decimal)value, 2);

    private static string Text(long npi) => (npi + 1_000_000_000L).ToString(CultureInfo.InvariantCulture);
}
