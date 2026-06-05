using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Services.Odoo.OdooModels;

namespace SterlingLams.Web.Services.Odoo;

public class OdooSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public Dictionary<string, int> Stores { get; set; } = new();
    public int InventoryCacheTtlSeconds { get; set; } = 60;

    /// <summary>
    /// When true, paid web orders are auto-confirmed in Odoo (creates the delivery and
    /// reserves stock). Confirmation is best-effort — if it fails (e.g. out of stock), the
    /// order simply stays as a quotation in Odoo and checkout is unaffected.
    /// </summary>
    public bool AutoConfirmOrders { get; set; } = true;

    /// <summary>
    /// When true, the delivery is also validated on payment so on-hand stock drops immediately
    /// (not just reserved). Off → stock is reserved and staff validate the delivery in Odoo.
    /// </summary>
    public bool AutoValidateDelivery { get; set; } = true;
}

public class OdooService : IOdooService
{
    private readonly HttpClient _http;
    private readonly OdooSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OdooService> _logger;

    // OdooService is registered as a typed HttpClient (transient), so the resolved uid
    // must live in shared cache — not an instance field — to avoid re-authenticating on
    // every call. The lock is static so it serialises auth across all instances.
    private const string UidCacheKey = "odoo:uid";
    private static readonly TimeSpan UidCacheTtl = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim _authLock = new(1, 1);

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public OdooService(HttpClient http, OdooSettings settings, IMemoryCache cache, ILogger<OdooService> logger)
    {
        _http = http;
        _settings = settings;
        _cache = cache;
        _logger = logger;
    }

    // ─── Authentication ──────────────────────────────────────────────────────

    private async Task<int> GetUidAsync()
    {
        if (_cache.TryGetValue(UidCacheKey, out int cachedUid)) return cachedUid;

        await _authLock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(UidCacheKey, out cachedUid)) return cachedUid;

            var request = new OdooRpcRequest
            {
                Params = new OdooRpcParams
                {
                    Service = "common",
                    Method = "authenticate",
                    Args = new object[] { _settings.Database, _settings.Username, _settings.ApiKey, new { } }
                }
            };

            var uid = await PostAsync<int>("jsonrpc", request);
            _cache.Set(UidCacheKey, uid, UidCacheTtl);
            _logger.LogInformation("Authenticated with Odoo. UID: {Uid}", uid);
            return uid;
        }
        finally
        {
            _authLock.Release();
        }
    }

    // ─── Core RPC helper ─────────────────────────────────────────────────────

    private async Task<T> ExecuteKwAsync<T>(string model, string method, object[] args, object? kwargs = null)
    {
        var uid = await GetUidAsync();

        var request = new OdooRpcRequest
        {
            Params = new OdooRpcParams
            {
                Service = "object",
                Method = "execute_kw",
                Args = new object[]
                {
                    _settings.Database,
                    uid,
                    _settings.ApiKey,
                    model,
                    method,
                    args,
                    kwargs ?? new { }
                }
            }
        };

        return await PostAsync<T>("jsonrpc", request);
    }

    private async Task<T> PostAsync<T>(string endpoint, OdooRpcRequest request)
    {
        var response = await _http.PostAsJsonAsync(endpoint, request, _json);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var rpc = JsonSerializer.Deserialize<OdooRpcResponse<T>>(content, _json)
            ?? throw new InvalidOperationException("Empty Odoo response");

        if (!rpc.IsSuccess)
            throw new OdooException(rpc.Error?.Message ?? "Odoo RPC error", rpc.Error?.Data?.Debug);

        return rpc.Result!;
    }

    // ─── Generic / diagnostics ─────────────────────────────────────────────────

    public Task<int> AuthenticateAsync() => GetUidAsync();

    public async Task<List<Dictionary<string, JsonElement>>> SearchReadAsync(
        string model, object[] domain, string[] fields, int limit = 0, object? context = null)
    {
        var kwargs = new Dictionary<string, object?> { ["fields"] = fields };
        if (limit > 0) kwargs["limit"] = limit;
        if (context != null) kwargs["context"] = context;
        return await ExecuteKwAsync<List<Dictionary<string, JsonElement>>>(
            model, "search_read", new object[] { domain }, kwargs);
    }

    public Task<int> CreateAsync(string model, Dictionary<string, object?> values)
        => ExecuteKwAsync<int>(model, "create", new object[] { values });

    public Task<List<int>> CreateManyAsync(string model, IEnumerable<Dictionary<string, object?>> records, object? context = null)
        => ExecuteKwAsync<List<int>>(model, "create", new object[] { records.ToArray() }, context == null ? null : new { context });

    public Task<bool> WriteAsync(string model, int[] ids, Dictionary<string, object?> values, object? context = null)
        => ExecuteKwAsync<bool>(model, "write", new object[] { ids, values }, context == null ? null : new { context });

    public Task<JsonElement> ExecuteAsync(string model, string method, object[] args, object? context = null)
        => ExecuteKwAsync<JsonElement>(model, method, args, context == null ? null : new { context });

    // ─── Products ────────────────────────────────────────────────────────────

    public async Task<List<OdooProduct>> GetProductsAsync(int offset = 0, int limit = 100)
    {
        var fields = new[] { "id", "name", "list_price", "default_code", "barcode", "categ_id", "description_sale", "active", "image_1920" };

        return await ExecuteKwAsync<List<OdooProduct>>(
            "product.template",
            "search_read",
            new object[] { new object[] { new object[] { "active", "=", true } } },
            new { fields, offset, limit, order = "id desc" }
        );
    }

    public async Task<OdooProduct?> GetProductByIdAsync(int odooProductId)
    {
        var fields = new[] { "id", "name", "list_price", "default_code", "barcode", "categ_id", "description_sale", "active", "image_1920" };

        var results = await ExecuteKwAsync<List<OdooProduct>>(
            "product.template",
            "search_read",
            new object[] { new object[] { new object[] { "id", "=", odooProductId } } },
            new { fields, limit = 1 }
        );

        return results.FirstOrDefault();
    }

    public async Task<List<OdooProductVariant>> GetProductVariantsAsync(int[] odooTemplateIds)
    {
        var fields = new[] { "id", "name", "default_code", "product_tmpl_id", "lst_price", "active" };

        return await ExecuteKwAsync<List<OdooProductVariant>>(
            "product.product",
            "search_read",
            new object[]
            {
                new object[]
                {
                    new object[] { "product_tmpl_id", "in", odooTemplateIds },
                    new object[] { "active", "=", true }
                }
            },
            new { fields }
        );
    }

    // ─── Inventory ───────────────────────────────────────────────────────────

    public async Task<List<OdooStockQuant>> GetStockQuantsAsync(int[] odooProductIds, int[] warehouseLocationIds)
    {
        var domain = new object[]
        {
            new object[] { "product_id", "in", odooProductIds },
            new object[] { "location_id", "in", warehouseLocationIds },
            new object[] { "location_id.usage", "=", "internal" }
        };

        var fields = new[] { "id", "product_id", "location_id", "quantity", "reserved_quantity" };

        return await ExecuteKwAsync<List<OdooStockQuant>>(
            "stock.quant",
            "search_read",
            new object[] { domain },
            new { fields }
        );
    }

    /// <summary>
    /// Returns a map: productOdooId → (warehouseId → quantity)
    /// </summary>
    public async Task<Dictionary<int, Dictionary<int, int>>> GetInventoryByStoreAsync(int[] odooProductIds)
    {
        var warehouseLocationIds = _settings.Stores.Values.ToArray();
        var quants = await GetStockQuantsAsync(odooProductIds, warehouseLocationIds);

        var result = new Dictionary<int, Dictionary<int, int>>();

        foreach (var quant in quants)
        {
            var productId = quant.ProductOdooId;
            var locationId = quant.LocationOdooId;
            var available = (int)Math.Max(0, quant.Quantity - quant.ReservedQuantity);

            if (!result.ContainsKey(productId))
                result[productId] = new Dictionary<int, int>();

            result[productId][locationId] = available;
        }

        return result;
    }

    // ─── Sale Orders ─────────────────────────────────────────────────────────

    public async Task<int> FindOrCreatePartnerAsync(string? email, string name)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            var existing = await SearchReadAsync("res.partner",
                new object[] { new object[] { "email", "=", email } }, new[] { "id" }, 1);
            if (existing.Count > 0) return existing[0]["id"].GetInt32();
        }

        return await CreateAsync("res.partner", new()
        {
            ["name"] = string.IsNullOrWhiteSpace(name) ? (email ?? "Website Customer") : name,
            ["email"] = email,
            ["customer_rank"] = 1,
        });
    }

    // ─── Variant / attribute helpers ───────────────────────────────────────────

    public async Task<int> EnsureAttributeAsync(string name)
    {
        var found = await SearchReadAsync("product.attribute",
            new object[] { new object[] { "name", "=", name } }, new[] { "id" }, 1);
        if (found.Count > 0) return found[0]["id"].GetInt32();
        return await CreateAsync("product.attribute", new() { ["name"] = name, ["create_variant"] = "always" });
    }

    public async Task<int> EnsureAttributeValueAsync(int attributeId, string value)
    {
        var found = await SearchReadAsync("product.attribute.value",
            new object[] { new object[] { "name", "=", value }, new object[] { "attribute_id", "=", attributeId } },
            new[] { "id" }, 1);
        if (found.Count > 0) return found[0]["id"].GetInt32();
        return await CreateAsync("product.attribute.value", new() { ["name"] = value, ["attribute_id"] = attributeId });
    }

    public Task AddTemplateAttributeLineAsync(int templateId, int attributeId, int[] valueIds)
        => CreateAsync("product.template.attribute.line", new()
        {
            ["product_tmpl_id"] = templateId,
            ["attribute_id"] = attributeId,
            ["value_ids"] = new object[] { new object[] { 6, 0, valueIds } },
        });

    public async Task<int> GetTemplateIdAsync(int variantProductId)
    {
        // active_test:false so an archived original variant (after variant generation) still resolves.
        var r = await SearchReadAsync("product.product",
            new object[] { new object[] { "id", "=", variantProductId } }, new[] { "product_tmpl_id" }, 1,
            new { active_test = false });
        return r.Count > 0 && r[0].TryGetValue("product_tmpl_id", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
            ? v[0].GetInt32() : 0;
    }

    public async Task<HashSet<int>> GetTemplateAttributeIdsAsync(int templateId)
    {
        var lines = await SearchReadAsync("product.template.attribute.line",
            new object[] { new object[] { "product_tmpl_id", "=", templateId } }, new[] { "attribute_id" });
        var ids = new HashSet<int>();
        foreach (var l in lines)
            if (l.TryGetValue("attribute_id", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0)
                ids.Add(a[0].GetInt32());
        return ids;
    }

    public async Task<List<(int ProductId, List<string> ValueNames)>> GetTemplateVariantsAsync(int templateId)
    {
        var prods = await SearchReadAsync("product.product",
            new object[] { new object[] { "product_tmpl_id", "=", templateId } },
            new[] { "id", "product_template_attribute_value_ids" });

        // Collect all PTAV ids → resolve to value names.
        var ptavIds = new HashSet<int>();
        foreach (var p in prods)
            if (p.TryGetValue("product_template_attribute_value_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number) ptavIds.Add(e.GetInt32());

        var nameByPtav = new Dictionary<int, string>();
        if (ptavIds.Count > 0)
        {
            var ptavs = await SearchReadAsync("product.template.attribute.value",
                new object[] { new object[] { "id", "in", ptavIds.ToArray() } }, new[] { "id", "name" });
            foreach (var pv in ptavs)
                nameByPtav[pv["id"].GetInt32()] = OdooModels.OdooValue.AsString(pv.GetValueOrDefault("name"));
        }

        var result = new List<(int, List<string>)>();
        foreach (var p in prods)
        {
            var names = new List<string>();
            if (p.TryGetValue("product_template_attribute_value_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number && nameByPtav.TryGetValue(e.GetInt32(), out var nm))
                        names.Add(nm);
            result.Add((p["id"].GetInt32(), names));
        }
        return result;
    }

    public async Task<int> CreateSaleOrderAsync(CreateSaleOrderRequest request)
    {
        var orderVals = new Dictionary<string, object>
        {
            ["partner_id"] = request.OdooPartnerId,
            ["warehouse_id"] = request.OdooWarehouseId,
            ["note"] = request.Note ?? string.Empty,
            ["order_line"] = request.Lines.Select(l => new object[]
            {
                0, 0, new Dictionary<string, object>
                {
                    ["product_id"] = l.OdooProductId,
                    ["product_uom_qty"] = l.Quantity,
                    ["price_unit"] = l.PriceUnit
                }
            }).ToArray()
        };

        return await ExecuteKwAsync<int>(
            "sale.order",
            "create",
            new object[] { orderVals }
        );
    }

    public async Task<bool> ConfirmSaleOrderAsync(int odooOrderId)
    {
        var result = await ExecuteKwAsync<bool>(
            "sale.order",
            "action_confirm",
            new object[] { new[] { odooOrderId } }
        );
        return result;
    }

    public async Task<bool> CancelSaleOrderAsync(int odooOrderId)
    {
        var result = await ExecuteKwAsync<bool>(
            "sale.order",
            "action_cancel",
            new object[] { new[] { odooOrderId } }
        );
        return result;
    }

    public async Task ValidateDeliveryAsync(int odooOrderId)
    {
        // Outgoing delivery picking(s) created when the sale order was confirmed.
        var pickings = await SearchReadAsync("stock.picking",
            new object[] { new object[] { "sale_id", "=", odooOrderId }, new object[] { "picking_type_code", "=", "outgoing" } },
            new[] { "id", "state" });

        foreach (var p in pickings)
        {
            var state = p.TryGetValue("state", out var s) ? s.GetString() : null;
            if (state is "done" or "cancel") continue;
            var pid = p["id"].GetInt32();

            // Set each move's done quantity to its demand and mark it picked → full delivery, no backorder.
            var moves = await SearchReadAsync("stock.move",
                new object[] { new object[] { "picking_id", "=", pid } }, new[] { "id", "product_uom_qty" });
            foreach (var m in moves)
            {
                var mid = m["id"].GetInt32();
                var demand = m.TryGetValue("product_uom_qty", out var d) && d.TryGetDouble(out var dv) ? dv : 0;
                await WriteAsync("stock.move", new[] { mid }, new() { ["quantity"] = demand, ["picked"] = true });
            }

            // Validate the picking (reduces on-hand). Skip the backorder wizard.
            await ExecuteAsync("stock.picking", "button_validate", new object[] { new[] { pid } },
                new { skip_backorder = true, picking_ids_not_to_backorder = new[] { pid } });
        }
    }
}

public class OdooException : Exception
{
    public string? Debug { get; }
    public OdooException(string message, string? debug = null) : base(message) => Debug = debug;
}
