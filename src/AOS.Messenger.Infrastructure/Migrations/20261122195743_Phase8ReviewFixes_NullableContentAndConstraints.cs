using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase8ReviewFixes_NullableContentAndConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Content",
                table: "Messages",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [Messages] SET [Content] = NULL WHERE [IsDeletedForEveryone] = 1;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages",
                sql: "([IsDeletedForEveryone] = 1 AND [Content] IS NULL) OR ([IsDeletedForEveryone] = 0 AND [Content] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Content_Tombstone",
                table: "Messages");

            migrationBuilder.Sql("UPDATE [Messages] SET [Content] = N'' WHERE [Content] IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "Content",
                table: "Messages",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }
    }
}
