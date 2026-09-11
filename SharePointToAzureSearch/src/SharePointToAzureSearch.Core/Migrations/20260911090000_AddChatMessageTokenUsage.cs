using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SharePointToAzureSearch.Core.Data;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations;

/// <inheritdoc />
[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20260911090000_AddChatMessageTokenUsage")]
public partial class AddChatMessageTokenUsage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "InputTokenCount",
            table: "ChatMessages",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "OutputTokenCount",
            table: "ChatMessages",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "TotalTokenCount",
            table: "ChatMessages",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "InputTokenCount", table: "ChatMessages");
        migrationBuilder.DropColumn(name: "OutputTokenCount", table: "ChatMessages");
        migrationBuilder.DropColumn(name: "TotalTokenCount", table: "ChatMessages");
    }
}
