namespace SterlingLams.Web.Models.Domain;

public class Store
{
    public int Id { get; set; }

    /// <summary>Odoo stock.warehouse id — used when creating sale orders for this store.</summary>
    public int OdooWarehouseId { get; set; }

    /// <summary>Odoo stock.location id (the warehouse's lot_stock_id) — used for stock-quant sync.</summary>
    public int OdooStockLocationId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;

    public string? Phone { get; set; }
    public string? Email { get; set; }

    public string? OpeningHours { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<StoreInventory> Inventories { get; set; } = new List<StoreInventory>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
