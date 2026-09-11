using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations
{
    /// <inheritdoc />
    public partial class LinkWebhookSubscriptionsToGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GraphSubscriptionId",
                table: "WebhookSubscriptions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSubscriptions_GraphSubscriptionId",
                table: "WebhookSubscriptions",
                column: "GraphSubscriptionId",
                unique: true,
                filter: "[GraphSubscriptionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookSubscriptions_GraphSubscriptionId",
                table: "WebhookSubscriptions");

            migrationBuilder.DropColumn(
                name: "GraphSubscriptionId",
                table: "WebhookSubscriptions");
        }
    }
}
