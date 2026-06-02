using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.ViewComponents;

/// <summary>Renders the top product categories (by stock of imaged products) for the homepage.</summary>
public class HomeCategoriesViewComponent : ViewComponent
{
    public record HomeCategory(string Name, string Slug, string ImageUrl, int Count);

    private readonly ApplicationDbContext _db;

    public HomeCategoriesViewComponent(ApplicationDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync(int count = 4)
    {
        // Step 1: top categories by number of imaged, active products (translatable).
        var top = await _db.Categories
            .Where(c => c.IsActive && c.Products.Any(p => p.IsActive && p.Images.Any()))
            .Select(c => new { c.Id, c.Name, c.Slug, Count = c.Products.Count(p => p.IsActive && p.Images.Any()) })
            .OrderByDescending(x => x.Count)
            .Take(count)
            .ToListAsync();

        // Step 2: pick one representative image per category.
        var cats = new List<HomeCategory>();
        foreach (var c in top)
        {
            var img = await _db.Products
                .Where(p => p.CategoryId == c.Id && p.IsActive)
                .SelectMany(p => p.Images)
                .Select(i => i.Url)
                .FirstOrDefaultAsync() ?? "/images/placeholder.jpg";
            cats.Add(new HomeCategory(c.Name, c.Slug, img, c.Count));
        }

        return View(cats);
    }
}
