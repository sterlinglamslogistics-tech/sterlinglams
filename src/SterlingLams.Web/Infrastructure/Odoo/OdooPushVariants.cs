using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (ODOO_PUSH_VARIANTS=1): for products that have website variants not yet linked to
/// Odoo, ensures the attributes/values exist in Odoo, adds the attribute lines to the product
/// template (Odoo then generates the variant combinations), and maps each website variant to
/// its Odoo product.product id. Limit via ODOO_PUSH_VARIANTS_LIMIT (for a trial run).
/// </summary>
public static class OdooPushVariants
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var odoo = scope.ServiceProvider.GetRequiredService<IOdooService>();

        var limit = int.TryParse(Environment.GetEnvironmentVariable("ODOO_PUSH_VARIANTS_LIMIT"), out var l) ? l : int.MaxValue;

        var productIds = await db.Products
            .Where(p => p.OdooProductId != 0 && p.Variants.Any(v => v.OdooVariantId == 0))
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .Take(limit)
            .ToListAsync();

        logger.LogInformation("ODOO_PUSH_VARIANTS: {Count} product(s) to push", productIds.Count);
        int done = 0, mapped = 0, unmatched = 0, failed = 0;

        foreach (var pid in productIds)
        {
            var product = await db.Products.FindAsync(pid);
            if (product == null) continue;

            var variants = await db.ProductVariants
                .Include(v => v.Values).ThenInclude(vv => vv.AttributeValue).ThenInclude(av => av.Attribute)
                .Where(v => v.ProductId == pid).ToListAsync();
            if (variants.Count == 0) continue;

            try
            {
                var templateId = await odoo.GetTemplateIdAsync(product.OdooProductId);
                if (templateId == 0) { logger.LogWarning("ODOO_PUSH_VARIANTS: no template for product {Id} ({Name})", pid, product.Name); failed++; continue; }

                // Ensure Odoo attributes/values, then add an attribute line per attribute
                // (skip attributes already on the template, so re-runs after a partial failure are safe).
                var existingAttrIds = await odoo.GetTemplateAttributeIdsAsync(templateId);
                var byAttr = variants
                    .SelectMany(v => v.Values.Select(vv => vv.AttributeValue))
                    .GroupBy(av => av.Attribute);

                foreach (var g in byAttr)
                {
                    var attr = g.Key;
                    if (attr.OdooAttributeId == 0)
                        attr.OdooAttributeId = await odoo.EnsureAttributeAsync(attr.Name);

                    var odooValueIds = new List<int>();
                    foreach (var av in g.DistinctBy(x => x.Id))
                    {
                        if (av.OdooValueId == 0)
                            av.OdooValueId = await odoo.EnsureAttributeValueAsync(attr.OdooAttributeId, av.Value);
                        odooValueIds.Add(av.OdooValueId);
                    }
                    if (!existingAttrIds.Contains(attr.OdooAttributeId))
                        await odoo.AddTemplateAttributeLineAsync(templateId, attr.OdooAttributeId, odooValueIds.ToArray());
                }
                await db.SaveChangesAsync(); // persist OdooAttributeId / OdooValueId

                // Map generated Odoo variants to our variants by attribute-value name set.
                var odooVariants = await odoo.GetTemplateVariantsAsync(templateId);
                foreach (var v in variants)
                {
                    var myNames = v.Values.Select(vv => vv.AttributeValue.Value)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    var match = odooVariants.FirstOrDefault(ov =>
                        ov.ValueNames.Count == myNames.Count &&
                        ov.ValueNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                     .SequenceEqual(myNames, StringComparer.OrdinalIgnoreCase));
                    if (match.ProductId > 0) { v.OdooVariantId = match.ProductId; mapped++; }
                    else { unmatched++; logger.LogWarning("ODOO_PUSH_VARIANTS: unmatched variant '{Name}' (product {Id})", v.Name, pid); }
                }
                await db.SaveChangesAsync();
                done++;
                if (done % 25 == 0) logger.LogInformation("ODOO_PUSH_VARIANTS: {Done} products done…", done);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex is OdooException oe ? null : ex,
                    "ODOO_PUSH_VARIANTS: failed for product {Id} ({Name}): {Msg}",
                    pid, product.Name, ex is OdooException o ? o.Message : ex.Message);
            }
        }

        logger.LogInformation("ODOO_PUSH_VARIANTS: done. products={Done}, variantsMapped={Mapped}, unmatched={Unmatched}, failed={Failed}",
            done, mapped, unmatched, failed);
    }
}
