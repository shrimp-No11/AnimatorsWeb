using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDiscordSeries",
                table: "EventPresets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscordSeriesEventId",
                table: "EventPresets",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceRuleJson",
                table: "EventPresets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceExceptionsJson",
                table: "EventPresets",
                type: "text",
                nullable: true);

            // Перенос: recurring CalendarEvents из паттерна → EventPreset.IsDiscordSeries
            migrationBuilder.Sql(@"
                UPDATE ""EventPresets"" p
                SET ""IsDiscordSeries"" = true,
                    ""DiscordSeriesEventId"" = ce.""DiscordEventId"",
                    ""RecurrenceRuleJson"" = ce.""RecurrenceRuleJson"",
                    ""RecurrenceExceptionsJson"" = ce.""RecurrenceExceptionsJson""
                FROM ""CalendarEvents"" ce
                WHERE ce.""EventPresetId"" = p.""Id""
                    AND ce.""IsRecurring"" = true
                    AND ce.""PatternInstanceId"" IS NOT NULL
                    AND ce.""DiscordEventId"" IS NOT NULL;

                DELETE FROM ""CalendarEvents""
                WHERE ""IsRecurring"" = true
                    AND ""PatternInstanceId"" IS NOT NULL
                    AND ""EventPresetId"" IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsDiscordSeries", table: "EventPresets");
            migrationBuilder.DropColumn(name: "DiscordSeriesEventId", table: "EventPresets");
            migrationBuilder.DropColumn(name: "RecurrenceRuleJson", table: "EventPresets");
            migrationBuilder.DropColumn(name: "RecurrenceExceptionsJson", table: "EventPresets");
        }
    }
}
