using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.ViewComponents;

/// <summary>
/// Renders the store's top product categories (by number of imaged, active products)
/// as navigation links. The <paramref name="style"/> selects the markup variant
/// (Nav, Mobile, Footer) so the header, mobile menu, and footer stay in sync with the catalog.
/// </summary>
public class TopCategoriesViewComponent : ViewComponent
{
    public record NavCategory(string Name, string Slug);

    private readonly ApplicationDbContext _db;

    public TopCategoriesViewComponent(ApplicationDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync(string style = "Nav", int count = 4)
    {
        var top = await _db.Categories
            .Where(c => c.IsActive && c.Products.Any(p => p.IsActive && p.Images.Any()))
            .Select(c => new { c.Name, c.Slug, Count = c.Products.Count(p => p.IsActive && p.Images.Any()) })
            .OrderByDescending(x => x.Count)
            .Take(count)
            .ToListAsync();

        // Display the chosen top-N alphabetically for a tidy, stable menu order.
        var cats = top
            .OrderBy(x => x.Name)
            .Select(x => new NavCategory(x.Name, x.Slug))
            .ToList();

        return View(style, cats);
    }
}
