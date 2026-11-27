using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase9MediaMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages");

            migrationBuilder.AddColumn<bool>(
                name: "HasAttachments",
                table: "Messages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    StorageObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UploaderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SanitizedFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    DeclaredContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    VerifiedContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DeclaredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttachedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PurgedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media", x => x.Id)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_Media_ObjectKey_Prefix", "([State] = 0 AND [ObjectKey] LIKE N'pending/%') OR ([State] IN (1, 2) AND [ObjectKey] LIKE N'media/%') OR ([State] IN (3, 4))");
                    table.CheckConstraint("CK_Media_Sizes_Positive", "[DeclaredSizeBytes] > 0 AND ([VerifiedSizeBytes] IS NULL OR [VerifiedSizeBytes] > 0)");
                    table.CheckConstraint("CK_Media_State_MessageId", "([State] = 2 AND [MessageId] IS NOT NULL) OR ([State] IN (0, 1, 3) AND [MessageId] IS NULL) OR ([State] = 4)");
                    table.ForeignKey(
                        name: "FK_Media_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Media_Messages_ConversationId_MessageId",
                        columns: x => new { x.ConversationId, x.MessageId },
                        principalTable: "Messages",
                        principalColumns: new[] { "ConversationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages",
                sql: "([IsDeletedForEveryone] = 1 AND [Content] IS NULL) OR ([IsDeletedForEveryone] = 0 AND ([Content] IS NOT NULL OR [HasAttachments] = 1))");

            migrationBuilder.CreateIndex(
                name: "IX_Media_ConversationId_MessageId_DisplayOrder",
                table: "Media",
                columns: new[] { "ConversationId", "MessageId", "DisplayOrder" },
                filter: "[MessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Media_ObjectKey",
                table: "Media",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_State_PurgedAtUtc_CreatedAtUtc",
                table: "Media",
                columns: new[] { "State", "PurgedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Media_StorageObjectId",
                table: "Media",
                column: "StorageObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_UploaderId_State_CreatedAtUtc",
                table: "Media",
                columns: new[] { "UploaderId", "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Media");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "HasAttachments",
                table: "Messages");

            migrationBuilder.Sql(
                "UPDATE [Messages] SET [Content] = N'[Attachment]' WHERE [IsDeletedForEveryone] = 0 AND [Content] IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages",
                sql: "([IsDeletedForEveryone] = 1 AND [Content] IS NULL) OR ([IsDeletedForEveryone] = 0 AND [Content] IS NOT NULL)");
        }
    }
}
