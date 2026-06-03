using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>One-off (ODOO_INSPECT=1): dumps stock + routing for the first linked product so
/// we can see why confirming a sale order reaches for inter-warehouse transit.</summary>
public static class OdooInspectRoutes
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var product = await db.Products.Where(p => p.OdooProductId != 0).OrderBy(p => p.Id).FirstAsync();
        var stores = await db.Stores.Where(s => s.OdooStockLocationId != 0).OrderBy(s => s.Id).ToListAsync();
        int variantId = product.OdooProductId;
        logger.LogInformation("INSPECT: product '{Name}' variant={Vid}", product.Name, variantId);

        // 1) Quants for this variant at each store location
        foreach (var s in stores)
        {
            var q = await odoo.SearchReadAsync("stock.quant",
                new object[] { new object[] { "product_id", "=", variantId }, new object[] { "location_id", "=", s.OdooStockLocationId } },
                new[] { "location_id", "quantity", "reserved_quantity", "available_quantity" });
            logger.LogInformation("INSPECT: {Store} (loc {Loc}) quants = {Q}", s.Name, s.OdooStockLocationId,
                q.Count == 0 ? "(none)" : string.Join(" | ", q.Select(r => $"qty={r.GetValueOrDefault("quantity")} reserved={r.GetValueOrDefault("reserved_quantity")} avail={r.GetValueOrDefault("available_quantity")}")));
        }

        // 2) Warehouse detail
        var wh = await odoo.SearchReadAsync("stock.warehouse",
            new object[] { new object[] { "id", "=", stores[0].OdooWarehouseId } },
            new[] { "name", "lot_stock_id", "delivery_steps", "delivery_route_id", "view_location_id" }, 1);
        if (wh.Count > 0)
            logger.LogInformation("INSPECT: warehouse {Name} lot_stock={Lot} delivery_steps={Del} delivery_route={Route} view_loc={View}",
                wh[0].GetValueOrDefault("name"), wh[0].GetValueOrDefault("lot_stock_id"), wh[0].GetValueOrDefault("delivery_steps"),
                wh[0].GetValueOrDefault("delivery_route_id"), wh[0].GetValueOrDefault("view_location_id"));

        // 3) Product template routes
        var pp = await odoo.SearchReadAsync("product.product",
            new object[] { new object[] { "id", "=", variantId } }, new[] { "product_tmpl_id", "route_ids" }, 1);
        var tmplId = pp.Count > 0 ? Many2OneId(pp[0], "product_tmpl_id") : 0;
        var routeIds = pp.Count > 0 ? IdList(pp[0], "route_ids") : new List<int>();
        logger.LogInformation("INSPECT: template={Tmpl} product route_ids={Routes}", tmplId, string.Join(",", routeIds));

        // 4) Names of the warehouse delivery route's rules
        var delRouteId = wh.Count > 0 ? Many2OneId(wh[0], "delivery_route_id") : 0;
        if (delRouteId > 0)
        {
            var route = await odoo.SearchReadAsync("stock.route",
                new object[] { new object[] { "id", "=", delRouteId } }, new[] { "name", "rule_ids" }, 1);
            var ruleIds = route.Count > 0 ? IdList(route[0], "rule_ids") : new List<int>();
            logger.LogInformation("INSPECT: delivery route '{Name}' rules={Rules}",
                route.Count > 0 ? route[0].GetValueOrDefault("name").ToString() : "?", string.Join(",", ruleIds));
            if (ruleIds.Count > 0)
            {
                var rules = await odoo.SearchReadAsync("stock.rule",
                    new object[] { new object[] { "id", "in", ruleIds } },
                    new[] { "name", "location_src_id", "location_dest_id", "action", "procure_method" });
                foreach (var r in rules)
                    logger.LogInformation("INSPECT:   rule '{Name}': {Src} -> {Dst} action={Act} procure={Proc}",
                        r.GetValueOrDefault("name"), r.GetValueOrDefault("location_src_id"),
                        r.GetValueOrDefault("location_dest_id"), r.GetValueOrDefault("action"), r.GetValueOrDefault("procure_method"));
            }
        }

        logger.LogInformation("INSPECT: done.");
    }

    private static int Many2OneId(Dictionary<string, JsonElement> row, string key)
        => row.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? v[0].GetInt32() : 0;

    private static List<int> IdList(Dictionary<string, JsonElement> row, string key)
        => row.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToList()
            : new List<int>();
}
