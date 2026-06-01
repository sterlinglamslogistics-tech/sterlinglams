using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductOdooIdFilteredUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_OdooProductId",
                table: "Products");

            migrationBuilder.CreateIndex(
                name: "IX_Products_OdooProductId",
                table: "Products",
                column: "OdooProductId",
                unique: true,
                filter: "\"OdooProductId\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_OdooProductId",
                table: "Products");

            migrationBuilder.CreateIndex(
                name: "IX_Products_OdooProductId",
                table: "Products",
                column: "OdooProductId",
                unique: true);
        }
    }
}
