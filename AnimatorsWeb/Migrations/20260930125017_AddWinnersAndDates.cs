using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddWinnersAndDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EndDate",
                table: "AnimJams",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "ResultsMessageUrl",
                table: "AnimJams",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                table: "AnimJams",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "Winners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnimJamId = table.Column<int>(type: "integer", nullable: false),
                    Nomination = table.Column<int>(type: "integer", nullable: false),
                    ParticipantName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Winners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Winners_AnimJams_AnimJamId",
                        column: x => x.AnimJamId,
                        principalTable: "AnimJams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Winners_AnimJamId",
                table: "Winners",
                column: "AnimJamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Winners");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "AnimJams");

            migrationBuilder.DropColumn(
                name: "ResultsMessageUrl",
                table: "AnimJams");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "AnimJams");
        }
    }
}
