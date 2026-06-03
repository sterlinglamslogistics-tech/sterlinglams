using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_TEST_ORDER=1): creates and confirms a test sale order in Odoo via the same
/// service the checkout uses, to validate the website→Odoo order flow and warehouse routing.
/// Leaves a confirmed "Sterlin Glams Web Order: TEST-..." order you can cancel in Odoo.
/// </summary>
public static class OdooTestOrder
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var store = await db.Stores.Where(s => s.OdooWarehouseId != 0).OrderBy(s => s.Id).FirstAsync();
        var products = await db.Products.Where(p => p.OdooProductId != 0).OrderBy(p => p.Id).Take(2).ToListAsync();
        if (products.Count == 0) { logger.LogWarning("ODOO_TEST_ORDER: no linked products"); return; }

        var partnerId = await odoo.FindOrCreatePartnerAsync("test.customer@sterlinglams.com", "Test Customer");
        logger.LogInformation("ODOO_TEST_ORDER: using customer partner id={Pid}", partnerId);
        var req = new CreateSaleOrderRequest
        {
            OdooPartnerId = partnerId,
            OdooWarehouseId = store.OdooWarehouseId,
            Note = $"Sterlin Glams Web Order: TEST-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Lines = products.Select(p => new SaleOrderLine
            {
                OdooProductId = p.OdooProductId,
                Quantity = 1,
                PriceUnit = p.Price,
            }).ToList()
        };

        logger.LogInformation("ODOO_TEST_ORDER: creating SO at warehouse {Wh} ({Store}) with {N} line(s)...",
            store.OdooWarehouseId, store.Name, req.Lines.Count);

        var orderId = await odoo.CreateSaleOrderAsync(req);
        logger.LogInformation("ODOO_TEST_ORDER: created sale.order id={Id}", orderId);

        var before = await odoo.SearchReadAsync("sale.order",
            new object[] { new object[] { "id", "=", orderId } },
            new[] { "name", "state", "amount_total", "warehouse_id" }, 1);
        if (before.Count > 0)
            logger.LogInformation("ODOO_TEST_ORDER: name={Name} state={State} total={Total} warehouse={Wh}",
                before[0].GetValueOrDefault("name"), before[0].GetValueOrDefault("state"),
                before[0].GetValueOrDefault("amount_total"), before[0].GetValueOrDefault("warehouse_id"));

        try
        {
            var confirmed = await odoo.ConfirmSaleOrderAsync(orderId);
            var after = await odoo.SearchReadAsync("sale.order",
                new object[] { new object[] { "id", "=", orderId } }, new[] { "name", "state" }, 1);
            logger.LogInformation("ODOO_TEST_ORDER: confirmed={Result}, state={State}",
                confirmed, after.Count > 0 ? after[0].GetValueOrDefault("state").ToString() : "?");
        }
        catch (OdooException oe)
        {
            logger.LogWarning("ODOO_TEST_ORDER: order CREATED ok but auto-confirm failed: {Msg}\n--- debug ---\n{Debug}",
                oe.Message, oe.Debug ?? "(none)");
        }
        catch (Exception ex)
        {
            logger.LogWarning("ODOO_TEST_ORDER: order CREATED ok but auto-confirm failed: {Msg}", ex.Message);
        }

        logger.LogInformation("ODOO_TEST_ORDER: done. Check Odoo → Sales for this order.");
    }
}
