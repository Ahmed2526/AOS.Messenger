using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase7ReceiptsAndPresence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastDeliveredAtUtc",
                table: "UserConversationStates",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastDeliveredMessageId",
                table: "UserConversationStates",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAtUtc",
                table: "AspNetUsers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserConversationStates_ConversationId_LastDeliveredMessageId",
                table: "UserConversationStates",
                columns: new[] { "ConversationId", "LastDeliveredMessageId" });

            migrationBuilder.AddForeignKey(
                name: "FK_UserConversationStates_Messages_ConversationId_LastDeliveredMessageId",
                table: "UserConversationStates",
                columns: new[] { "ConversationId", "LastDeliveredMessageId" },
                principalTable: "Messages",
                principalColumns: new[] { "ConversationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserConversationStates_Messages_ConversationId_LastDeliveredMessageId",
                table: "UserConversationStates");

            migrationBuilder.DropIndex(
                name: "IX_UserConversationStates_ConversationId_LastDeliveredMessageId",
                table: "UserConversationStates");

            migrationBuilder.DropColumn(
                name: "LastDeliveredAtUtc",
                table: "UserConversationStates");

            migrationBuilder.DropColumn(
                name: "LastDeliveredMessageId",
                table: "UserConversationStates");

            migrationBuilder.DropColumn(
                name: "LastSeenAtUtc",
                table: "AspNetUsers");
        }
    }
}
