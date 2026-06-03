using SterlingLams.Web.Services.Odoo.OdooModels;

namespace SterlingLams.Web.Services.Odoo;

public interface IOdooService
{
    /// <summary>Authenticates and returns the Odoo user id (uid). Throws on failure.</summary>
    Task<int> AuthenticateAsync();

    /// <summary>Generic search_read for diagnostics/linking. Returns rows as field→JSON maps.</summary>
    Task<List<Dictionary<string, System.Text.Json.JsonElement>>> SearchReadAsync(
        string model, object[] domain, string[] fields, int limit = 0);

    /// <summary>Creates a single record, returns its new id.</summary>
    Task<int> CreateAsync(string model, Dictionary<string, object?> values);

    /// <summary>Creates many records in one call, returns their new ids (order preserved). Optional Odoo context.</summary>
    Task<List<int>> CreateManyAsync(string model, IEnumerable<Dictionary<string, object?>> records, object? context = null);

    /// <summary>Writes values to the given record ids. Optional Odoo context.</summary>
    Task<bool> WriteAsync(string model, int[] ids, Dictionary<string, object?> values, object? context = null);

    /// <summary>Generic model method call (e.g. action_apply_inventory); returns the raw result.</summary>
    Task<System.Text.Json.JsonElement> ExecuteAsync(string model, string method, object[] args, object? context = null);

    Task<List<OdooProduct>> GetProductsAsync(int offset = 0, int limit = 100);
    Task<OdooProduct?> GetProductByIdAsync(int odooProductId);
    Task<List<OdooProductVariant>> GetProductVariantsAsync(int[] odooTemplateIds);
    Task<List<OdooStockQuant>> GetStockQuantsAsync(int[] odooProductIds, int[] warehouseLocationIds);
    Task<Dictionary<int, Dictionary<int, int>>> GetInventoryByStoreAsync(int[] odooProductIds);
    /// <summary>Finds a res.partner by email, or creates a customer partner. Returns its id.</summary>
    Task<int> FindOrCreatePartnerAsync(string? email, string name);

    Task<int> CreateSaleOrderAsync(CreateSaleOrderRequest request);
    Task<bool> ConfirmSaleOrderAsync(int odooOrderId);
    Task<bool> CancelSaleOrderAsync(int odooOrderId);
}

public class CreateSaleOrderRequest
{
    public int OdooPartnerId { get; set; }
    public int OdooWarehouseId { get; set; }
    public List<SaleOrderLine> Lines { get; set; } = new();
    public string? Note { get; set; }
}

public class SaleOrderLine
{
    public int OdooProductId { get; set; }
    public int Quantity { get; set; }
    public decimal PriceUnit { get; set; }
}
