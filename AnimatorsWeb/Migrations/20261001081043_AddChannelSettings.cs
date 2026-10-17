using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelSettings");
        }
    }
}
