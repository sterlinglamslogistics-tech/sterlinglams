using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_PUSH_VARIANT_STOCK=1): seeds Odoo with the website's per-variant stock
/// (the generated Odoo variants start at 0). Pushes StoreInventory rows that belong to a
/// variant, keyed by the variant's Odoo product id, at each store's location.
/// </summary>
public static class OdooPushVariantStock
{
    private const int BatchSize = 100;

    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var rows = await (
            from si in db.StoreInventories
            join v in db.ProductVariants on si.ProductVariantId equals v.Id
            join s in db.Stores on si.StoreId equals s.Id
            where si.ProductVariantId != 0 && v.OdooVariantId != 0 && s.OdooStockLocationId != 0 && si.QuantityOnHand > 0
            select new { v.OdooVariantId, s.OdooStockLocationId, si.QuantityOnHand }
        ).ToListAsync();

        logger.LogInformation("ODOO_PUSH_VARIANT_STOCK: {Count} variant stock lines to set", rows.Count);
        if (rows.Count == 0) return;

        var ctx = new { inventory_mode = true };
        int done = 0;
        foreach (var batch in rows.Chunk(BatchSize))
        {
            var vals = batch.Select(r => new Dictionary<string, object?>
            {
                ["product_id"] = r.OdooVariantId,
                ["location_id"] = r.OdooStockLocationId,
                ["inventory_quantity"] = r.QuantityOnHand,
            }).ToList();
            try
            {
                var quantIds = await odoo.CreateManyAsync("stock.quant", vals, ctx);
                await odoo.ExecuteAsync("stock.quant", "action_apply_inventory", new object[] { quantIds }, ctx);
                done += batch.Length;
                logger.LogInformation("ODOO_PUSH_VARIANT_STOCK: set {Done}/{Total}", done, rows.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ODOO_PUSH_VARIANT_STOCK: batch failed (after {Done}) — stopping", done);
                break;
            }
        }
        logger.LogInformation("ODOO_PUSH_VARIANT_STOCK: done. set={Done}", done);
    }
}
