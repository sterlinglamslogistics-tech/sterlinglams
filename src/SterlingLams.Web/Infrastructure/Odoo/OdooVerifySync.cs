using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Inventory;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_VERIFY_SYNC=1): proves the Odoo→website stock sync. It sets a distinctive
/// quantity on one product at one store's Odoo location, runs the sync, and reads the local
/// StoreInventory back to confirm it matches.
/// </summary>
public static class OdooVerifySync
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

        var product = await db.Products.Where(p => p.OdooProductId != 0).OrderBy(p => p.Id).FirstOrDefaultAsync();
        var store = await db.Stores.Where(s => s.OdooStockLocationId != 0).OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (product == null || store == null) { logger.LogWarning("VERIFY: no linked product/store"); return; }

        const int testQty = 42;
        var ctx = new { inventory_mode = true };
        logger.LogInformation("VERIFY: '{Name}' (variant {Vid}) @ {Store} (loc {Loc}) → set Odoo on-hand = {Q}",
            product.Name, product.OdooProductId, store.Name, store.OdooStockLocationId, testQty);

        // Find the existing quant for this product+location and set its counted qty.
        var quants = await odoo.SearchReadAsync("stock.quant",
            new object[] {
                new object[] { "product_id", "=", product.OdooProductId },
                new object[] { "location_id", "=", store.OdooStockLocationId }
            },
            new[] { "id" }, 1);

        int quantId;
        if (quants.Count > 0)
        {
            quantId = quants[0]["id"].GetInt32();
            await odoo.WriteAsync("stock.quant", new[] { quantId }, new() { ["inventory_quantity"] = testQty }, ctx);
        }
        else
        {
            var ids = await odoo.CreateManyAsync("stock.quant", new[] {
                new Dictionary<string, object?> {
                    ["product_id"] = product.OdooProductId,
                    ["location_id"] = store.OdooStockLocationId,
                    ["inventory_quantity"] = testQty
                }}, ctx);
            quantId = ids[0];
        }
        await odoo.ExecuteAsync("stock.quant", "action_apply_inventory", new object[] { new[] { quantId } }, ctx);

        // Pull from Odoo into the website.
        await inventory.SyncProductInventoryAsync(new[] { product.OdooProductId });

        var si = await db.StoreInventories.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProductId == product.Id && x.StoreId == store.Id);

        logger.LogInformation("VERIFY: website StoreInventory on-hand = {Qty} (expected {Exp}) → {Result}",
            si?.QuantityOnHand, testQty, si?.QuantityOnHand == testQty ? "SYNC OK" : "MISMATCH");
    }
}
