using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Infrastructure.Odoo;

/// <summary>
/// One-off (PAYMENT_INIT_TEST=1): calls the configured payment provider's "initiate" with a
/// dummy order to confirm the API keys work and an authorization URL comes back.
/// </summary>
public static class PaymentInitTest
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var payment = scope.ServiceProvider.GetRequiredService<IPaymentService>();

        logger.LogInformation("PAYMENT_TEST: provider = {Provider}", payment.ProviderName);

        var result = await payment.InitiatePaymentAsync(new InitiatePaymentRequest
        {
            OrderNumber = $"TEST-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Amount = 5000m,
            Currency = "NGN",
            CustomerEmail = "test.customer@sterlinglams.com",
            CustomerName = "Test Customer",
            CallbackUrl = "http://localhost:5000/Checkout/PaymentCallback",
            Metadata = new() { ["order_id"] = "0" }
        });

        if (result.Success)
            logger.LogInformation("PAYMENT_TEST: ✔ OK. authorization_url = {Url}", result.AuthorizationUrl);
        else
            logger.LogError("PAYMENT_TEST: FAILED — {Error}", result.ErrorMessage);
    }
}
