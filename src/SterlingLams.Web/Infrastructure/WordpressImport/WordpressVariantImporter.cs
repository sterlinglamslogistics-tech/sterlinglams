using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure.WordpressImport;

/// <summary>
/// One-off (WP_IMPORT_VARIANTS=1): imports WooCommerce variable-product variants into the
/// catalog. Reads out/products.json (Woo id→slug) + out/variants.json (byParent), matches
/// parents to local products by slug, creates attributes/values + variants (SKU + price
/// adjustment) and a default per-store stock. Idempotent — products that already have variants
/// are skipped.
/// </summary>
public static class WordpressVariantImporter
{
    private const int DefaultStockPerStore = 5;

    private sealed class WpProduct { public int Id { get; set; } public string Slug { get; set; } = ""; }
    private sealed class WpVariation
    {
        public string? Sku { get; set; }
        public string? Price { get; set; }
        public int Stock { get; set; }
        public Dictionary<string, string> Attrs { get; set; } = new();
    }
    private sealed class WpVariantsRoot
    {
        public Dictionary<string, List<WpVariation>> ByParent { get; set; } = new();
    }

    public static async Task RunAsync(IServiceProvider services, string baseDir, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var products = JsonSerializer.Deserialize<List<WpProduct>>(
            await File.ReadAllTextAsync(Path.Combine(baseDir, "out", "products.json")), opts) ?? new();
        var variants = JsonSerializer.Deserialize<WpVariantsRoot>(
            await File.ReadAllTextAsync(Path.Combine(baseDir, "out", "variants.json")), opts) ?? new();

        // Woo post id -> our local product (matched by slug).
        var dbBySlug = await db.Products.ToDictionaryAsync(p => p.Slug, p => p);
        var wooIdToProduct = new Dictionary<int, Product>();
        foreach (var wp in products)
        {
            var slug = Slugify(wp.Slug);
            if (!string.IsNullOrEmpty(slug) && dbBySlug.TryGetValue(slug, out var prod))
                wooIdToProduct[wp.Id] = prod;
        }

        var stores = await db.Stores.ToListAsync();
        var attrByName = await db.ProductAttributes.Include(a => a.Values).ToDictionaryAsync(a => a.Name, a => a);
        var productsWithVariants = (await db.ProductVariants.Select(v => v.ProductId).Distinct().ToListAsync()).ToHashSet();

        async Task<ProductAttributeValue> EnsureValueAsync(string attrName, string value)
        {
            if (!attrByName.TryGetValue(attrName, out var attr))
            {
                attr = new ProductAttribute { Name = attrName };
                db.ProductAttributes.Add(attr);
                await db.SaveChangesAsync();
                attrByName[attrName] = attr;
            }
            var val = attr.Values.FirstOrDefault(v => v.Value == value);
            if (val == null)
            {
                val = new ProductAttributeValue { ProductAttributeId = attr.Id, Value = value };
                db.ProductAttributeValues.Add(val);
                await db.SaveChangesAsync();
                attr.Values.Add(val);
            }
            return val;
        }

        int productsDone = 0, variantsCreated = 0, skipped = 0;

        foreach (var (parentId, vlist) in variants.ByParent)
        {
            if (!int.TryParse(parentId, out var pid) || !wooIdToProduct.TryGetValue(pid, out var product))
                continue;
            if (productsWithVariants.Contains(product.Id)) { skipped++; continue; }

            var seenSets = new HashSet<string>();
            var newVariants = new List<ProductVariant>();

            foreach (var v in vlist)
            {
                if (v.Attrs.Count == 0) continue;

                // Resolve attribute values (create attributes/values as needed).
                var values = new List<ProductAttributeValue>();
                foreach (var (an, av) in v.Attrs.OrderBy(x => x.Key))
                    values.Add(await EnsureValueAsync(an.Trim(), av.Trim()));

                var setKey = string.Join("-", values.Select(x => x.Id).OrderBy(x => x));
                if (!seenSets.Add(setKey)) continue;   // de-dupe combos

                decimal.TryParse(v.Price, out var price);
                var adjustment = price > 0 ? price - product.Price : 0m;

                newVariants.Add(new ProductVariant
                {
                    ProductId = product.Id,
                    Name = string.Join(" / ", values.Select(x => x.Value)),
                    Sku = string.IsNullOrWhiteSpace(v.Sku) ? null : v.Sku.Trim(),
                    PriceAdjustment = adjustment,
                    IsActive = true,
                    Values = values.Select(x => new ProductVariantValue { ProductAttributeValueId = x.Id }).ToList()
                });
            }

            if (newVariants.Count == 0) continue;

            db.ProductVariants.AddRange(newVariants);
            await db.SaveChangesAsync();
            variantsCreated += newVariants.Count;

            // Replace product-level stock with per-variant stock (default qty per store).
            var productLevel = db.StoreInventories.Where(si => si.ProductId == product.Id && si.ProductVariantId == 0);
            db.StoreInventories.RemoveRange(productLevel);
            foreach (var variant in newVariants)
                foreach (var store in stores)
                    db.StoreInventories.Add(new StoreInventory
                    {
                        ProductId = product.Id,
                        ProductVariantId = variant.Id,
                        StoreId = store.Id,
                        QuantityOnHand = DefaultStockPerStore,
                        LastSyncedAt = DateTime.UtcNow
                    });
            await db.SaveChangesAsync();

            productsWithVariants.Add(product.Id);
            productsDone++;
            if (productsDone % 100 == 0) logger.LogInformation("WP_VARIANTS: {Done} products done…", productsDone);
        }

        logger.LogInformation("WP_VARIANTS: done. products={Products} (skipped {Skipped}), variants={Variants}, attributes={Attrs}",
            productsDone, skipped, variantsCreated, attrByName.Count);
    }

    private static string Slugify(string s)
    {
        s = WebUtility.HtmlDecode(s ?? "").ToLowerInvariant();
        return Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
    }
}
