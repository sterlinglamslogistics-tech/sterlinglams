using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_PUSH_STOCK=1): seeds Odoo with the website's current per-store stock so
/// Odoo (the inventory master) isn't empty. For each linked product × store it sets the
/// on-hand quantity at that store's Odoo location via an inventory adjustment.
/// Run ONCE for the initial baseline (re-running would add stock again).
/// </summary>
public static class OdooPushStock
{
    private const int BatchSize = 100;

    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var rows = await (
            from si in db.StoreInventories
            join p in db.Products on si.ProductId equals p.Id
            join s in db.Stores on si.StoreId equals s.Id
            where p.OdooProductId != 0 && s.OdooStockLocationId != 0 && si.QuantityOnHand > 0
            select new { p.OdooProductId, s.OdooStockLocationId, si.QuantityOnHand }
        ).ToListAsync();

        logger.LogInformation("ODOO_PUSH_STOCK: {Count} stock lines to set", rows.Count);
        if (rows.Count == 0) return;

        var ctx = new { inventory_mode = true };
        int done = 0;
        foreach (var batch in rows.Chunk(BatchSize))
        {
            var vals = batch.Select(r => new Dictionary<string, object?>
            {
                ["product_id"] = r.OdooProductId,
                ["location_id"] = r.OdooStockLocationId,
                ["inventory_quantity"] = r.QuantityOnHand,
            }).ToList();

            try
            {
                var quantIds = await odoo.CreateManyAsync("stock.quant", vals, ctx);
                await odoo.ExecuteAsync("stock.quant", "action_apply_inventory", new object[] { quantIds }, ctx);
                done += batch.Length;
                logger.LogInformation("ODOO_PUSH_STOCK: set {Done}/{Total}", done, rows.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ODOO_PUSH_STOCK: batch failed (after {Done}) — stopping", done);
                break;
            }
        }

        logger.LogInformation("ODOO_PUSH_STOCK: done. set={Done}", done);
    }
}
