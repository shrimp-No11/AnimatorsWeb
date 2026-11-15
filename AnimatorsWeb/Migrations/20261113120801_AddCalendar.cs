using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventPatterns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CycleWeeksCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPatterns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventPresets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DefaultTitle = table.Column<string>(type: "text", nullable: false),
                    DefaultDescription = table.Column<string>(type: "text", nullable: true),
                    DurationHours = table.Column<int>(type: "integer", nullable: false),
                    Location = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPresets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PatternInstances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventPatternId = table.Column<int>(type: "integer", nullable: false),
                    StartWeekUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MaterializedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatternInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatternInstances_EventPatterns_EventPatternId",
                        column: x => x.EventPatternId,
                        principalTable: "EventPatterns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatternEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventPatternId = table.Column<int>(type: "integer", nullable: false),
                    EventPresetId = table.Column<int>(type: "integer", nullable: false),
                    WeekOffset = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    StartTimeUtc = table.Column<TimeSpan>(type: "interval", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatternEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatternEvents_EventPatterns_EventPatternId",
                        column: x => x.EventPatternId,
                        principalTable: "EventPatterns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PatternEvents_EventPresets_EventPresetId",
                        column: x => x.EventPresetId,
                        principalTable: "EventPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventPresetId = table.Column<int>(type: "integer", nullable: true),
                    PatternInstanceId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Location = table.Column<string>(type: "text", nullable: true),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiscordEventId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    DiscordSnapshot = table.Column<string>(type: "text", nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_EventPresets_EventPresetId",
                        column: x => x.EventPresetId,
                        principalTable: "EventPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_PatternInstances_PatternInstanceId",
                        column: x => x.PatternInstanceId,
                        principalTable: "PatternInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_DiscordEventId",
                table: "CalendarEvents",
                column: "DiscordEventId",
                unique: true,
                filter: "\"DiscordEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_EventPresetId",
                table: "CalendarEvents",
                column: "EventPresetId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_PatternInstanceId",
                table: "CalendarEvents",
                column: "PatternInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_StartUtc",
                table: "CalendarEvents",
                column: "StartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PatternEvents_EventPatternId",
                table: "PatternEvents",
                column: "EventPatternId");

            migrationBuilder.CreateIndex(
                name: "IX_PatternEvents_EventPresetId",
                table: "PatternEvents",
                column: "EventPresetId");

            migrationBuilder.CreateIndex(
                name: "IX_PatternInstances_EventPatternId",
                table: "PatternInstances",
                column: "EventPatternId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarEvents");

            migrationBuilder.DropTable(
                name: "PatternEvents");

            migrationBuilder.DropTable(
                name: "PatternInstances");

            migrationBuilder.DropTable(
                name: "EventPresets");

            migrationBuilder.DropTable(
                name: "EventPatterns");
        }
    }
}
