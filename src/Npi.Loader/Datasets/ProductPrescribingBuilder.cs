using MySqlConnector;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Rebuilds the prescribing overlap of the drug products (CLAUDE.md §7 Stage 5.5 item 19, part 4) from its two halves,
/// <c>op_product_npi</c> (who was paid for which drug) and <c>part_d_brand_prescriber</c> (who prescribed which brand),
/// after either reloads: each product's Part D brands, the paid providers who prescribed it, and per-product totals.
/// Skipped (logged) while either half is still empty.
/// </summary>
public static class ProductPrescribingBuilder
{
    public static async Task<long> RebuildAsync(MySqlConnection connection, DatasetContext context, CancellationToken ct)
    {
        if (await Database.CountAsync(connection, "op_product_npi", ct) == 0 || await Database.CountAsync(connection, "part_d_brand", ct) == 0)
        {
            context.Log.Information("Prescribing overlap: waiting for both Open Payments and Part D; not built");
            return 0;
        }

        var started = DateTime.UtcNow;
        var counts = await TableSwap.ReplaceAsync(connection, ["product_part_d_brand", "product_prescriber", "product_prescribing"], context.Options.MinRowRatio, async () =>
        {
            // A product's Part D brands: its name or FDA brand name, or one of them plus more words ("Dupixent Pen"). The LIKE
            // pattern escapes the name's own wildcards.
            await Database.ExecuteAsync(connection,
                """
                INSERT IGNORE INTO `product_part_d_brand_staging` (`slug`, `brand`)
                SELECT c.`slug`, b.`brand`
                FROM (
                  SELECT p.`slug`, p.`name` AS `candidate` FROM `op_product` p WHERE p.`kind` IN ('Drug', 'Biological')
                  UNION
                  SELECT p.`slug`, n.`brand_name` FROM `op_product` p JOIN `fda_ndc_product` n ON n.`ndc_key` = p.`ndc_key`
                  WHERE p.`kind` IN ('Drug', 'Biological') AND n.`brand_name` IS NOT NULL
                ) c
                JOIN `part_d_brand` b
                  ON b.`brand` = c.`candidate`
                  OR b.`brand` LIKE CONCAT(REPLACE(REPLACE(REPLACE(c.`candidate`, '\\', '\\\\'), '%', '\\%'), '_', '\\_'), ' %')
                """, ct);
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `product_prescriber_staging` (`slug`, `npi`, `amount`, `records`, `claims`, `drug_cost`, `beneficiaries`)
                SELECT pn.`slug`, pn.`npi`, pn.`amount`, pn.`records`, SUM(b.`claims`), ROUND(SUM(b.`drug_cost`), 2), SUM(b.`beneficiaries`)
                FROM `op_product_npi` pn
                JOIN `provider` pr ON pr.`npi` = pn.`npi`
                JOIN `product_part_d_brand_staging` m ON m.`slug` = pn.`slug`
                JOIN `part_d_brand_prescriber` b ON b.`brand` = m.`brand` AND b.`npi` = pn.`npi`
                GROUP BY pn.`slug`, pn.`npi`, pn.`amount`, pn.`records`
                """, ct);
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `product_prescribing_staging` (`slug`, `data_year`, `payment_year`, `brands`, `prescribers`, `claims`, `drug_cost`, `paid_providers`,
                  `paid_prescribers`, `paid_claims`, `paid_drug_cost`)
                SELECT m.`slug`, MAX(b.`data_year`), MAX(p.`program_year`), LEFT(GROUP_CONCAT(DISTINCT b.`brand` ORDER BY b.`claims` DESC SEPARATOR ' | '), 2000),
                  (SELECT COUNT(DISTINCT x.`npi`) FROM `part_d_brand_prescriber` x JOIN `product_part_d_brand_staging` mx ON mx.`brand` = x.`brand` WHERE mx.`slug` = m.`slug`),
                  SUM(b.`claims`), ROUND(SUM(b.`drug_cost`), 2),
                  (SELECT COUNT(*) FROM `op_product_npi` pn JOIN `provider` pr ON pr.`npi` = pn.`npi` WHERE pn.`slug` = m.`slug`),
                  COALESCE((SELECT COUNT(*) FROM `product_prescriber_staging` s WHERE s.`slug` = m.`slug`), 0),
                  COALESCE((SELECT SUM(s.`claims`) FROM `product_prescriber_staging` s WHERE s.`slug` = m.`slug`), 0),
                  (SELECT ROUND(SUM(s.`drug_cost`), 2) FROM `product_prescriber_staging` s WHERE s.`slug` = m.`slug`)
                FROM `product_part_d_brand_staging` m
                JOIN `part_d_brand` b ON b.`brand` = m.`brand`
                JOIN `op_product` p ON p.`slug` = m.`slug`
                GROUP BY m.`slug`
                """, ct);
        }, ct);
        context.Log.Information("Prescribing overlap: {Products:N0} drug products with Part D brands, {Rows:N0} paid prescribers, in {Seconds:N0}s",
            counts["product_prescribing"], counts["product_prescriber"], (DateTime.UtcNow - started).TotalSeconds);
        return counts["product_prescriber"];
    }
}
