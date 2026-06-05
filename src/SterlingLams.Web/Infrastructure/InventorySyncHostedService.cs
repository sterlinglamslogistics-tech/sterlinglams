using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Background service that periodically syncs inventory from Odoo
/// into the local StoreInventory table.
/// </summary>
public class InventorySyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InventorySyncHostedService> _logger;
    private readonly TimeSpan _interval;

    public InventorySyncHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<InventorySyncHostedService> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Configurable cadence (minutes); default 2. Per-order sync already updates instantly,
        // so this is the catch-all for stock changed directly in Odoo / at the POS.
        var minutes = config.GetValue("Odoo:InventorySyncMinutes", 2.0);
        if (minutes < 0.5) minutes = 0.5;
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Inventory sync service started. Interval: {Interval}", _interval);

        // Initial delay to let the app fully start
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncInventoryAsync(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task SyncInventoryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

            _logger.LogInformation("Syncing inventory from Odoo (products + variants)...");
            await inventory.SyncAllAsync();   // variant-aware: simple products + variant ids
            _logger.LogInformation("Inventory sync complete.");
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inventory sync failed. Will retry in {Interval}.", _interval);
        }
    }
}
