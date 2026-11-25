using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStickersSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages");

            migrationBuilder.AddColumn<Guid>(
                name: "StickerId",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StickerPacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AuthorName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsSystemPack = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StickerPacks", x => x.Id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_StickerPacks_Ownership", "([IsSystemPack] = 1 AND [OwnerUserId] IS NULL AND [Slug] IS NOT NULL) OR ([IsSystemPack] = 0 AND [OwnerUserId] IS NOT NULL AND [Slug] IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Stickers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    PackId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploaderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    StorageObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ObjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StaticAssetPath = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeclaredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    IsAnimated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    EmojiTags = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PurgedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stickers", x => x.Id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_Stickers_Dimensions_Positive", "[DeclaredSizeBytes] > 0 AND ([VerifiedSizeBytes] IS NULL OR [VerifiedSizeBytes] > 0) AND [Width] > 0 AND [Height] > 0");
                    table.CheckConstraint("CK_Stickers_StorageSource", "([UploaderId] IS NULL AND [StaticAssetPath] IS NOT NULL AND [ObjectKey] IS NULL AND [State] = 1) OR ([UploaderId] IS NOT NULL AND [StaticAssetPath] IS NULL AND [ObjectKey] IS NOT NULL AND (([State] IN (0, 2) AND [ObjectKey] LIKE N'pending/%') OR ([State] IN (1, 3) AND [ObjectKey] LIKE N'stickers/%')))");
                    table.ForeignKey(
                        name: "FK_Stickers_StickerPacks_PackId",
                        column: x => x.PackId,
                        principalTable: "StickerPacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserFavoriteStickers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StickerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFavoriteStickers", x => new { x.UserId, x.StickerId });
                    table.ForeignKey(
                        name: "FK_UserFavoriteStickers_Stickers_StickerId",
                        column: x => x.StickerId,
                        principalTable: "Stickers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserRecentStickers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StickerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UseCount = table.Column<int>(type: "int", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRecentStickers", x => new { x.UserId, x.StickerId });
                    table.ForeignKey(
                        name: "FK_UserRecentStickers_Stickers_StickerId",
                        column: x => x.StickerId,
                        principalTable: "Stickers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_StickerId",
                table: "Messages",
                column: "StickerId",
                filter: "[StickerId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages",
                sql: "([IsDeletedForEveryone] = 1 AND [Content] IS NULL AND [StickerId] IS NULL) OR ([IsDeletedForEveryone] = 0 AND (([StickerId] IS NOT NULL AND [Content] IS NULL AND [HasAttachments] = 0) OR ([StickerId] IS NULL AND ([Content] IS NOT NULL OR [HasAttachments] = 1))))");

            migrationBuilder.CreateIndex(
                name: "IX_StickerPacks_IsSystemPack_IsDeleted_SortOrder",
                table: "StickerPacks",
                columns: new[] { "IsSystemPack", "IsDeleted", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_StickerPacks_OwnerUserId_IsDeleted",
                table: "StickerPacks",
                columns: new[] { "OwnerUserId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_StickerPacks_Slug",
                table: "StickerPacks",
                column: "Slug",
                unique: true,
                filter: "[Slug] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Stickers_ObjectKey",
                table: "Stickers",
                column: "ObjectKey",
                unique: true,
                filter: "[ObjectKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Stickers_PackId_State_DisplayOrder",
                table: "Stickers",
                columns: new[] { "PackId", "State", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Stickers_State_PurgedAtUtc_CreatedAtUtc",
                table: "Stickers",
                columns: new[] { "State", "PurgedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Stickers_StaticAssetPath",
                table: "Stickers",
                column: "StaticAssetPath",
                unique: true,
                filter: "[StaticAssetPath] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Stickers_StorageObjectId",
                table: "Stickers",
                column: "StorageObjectId",
                unique: true,
                filter: "[StorageObjectId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Stickers_UploaderId_CreatedAtUtc",
                table: "Stickers",
                columns: new[] { "UploaderId", "CreatedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "State", "DeclaredSizeBytes", "VerifiedSizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_Stickers_UploaderId_State_CreatedAtUtc",
                table: "Stickers",
                columns: new[] { "UploaderId", "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserFavoriteStickers_StickerId",
                table: "UserFavoriteStickers",
                column: "StickerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavoriteStickers_UserId_CreatedAtUtc",
                table: "UserFavoriteStickers",
                columns: new[] { "UserId", "CreatedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_UserRecentStickers_StickerId",
                table: "UserRecentStickers",
                column: "StickerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRecentStickers_UserId_LastUsedAtUtc",
                table: "UserRecentStickers",
                columns: new[] { "UserId", "LastUsedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Stickers_StickerId",
                table: "Messages",
                column: "StickerId",
                principalTable: "Stickers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Stickers_StickerId",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "UserFavoriteStickers");

            migrationBuilder.DropTable(
                name: "UserRecentStickers");

            migrationBuilder.DropTable(
                name: "Stickers");

            migrationBuilder.DropTable(
                name: "StickerPacks");

            migrationBuilder.DropIndex(
                name: "IX_Messages_StickerId",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "StickerId",
                table: "Messages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages",
                sql: "([IsDeletedForEveryone] = 1 AND [Content] IS NULL) OR ([IsDeletedForEveryone] = 0 AND ([Content] IS NOT NULL OR [HasAttachments] = 1))");
        }
    }
}
