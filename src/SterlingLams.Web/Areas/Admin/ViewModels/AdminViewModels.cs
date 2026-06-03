using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.ViewModels
{
    // ─── Dashboard ────────────────────────────────────────────────────────
    public class DashboardViewModel
    {
        public decimal RevenueToday { get; set; }
        public decimal RevenueThisMonth { get; set; }
        public int OrdersToday { get; set; }
        public int OrdersPending { get; set; }
        public int TotalProducts { get; set; }
        public int LowStockAlerts { get; set; }
        public List<RecentOrderRow> RecentOrders { get; set; } = new();
        public List<LowStockRow> LowStockItems { get; set; } = new();
    }

    public class RecentOrderRow
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public decimal Total { get; set; }
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public class LowStockRow
    {
        public string ProductName { get; set; } = "";
        public string StoreName { get; set; } = "";
        public int Quantity { get; set; }
    }

    // ─── Orders ───────────────────────────────────────────────────────────
    public class AdminOrderListViewModel
    {
        public List<AdminOrderRow> Orders { get; set; } = new();
        public string StatusFilter { get; set; } = "";
        public string SearchQuery { get; set; } = "";
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
    }

    public class AdminOrderRow
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        public decimal Total { get; set; }
        public string Status { get; set; } = "";
        public bool IsPaid { get; set; }
        public string FulfillmentType { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public class AdminOrderDetailViewModel
    {
        public Order Order { get; set; } = null!;
        public string CustomerName { get; set; } = "";
        public string CustomerEmail { get; set; } = "";
        public List<string> AvailableStatuses { get; set; } = new()
        {
            "Pending", "Confirmed", "Processing", "ReadyForPickup", "Shipped", "Delivered", "Cancelled"
        };
    }

    // ─── Shared pager ───────────────────────────────────────────────────────
    public record PagerModel(int CurrentPage, int TotalPages, string Action, string Controller, Dictionary<string, string> RouteValues);

    // ─── Products ─────────────────────────────────────────────────────────
    public class AdminProductListViewModel
    {
        public List<Product> Products { get; set; } = new();
        public string SearchQuery { get; set; } = "";
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
    }

    public class AdminProductEditViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Name is required.")]
        public string Name { get; set; } = "";

        public string Slug { get; set; } = "";
        public string Description { get; set; } = "";

        [Range(0, double.MaxValue, ErrorMessage = "Price must be zero or more.")]
        public decimal Price { get; set; }

        public string? Material { get; set; }
        public string? Carat { get; set; }
        public string? GemstoneType { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsFeatured { get; set; }

        // Optional: only set when the product is linked to an Odoo template.
        // Left blank for manually-created products.
        public int? OdooProductId { get; set; }

        [Required(ErrorMessage = "Please select a category.")]
        public int? CategoryId { get; set; }

        public List<Category> Categories { get; set; } = new();
        public IFormFile? ImageFile { get; set; }
        public List<ProductImage> ExistingImages { get; set; } = new();
    }

    // ─── Inventory ────────────────────────────────────────────────────────
    public class AdminInventoryViewModel
    {
        public List<Store> Stores { get; set; } = new();   // for the store filter
        public int? SelectedStoreId { get; set; }
        public string SearchQuery { get; set; } = "";
        public List<InventoryProductRow> Rows { get; set; } = new();
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public DateTime? LastSyncedAt { get; set; }
    }

    public class InventoryProductRow
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public string StoreName { get; set; } = "";
        public string Sku { get; set; } = "";
        public int QuantityOnHand { get; set; }
        public int QuantityReserved { get; set; }
        public int Available => Math.Max(0, QuantityOnHand - QuantityReserved);
        public bool IsLowStock => Available < 3;
    }

    // ─── Stores ───────────────────────────────────────────────────────────
    public class AdminStoreEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? OpeningHours { get; set; }
        public int OdooWarehouseId { get; set; }
        public int OdooStockLocationId { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
