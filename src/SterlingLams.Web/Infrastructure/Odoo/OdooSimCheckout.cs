using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_SIM_CHECKOUT=1): simulates the customer checkout's backend (create customer,
/// create sale order in the store's warehouse, confirm it) and prints the product's Odoo stock
/// before vs after to prove a paid web order deducts (reserves) stock in Odoo.
/// Leaves a confirmed test order you can cancel in Odoo.
/// </summary>
public static class OdooSimCheckout
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var store = await db.Stores.Where(s => s.OdooWarehouseId != 0 && s.OdooStockLocationId != 0).OrderBy(s => s.Id).FirstAsync();
        var product = await db.Products.Where(p => p.OdooProductId != 0).OrderBy(p => p.Id).FirstAsync();
        const int qty = 1;

        var (oh0, rsv0) = await ReadAsync(odoo, product.OdooProductId, store.OdooStockLocationId);
        logger.LogInformation("SIM: '{P}' @ {S} (loc {L}) BEFORE  on-hand={Oh} reserved={Rsv} available={Av}",
            product.Name, store.Name, store.OdooStockLocationId, oh0, rsv0, oh0 - rsv0);

        var partnerId = await odoo.FindOrCreatePartnerAsync("sim.checkout@sterlinglams.com", "Sim Checkout");
        var soId = await odoo.CreateSaleOrderAsync(new CreateSaleOrderRequest
        {
            OdooPartnerId = partnerId,
            OdooWarehouseId = store.OdooWarehouseId,
            Note = $"SIM checkout {DateTime.UtcNow:HHmmss}",
            Lines = new() { new SaleOrderLine { OdooProductId = product.OdooProductId, Quantity = qty, PriceUnit = product.Price } }
        });
        logger.LogInformation("SIM: created sale.order id={Id}; confirming…", soId);
        await odoo.ConfirmSaleOrderAsync(soId);

        var (oh1, rsv1) = await ReadAsync(odoo, product.OdooProductId, store.OdooStockLocationId);
        logger.LogInformation("SIM: '{P}' @ {S}            AFTER   on-hand={Oh} reserved={Rsv} available={Av}",
            product.Name, store.Name, oh1, rsv1, oh1 - rsv1);

        var availBefore = oh0 - rsv0;
        var availAfter = oh1 - rsv1;
        logger.LogInformation("SIM: available {Before} -> {After} (ordered {Qty}) => {Result}",
            availBefore, availAfter, qty, availAfter == availBefore - qty ? "STOCK DEDUCTED OK" : "UNEXPECTED — check");
    }

    private static async Task<(int onHand, int reserved)> ReadAsync(IOdooService odoo, int variantId, int locationId)
    {
        var quants = await odoo.SearchReadAsync("stock.quant",
            new object[] { new object[] { "product_id", "=", variantId }, new object[] { "location_id", "=", locationId } },
            new[] { "quantity", "reserved_quantity" });
        int oh = 0, rsv = 0;
        foreach (var q in quants)
        {
            oh += (int)(q.TryGetValue("quantity", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? a.GetDouble() : 0);
            rsv += (int)(q.TryGetValue("reserved_quantity", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number ? b.GetDouble() : 0);
        }
        return (oh, rsv);
    }
}
