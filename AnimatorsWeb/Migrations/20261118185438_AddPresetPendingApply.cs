using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimatorsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddPresetPendingApply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PendingApply",
                table: "EventPresets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingApply",
                table: "EventPresets");
        }
    }
}
