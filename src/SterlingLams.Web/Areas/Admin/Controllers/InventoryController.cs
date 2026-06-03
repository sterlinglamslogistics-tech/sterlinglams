using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class InventoryController : AdminBaseController
    {
        private const int PageSize = 50;

        private readonly ApplicationDbContext _db;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<InventoryController> _logger;

        public InventoryController(ApplicationDbContext db, IServiceScopeFactory scopeFactory, ILogger<InventoryController> logger)
        {
            _db = db;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<IActionResult> Index(int? storeId, string q = "", int page = 1)
        {
            ViewData["Title"] = "Inventory";

            var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
            var selectedStoreId = storeId ?? stores.FirstOrDefault()?.Id;
            if (page < 1) page = 1;

            var query = _db.StoreInventories
                .Where(si => si.Product.IsActive);

            if (selectedStoreId.HasValue)
                query = query.Where(si => si.StoreId == selectedStoreId.Value);

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(si =>
                    EF.Functions.ILike(si.Product.Name, $"%{q}%") ||
                    (si.Product.Sku != null && EF.Functions.ILike(si.Product.Sku, $"%{q}%")));

            var total = await query.CountAsync();
            var rows = await query
                .OrderBy(si => si.Product.Name)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(si => new InventoryProductRow
                {
                    ProductId = si.ProductId,
                    ProductName = si.Product.Name,
                    StoreName = si.Store.Name,
                    Sku = si.Product.Sku ?? "",
                    QuantityOnHand = si.QuantityOnHand,
                    QuantityReserved = si.QuantityReserved
                })
                .ToListAsync();

            var lastSync = await _db.StoreInventories.MaxAsync(si => (DateTime?)si.LastSyncedAt);

            var vm = new AdminInventoryViewModel
            {
                Stores = stores,
                SelectedStoreId = selectedStoreId,
                SearchQuery = q,
                Rows = rows,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize),
                LastSyncedAt = lastSync
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Sync()
        {
            // Stock sync hits Odoo for ~1k products; run it in the background so the
            // request returns immediately instead of timing out.
            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                try { await inventory.SyncAllAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Background inventory sync failed"); }
            });

            TempData["Success"] = "Inventory sync started in the background. Refresh in a moment to see updated quantities.";
            return RedirectToAction(nameof(Index));
        }
    }
}
