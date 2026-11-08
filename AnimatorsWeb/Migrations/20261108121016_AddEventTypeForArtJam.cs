using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddEventTypeForArtJam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Winners_AnimJamNumber_DiscordUserId_Nomination_WorkUrl",
                table: "Winners");

            migrationBuilder.AddColumn<int>(
                name: "EventType",
                table: "Winners",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMultiSelect",
                table: "WinnerPolls",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "EventType",
                table: "WinnerPolls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EventType",
                table: "ThemePolls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EventType",
                table: "Participants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ArtJamStartChannelId",
                table: "ChannelSettings",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ArtJamThemePollChannelId",
                table: "ChannelSettings",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ArtJamWinnerPollChannelId",
                table: "ChannelSettings",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ArtJamWinnersAnnouncementChannelId",
                table: "ChannelSettings",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EventType",
                table: "AnimJams",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Winners_EventType_AnimJamNumber_DiscordUserId_Nomination_WorkUrl",
                table: "Winners",
                columns: new[] { "EventType", "AnimJamNumber", "DiscordUserId", "Nomination", "WorkUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Winners_EventType_AnimJamNumber_DiscordUserId_Nomination_WorkUrl",
                table: "Winners");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "Winners");

            migrationBuilder.DropColumn(
                name: "AllowMultiSelect",
                table: "WinnerPolls");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "WinnerPolls");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "ThemePolls");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "ArtJamStartChannelId",
                table: "ChannelSettings");

            migrationBuilder.DropColumn(
                name: "ArtJamThemePollChannelId",
                table: "ChannelSettings");

            migrationBuilder.DropColumn(
                name: "ArtJamWinnerPollChannelId",
                table: "ChannelSettings");

            migrationBuilder.DropColumn(
                name: "ArtJamWinnersAnnouncementChannelId",
                table: "ChannelSettings");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "AnimJams");

            migrationBuilder.CreateIndex(
                name: "IX_Winners_AnimJamNumber_DiscordUserId_Nomination_WorkUrl",
                table: "Winners",
                columns: new[] { "AnimJamNumber", "DiscordUserId", "Nomination", "WorkUrl" },
                unique: true);
        }
    }
}
