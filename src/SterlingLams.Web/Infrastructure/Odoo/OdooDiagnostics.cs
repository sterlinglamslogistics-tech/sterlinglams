using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off Odoo connectivity check (triggered by ODOO_DIAG=1). Verifies authentication
/// and prints the warehouses + their stock locations and a product sample — exactly the
/// ids needed to configure Odoo:Stores (warehouse id) and stock-location mapping.
/// </summary>
public static class OdooDiagnostics
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        try
        {
            var uid = await odoo.AuthenticateAsync();
            logger.LogInformation("ODOO_DIAG: ✔ authenticated. uid={Uid}", uid);

            // Warehouses (use warehouse id for sale.order.warehouse_id; lot_stock_id is its stock location)
            var warehouses = await odoo.SearchReadAsync(
                "stock.warehouse", Array.Empty<object>(), new[] { "id", "name", "code", "lot_stock_id" });
            logger.LogInformation("ODOO_DIAG: {Count} warehouse(s):", warehouses.Count);
            foreach (var w in warehouses)
                logger.LogInformation("  warehouse → id={Id} name={Name} code={Code} lot_stock_id={Loc}",
                    Get(w, "id"), Get(w, "name"), Get(w, "code"), Get(w, "lot_stock_id"));

            // Internal stock locations (stock.quant.location_id references these)
            var locations = await odoo.SearchReadAsync(
                "stock.location",
                new object[] { new object[] { "usage", "=", "internal" } },
                new[] { "id", "complete_name", "warehouse_id" });
            logger.LogInformation("ODOO_DIAG: {Count} internal location(s):", locations.Count);
            foreach (var l in locations)
                logger.LogInformation("  location → id={Id} name={Name} warehouse_id={Wh}",
                    Get(l, "id"), Get(l, "complete_name"), Get(l, "warehouse_id"));

            // Product sample (confirms catalog access + shows SKU/barcode for linking)
            var products = await odoo.SearchReadAsync(
                "product.template",
                new object[] { new object[] { "active", "=", true } },
                new[] { "id", "name", "default_code", "barcode" }, limit: 5);
            logger.LogInformation("ODOO_DIAG: product sample ({Count} shown):", products.Count);
            foreach (var p in products)
                logger.LogInformation("  product → id={Id} sku={Sku} barcode={Bc} name={Name}",
                    Get(p, "id"), Get(p, "default_code"), Get(p, "barcode"), Get(p, "name"));

            logger.LogInformation("ODOO_DIAG: done. Use warehouse 'id' for Odoo:Stores, and the matching " +
                "stock location 'id' for stock sync.");
        }
        catch (OdooException oe)
        {
            logger.LogError("ODOO_DIAG: FAILED. Message={Message}\n----- Odoo debug/traceback -----\n{Debug}",
                oe.Message, oe.Debug ?? "(none)");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ODOO_DIAG: FAILED — check BaseUrl/Database/Username/ApiKey and that the API key is valid.");
        }
    }

    private static string Get(Dictionary<string, System.Text.Json.JsonElement> row, string key)
        => row.TryGetValue(key, out var v) ? v.ToString() : "—";
}
