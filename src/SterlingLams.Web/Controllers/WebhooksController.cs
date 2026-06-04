using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Odoo;
using SterlingLams.Web.Services.Payment;
using SterlingLams.Web.Services.Inventory;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text;
using Stripe;

namespace SterlingLams.Web.Controllers;

[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IPaymentService _payment;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        ApplicationDbContext db,
        IPaymentService payment,
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<WebhooksController> logger)
    {
        _db = db;
        _payment = payment;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    // Background: confirm the Odoo sale order, optionally validate its delivery (drop on-hand),
    // and refresh the ordered products' stock. Runs in its own scope (the request scope is gone).
    private void ScheduleOdooFulfillment(int? odooSaleOrderId, int localOrderId, string orderNumber)
    {
        if (!_config.GetValue<bool>("Odoo:AutoConfirmOrders") || odooSaleOrderId is not int soId || soId <= 0)
            return;

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var odoo = sp.GetRequiredService<IOdooService>();
            var cfg = sp.GetRequiredService<IConfiguration>();
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var inventory = sp.GetRequiredService<IInventoryService>();
            try
            {
                await odoo.ConfirmSaleOrderAsync(soId);
                if (cfg.GetValue<bool>("Odoo:AutoValidateDelivery"))
                    await odoo.ValidateDeliveryAsync(soId);

                var variantIds = await db.OrderItems
                    .Where(oi => oi.OrderId == localOrderId)
                    .Join(db.Products, oi => oi.ProductId, p => p.Id, (oi, p) => p.OdooProductId)
                    .Where(v => v != 0).Distinct().ToArrayAsync();
                if (variantIds.Length > 0)
                    await inventory.SyncProductInventoryAsync(variantIds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Odoo post-payment processing failed for {OrderNumber}", orderNumber);
            }
        });
    }

    [HttpPost("paystack")]
    public async Task<IActionResult> PaystackWebhook()
    {
        // Read raw body for HMAC validation
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();

        var signature = Request.Headers["x-paystack-signature"].FirstOrDefault() ?? string.Empty;

        if (!await _payment.ValidateWebhookAsync(payload, signature))
        {
            _logger.LogWarning("Invalid Paystack webhook signature");
            return Unauthorized();
        }

        // Parse event type
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var eventType = root.TryGetProperty("event", out var evtProp) ? evtProp.GetString() : null;
        _logger.LogInformation("Paystack webhook event: {Event}", eventType);

        if (eventType == "charge.success")
        {
            var data = root.GetProperty("data");
            var reference = data.GetProperty("reference").GetString();
            var amount = data.GetProperty("amount").GetDecimal() / 100m;

            // Match on the order number we passed in metadata (exact), falling back to
            // the stored payment reference. Never substring-match — order numbers share
            // a common prefix and would collide.
            string? orderNumber = null;
            if (data.TryGetProperty("metadata", out var meta)
                && meta.ValueKind == System.Text.Json.JsonValueKind.Object
                && meta.TryGetProperty("order_number", out var onProp))
            {
                orderNumber = onProp.GetString();
            }

            var order = await _db.Orders
                .FirstOrDefaultAsync(o => o.PaymentReference == reference
                    || (orderNumber != null && o.OrderNumber == orderNumber));

            if (order == null)
            {
                _logger.LogWarning("Paystack webhook: no order matched reference {Reference} / order_number {OrderNumber}", reference, orderNumber);
                return Ok();
            }

            // Verify the captured amount covers the order total before confirming.
            if (amount < order.Total)
            {
                _logger.LogWarning("Paystack webhook: amount {Amount} is less than order {OrderNumber} total {Total} — not confirming",
                    amount, order.OrderNumber, order.Total);
                return Ok();
            }

            if (!order.IsPaid)
            {
                order.IsPaid = true;
                order.PaidAt = DateTime.UtcNow;
                order.Status = OrderStatus.Confirmed;
                order.PaymentReference = reference;
                order.PaymentProvider = "Paystack";
                await _db.SaveChangesAsync();

                // Confirm + (optionally) validate delivery + refresh stock in the background.
                ScheduleOdooFulfillment(order.OdooSaleOrderId, order.Id, order.OrderNumber);

                _logger.LogInformation("Order {OrderNumber} marked as paid via webhook", order.OrderNumber);
            }
        }

        return Ok();
    }

    [HttpPost("stripe")]
    public async Task<IActionResult> StripeWebhook()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault() ?? string.Empty;
        var webhookSecret = _config["Payment:Stripe:WebhookSecret"] ?? string.Empty;

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signature, webhookSecret);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Invalid Stripe webhook signature");
            return Unauthorized();
        }

        _logger.LogInformation("Stripe webhook event: {Type}", stripeEvent.Type);

        if (stripeEvent.Type == EventTypes.CheckoutSessionCompleted)
        {
            var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
            if (session?.PaymentStatus == "paid"
                && session.Metadata.TryGetValue("order_number", out var orderNumber))
            {
                var order = await _db.Orders
                    .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);

                // Verify the captured amount covers the order total before confirming.
                var amountPaid = session.AmountTotal.HasValue ? session.AmountTotal.Value / 100m : 0m;
                if (order != null && amountPaid < order.Total)
                {
                    _logger.LogWarning("Stripe webhook: amount {Amount} is less than order {OrderNumber} total {Total} — not confirming",
                        amountPaid, order.OrderNumber, order.Total);
                    return Ok();
                }

                if (order != null && !order.IsPaid)
                {
                    order.IsPaid = true;
                    order.PaidAt = DateTime.UtcNow;
                    order.Status = OrderStatus.Confirmed;
                    order.PaymentReference = session.Id;
                    order.PaymentProvider = "Stripe";
                    await _db.SaveChangesAsync();

                    ScheduleOdooFulfillment(order.OdooSaleOrderId, order.Id, order.OrderNumber);

                    _logger.LogInformation("Order {OrderNumber} marked as paid via Stripe webhook", orderNumber);
                }
            }
        }

        return Ok();
    }
}
