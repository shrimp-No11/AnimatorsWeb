using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundDecoration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackgroundDecorationFileName",
                table: "DesignSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RepeatBackgroundDecoration",
                table: "DesignSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackgroundDecorationFileName",
                table: "DesignSettings");

            migrationBuilder.DropColumn(
                name: "RepeatBackgroundDecoration",
                table: "DesignSettings");
        }
    }
}
