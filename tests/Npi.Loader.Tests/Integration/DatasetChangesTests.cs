using Npi.Loader.Datasets;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>The change log's entries from dataset reloads (Phase 3 step 3.2). Skipped unless NPI_TEST_MYSQL is set.</summary>
public sealed class DatasetChangesTests
{
    private const string A = "1000000004";
    private const string B = "1000000012";
    private const string C = "1000000020";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static Task<IReadOnlyList<(string, string, string?, string?)>> ChangesAsync(TestDatabase db) =>
        db.QueryAsync<(string, string, string?, string?)>("SELECT npi, change_type, old_value, new_value FROM provider_change ORDER BY npi, change_type, id");

    [Fact]
    public async Task Reloading_exclusions_and_opt_outs_records_what_started_ended_or_was_renewed()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.Database.OpenAsync(_ct);

        // A first load has nothing to compare with.
        await TableSwap.ReplaceAsync(connection, ["oig_exclusion", "medicare_opt_out"], 0.0, () => db.ExecuteAsync(
            $"""
            INSERT INTO oig_exclusion_staging (npi, exclusion_type, exclusion_date) VALUES ('{A}', '1128a1', '2020-01-15'), ('{B}', '1128b4', '2021-03-01');
            INSERT INTO medicare_opt_out_staging (npi, end_date) VALUES ('{A}', '2026-12-31'), ('{B}', '2027-06-30');
            """), _ct);
        Assert.Empty(await ChangesAsync(db));

        // B reinstated, C excluded (twice listed); A's opt-out renewed, B's ended, C opted out.
        await TableSwap.ReplaceAsync(connection, ["oig_exclusion", "medicare_opt_out"], 0.0, () => db.ExecuteAsync(
            $"""
            INSERT INTO oig_exclusion_staging (npi, exclusion_type, exclusion_date) VALUES ('{A}', '1128a1', '2020-01-15'),
              ('{C}', '1128b7', '2026-09-01'), ('{C}', '1128a1', '2026-10-01'), (NULL, '1128a1', '2026-10-01');
            INSERT INTO medicare_opt_out_staging (npi, end_date) VALUES ('{A}', '2028-12-31'), ('{C}', '2028-10-01');
            """), _ct, log: Logger.None);

        Assert.Equal(
            [
                (A, "opt_out_renewed", "until 2026-12-31", "until 2028-12-31"),
                (B, "exclusion_ended", "1128b4 since 2021-03-01", null), (B, "opt_out_ended", "until 2027-06-30", null),
                (C, "excluded", null, "1128a1, 1128b7 since 2026-09-01"), (C, "opted_out", null, "until 2028-10-01"),
            ],
            await ChangesAsync(db));
    }

    [Fact]
    public async Task Reloading_a_states_licenses_records_licenses_statuses_renewals_and_board_actions()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.Database.OpenAsync(_ct);
        const string columns = "npi, state, kind, source, license_number, license_type, status, expiration_date, action_date, action";
        await db.ExecuteAsync(
            $"""
            INSERT INTO provider_state_license ({columns}) VALUES
              ('{A}', 'TX', 'license', 'tx_tmb', 'M1234', 'MD', 'ACTIVE', '2026-11-30', NULL, NULL),
              ('{B}', 'TX', 'license', 'tx_tmb', 'P555', 'PA', 'ACTIVE', '2027-01-31', NULL, NULL),
              ('{C}', 'TX', 'license', 'tx_tmb', 'M999', 'MD', 'ACTIVE', '2026-12-31', '2019-05-01', 'Remedial plan'),
              ('{A}', 'NY', 'action', 'ny_bpmc', NULL, NULL, NULL, NULL, '2010-01-01', 'Censure');
            CREATE TABLE provider_state_license_new LIKE provider_state_license;
            INSERT INTO provider_state_license_new ({columns}) VALUES
              ('{A}', 'TX', 'license', 'tx_tmb', 'M1234', 'MD', 'ACTIVE', '2028-11-30', NULL, NULL),
              ('{C}', 'TX', 'license', 'tx_tmb', 'M999', 'MD', 'PROBATION', '2026-12-31', '2019-05-01', 'Remedial plan'),
              ('{C}', 'TX', 'license', 'tx_tmb', 'M999', 'MD', 'PROBATION', '2026-12-31', '2026-10-01', 'Probation order'),
              ('{C}', 'TX', 'license', 'tx_tmb', 'DO77', 'DO', 'ACTIVE', '2027-12-31', NULL, NULL);
            """);

        await DatasetChanges.RecordStateLicensesAsync(connection, "TX", "provider_state_license_new", DateTime.UtcNow, Logger.None, _ct);

        // A renewed; B no longer listed; C's status changed, a new board action, a second license. NY's rows aren't compared.
        Assert.Equal(
            [
                (A, "license_renewed", "TX M1234 (MD): expires 2026-11-30", "TX M1234 (MD): expires 2028-11-30"),
                (B, "license_removed", "TX P555 (PA): ACTIVE", null),
                (C, "board_action", null, "TX 2026-10-01: Probation order"),
                (C, "license_added", null, "TX DO77 (DO): ACTIVE"),
                (C, "license_status", "TX M999 (MD): ACTIVE", "TX M999 (MD): PROBATION"),
            ],
            await ChangesAsync(db));
    }
}
