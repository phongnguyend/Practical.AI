using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SharePointToAzureSearch.Core.Data;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations;

/// <inheritdoc />
[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20260911093000_AddAgentDefinitions")]
public partial class AddAgentDefinitions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AgentDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Instructions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentDefinitions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AgentDefinitions_Name",
            table: "AgentDefinitions",
            column: "Name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AgentDefinitions");
    }
}
