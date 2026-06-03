using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.ViewComponents;

/// <summary>Renders the header cart-count badge from the session cart (always accurate per page).</summary>
public class CartCountViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var count = 0;
        var json = HttpContext.Session.GetString("cart");
        if (!string.IsNullOrEmpty(json))
        {
            try { count = JsonSerializer.Deserialize<CartViewModel>(json)?.TotalItems ?? 0; }
            catch { count = 0; }
        }
        return View(count);
    }
}
