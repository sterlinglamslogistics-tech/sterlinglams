using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure.WordpressImport;

/// <summary>
/// One-off importer that loads products parsed from a WooCommerce (.wpress) backup
/// into the local catalog. Triggered only when WP_IMPORT=1; never runs on normal startup.
///
/// Expects, under <paramref name="baseDir"/>:
///   out/products.json   — parsed product list (.wpimport/parse.js output)
///   images/uploads/...  — extracted original image files
/// </summary>
public static class WordpressProductImporter
{
    private const int DefaultStockPerStore = 5;

    private sealed class WpProduct
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Price { get; set; }
        public string? Sku { get; set; }
        public string Excerpt { get; set; } = "";
        public List<string> Categories { get; set; } = new();
        public List<string> ImagePaths { get; set; } = new();
    }

    public static async Task RunAsync(IServiceProvider services, string baseDir, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        var jsonPath = Path.Combine(baseDir, "out", "products.json");
        var imagesRoot = Path.Combine(baseDir, "images", "uploads");
        if (!File.Exists(jsonPath))
        {
            logger.LogError("WP import: products.json not found at {Path}", jsonPath);
            return;
        }

        var json = await File.ReadAllTextAsync(jsonPath);
        var all = JsonSerializer.Deserialize<List<WpProduct>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        // Scope: published, has an image, has a positive price.
        var scoped = all.Where(p =>
            p.Status == "publish" &&
            p.ImagePaths.Count > 0 &&
            decimal.TryParse(p.Price, out var pr) && pr > 0).ToList();

        logger.LogInformation("WP import: {Count} products in scope", scoped.Count);

        var stores = await db.Stores.ToListAsync();

        // Ensure categories exist (decode entities like &amp;).
        var categoryByKey = await db.Categories.ToDictionaryAsync(c => c.Slug, c => c);
        async Task<Category> EnsureCategoryAsync(string rawName)
        {
            var name = Decode(rawName).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "Uncategorized";
            var slug = Slugify(name);
            if (categoryByKey.TryGetValue(slug, out var existing)) return existing;
            var cat = new Category { Name = name, Slug = slug, IsActive = true };
            db.Categories.Add(cat);
            await db.SaveChangesAsync();
            categoryByKey[slug] = cat;
            logger.LogInformation("WP import: created category '{Name}'", name);
            return cat;
        }

        var usedSlugs = new HashSet<string>(await db.Products.Select(p => p.Slug).ToListAsync());

        int created = 0, skipped = 0, imagesCopied = 0, missingImages = 0;

        foreach (var wp in scoped)
        {
            var name = Decode(wp.Title).Trim();
            if (string.IsNullOrWhiteSpace(name)) { skipped++; continue; }

            var baseSlug = !string.IsNullOrWhiteSpace(wp.Slug) ? Slugify(wp.Slug) : Slugify(name);
            if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = $"product-{wp.Id}";

            // Idempotency: if this slug is already taken, assume it was imported and skip.
            if (usedSlugs.Contains(baseSlug)) { skipped++; continue; }

            var category = await EnsureCategoryAsync(wp.Categories.FirstOrDefault() ?? "Uncategorized");
            decimal.TryParse(wp.Price, out var price);
            var shortDesc = CleanText(wp.Excerpt);

            var product = new Product
            {
                Name = name,
                Slug = baseSlug,
                Description = shortDesc,
                ShortDescription = Truncate(shortDesc, 280),
                Price = price,
                Currency = "NGN",
                Sku = string.IsNullOrWhiteSpace(wp.Sku) ? null : wp.Sku.Trim(),
                OdooProductId = 0,
                IsActive = true,
                CategoryId = category.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            usedSlugs.Add(baseSlug);

            // Images → wwwroot/images/products/{id}/
            var destDir = Path.Combine(env.WebRootPath, "images", "products", product.Id.ToString());
            Directory.CreateDirectory(destDir);
            int order = 0;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in wp.ImagePaths)
            {
                var src = Path.Combine(imagesRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src)) { missingImages++; continue; }

                var fileName = Path.GetFileName(src);
                if (!seenNames.Add(fileName)) fileName = $"{order}-{fileName}";
                var dest = Path.Combine(destDir, fileName);
                File.Copy(src, dest, overwrite: true);
                imagesCopied++;

                db.ProductImages.Add(new ProductImage
                {
                    ProductId = product.Id,
                    Url = $"/images/products/{product.Id}/{fileName}",
                    AltText = name,
                    IsPrimary = order == 0,
                    SortOrder = order,
                });
                order++;
            }

            // Stock: default qty at every store.
            foreach (var store in stores)
            {
                db.StoreInventories.Add(new StoreInventory
                {
                    ProductId = product.Id,
                    StoreId = store.Id,
                    QuantityOnHand = DefaultStockPerStore,
                    QuantityReserved = 0,
                    LastSyncedAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();
            created++;
            if (created % 100 == 0) logger.LogInformation("WP import: {Created} products created...", created);
        }

        logger.LogInformation(
            "WP import complete. Created={Created}, Skipped={Skipped}, ImagesCopied={Images}, MissingImages={Missing}, Categories={Cats}",
            created, skipped, imagesCopied, missingImages, categoryByKey.Count);
    }

    private static string Decode(string s) => WebUtility.HtmlDecode(s ?? "").Trim();

    private static string CleanText(string s)
    {
        s = Decode(s);
        s = Regex.Replace(s, "<[^>]+>", " ");        // strip HTML tags
        s = Regex.Replace(s, @"\[[^\]]+\]", " ");     // strip shortcodes
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string Slugify(string s)
    {
        s = Decode(s).ToLowerInvariant();
        s = Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
        return s;
    }
}
