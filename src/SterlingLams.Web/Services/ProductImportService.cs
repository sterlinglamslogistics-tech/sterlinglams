using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Odoo;
using SterlingLams.Web.Services.Odoo.OdooModels;

namespace SterlingLams.Web.Services;

/// <summary>
/// Imports product catalog from Odoo into the local PostgreSQL database.
/// Products are upserted (insert-or-update) by OdooProductId.
/// </summary>
public interface IProductImportService
{
    Task<ProductImportResult> ImportAllFromOdooAsync(IProgress<string>? progress = null);
}

public class ProductImportResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool Success => !Errors.Any();
    public string Summary => $"{Created} created, {Updated} updated, {Skipped} skipped" +
                             (Errors.Any() ? $", {Errors.Count} errors" : "");
}

public class ProductImportService : IProductImportService
{
    private readonly IOdooService _odoo;
    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ProductImportService> _logger;

    // Maps Odoo category names (lowercased) to our local slugs
    private static readonly Dictionary<string, string> CategorySlugMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rings"]     = "rings",
        ["ring"]      = "rings",
        ["necklace"]  = "necklaces",
        ["necklaces"] = "necklaces",
        ["earring"]   = "earrings",
        ["earrings"]  = "earrings",
        ["bracelet"]  = "bracelets",
        ["bracelets"] = "bracelets",
        ["brooch"]    = "brooches",
        ["brooches"]  = "brooches",
        ["watch"]     = "watches",
        ["watches"]   = "watches",
        ["set"]       = "sets",
        ["sets"]      = "sets",
    };

    public ProductImportService(IOdooService odoo, ApplicationDbContext db, IWebHostEnvironment env, ILogger<ProductImportService> logger)
    {
        _odoo = odoo;
        _db = db;
        _env = env;
        _logger = logger;
    }

    public async Task<ProductImportResult> ImportAllFromOdooAsync(IProgress<string>? progress = null)
    {
        var result = new ProductImportResult();

        // Load local state once
        var localCategories = await _db.Categories.ToListAsync();
        var defaultCategory = localCategories.FirstOrDefault(c => c.Slug == "rings")
                           ?? localCategories.FirstOrDefault()
                           ?? throw new InvalidOperationException("No categories seeded. Run the seeder first.");

        var existingProducts = await _db.Products
            .ToDictionaryAsync(p => p.OdooProductId);

        int offset = 0;
        const int batchSize = 50;
        int totalFetched = 0;

        while (true)
        {
            progress?.Report($"Fetching products from Odoo (offset {offset})…");

            List<OdooProduct> batch;
            try
            {
                batch = await _odoo.GetProductsAsync(offset, batchSize);
            }
            catch (Exception ex)
            {
                var msg = $"Odoo fetch failed at offset {offset}: {ex.Message}";
                _logger.LogError(ex, msg);
                result.Errors.Add(msg);
                break;
            }

            if (!batch.Any()) break;

            totalFetched += batch.Count;
            progress?.Report($"Processing {batch.Count} products (total fetched: {totalFetched})…");

            foreach (var odooProduct in batch)
            {
                try
                {
                    // Resolve category
                    var category = ResolveCategory(odooProduct.CategoryName, localCategories) ?? defaultCategory;

                    // Build slug
                    var slug = Slugify(odooProduct.Name);

                    if (existingProducts.TryGetValue(odooProduct.Id, out var existing))
                    {
                        // Update
                        existing.Name = odooProduct.Name;
                        existing.Price = odooProduct.ListPrice;
                        existing.Sku = odooProduct.Sku;
                        existing.Description = SafeString(odooProduct.DescriptionSale);
                        existing.IsActive = odooProduct.Active;
                        existing.CategoryId = category.Id;
                        existing.UpdatedAt = DateTime.UtcNow;
                        // Preserve slug if already set (avoid breaking existing URLs)
                        if (string.IsNullOrEmpty(existing.Slug))
                            existing.Slug = await UniqueSlugAsync(slug, existing.Id);

                        result.Updated++;
                    }
                    else
                    {
                        // Insert
                        var uniqueSlug = await UniqueSlugAsync(slug, 0);
                        var product = new Product
                        {
                            OdooProductId = odooProduct.Id,
                            Name = odooProduct.Name,
                            Slug = uniqueSlug,
                            Price = odooProduct.ListPrice,
                            Sku = odooProduct.Sku,
                            Description = SafeString(odooProduct.DescriptionSale),
                            IsActive = odooProduct.Active,
                            CategoryId = category.Id,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        _db.Products.Add(product);
                        existingProducts[odooProduct.Id] = product; // prevent duplicate on re-run
                        result.Created++;
                    }
                }
                catch (Exception ex)
                {
                    var msg = $"Error importing product '{odooProduct.Name}' (Odoo ID {odooProduct.Id}): {ex.Message}";
                    _logger.LogWarning(ex, msg);
                    result.Errors.Add(msg);
                    result.Skipped++;
                }
            }

            // Flush each batch to DB
            try
            {
                await _db.SaveChangesAsync();
                progress?.Report($"Saved batch. Running total: {result.Created} created, {result.Updated} updated.");
            }
            catch (Exception ex)
            {
                var msg = $"DB save failed for batch at offset {offset}: {ex.Message}";
                _logger.LogError(ex, msg);
                result.Errors.Add(msg);
            }

            // ─── Images: save Odoo base64 image for new/updated products ──────────
            foreach (var odooProduct in batch)
            {
                if (odooProduct.Image is not string b64 || b64 is "false" or { Length: 0 })
                    continue;

                if (!existingProducts.TryGetValue(odooProduct.Id, out var localProduct))
                    continue;

                try
                {
                    var imgDir = Path.Combine(_env.WebRootPath, "images", "products", localProduct.Id.ToString());
                    Directory.CreateDirectory(imgDir);
                    var imgPath = Path.Combine(imgDir, "odoo.jpg");
                    await File.WriteAllBytesAsync(imgPath, Convert.FromBase64String(b64));

                    var imgUrl = $"/images/products/{localProduct.Id}/odoo.jpg";
                    var imgRecord = await _db.Set<ProductImage>()
                        .FirstOrDefaultAsync(i => i.ProductId == localProduct.Id && i.Url == imgUrl);

                    if (imgRecord == null)
                    {
                        var hasPrimary = await _db.Set<ProductImage>().AnyAsync(i => i.ProductId == localProduct.Id && i.IsPrimary);
                        _db.Set<ProductImage>().Add(new ProductImage
                        {
                            ProductId = localProduct.Id,
                            Url = imgUrl,
                            AltText = localProduct.Name,
                            IsPrimary = !hasPrimary,
                            SortOrder = 0
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save Odoo image for product {Id}", odooProduct.Id);
                }
            }

            // ─── Variants: fetch and upsert for this batch of templates ──────────
            try
            {
                var templateIds = batch.Select(p => p.Id).ToArray();
                var odooVariants = await _odoo.GetProductVariantsAsync(templateIds);

                var localProductMap = existingProducts.Values
                    .Where(p => templateIds.Contains(p.OdooProductId))
                    .ToDictionary(p => p.OdooProductId);

                var localProductIds = localProductMap.Values.Select(p => p.Id).ToList();
                var existingVariants = await _db.ProductVariants
                    .Where(v => localProductIds.Contains(v.ProductId))
                    .ToDictionaryAsync(v => v.OdooVariantId);

                foreach (var v in odooVariants)
                {
                    if (!localProductMap.TryGetValue(v.TemplateId, out var localProduct)) continue;

                    if (existingVariants.TryGetValue(v.Id, out var existing))
                    {
                        existing.Name = v.Name;
                        existing.Sku = v.Sku;
                        existing.IsActive = v.Active;
                    }
                    else
                    {
                        _db.ProductVariants.Add(new ProductVariant
                        {
                            OdooVariantId = v.Id,
                            ProductId = localProduct.Id,
                            Name = v.Name,
                            Sku = v.Sku,
                            IsActive = v.Active
                        });
                    }
                }

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import variants/images for batch at offset {Offset}", offset);
            }

            if (batch.Count < batchSize) break; // Last page
            offset += batchSize;
        }

        progress?.Report($"Import complete. {result.Summary}");
        _logger.LogInformation("Odoo product import complete: {Summary}", result.Summary);
        return result;
    }

    private Category? ResolveCategory(string? odooName, List<Category> categories)
    {
        if (string.IsNullOrWhiteSpace(odooName)) return null;

        // Try exact slug map first
        foreach (var (key, slug) in CategorySlugMap)
        {
            if (odooName.Contains(key, StringComparison.OrdinalIgnoreCase))
                return categories.FirstOrDefault(c => c.Slug == slug);
        }

        // Fallback: fuzzy match by category name
        return categories.FirstOrDefault(c =>
            odooName.Contains(c.Name, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains(odooName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, int excludeId)
    {
        var slug = baseSlug;
        var counter = 1;
        while (await _db.Products.AnyAsync(p => p.Slug == slug && p.Id != excludeId))
        {
            slug = $"{baseSlug}-{counter++}";
        }
        return slug;
    }

    private static string Slugify(string name) =>
        System.Text.RegularExpressions.Regex
            .Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-")
            .Trim('-');

    private static string? SafeString(object? val) =>
        val is string s && s != "false" ? s : null;
}
