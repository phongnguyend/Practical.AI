using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations
{
    /// <inheritdoc />
    public partial class RenameUploadsToAttachmentFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessageAttachments_Uploads_UploadId",
                table: "ChatMessageAttachments");

            migrationBuilder.RenameTable(
                name: "Uploads",
                newName: "ChatMessageAttachmentFiles");

            migrationBuilder.RenameColumn(
                name: "UploadId",
                table: "ChatMessageAttachments",
                newName: "AttachmentFileId");

            migrationBuilder.RenameIndex(
                name: "IX_ChatMessageAttachments_UploadId",
                table: "ChatMessageAttachments",
                newName: "IX_ChatMessageAttachments_AttachmentFileId");

            migrationBuilder.RenameIndex(
                name: "IX_ChatMessageAttachments_MessageId_UploadId",
                table: "ChatMessageAttachments",
                newName: "IX_ChatMessageAttachments_MessageId_AttachmentFileId");

            migrationBuilder.RenameIndex(
                name: "IX_Uploads_CreatedAtUtc",
                table: "ChatMessageAttachmentFiles",
                newName: "IX_ChatMessageAttachmentFiles_CreatedAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_Uploads_Status",
                table: "ChatMessageAttachmentFiles",
                newName: "IX_ChatMessageAttachmentFiles_Status");

            migrationBuilder.AddColumn<Guid>(
                name: "ChatMessageAttachmentId",
                table: "ChatMessageAttachmentFiles",
                type: "uniqueidentifier",
                nullable: true);

            // Preserve existing links by choosing their first attachment row as the file's canonical link.
            migrationBuilder.Sql("""
                UPDATE files
                SET ChatMessageAttachmentId = links.Id
                FROM ChatMessageAttachmentFiles AS files
                CROSS APPLY (
                    SELECT TOP (1) attachments.Id
                    FROM ChatMessageAttachments AS attachments
                    WHERE attachments.AttachmentFileId = files.Id
                    ORDER BY attachments.CreatedAtUtc, attachments.Id
                ) AS links;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageAttachmentFiles_ChatMessageAttachmentId",
                table: "ChatMessageAttachmentFiles",
                column: "ChatMessageAttachmentId",
                unique: true,
                filter: "[ChatMessageAttachmentId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessageAttachments_ChatMessageAttachmentFiles_AttachmentFileId",
                table: "ChatMessageAttachments",
                column: "AttachmentFileId",
                principalTable: "ChatMessageAttachmentFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessageAttachmentFiles_ChatMessageAttachments_ChatMessageAttachmentId",
                table: "ChatMessageAttachmentFiles",
                column: "ChatMessageAttachmentId",
                principalTable: "ChatMessageAttachments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessageAttachments_ChatMessageAttachmentFiles_AttachmentFileId",
                table: "ChatMessageAttachments");

            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessageAttachmentFiles_ChatMessageAttachments_ChatMessageAttachmentId",
                table: "ChatMessageAttachmentFiles");

            migrationBuilder.DropIndex(
                name: "IX_ChatMessageAttachmentFiles_ChatMessageAttachmentId",
                table: "ChatMessageAttachmentFiles");

            migrationBuilder.DropColumn(
                name: "ChatMessageAttachmentId",
                table: "ChatMessageAttachmentFiles");

            migrationBuilder.RenameColumn(
                name: "AttachmentFileId",
                table: "ChatMessageAttachments",
                newName: "UploadId");

            migrationBuilder.RenameIndex(
                name: "IX_ChatMessageAttachments_MessageId_AttachmentFileId",
                table: "ChatMessageAttachments",
                newName: "IX_ChatMessageAttachments_MessageId_UploadId");

            migrationBuilder.RenameIndex(
                name: "IX_ChatMessageAttachments_AttachmentFileId",
                table: "ChatMessageAttachments",
                newName: "IX_ChatMessageAttachments_UploadId");

            migrationBuilder.RenameIndex(
                name: "IX_ChatMessageAttachmentFiles_CreatedAtUtc",
                table: "ChatMessageAttachmentFiles",
                newName: "IX_Uploads_CreatedAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_ChatMessageAttachmentFiles_Status",
                table: "ChatMessageAttachmentFiles",
                newName: "IX_Uploads_Status");

            migrationBuilder.RenameTable(
                name: "ChatMessageAttachmentFiles",
                newName: "Uploads");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessageAttachments_Uploads_UploadId",
                table: "ChatMessageAttachments",
                column: "UploadId",
                principalTable: "Uploads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
