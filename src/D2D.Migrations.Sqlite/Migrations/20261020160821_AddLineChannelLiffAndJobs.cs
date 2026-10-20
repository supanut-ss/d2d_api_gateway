using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D2D.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddLineChannelLiffAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JobsKeyEncrypted",
                table: "line_channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiffId",
                table: "line_channels",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JobsKeyEncrypted",
                table: "line_channels");

            migrationBuilder.DropColumn(
                name: "LiffId",
                table: "line_channels");
        }
    }
}
