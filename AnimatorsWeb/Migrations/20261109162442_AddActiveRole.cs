using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActiveRoleSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RoleId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    MessageEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MessageDaysRequired = table.Column<int>(type: "integer", nullable: false),
                    MessageWindowDays = table.Column<int>(type: "integer", nullable: false),
                    VoiceEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    VoiceDaysRequired = table.Column<int>(type: "integer", nullable: false),
                    VoiceWindowDays = table.Column<int>(type: "integer", nullable: false),
                    EventEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    EventRequired = table.Column<int>(type: "integer", nullable: false),
                    EventWindowDays = table.Column<int>(type: "integer", nullable: false),
                    LastCheckUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveRoleSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscordUserId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserActivities_DiscordUserId_Date_Type",
                table: "UserActivities",
                columns: new[] { "DiscordUserId", "Date", "Type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActiveRoleSettings");

            migrationBuilder.DropTable(
                name: "UserActivities");
        }
    }
}
