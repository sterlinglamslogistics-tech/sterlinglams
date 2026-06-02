using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.ViewComponents;

/// <summary>Renders the active stores in the site footer, sourced from the database.</summary>
public class StoresFooterViewComponent : ViewComponent
{
    public record StoreFooterItem(string Name, string Address);

    private readonly ApplicationDbContext _db;

    public StoresFooterViewComponent(ApplicationDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var stores = await _db.Stores
            .Where(s => s.IsActive)
            .OrderBy(s => s.Id)
            .ToListAsync();

        var items = stores
            .Select(s => new StoreFooterItem(s.Name, BuildAddress(s.Address, s.City, s.State)))
            .ToList();

        return View(items);
    }

    // Joins address/city/state, skipping any part already represented in what we've kept
    // (the admin Address field often already contains the city/state).
    private static string BuildAddress(string? address, string? city, string? state)
    {
        var parts = new List<string>();
        foreach (var raw in new[] { address, city, state })
        {
            var p = raw?.Trim();
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (parts.Any(x => x.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            parts.Add(p);
        }
        return string.Join(", ", parts);
    }
}
