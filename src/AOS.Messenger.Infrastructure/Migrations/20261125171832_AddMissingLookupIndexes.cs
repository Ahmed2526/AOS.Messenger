using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMissingLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Conversations_Participant2Id",
                table: "Conversations",
                column: "Participant2Id");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_EmailConfirmed_CreatedAtUtc",
                table: "AspNetUsers",
                columns: new[] { "EmailConfirmed", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_Participant2Id",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_EmailConfirmed_CreatedAtUtc",
                table: "AspNetUsers");
        }
    }
}
