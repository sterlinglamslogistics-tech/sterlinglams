using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class ProductsController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ProductsController> _logger;
        private const int PageSize = 30;

        public ProductsController(
            ApplicationDbContext db,
            IWebHostEnvironment env,
            IServiceScopeFactory scopeFactory,
            ILogger<ProductsController> logger)
        {
            _db = db;
            _env = env;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<IActionResult> Index(string q = "", int page = 1)
        {
            ViewData["Title"] = "Products";

            var query = _db.Products
                .Include(p => p.Category)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%"));

            var total = await query.CountAsync();
            var products = await query
                .OrderBy(p => p.Name)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            var vm = new AdminProductListViewModel
            {
                Products = products,
                SearchQuery = q,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize)
            };

            return View(vm);
        }

        public async Task<IActionResult> Create()
        {
            ViewData["Title"] = "New Product";
            var vm = new AdminProductEditViewModel
            {
                Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync()
            };
            return View("Edit", vm);
        }

        public async Task<IActionResult> Edit(int id)
        {
            ViewData["Title"] = "Edit Product";

            var product = await _db.Products
                .Include(p => p.Images)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var vm = new AdminProductEditViewModel
            {
                Id = product.Id,
                Name = product.Name,
                Slug = product.Slug,
                Description = product.Description ?? "",
                Price = product.Price,
                Material = product.Material,
                Carat = product.Carat,
                GemstoneType = product.GemstoneType,
                IsActive = product.IsActive,
                IsFeatured = product.IsFeatured,
                OdooProductId = product.OdooProductId == 0 ? null : product.OdooProductId,
                CategoryId = product.CategoryId,
                Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
                ExistingImages = product.Images.OrderBy(i => i.SortOrder).ToList()
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AdminProductEditViewModel vm)
        {
            // Re-show the edit form with dropdown/image state repopulated.
            async Task<IActionResult> RedisplayAsync()
            {
                vm.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
                vm.ExistingImages = await _db.Set<ProductImage>()
                    .Where(i => i.ProductId == vm.Id).OrderBy(i => i.SortOrder).ToListAsync();
                ViewData["Title"] = vm.Id == 0 ? "New Product" : "Edit Product";
                return View("Edit", vm);
            }

            if (!ModelState.IsValid)
                return await RedisplayAsync();

            // Validate the selected category actually exists.
            if (!await _db.Categories.AnyAsync(c => c.Id == vm.CategoryId))
            {
                ModelState.AddModelError(nameof(vm.CategoryId), "The selected category no longer exists.");
                return await RedisplayAsync();
            }

            Product product;
            if (vm.Id == 0)
            {
                product = new Product();
                _db.Products.Add(product);
            }
            else
            {
                var existing = await _db.Products.FindAsync(vm.Id);
                if (existing == null) return NotFound();
                product = existing;
            }

            product.Name = vm.Name.Trim();
            product.Slug = string.IsNullOrWhiteSpace(vm.Slug)
                ? Regex.Replace(vm.Name.ToLower().Trim(), @"[^a-z0-9]+", "-").Trim('-')
                : vm.Slug.Trim();

            // Slug uniqueness check
            if (await _db.Products.AnyAsync(p => p.Slug == product.Slug && p.Id != vm.Id))
            {
                ModelState.AddModelError(nameof(vm.Slug), "This slug is already used by another product. Choose a different one.");
                return await RedisplayAsync();
            }

            // Odoo ID is optional (0 = not linked); enforce uniqueness only when supplied.
            if (vm.OdooProductId is int odooId && odooId != 0
                && await _db.Products.AnyAsync(p => p.OdooProductId == odooId && p.Id != vm.Id))
            {
                ModelState.AddModelError(nameof(vm.OdooProductId), "Another product is already linked to this Odoo Product ID.");
                return await RedisplayAsync();
            }

            product.Description = vm.Description;
            product.Price = vm.Price;
            product.Material = vm.Material;
            product.Carat = vm.Carat;
            product.GemstoneType = vm.GemstoneType;
            product.IsActive = vm.IsActive;
            product.IsFeatured = vm.IsFeatured;
            product.OdooProductId = vm.OdooProductId ?? 0;
            product.CategoryId = vm.CategoryId!.Value;
            product.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Failed to save product '{Name}'", vm.Name);
                ModelState.AddModelError("", "Could not save the product. Please check your input and try again.");
                return await RedisplayAsync();
            }

            // ─── Image upload ─────────────────────────────────────────────
            if (vm.ImageFile is { Length: > 0 })
            {
                var ext = Path.GetExtension(vm.ImageFile.FileName).ToLowerInvariant();
                var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp" };
                if (!allowed.Contains(ext))
                {
                    ModelState.AddModelError(nameof(vm.ImageFile), "Only JPG, PNG, and WEBP images are allowed.");
                    return await RedisplayAsync();
                }

                if (vm.ImageFile.Length > 5 * 1024 * 1024)
                {
                    ModelState.AddModelError(nameof(vm.ImageFile), "Image must be 5 MB or smaller.");
                    return await RedisplayAsync();
                }

                var uploadDir = Path.Combine(_env.WebRootPath, "images", "products", product.Id.ToString());
                Directory.CreateDirectory(uploadDir);

                var fileName = $"{Guid.NewGuid()}{ext}";
                var filePath = Path.Combine(uploadDir, fileName);

                await using (var stream = System.IO.File.Create(filePath))
                    await vm.ImageFile.CopyToAsync(stream);

                var isPrimary = !await _db.Set<ProductImage>().AnyAsync(i => i.ProductId == product.Id && i.IsPrimary);
                _db.Set<ProductImage>().Add(new ProductImage
                {
                    ProductId = product.Id,
                    Url = $"/images/products/{product.Id}/{fileName}",
                    AltText = product.Name,
                    IsPrimary = isPrimary,
                    SortOrder = await _db.Set<ProductImage>().CountAsync(i => i.ProductId == product.Id)
                });

                await _db.SaveChangesAsync();
            }

            TempData["Success"] = $"Product '{product.Name}' saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.IsActive = !product.IsActive;
            product.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            TempData["Success"] = $"'{product.Name}' is now {(product.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SyncFromOdoo()
        {
            // Runs against ~1k products; do it in the background so the request returns fast.
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

        // ─── Variants ─────────────────────────────────────────────────────────
        public async Task<IActionResult> Variants(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();
            ViewData["Title"] = $"Variants — {product.Name}";

            var attributes = await _db.ProductAttributes.Include(a => a.Values)
                .OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ToListAsync();
            var variants = await _db.ProductVariants.Include(v => v.Values)
                .Where(v => v.ProductId == id).OrderBy(v => v.Name).ToListAsync();

            var stockByVariant = await _db.StoreInventories
                .Where(si => si.ProductId == id && si.ProductVariantId != 0)
                .GroupBy(si => si.ProductVariantId)
                .Select(g => new { Vid = g.Key, Qty = g.Sum(x => x.QuantityOnHand) })
                .ToDictionaryAsync(x => x.Vid, x => x.Qty);

            var vm = new AdminProductVariantsViewModel
            {
                ProductId = id,
                ProductName = product.Name,
                BasePrice = product.Price,
                Attributes = attributes,
                SelectedValueIds = variants.SelectMany(v => v.Values.Select(vv => vv.ProductAttributeValueId)).ToHashSet(),
                Variants = variants.Select(v => new AdminVariantRow
                {
                    Id = v.Id,
                    Name = v.Name,
                    Sku = v.Sku,
                    PriceAdjustment = v.PriceAdjustment ?? 0,
                    IsActive = v.IsActive,
                    OdooVariantId = v.OdooVariantId,
                    Stock = stockByVariant.TryGetValue(v.Id, out var q) ? q : 0
                }).ToList()
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateVariants(int id, int[] valueIds)
        {
            if (await _db.Products.FindAsync(id) == null) return NotFound();
            valueIds ??= System.Array.Empty<int>();

            var values = await _db.ProductAttributeValues.Include(v => v.Attribute)
                .Where(v => valueIds.Contains(v.Id)).ToListAsync();
            if (values.Count == 0)
            {
                TempData["Error"] = "Select at least one value to generate variants.";
                return RedirectToAction(nameof(Variants), new { id });
            }

            // Cartesian product across the chosen attributes.
            var groups = values.GroupBy(v => v.ProductAttributeId).Select(g => g.ToList()).ToList();
            IEnumerable<List<ProductAttributeValue>> combos = new List<List<ProductAttributeValue>> { new() };
            foreach (var g in groups)
                combos = combos.SelectMany(c => g.Select(v => new List<ProductAttributeValue>(c) { v })).ToList();

            var existing = await _db.ProductVariants.Include(v => v.Values)
                .Where(v => v.ProductId == id).ToListAsync();
            var existingSets = existing.Select(v => v.Values.Select(x => x.ProductAttributeValueId).OrderBy(x => x).ToArray()).ToList();

            int created = 0;
            foreach (var combo in combos)
            {
                var setIds = combo.Select(c => c.Id).OrderBy(x => x).ToArray();
                if (existingSets.Any(es => es.SequenceEqual(setIds))) continue;

                _db.ProductVariants.Add(new ProductVariant
                {
                    ProductId = id,
                    Name = string.Join(" / ", combo.OrderBy(c => c.Attribute.DisplayOrder).ThenBy(c => c.Attribute.Name).Select(c => c.Value)),
                    IsActive = true,
                    PriceAdjustment = 0,
                    Values = combo.Select(c => new ProductVariantValue { ProductAttributeValueId = c.Id }).ToList()
                });
                created++;
            }
            await _db.SaveChangesAsync();

            TempData[created > 0 ? "Success" : "Warning"] = created > 0
                ? $"{created} new variant(s) generated."
                : "No new variants — those combinations already exist.";
            return RedirectToAction(nameof(Variants), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveVariant(int id, int variantId, string? sku, decimal priceAdjustment, bool isActive)
        {
            var v = await _db.ProductVariants.FirstOrDefaultAsync(x => x.Id == variantId && x.ProductId == id);
            if (v != null)
            {
                v.Sku = string.IsNullOrWhiteSpace(sku) ? null : sku.Trim();
                v.PriceAdjustment = priceAdjustment;
                v.IsActive = isActive;
                await _db.SaveChangesAsync();
                TempData["Success"] = "Variant updated.";
            }
            return RedirectToAction(nameof(Variants), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteVariant(int id, int variantId)
        {
            var v = await _db.ProductVariants.FirstOrDefaultAsync(x => x.Id == variantId && x.ProductId == id);
            if (v != null)
            {
                var inv = _db.StoreInventories.Where(si => si.ProductVariantId == variantId);
                _db.StoreInventories.RemoveRange(inv);
                _db.ProductVariants.Remove(v); // cascades variant values
                await _db.SaveChangesAsync();
                TempData["Success"] = "Variant deleted.";
            }
            return RedirectToAction(nameof(Variants), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return RedirectToAction(nameof(Index));

            // If the product appears in any order, preserve order history by
            // deactivating instead of physically deleting it.
            var isReferenced = await _db.OrderItems.AnyAsync(oi => oi.ProductId == id);
            if (isReferenced)
            {
                product.IsActive = false;
                product.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Product '{product.Name}' has past orders, so it was deactivated instead of deleted.";
            }
            else
            {
                _db.Products.Remove(product);
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Product '{product.Name}' deleted.";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
