namespace SterlingLams.Web.Models.Domain;

/// <summary>A configurable product option (e.g. "Ring Size", "Metal", "Length").</summary>
public class ProductAttribute
{
    public int Id { get; set; }

    /// <summary>Odoo product.attribute id (0 until pushed to Odoo).</summary>
    public int OdooAttributeId { get; set; }

    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }

    public ICollection<ProductAttributeValue> Values { get; set; } = new List<ProductAttributeValue>();
}
