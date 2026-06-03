using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_ROUTE_FIX=1): inspects each store warehouse's routing and forces direct
/// 1-step delivery from its own stock (delivery_steps = ship_only), clearing any
/// inter-warehouse resupply — so confirming a web order ships from the store, not via transit.
/// </summary>
public static class OdooRouteFix
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var whIds = await db.Stores.Where(s => s.OdooWarehouseId != 0)
            .Select(s => s.OdooWarehouseId).ToListAsync();

        var before = await odoo.SearchReadAsync("stock.warehouse",
            new object[] { new object[] { "id", "in", whIds } },
            new[] { "id", "name", "delivery_steps", "reception_steps", "resupply_wh_ids" });
        foreach (var w in before)
            logger.LogInformation("ROUTE_FIX(before): wh={Id} '{Name}' delivery={Del} reception={Rec} resupply={Res}",
                w.GetValueOrDefault("id"), w.GetValueOrDefault("name"), w.GetValueOrDefault("delivery_steps"),
                w.GetValueOrDefault("reception_steps"), w.GetValueOrDefault("resupply_wh_ids"));

        foreach (var w in before)
        {
            var id = w["id"].GetInt32();
            try
            {
                await odoo.WriteAsync("stock.warehouse", new[] { id }, new()
                {
                    ["delivery_steps"] = "ship_only",
                    ["reception_steps"] = "one_step",
                    ["resupply_wh_ids"] = new object[] { new object[] { 6, 0, Array.Empty<int>() } }, // clear
                });
                logger.LogInformation("ROUTE_FIX: warehouse {Id} set to ship_only / one_step, resupply cleared", id);
            }
            catch (Exception ex)
            {
                logger.LogWarning("ROUTE_FIX: failed for warehouse {Id}: {Msg}",
                    id, ex is OdooException oe ? oe.Message : ex.Message);
            }
        }

        logger.LogInformation("ROUTE_FIX: done.");
    }
}
