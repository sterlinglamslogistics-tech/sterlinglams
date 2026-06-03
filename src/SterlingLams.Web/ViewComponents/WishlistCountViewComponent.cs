using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.ViewComponents;

/// <summary>Renders the header wishlist-count badge for the signed-in user.</summary>
public class WishlistCountViewComponent : ViewComponent
{
    private readonly ApplicationDbContext _db;

    public WishlistCountViewComponent(ApplicationDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var count = 0;
        var userId = UserClaimsPrincipal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
            count = await _db.WishlistItems.CountAsync(w => w.UserId == userId);
        return View(count);
    }
}
