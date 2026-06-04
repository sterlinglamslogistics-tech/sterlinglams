using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class AttributesController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;

        public AttributesController(ApplicationDbContext db) => _db = db;

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Attributes";
            var attributes = await _db.ProductAttributes
                .Include(a => a.Values)
                .OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name)
                .ToListAsync();
            return View(attributes);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateAttribute(string name)
        {
            name = name?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                TempData["Error"] = "Attribute name is required.";
            else if (await _db.ProductAttributes.AnyAsync(a => a.Name == name))
                TempData["Error"] = $"Attribute '{name}' already exists.";
            else
            {
                _db.ProductAttributes.Add(new ProductAttribute { Name = name });
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Attribute '{name}' added.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddValue(int attributeId, string value)
        {
            value = value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(value))
                TempData["Error"] = "Value is required.";
            else if (await _db.ProductAttributeValues.AnyAsync(v => v.ProductAttributeId == attributeId && v.Value == value))
                TempData["Error"] = $"Value '{value}' already exists for this attribute.";
            else
            {
                _db.ProductAttributeValues.Add(new ProductAttributeValue { ProductAttributeId = attributeId, Value = value });
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Value '{value}' added.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteValue(int id)
        {
            var inUse = await _db.ProductVariantValues.AnyAsync(vv => vv.ProductAttributeValueId == id);
            if (inUse)
            {
                TempData["Error"] = "Can't delete a value that's used by product variants.";
                return RedirectToAction(nameof(Index));
            }
            var value = await _db.ProductAttributeValues.FindAsync(id);
            if (value != null)
            {
                _db.ProductAttributeValues.Remove(value);
                await _db.SaveChangesAsync();
                TempData["Success"] = "Value deleted.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAttribute(int id)
        {
            var valueIds = await _db.ProductAttributeValues.Where(v => v.ProductAttributeId == id).Select(v => v.Id).ToListAsync();
            var inUse = await _db.ProductVariantValues.AnyAsync(vv => valueIds.Contains(vv.ProductAttributeValueId));
            if (inUse)
            {
                TempData["Error"] = "Can't delete an attribute whose values are used by product variants.";
                return RedirectToAction(nameof(Index));
            }
            var attr = await _db.ProductAttributes.FindAsync(id);
            if (attr != null)
            {
                _db.ProductAttributes.Remove(attr); // cascades values
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Attribute '{attr.Name}' deleted.";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
