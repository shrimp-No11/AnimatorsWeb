using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnimJams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Theme = table.Column<string>(type: "text", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResultsMessageUrl = table.Column<string>(type: "text", nullable: true),
                    AnounceMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimJams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ThemePollChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    AnimJamStartChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    WinnerPollChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    WinnersAnnouncementChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    AnnouncementChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DesignSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BackgroundColor = table.Column<string>(type: "text", nullable: false),
                    TextColor = table.Column<string>(type: "text", nullable: false),
                    PrimaryColor = table.Column<string>(type: "text", nullable: false),
                    PrimaryHoverColor = table.Column<string>(type: "text", nullable: false),
                    AccentColor = table.Column<string>(type: "text", nullable: false),
                    CardColor = table.Column<string>(type: "text", nullable: false),
                    PaletteUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesignSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LandingFeaturedWorks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ParticipantId = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LandingFeaturedWorks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LinkedAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PrimaryDiscordId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    LinkedDiscordId = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    LinkedName = table.Column<string>(type: "text", nullable: true),
                    IsConfirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Participants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnimJamNumber = table.Column<int>(type: "integer", nullable: false),
                    DiscordUserId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    WorkUrl = table.Column<string>(type: "text", nullable: false),
                    MessageUrl = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RulesSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GuildId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    RoleId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    LastMessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    AllMessageIds = table.Column<string>(type: "text", nullable: false),
                    SectionsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RulesSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThemePolls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnimJamNumber = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    MessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThemePolls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WinnerPolls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnimJamNumber = table.Column<int>(type: "integer", nullable: false),
                    Nomination = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    MessageId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinnerPolls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Winners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnimJamNumber = table.Column<int>(type: "integer", nullable: false),
                    DiscordUserId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Nomination = table.Column<int>(type: "integer", nullable: false),
                    WorkUrl = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Winners", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LandingFeaturedWorks_ParticipantId",
                table: "LandingFeaturedWorks",
                column: "ParticipantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinkedAccounts_PrimaryDiscordId_LinkedDiscordId",
                table: "LinkedAccounts",
                columns: new[] { "PrimaryDiscordId", "LinkedDiscordId" },
                unique: true,
                filter: "\"LinkedDiscordId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Winners_AnimJamNumber_DiscordUserId_Nomination_WorkUrl",
                table: "Winners",
                columns: new[] { "AnimJamNumber", "DiscordUserId", "Nomination", "WorkUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimJams");

            migrationBuilder.DropTable(
                name: "ChannelSettings");

            migrationBuilder.DropTable(
                name: "DesignSettings");

            migrationBuilder.DropTable(
                name: "LandingFeaturedWorks");

            migrationBuilder.DropTable(
                name: "LinkedAccounts");

            migrationBuilder.DropTable(
                name: "Participants");

            migrationBuilder.DropTable(
                name: "RulesSettings");

            migrationBuilder.DropTable(
                name: "ThemePolls");

            migrationBuilder.DropTable(
                name: "WinnerPolls");

            migrationBuilder.DropTable(
                name: "Winners");
        }
    }
}
