using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharePointToAzureSearch.Core.Migrations
{
    /// <summary>Associates each conversation with the agent whose instructions it uses.</summary>
    public partial class AssociateConversationsWithAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "ChatConversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversations_AgentId",
                table: "ChatConversations",
                column: "AgentId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatConversations_AgentDefinitions_AgentId",
                table: "ChatConversations",
                column: "AgentId",
                principalTable: "AgentDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatConversations_AgentDefinitions_AgentId",
                table: "ChatConversations");

            migrationBuilder.DropIndex(
                name: "IX_ChatConversations_AgentId",
                table: "ChatConversations");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "ChatConversations");
        }
    }
}
