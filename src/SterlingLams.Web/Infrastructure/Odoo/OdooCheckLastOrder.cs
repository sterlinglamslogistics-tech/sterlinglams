using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Inventory;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>One-off (ODOO_CHECK_LASTORDER=1): for the most recent order, shows the Odoo sale-order
/// state + Odoo reserved stock, then syncs and shows the website's StoreInventory — to confirm a
/// paid order reserves stock in Odoo and the site reflects it after sync.</summary>
public static class OdooCheckLastOrder
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.Product)
            .OrderByDescending(o => o.Id).FirstOrDefaultAsync();
        if (order == null) { logger.LogWarning("CHECK: no orders"); return; }

        var storeId = order.PickupStoreId ?? (await db.Stores.Where(s => s.IsActive).OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync());
        var store = storeId.HasValue ? await db.Stores.FindAsync(storeId.Value) : null;
        logger.LogInformation("CHECK: order {Num} paid={Paid} odooSO={SO} store={Store} (loc {Loc})",
            order.OrderNumber, order.IsPaid, order.OdooSaleOrderId, store?.Name, store?.OdooStockLocationId);

        if (order.OdooSaleOrderId is int so && so > 0)
        {
            var soRows = await odoo.SearchReadAsync("sale.order", new object[] { new object[] { "id", "=", so } }, new[] { "name", "state" }, 1);
            logger.LogInformation("CHECK: Odoo SO {Name} state={State}",
                soRows.Count > 0 ? soRows[0].GetValueOrDefault("name") : "?", soRows.Count > 0 ? soRows[0].GetValueOrDefault("state") : "?");
        }

        var variantIds = order.Items.Select(i => i.Product.OdooProductId).Where(v => v != 0).Distinct().ToArray();
        if (store?.OdooStockLocationId is int loc && loc > 0)
        {
            foreach (var item in order.Items)
            {
                var q = await odoo.SearchReadAsync("stock.quant",
                    new object[] { new object[] { "product_id", "=", item.Product.OdooProductId }, new object[] { "location_id", "=", loc } },
                    new[] { "quantity", "reserved_quantity" }, 1);
                var oh = q.Count > 0 && q[0]["quantity"].TryGetDouble(out var a) ? a : 0;
                var rsv = q.Count > 0 && q[0]["reserved_quantity"].TryGetDouble(out var b) ? b : 0;
                logger.LogInformation("CHECK: Odoo  {Name}: on-hand={Oh} reserved={Rsv} available={Av}",
                    item.ProductName, oh, rsv, oh - rsv);
            }
        }

        logger.LogInformation("CHECK: syncing website from Odoo…");
        await inventory.SyncProductInventoryAsync(variantIds);

        foreach (var item in order.Items)
        {
            var si = await db.StoreInventories.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProductId == item.ProductId && x.StoreId == storeId);
            logger.LogInformation("CHECK: site  {Name}: on-hand={Oh} reserved={Rsv} available={Av}",
                item.ProductName, si?.QuantityOnHand, si?.QuantityReserved, si == null ? 0 : si.AvailableQuantity);
        }
        logger.LogInformation("CHECK: done.");
    }
}
