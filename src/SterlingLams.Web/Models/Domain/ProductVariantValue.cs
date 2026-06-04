namespace SterlingLams.Web.Models.Domain;

/// <summary>Links a <see cref="ProductVariant"/> to the attribute values that define it
/// (e.g. variant "Size 7 / Rose Gold" → values [7], [Rose Gold]).</summary>
public class ProductVariantValue
{
    public int Id { get; set; }

    public int ProductVariantId { get; set; }
    public ProductVariant Variant { get; set; } = null!;

    public int ProductAttributeValueId { get; set; }
    public ProductAttributeValue AttributeValue { get; set; } = null!;
}
