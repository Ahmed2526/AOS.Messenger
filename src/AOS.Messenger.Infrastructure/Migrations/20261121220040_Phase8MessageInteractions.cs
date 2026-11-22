using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase8MessageInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsDeletedForEveryone",
                table: "Messages",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit");

            migrationBuilder.CreateTable(
                name: "MessageReactions",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Emoji = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageReactions", x => new { x.MessageId, x.UserId });
                    table.ForeignKey(
                        name: "FK_MessageReactions_Messages_ConversationId_MessageId",
                        columns: x => new { x.ConversationId, x.MessageId },
                        principalTable: "Messages",
                        principalColumns: new[] { "ConversationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserMessageDeletions",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMessageDeletions", x => new { x.UserId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_UserMessageDeletions_Messages_ConversationId_MessageId",
                        columns: x => new { x.ConversationId, x.MessageId },
                        principalTable: "Messages",
                        principalColumns: new[] { "ConversationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageReactions_ConversationId_MessageId",
                table: "MessageReactions",
                columns: new[] { "ConversationId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserMessageDeletions_ConversationId_MessageId",
                table: "UserMessageDeletions",
                columns: new[] { "ConversationId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserMessageDeletions_ConversationId_UserId_MessageId",
                table: "UserMessageDeletions",
                columns: new[] { "ConversationId", "UserId", "MessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageReactions");

            migrationBuilder.DropTable(
                name: "UserMessageDeletions");

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeletedForEveryone",
                table: "Messages",
                type: "bit",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: false);
        }
    }
}
