using System.Text.Json.Serialization;

namespace SterlingLams.Web.Services.Odoo.OdooModels;

public class OdooProduct
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("list_price")]
    public decimal ListPrice { get; set; }

    [JsonPropertyName("default_code")]
    public object? DefaultCode { get; set; }

    [JsonPropertyName("barcode")]
    public object? Barcode { get; set; }

    [JsonPropertyName("categ_id")]
    public object[] CategoryId { get; set; } = Array.Empty<object>();

    [JsonPropertyName("description_sale")]
    public object? DescriptionSale { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("image_1920")]
    public object? Image { get; set; }

    public string? Sku { get { var s = OdooValue.AsString(DefaultCode); return string.IsNullOrEmpty(s) || s == "false" ? null : s; } }
    public string? CategoryName => CategoryId.Length > 1 ? OdooValue.AsString(CategoryId[1]) : null;
}

public class OdooStockQuant
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("product_id")]
    public object[] ProductId { get; set; } = Array.Empty<object>();

    [JsonPropertyName("location_id")]
    public object[] LocationId { get; set; } = Array.Empty<object>();

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("reserved_quantity")]
    public decimal ReservedQuantity { get; set; }

    public int ProductOdooId => ProductId.Length > 0 ? OdooValue.AsInt(ProductId[0]) : 0;
    public int LocationOdooId => LocationId.Length > 0 ? OdooValue.AsInt(LocationId[0]) : 0;
    public string LocationName => LocationId.Length > 1 ? OdooValue.AsString(LocationId[1]) : "";
}

/// <summary>Helpers for Odoo values that System.Text.Json deserializes into JsonElement.</summary>
internal static class OdooValue
{
    public static int AsInt(object? o) => o switch
    {
        System.Text.Json.JsonElement je => je.ValueKind == System.Text.Json.JsonValueKind.Number
            ? je.GetInt32()
            : int.TryParse(je.ToString(), out var n) ? n : 0,
        IConvertible c => Convert.ToInt32(c),
        _ => 0
    };

    public static string AsString(object? o) => o switch
    {
        System.Text.Json.JsonElement je => je.ValueKind == System.Text.Json.JsonValueKind.String
            ? je.GetString() ?? "" : je.ToString(),
        _ => o?.ToString() ?? ""
    };
}

public class OdooProductVariant
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("default_code")]
    public object? DefaultCode { get; set; }

    [JsonPropertyName("product_tmpl_id")]
    public object[] ProductTmplId { get; set; } = Array.Empty<object>();

    [JsonPropertyName("lst_price")]
    public decimal ListPrice { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    public int TemplateId => ProductTmplId.Length > 0 ? OdooValue.AsInt(ProductTmplId[0]) : 0;
    public string? Sku { get { var s = OdooValue.AsString(DefaultCode); return string.IsNullOrEmpty(s) || s == "false" ? null : s; } }
}

public class OdooSaleOrder
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("amount_total")]
    public decimal AmountTotal { get; set; }

    [JsonPropertyName("partner_id")]
    public object[] PartnerId { get; set; } = Array.Empty<object>();
}
