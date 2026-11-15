using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordSyncFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreateInDiscord",
                table: "EventPresets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsRecurring",
                table: "EventPresets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceByWeekdays",
                table: "EventPresets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurrenceFrequency",
                table: "EventPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurrenceInterval",
                table: "EventPresets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SkipDiscordSync",
                table: "CalendarEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreateInDiscord",
                table: "EventPresets");

            migrationBuilder.DropColumn(
                name: "IsRecurring",
                table: "EventPresets");

            migrationBuilder.DropColumn(
                name: "RecurrenceByWeekdays",
                table: "EventPresets");

            migrationBuilder.DropColumn(
                name: "RecurrenceFrequency",
                table: "EventPresets");

            migrationBuilder.DropColumn(
                name: "RecurrenceInterval",
                table: "EventPresets");

            migrationBuilder.DropColumn(
                name: "SkipDiscordSync",
                table: "CalendarEvents");
        }
    }
}
