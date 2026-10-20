using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D2D.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLineChannelLiffAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded: an earlier build of the gateway may already have added these columns.
            migrationBuilder.Sql("IF COL_LENGTH('line_channels', 'JobsKeyEncrypted') IS NULL ALTER TABLE [line_channels] ADD [JobsKeyEncrypted] nvarchar(max) NULL;");
            migrationBuilder.Sql("IF COL_LENGTH('line_channels', 'LiffId') IS NULL ALTER TABLE [line_channels] ADD [LiffId] nvarchar(64) NULL;");
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
