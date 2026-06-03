using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_CLEANUP_TESTORDERS=1): cancels and deletes the sale orders created by our
/// test/sim commands (notes containing "TEST-" or "SIM checkout"), releasing their reserved stock.
/// </summary>
public static class OdooCleanupTestOrders
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var orders = await odoo.SearchReadAsync("sale.order",
            new object[] { "|", new object[] { "note", "ilike", "TEST-" }, new object[] { "note", "ilike", "SIM checkout" } },
            new[] { "id", "name", "state" });

        if (orders.Count == 0) { logger.LogInformation("CLEANUP: no test orders found"); return; }

        var ids = orders.Select(o => o["id"].GetInt32()).ToArray();
        logger.LogInformation("CLEANUP: found {Count} test order(s): {Names}", ids.Length,
            string.Join(", ", orders.Select(o => o.GetValueOrDefault("name").ToString())));

        try
        {
            await odoo.ExecuteAsync("sale.order", "action_cancel", new object[] { ids });
            logger.LogInformation("CLEANUP: cancelled (reserved stock released)");
        }
        catch (Exception ex) { logger.LogWarning("CLEANUP: cancel failed: {Msg}", ex is OdooException oe ? oe.Message : ex.Message); }

        try
        {
            await odoo.ExecuteAsync("sale.order", "unlink", new object[] { ids });
            logger.LogInformation("CLEANUP: deleted {Count} test order(s)", ids.Length);
        }
        catch (Exception ex) { logger.LogWarning("CLEANUP: delete failed (they're cancelled, delete manually if needed): {Msg}", ex is OdooException oe ? oe.Message : ex.Message); }

        logger.LogInformation("CLEANUP: done.");
    }
}
