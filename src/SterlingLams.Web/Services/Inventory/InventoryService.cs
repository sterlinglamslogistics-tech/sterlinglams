using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Services.Inventory;

public interface IInventoryService
{
    Task<Dictionary<int, int>> GetStoreInventoryForProductAsync(int odooProductId);
    Task SyncProductInventoryAsync(int[] odooProductIds);
    Task SyncAllAsync();
    Task<bool> IsAvailableInStoreAsync(int odooProductId, int storeId, int requiredQty = 1);
}

public class InventoryService : IInventoryService
{
    private readonly IOdooService _odoo;
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly OdooSettings _odooSettings;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(
        IOdooService odoo,
        ApplicationDbContext db,
        IMemoryCache cache,
        OdooSettings odooSettings,
        ILogger<InventoryService> logger)
    {
        _odoo = odoo;
        _db = db;
        _cache = cache;
        _odooSettings = odooSettings;
        _logger = logger;
    }

    /// <summary>Returns storeId → availableQty for a given Odoo product.</summary>
    /// <summary>Returns storeId → available qty for a single Odoo product (variant) id.</summary>
    public async Task<Dictionary<int, int>> GetStoreInventoryForProductAsync(int odooProductId)
    {
        var cacheKey = $"inventory:product:{odooProductId}";

        if (_cache.TryGetValue(cacheKey, out Dictionary<int, int>? cached) && cached != null)
            return cached;

        var storeByLocation = await _db.Stores
            .Where(s => s.OdooStockLocationId != 0)
            .ToDictionaryAsync(s => s.OdooStockLocationId, s => s.Id);

        var result = new Dictionary<int, int>();
        if (storeByLocation.Count > 0)
        {
            var quants = await _odoo.GetStockQuantsAsync(new[] { odooProductId }, storeByLocation.Keys.ToArray());
            foreach (var q in quants)
            {
                if (!storeByLocation.TryGetValue(q.LocationOdooId, out var storeId)) continue;
                var available = (int)Math.Max(0, q.Quantity - q.ReservedQuantity);
                result[storeId] = result.TryGetValue(storeId, out var cur) ? cur + available : available;
            }
        }

        _cache.Set(cacheKey, result, TimeSpan.FromSeconds(_odooSettings.InventoryCacheTtlSeconds));
        return result;
    }

    /// <summary>Syncs stock from Odoo (per store location) into the local StoreInventory.</summary>
    public async Task SyncProductInventoryAsync(int[] odooProductIds)
    {
        try
        {
            var stores = await _db.Stores.Where(s => s.OdooStockLocationId != 0).ToListAsync();
            if (stores.Count == 0) return;
            var storeByLocation = stores.ToDictionary(s => s.OdooStockLocationId, s => s);

            var products = await _db.Products
                .Where(p => odooProductIds.Contains(p.OdooProductId))
                .ToListAsync();
            var productByOdoo = products.ToDictionary(p => p.OdooProductId, p => p);

            var quants = await _odoo.GetStockQuantsAsync(odooProductIds, storeByLocation.Keys.ToArray());

            foreach (var q in quants)
            {
                if (!productByOdoo.TryGetValue(q.ProductOdooId, out var product)) continue;
                if (!storeByLocation.TryGetValue(q.LocationOdooId, out var store)) continue;

                var onHand = (int)Math.Max(0, q.Quantity);
                var reserved = (int)Math.Max(0, q.ReservedQuantity);

                var existing = await _db.StoreInventories
                    .FirstOrDefaultAsync(si => si.ProductId == product.Id && si.StoreId == store.Id);

                if (existing != null)
                {
                    existing.QuantityOnHand = onHand;
                    existing.QuantityReserved = reserved;
                    existing.LastSyncedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.StoreInventories.Add(new StoreInventory
                    {
                        ProductId = product.Id,
                        StoreId = store.Id,
                        QuantityOnHand = onHand,
                        QuantityReserved = reserved,
                        LastSyncedAt = DateTime.UtcNow
                    });
                }
            }

            await _db.SaveChangesAsync();

            foreach (var id in odooProductIds)
                _cache.Remove($"inventory:product:{id}");

            _logger.LogInformation("Synced inventory for {Count} products", odooProductIds.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync inventory for products {Ids}", string.Join(",", odooProductIds));
            throw;
        }
    }

    public async Task<bool> IsAvailableInStoreAsync(int odooProductId, int storeId, int requiredQty = 1)
    {
        var inventory = await GetStoreInventoryForProductAsync(odooProductId);
        return inventory.TryGetValue(storeId, out var qty) && qty >= requiredQty;
    }

    /// <summary>Syncs all active products' inventory from Odoo in batches.</summary>
    public async Task SyncAllAsync()
    {
        var odooProductIds = await _db.Products
            .Where(p => p.IsActive && p.OdooProductId != 0)
            .Select(p => p.OdooProductId)
            .ToArrayAsync();

        if (odooProductIds.Length == 0) return;

        foreach (var batch in odooProductIds.Chunk(50))
            await SyncProductInventoryAsync(batch);
    }
}
