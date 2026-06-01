using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services.Payment;
using SterlingLams.Web.Services.Odoo;
using Microsoft.EntityFrameworkCore;

namespace SterlingLams.Web.Controllers;

[Authorize]
public class CheckoutController : Controller
{
    private const string CartSessionKey = "cart";

    private readonly ApplicationDbContext _db;
    private readonly IPaymentService _payment;
    private readonly IOdooService _odoo;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<CheckoutController> _logger;
    private readonly IConfiguration _config;

    public CheckoutController(
        ApplicationDbContext db,
        IPaymentService payment,
        IOdooService odoo,
        UserManager<ApplicationUser> userManager,
        ILogger<CheckoutController> logger,
        IConfiguration config)
    {
        _db = db;
        _payment = payment;
        _odoo = odoo;
        _userManager = userManager;
        _logger = logger;
        _config = config;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var cart = GetCart();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
        var user = await _userManager.GetUserAsync(User);

        // Check which cart items are available in each store
        var cartProductIds = cart.Items.Select(i => i.ProductId).ToList();
        var inventories = await _db.StoreInventories
            .Where(si => cartProductIds.Contains(si.ProductId))
            .ToListAsync();

        var storeAvailability = stores.ToDictionary(
            s => s.Id,
            s => cart.Items.All(item =>
            {
                var inv = inventories.FirstOrDefault(i => i.ProductId == item.ProductId && i.StoreId == s.Id);
                return inv != null && inv.AvailableQuantity >= item.Quantity;
            })
        );

        var vm = new CheckoutViewModel
        {
            Cart = cart,
            Subtotal = cart.Subtotal,
            DeliveryFee = 0,
            PaystackPublicKey = _config["Payment:Paystack:PublicKey"],
            AvailableStores = stores.Select(s => new StorePickupOptionViewModel
            {
                StoreId = s.Id,
                StoreName = s.Name,
                Address = s.Address,
                OpeningHours = s.OpeningHours,
                AllItemsAvailable = storeAvailability.TryGetValue(s.Id, out var avail) && avail
            }).ToList()
        };

        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> PlaceOrder(CheckoutViewModel vm)
    {
        if (!ModelState.IsValid) return View("Index", vm);

        var cart = GetCart();
        if (cart.IsEmpty) return RedirectToAction("Index", "Cart");

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        // Re-validate stock at order time to prevent overselling (the availability shown
        // on the checkout page is only a snapshot and may be stale by now).
        var cartProductIds = cart.Items.Select(i => i.ProductId).ToList();
        var inventories = await _db.StoreInventories
            .Where(si => cartProductIds.Contains(si.ProductId))
            .ToListAsync();

        bool ItemsAvailable() =>
            vm.FulfillmentType == FulfillmentChoice.StorePickup && vm.SelectedStoreId.HasValue
                ? cart.Items.All(i => inventories
                    .Where(si => si.ProductId == i.ProductId && si.StoreId == vm.SelectedStoreId.Value)
                    .Sum(si => si.AvailableQuantity) >= i.Quantity)
                : cart.Items.All(i => inventories
                    .Where(si => si.ProductId == i.ProductId)
                    .Sum(si => si.AvailableQuantity) >= i.Quantity);

        if (!ItemsAvailable())
        {
            TempData["Error"] = "Some items in your cart are no longer available in the requested quantity. Please review your cart.";
            return RedirectToAction("Index");
        }

        // Compute delivery fee server-side (do not trust client-submitted value)
        var deliveryFee = ComputeDeliveryFee(vm.FulfillmentType, vm.DeliveryAddress?.State);

        // Build order
        var orderNumber = $"SL-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";

        var order = new Order
        {
            OrderNumber = orderNumber,
            UserId = user.Id,
            FulfillmentType = vm.FulfillmentType == FulfillmentChoice.StorePickup
                ? FulfillmentType.StorePickup
                : FulfillmentType.Delivery,
            PickupStoreId = vm.FulfillmentType == FulfillmentChoice.StorePickup ? vm.SelectedStoreId : null,
            Subtotal = cart.Subtotal,
            DeliveryFee = deliveryFee,
            Total = cart.Subtotal + deliveryFee,
            Items = cart.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                ProductVariantId = i.VariantId,
                ProductName = i.ProductName,
                VariantName = i.VariantName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        if (vm.FulfillmentType == FulfillmentChoice.Delivery)
        {
            if (vm.DeliveryAddress == null)
            {
                TempData["Error"] = "A delivery address is required for delivery orders.";
                return RedirectToAction("Index");
            }

            var addr = new Address
            {
                UserId = user.Id,
                FullName = vm.DeliveryAddress.FullName,
                Phone = vm.DeliveryAddress.Phone,
                Line1 = vm.DeliveryAddress.Line1,
                Line2 = vm.DeliveryAddress.Line2,
                City = vm.DeliveryAddress.City,
                State = vm.DeliveryAddress.State,
                Country = vm.DeliveryAddress.Country,
                PostalCode = vm.DeliveryAddress.PostalCode
            };
            _db.Addresses.Add(addr);
            await _db.SaveChangesAsync();
            order.DeliveryAddressId = addr.Id;
        }

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // Try to create the corresponding Odoo sale order (non-blocking)
        try
        {
            int odooWarehouseId;
            if (vm.FulfillmentType == FulfillmentChoice.StorePickup && vm.SelectedStoreId.HasValue)
            {
                var store = await _db.Stores.FindAsync(vm.SelectedStoreId.Value);
                odooWarehouseId = store?.OdooWarehouseId ?? 0;
            }
            else
            {
                odooWarehouseId = await _db.Stores
                    .Where(s => s.IsActive)
                    .Select(s => s.OdooWarehouseId)
                    .FirstOrDefaultAsync();
            }

            var odooProductIdMap = await _db.Products
                .Where(p => cartProductIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.OdooProductId);

            var odooOrderId = await _odoo.CreateSaleOrderAsync(new CreateSaleOrderRequest
            {
                OdooPartnerId = 1, // default partner; customers don't have Odoo IDs yet
                OdooWarehouseId = odooWarehouseId,
                Lines = cart.Items
                    .Where(i => odooProductIdMap.ContainsKey(i.ProductId))
                    .Select(i => new SaleOrderLine
                    {
                        OdooProductId = odooProductIdMap[i.ProductId],
                        Quantity = i.Quantity,
                        PriceUnit = i.UnitPrice
                    }).ToList(),
                Note = $"Sterlin Glams Web Order: {orderNumber}"
            });

            order.OdooSaleOrderId = odooOrderId;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Odoo sale order for {OrderNumber} — payment will still proceed", orderNumber);
        }

        // Initiate payment
        var callbackUrl = Url.Action("PaymentCallback", "Checkout", null, Request.Scheme) ?? string.Empty;
        var result = await _payment.InitiatePaymentAsync(new InitiatePaymentRequest
        {
            OrderNumber = order.OrderNumber,
            Amount = order.Total,
            Currency = order.Currency,
            CustomerEmail = user.Email ?? string.Empty,
            CustomerName = user.FullName,
            CallbackUrl = callbackUrl,
            Metadata = new Dictionary<string, string> { ["order_id"] = order.Id.ToString() }
        });

        if (!result.Success)
        {
            _logger.LogError("Payment initiation failed for order {OrderNumber}: {Error}", orderNumber, result.ErrorMessage);
            ModelState.AddModelError("", "Payment could not be initiated. Please try again.");
            return View("Index", vm);
        }

        return Redirect(result.AuthorizationUrl!);
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> PaymentCallback(string? reference, string? trxref, string? session_id)
    {
        // Paystack returns reference/trxref; Stripe Checkout returns session_id.
        var refToVerify = reference ?? trxref ?? session_id;
        if (string.IsNullOrEmpty(refToVerify)) return RedirectToAction("Index", "Home");

        var result = await _payment.VerifyPaymentAsync(refToVerify);

        if (!result.IsPaid)
        {
            TempData["Error"] = "Payment could not be verified. Please contact support.";
            return RedirectToAction("Index", "Cart");
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == result.OrderNumber);

        // Defence-in-depth: confirm the verified amount covers the order total.
        if (order != null && result.AmountPaid < order.Total)
        {
            _logger.LogWarning("Payment for order {OrderNumber} returned amount {Amount} below total {Total} — not confirming",
                order.OrderNumber, result.AmountPaid, order.Total);
            TempData["Error"] = "The amount paid did not match the order total. Please contact support.";
            return RedirectToAction("Index", "Cart");
        }

        if (order != null && !order.IsPaid)
        {
            order.IsPaid = true;
            order.PaidAt = DateTime.UtcNow;
            order.Status = OrderStatus.Confirmed;
            order.PaymentReference = refToVerify;
            order.PaymentProvider = _payment.ProviderName;
            await _db.SaveChangesAsync();
        }

        // Clear cart
        HttpContext.Session.Remove(CartSessionKey);

        return RedirectToAction("Confirmation", new { orderNumber = result.OrderNumber });
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult GetDeliveryFee(string state)
    {
        var fee = ComputeDeliveryFee(FulfillmentChoice.Delivery, state);
        return Json(new { fee, formatted = fee == 0 ? "Free" : $"₦{fee:N0}" });
    }

    private decimal ComputeDeliveryFee(FulfillmentChoice fulfillmentType, string? state)
    {
        if (fulfillmentType == FulfillmentChoice.StorePickup) return 0m;
        if (string.IsNullOrWhiteSpace(state)) return 0m;
        var lagosRate = _config.GetValue<decimal>("DeliveryFees:Lagos", 2500m);
        var defaultRate = _config.GetValue<decimal>("DeliveryFees:Default", 5000m);
        return state.Trim().Equals("Lagos", StringComparison.OrdinalIgnoreCase) ? lagosRate : defaultRate;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Confirmation(string orderNumber)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber && o.UserId == _userManager.GetUserId(User));

        if (order == null) return NotFound();

        return View(order);
    }

    private CartViewModel GetCart()
    {
        var json = HttpContext.Session.GetString(CartSessionKey);
        if (string.IsNullOrEmpty(json)) return new CartViewModel();
        return JsonSerializer.Deserialize<CartViewModel>(json) ?? new CartViewModel();
    }
}
