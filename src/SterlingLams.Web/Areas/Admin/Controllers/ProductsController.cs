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
