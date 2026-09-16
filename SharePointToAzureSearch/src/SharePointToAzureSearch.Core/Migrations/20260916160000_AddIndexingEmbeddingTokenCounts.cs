using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SharePointToAzureSearch.Core.Data;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations;

/// <inheritdoc />
[DbContext(typeof(SharePointIndexDbContext))]
[Migration("20260916160000_AddIndexingEmbeddingTokenCounts")]
public partial class AddIndexingEmbeddingTokenCounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "EmbeddingTokenCount",
            table: "SharePointIndexedFiles",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "EmbeddingTokenCount",
            table: "ChatMessageAttachmentFiles",
            type: "bigint",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "EmbeddingTokenCount", table: "ChatMessageAttachmentFiles");
        migrationBuilder.DropColumn(name: "EmbeddingTokenCount", table: "SharePointIndexedFiles");
    }
}
