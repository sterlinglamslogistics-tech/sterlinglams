namespace SterlingLams.Web.Models.ViewModels;

public class StoreStockViewModel
{
    public string StoreName { get; set; } = string.Empty;
    public string StoreSlug { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool IsAvailable => Quantity > 0;
}

public class ProductDetailViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "NGN";
    public string FormattedPrice => $"₦{Price:N0}";

    public string? Material { get; set; }
    public string? Metal { get; set; }
    public string? GemstoneType { get; set; }
    public string? Carat { get; set; }
    public string? Weight { get; set; }

    public string CategoryName { get; set; } = string.Empty;
    public string CategorySlug { get; set; } = string.Empty;

    public List<string> ImageUrls { get; set; } = new();
    public string PrimaryImageUrl => ImageUrls.FirstOrDefault() ?? "/images/placeholder.jpg";

    public List<StoreStockViewModel> StoreStock { get; set; } = new();
    public bool IsAvailableAnywhere => StoreStock.Any(s => s.IsAvailable);
    public int TotalStock => StoreStock.Sum(s => s.Quantity);

    public List<ProductVariantOptionViewModel> Variants { get; set; } = new();
    public List<AttributeOptionViewModel> AttributeOptions { get; set; } = new();
    public bool HasVariants => AttributeOptions.Any();
    public List<string> Tags { get; set; } = new();

    public bool IsInWishlist { get; set; }
    public List<ProductCardViewModel> RelatedProducts { get; set; } = new();
}

public class ProductVariantOptionViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Size { get; set; }
    public string? Color { get; set; }
    public decimal? PriceAdjustment { get; set; }

    /// <summary>Attribute name → chosen value for this variant (e.g. "Ring Size" → "7").</summary>
    public Dictionary<string, string> ValueMap { get; set; } = new();
    /// <summary>Absolute price for this variant (base + adjustment).</summary>
    public decimal Price { get; set; }
    public string FormattedPrice => $"₦{Price:N0}";
    /// <summary>Total available (on hand − reserved) across all stores for this variant.</summary>
    public int Available { get; set; }
    public bool InStock => Available > 0;
}

/// <summary>A single attribute (e.g. "Ring Size") and the distinct values offered for this product.</summary>
public class AttributeOptionViewModel
{
    public string Name { get; set; } = string.Empty;
    public List<string> Values { get; set; } = new();
}
