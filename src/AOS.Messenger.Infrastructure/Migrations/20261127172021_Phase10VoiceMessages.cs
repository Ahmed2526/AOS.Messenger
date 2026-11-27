using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AOS.Messenger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase10VoiceMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlayedAtUtc",
                table: "Media",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaveformPeaks",
                table: "Media",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Media_PlayedAtUtc_Voice",
                table: "Media",
                sql: "([PlayedAtUtc] IS NULL) OR ([Kind] = 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Media_Voice_Duration",
                table: "Media",
                sql: "([Kind] <> 3) OR ([DurationMs] IS NOT NULL AND [DurationMs] >= 500 AND [DurationMs] <= 1200000)");

            // Safe backfill for any pre-existing voice rows if this migration was previously rolled back and re-applied.
            migrationBuilder.Sql(
                "UPDATE [Media] SET [WaveformPeaks] = N'50' WHERE [Kind] = 3 AND ([WaveformPeaks] IS NULL OR LEN([WaveformPeaks]) = 0);");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Media_Voice_Waveform",
                table: "Media",
                sql: "([Kind] <> 3 AND [WaveformPeaks] IS NULL) OR ([Kind] = 3 AND [WaveformPeaks] IS NOT NULL AND LEN([WaveformPeaks]) > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Media_PlayedAtUtc_Voice",
                table: "Media");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Media_Voice_Duration",
                table: "Media");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Media_Voice_Waveform",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "PlayedAtUtc",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "WaveformPeaks",
                table: "Media");
        }
    }
}
