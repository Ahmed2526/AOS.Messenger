using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStickerMessageLookupIncludes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_StickerId",
                table: "Messages");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_StickerId",
                table: "Messages",
                column: "StickerId",
                filter: "[StickerId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "ConversationId", "CreatedAtUtc", "IsDeletedForEveryone" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_StickerId",
                table: "Messages");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_StickerId",
                table: "Messages",
                column: "StickerId",
                filter: "[StickerId] IS NOT NULL");
        }
    }
}
