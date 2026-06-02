using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.ViewComponents;

/// <summary>Renders a random selection of active products that have an image, for the homepage.</summary>
public class FeaturedProductsViewComponent : ViewComponent
{
    public record FeaturedItem(string Name, string Slug, decimal Price, string ImageUrl);

    private readonly ApplicationDbContext _db;

    public FeaturedProductsViewComponent(ApplicationDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync(int count = 4)
    {
        // Prefer flagged-featured products; otherwise fall back to any with an image.
        var query = _db.Products
            .Where(p => p.IsActive && p.Images.Any());

        var featured = await query.Where(p => p.IsFeatured)
            .OrderBy(_ => EF.Functions.Random())
            .Take(count)
            .Select(Project())
            .ToListAsync();

        if (featured.Count < count)
        {
            var fillerNeeded = count - featured.Count;
            var have = featured.Select(f => f.Slug).ToList();
            var filler = await query
                .Where(p => !have.Contains(p.Slug))
                .OrderBy(_ => EF.Functions.Random())
                .Take(fillerNeeded)
                .Select(Project())
                .ToListAsync();
            featured.AddRange(filler);
        }

        return View(featured);
    }

    private static System.Linq.Expressions.Expression<Func<Models.Domain.Product, FeaturedItem>> Project() =>
        p => new FeaturedItem(
            p.Name,
            p.Slug,
            p.Price,
            p.Images.Where(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault()
                ?? p.Images.Select(i => i.Url).FirstOrDefault()
                ?? "/images/placeholder.jpg");
}
