using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBuiltInStarterStickerPacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE ufs
                FROM [UserFavoriteStickers] AS ufs
                INNER JOIN [Stickers] AS s ON s.[Id] = ufs.[StickerId]
                INNER JOIN [StickerPacks] AS p ON p.[Id] = s.[PackId]
                WHERE p.[IsSystemPack] = 1
                  AND p.[Slug] IN (N'aos-greetings', N'aos-moods', N'aos-reactions');

                DELETE urs
                FROM [UserRecentStickers] AS urs
                INNER JOIN [Stickers] AS s ON s.[Id] = urs.[StickerId]
                INNER JOIN [StickerPacks] AS p ON p.[Id] = s.[PackId]
                WHERE p.[IsSystemPack] = 1
                  AND p.[Slug] IN (N'aos-greetings', N'aos-moods', N'aos-reactions');

                DELETE s
                FROM [Stickers] AS s
                INNER JOIN [StickerPacks] AS p ON p.[Id] = s.[PackId]
                WHERE p.[IsSystemPack] = 1
                  AND p.[Slug] IN (N'aos-greetings', N'aos-moods', N'aos-reactions')
                  AND NOT EXISTS (SELECT 1 FROM [Messages] AS m WHERE m.[StickerId] = s.[Id]);

                DELETE p
                FROM [StickerPacks] AS p
                WHERE p.[IsSystemPack] = 1
                  AND p.[Slug] IN (N'aos-greetings', N'aos-moods', N'aos-reactions')
                  AND NOT EXISTS (SELECT 1 FROM [Stickers] AS s WHERE s.[PackId] = p.[Id]);

                UPDATE [StickerPacks]
                SET [IsDeleted] = 1,
                    [UpdatedAtUtc] = SYSUTCDATETIME()
                WHERE [IsSystemPack] = 1
                  AND [Slug] IN (N'aos-greetings', N'aos-moods', N'aos-reactions')
                  AND [IsDeleted] = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
