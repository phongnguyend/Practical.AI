using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SharePointToAzureSearch.Core.Data;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations;

/// <inheritdoc />
[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20260916170000_AddWebhookSubscriptionSettings")]
public partial class AddWebhookSubscriptionSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ClientState",
            table: "WebhookSubscriptions",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "AutoRenewEnabled",
            table: "WebhookSubscriptions",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("UPDATE [WebhookSubscriptions] SET [AutoRenewEnabled] = 1 WHERE [Name] = 'Default'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AutoRenewEnabled", table: "WebhookSubscriptions");
        migrationBuilder.DropColumn(name: "ClientState", table: "WebhookSubscriptions");
    }
}
