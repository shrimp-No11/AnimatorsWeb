using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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


            migrationBuilder.CreateIndex(
                name: "IX_LinkedAccounts_PrimaryDiscordId_LinkedDiscordId",
                table: "LinkedAccounts",
                columns: new[] { "PrimaryDiscordId", "LinkedDiscordId" },
                unique: true,
                filter: "\"LinkedDiscordId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkedAccounts");
        }
    }
}
