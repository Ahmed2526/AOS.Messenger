using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfilePhotosAndThumbnails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThumbnailContentType",
                table: "Media",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailObjectKey",
                table: "Media",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ThumbnailSizeBytes",
                table: "Media",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarContentType",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarObjectKey",
                table: "AspNetUsers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AvatarSizeBytes",
                table: "AspNetUsers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarThumbnailObjectKey",
                table: "AspNetUsers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AvatarThumbnailSizeBytes",
                table: "AspNetUsers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvatarUpdatedAtUtc",
                table: "AspNetUsers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarVersion",
                table: "AspNetUsers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingAvatarUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    DeclaredContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DeclaredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PurgedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAvatarUploads", x => x.Id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_PendingAvatarUploads_ObjectKey_Prefix", "[ObjectKey] LIKE N'pending/%'");
                    table.CheckConstraint("CK_PendingAvatarUploads_Sizes_Positive", "[DeclaredSizeBytes] > 0 AND ([VerifiedSizeBytes] IS NULL OR [VerifiedSizeBytes] > 0)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Media_ThumbnailObjectKey",
                table: "Media",
                column: "ThumbnailObjectKey",
                unique: true,
                filter: "[ThumbnailObjectKey] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Media_ThumbnailObjectKey",
                table: "Media",
                sql: "([ThumbnailObjectKey] IS NULL OR [ThumbnailObjectKey] LIKE N'thumbs/%')");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_AvatarObjectKey",
                table: "AspNetUsers",
                column: "AvatarObjectKey",
                unique: true,
                filter: "[AvatarObjectKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_AvatarThumbnailObjectKey",
                table: "AspNetUsers",
                column: "AvatarThumbnailObjectKey",
                unique: true,
                filter: "[AvatarThumbnailObjectKey] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_AvatarKeys",
                table: "AspNetUsers",
                sql: "(([AvatarObjectKey] IS NULL AND [AvatarThumbnailObjectKey] IS NULL) OR ([AvatarObjectKey] LIKE N'avatars/%' AND [AvatarThumbnailObjectKey] LIKE N'avatars/%' AND [AvatarObjectKey] <> [AvatarThumbnailObjectKey]))");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAvatarUploads_ObjectKey",
                table: "PendingAvatarUploads",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingAvatarUploads_State_PurgedAtUtc_CreatedAtUtc",
                table: "PendingAvatarUploads",
                columns: new[] { "State", "PurgedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAvatarUploads_StorageObjectId",
                table: "PendingAvatarUploads",
                column: "StorageObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingAvatarUploads_UserId_CreatedAtUtc",
                table: "PendingAvatarUploads",
                columns: new[] { "UserId", "CreatedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "State", "DeclaredSizeBytes", "VerifiedSizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAvatarUploads_UserId_State_CreatedAtUtc",
                table: "PendingAvatarUploads",
                columns: new[] { "UserId", "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingAvatarUploads");

            migrationBuilder.DropIndex(
                name: "IX_Media_ThumbnailObjectKey",
                table: "Media");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Media_ThumbnailObjectKey",
                table: "Media");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_AvatarObjectKey",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_AvatarThumbnailObjectKey",
                table: "AspNetUsers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_AvatarKeys",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ThumbnailContentType",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ThumbnailObjectKey",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ThumbnailSizeBytes",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "AvatarContentType",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AvatarObjectKey",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AvatarSizeBytes",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AvatarThumbnailObjectKey",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AvatarThumbnailSizeBytes",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AvatarUpdatedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AvatarVersion",
                table: "AspNetUsers");
        }
    }
}
