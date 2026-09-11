using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations
{
    /// <summary>Stores the provider-returned model identifier for each assistant response.</summary>
    public partial class AddChatMessageModelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "ChatMessages",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "ChatMessages");
        }
    }
}
