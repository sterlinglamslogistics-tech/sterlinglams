using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_PROVISION=1): ensures each active website store has a matching Odoo
/// warehouse, and records the warehouse id + its stock location id (lot_stock_id) on the
/// Store. Idempotent — existing warehouses (matched by name) are reused.
/// </summary>
public static class OdooProvisionStores
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var stores = await db.Stores.Where(s => s.IsActive).OrderBy(s => s.Id).ToListAsync();
        if (stores.Count == 0) { logger.LogWarning("ODOO_PROVISION: no active stores"); return; }

        // Existing warehouse codes (must be unique, <=5 chars).
        var existing = await odoo.SearchReadAsync("stock.warehouse", Array.Empty<object>(), new[] { "id", "name", "code" });
        var usedCodes = existing
            .Where(w => w.TryGetValue("code", out var c) && c.ValueKind == JsonValueKind.String)
            .Select(w => w["code"].GetString()!.ToUpperInvariant())
            .ToHashSet();

        foreach (var store in stores)
        {
            // Reuse a warehouse with the same name if present.
            var match = existing.FirstOrDefault(w =>
                w.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String &&
                string.Equals(n.GetString(), store.Name, StringComparison.OrdinalIgnoreCase));

            int warehouseId;
            if (match != null)
            {
                warehouseId = match["id"].GetInt32();
                logger.LogInformation("ODOO_PROVISION: reusing warehouse '{Name}' (id={Id})", store.Name, warehouseId);
            }
            else
            {
                var code = MakeCode(store.Name, usedCodes);
                usedCodes.Add(code);
                warehouseId = await odoo.CreateAsync("stock.warehouse", new()
                {
                    ["name"] = store.Name,
                    ["code"] = code,
                });
                logger.LogInformation("ODOO_PROVISION: created warehouse '{Name}' (id={Id}, code={Code})", store.Name, warehouseId, code);
            }

            // Read its stock location (lot_stock_id).
            var wh = await odoo.SearchReadAsync(
                "stock.warehouse", new object[] { new object[] { "id", "=", warehouseId } }, new[] { "lot_stock_id" }, 1);
            var locId = wh.Count > 0 ? Many2OneId(wh[0], "lot_stock_id") : 0;

            store.OdooWarehouseId = warehouseId;
            store.OdooStockLocationId = locId;
            logger.LogInformation("ODOO_PROVISION: store '{Name}' → warehouse={Wh}, stock location={Loc}",
                store.Name, warehouseId, locId);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("ODOO_PROVISION: done. {Count} store(s) mapped.", stores.Count);
    }

    private static string MakeCode(string name, HashSet<string> used)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (letters.Length == 0) letters = "ST";
        var baseCode = letters.Length <= 5 ? letters : letters[..5];
        var code = baseCode;
        var n = 1;
        while (used.Contains(code))
            code = (baseCode.Length <= 4 ? baseCode : baseCode[..4]) + n++;
        return code;
    }

    private static int Many2OneId(Dictionary<string, JsonElement> row, string key)
        => row.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
            ? v[0].GetInt32()
            : 0;
}
