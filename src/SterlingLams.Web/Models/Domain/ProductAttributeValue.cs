namespace SterlingLams.Web.Models.Domain;

/// <summary>A value for a <see cref="ProductAttribute"/> (e.g. Size "7", Metal "Rose Gold").</summary>
public class ProductAttributeValue
{
    public int Id { get; set; }

    /// <summary>Odoo product.attribute.value id (0 until pushed to Odoo).</summary>
    public int OdooValueId { get; set; }

    public int ProductAttributeId { get; set; }
    public ProductAttribute Attribute { get; set; } = null!;

    public string Value { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}
