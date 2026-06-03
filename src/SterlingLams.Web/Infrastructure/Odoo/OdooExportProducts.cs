using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_EXPORT_PRODUCTS=1): pushes website products that aren't yet linked
/// (OdooProductId == 0) into Odoo as storable goods, then records Odoo's product *variant*
/// id on each Product (variant id is what stock quants and sale-order lines reference).
/// Idempotent — already-linked products are skipped, so it's safe to re-run.
/// </summary>
public static class OdooExportProducts
{
    private const int BatchSize = 40;

    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var pending = await db.Products
            .Where(p => p.IsActive && p.OdooProductId == 0)
            .OrderBy(p => p.Id)
            .ToListAsync();

        logger.LogInformation("ODOO_EXPORT: {Count} product(s) to push", pending.Count);
        if (pending.Count == 0) return;

        int done = 0, failed = 0;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            var vals = batch.Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name,
                ["list_price"] = p.Price,
                ["default_code"] = string.IsNullOrWhiteSpace(p.Sku) ? null : p.Sku,
                ["type"] = "consu",       // "goods" in Odoo 17+
                ["is_storable"] = true,    // track inventory
            }).ToList();

            List<int> templateIds;
            try
            {
                templateIds = await odoo.CreateManyAsync("product.template", vals);
            }
            catch (Exception ex)
            {
                failed += batch.Length;
                logger.LogError(ex, "ODOO_EXPORT: batch create failed (after {Done} ok) — stopping", done);
                break;
            }

            // Resolve each template's single variant id (product.product).
            var tmpl = await odoo.SearchReadAsync(
                "product.template",
                new object[] { new object[] { "id", "in", templateIds } },
                new[] { "id", "product_variant_id" });
            var variantByTmpl = tmpl.ToDictionary(r => r["id"].GetInt32(), r => Many2OneId(r, "product_variant_id"));

            for (int i = 0; i < batch.Length; i++)
            {
                var tid = templateIds[i];
                batch[i].OdooProductId = variantByTmpl.TryGetValue(tid, out var vid) && vid > 0 ? vid : tid;
            }

            await db.SaveChangesAsync();
            done += batch.Length;
            logger.LogInformation("ODOO_EXPORT: linked {Done}/{Total}", done, pending.Count);
        }

        logger.LogInformation("ODOO_EXPORT: done. linked={Done}, failed={Failed}", done, failed);
    }

    private static int Many2OneId(Dictionary<string, JsonElement> row, string key)
        => row.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
            ? v[0].GetInt32()
            : 0;
}
