using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Infrastructure.Maintenance;

/// <summary>
/// One-off maintenance: merges duplicate product categories. Triggered by the
/// CATEGORY_MERGE env var, formatted as "sourceSlug&gt;targetSlug;sourceSlug2&gt;targetSlug2".
/// For each pair, products in the source category are reassigned to the target and the
/// (now empty) source category is deleted. Idempotent — missing sources are skipped.
/// </summary>
public static class CategoryMerge
{
    public static async Task RunAsync(IServiceProvider services, string spec, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var pairs = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('>', StringSplitOptions.TrimEntries);
            if (parts.Length != 2) { logger.LogWarning("CATEGORY_MERGE: bad pair '{Pair}'", pair); continue; }
            var (sourceSlug, targetSlug) = (parts[0], parts[1]);

            var source = await db.Categories.FirstOrDefaultAsync(c => c.Slug == sourceSlug);
            var target = await db.Categories.FirstOrDefaultAsync(c => c.Slug == targetSlug);

            if (target == null) { logger.LogWarning("CATEGORY_MERGE: target '{Target}' not found", targetSlug); continue; }
            if (source == null) { logger.LogInformation("CATEGORY_MERGE: source '{Source}' not found — nothing to do", sourceSlug); continue; }
            if (source.Id == target.Id) { logger.LogInformation("CATEGORY_MERGE: '{S}' == '{T}', skipping", sourceSlug, targetSlug); continue; }

            var moved = await db.Products
                .Where(p => p.CategoryId == source.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CategoryId, target.Id));

            db.Categories.Remove(source);
            await db.SaveChangesAsync();

            logger.LogInformation("CATEGORY_MERGE: moved {Count} products from '{Source}' into '{Target}' and deleted the source",
                moved, sourceSlug, targetSlug);
        }
    }
}
