using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentModelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "AgentDefinitions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "AgentDefinitions");
        }
    }
}
