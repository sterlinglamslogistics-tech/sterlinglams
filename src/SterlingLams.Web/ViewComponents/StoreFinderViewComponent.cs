using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.ViewComponents;

/// <summary>Renders active store names for the homepage "Visit Us In-Store" banner.</summary>
public class StoreFinderViewComponent : ViewComponent
{
    private readonly ApplicationDbContext _db;

    public StoreFinderViewComponent(ApplicationDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var names = await _db.Stores
            .Where(s => s.IsActive)
            .OrderBy(s => s.Id)
            .Select(s => s.Name)
            .ToListAsync();

        return View(names);
    }
}
