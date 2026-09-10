using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SharePointToAzureSearch.Core.Data;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations;

/// <inheritdoc />
[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20260910163814_AddChatConversationTokenUsage")]
public partial class AddChatConversationTokenUsage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "InputTokenCount",
            table: "ChatConversations",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "OutputTokenCount",
            table: "ChatConversations",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "TotalTokenCount",
            table: "ChatConversations",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "InputTokenCount", table: "ChatConversations");
        migrationBuilder.DropColumn(name: "OutputTokenCount", table: "ChatConversations");
        migrationBuilder.DropColumn(name: "TotalTokenCount", table: "ChatConversations");
    }
}
