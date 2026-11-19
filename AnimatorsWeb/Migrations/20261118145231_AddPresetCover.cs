using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddPresetCover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverImageFileName",
                table: "EventPresets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverImageUrl",
                table: "EventPresets",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverImageFileName",
                table: "EventPresets");

            migrationBuilder.DropColumn(
                name: "CoverImageUrl",
                table: "EventPresets");
        }
    }
}
