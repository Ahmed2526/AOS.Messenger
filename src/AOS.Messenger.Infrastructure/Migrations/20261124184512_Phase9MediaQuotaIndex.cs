using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase9MediaQuotaIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Media_UploaderId_CreatedAtUtc",
                table: "Media",
                columns: new[] { "UploaderId", "CreatedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "State", "DeclaredSizeBytes", "VerifiedSizeBytes" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Media_UploaderId_CreatedAtUtc",
                table: "Media");
        }
    }
}
