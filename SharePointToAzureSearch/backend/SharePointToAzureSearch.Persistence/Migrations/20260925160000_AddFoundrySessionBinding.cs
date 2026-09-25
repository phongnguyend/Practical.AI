using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SharePointToAzureSearch.Persistence;

namespace SharePointToAzureSearch.Persistence.Migrations;

[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20260925160000_AddFoundrySessionBinding")]
public sealed class AddFoundrySessionBinding : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("FoundryEndpoint", "ChatConversations", type: "nvarchar(2048)", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<string>("FoundrySessionId", "ChatConversations", type: "nvarchar(200)", maxLength: 200, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("FoundryEndpoint", "ChatConversations");
        migrationBuilder.DropColumn("FoundrySessionId", "ChatConversations");
    }
}
