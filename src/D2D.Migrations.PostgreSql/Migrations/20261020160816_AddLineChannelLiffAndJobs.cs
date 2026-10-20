using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D2D.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddLineChannelLiffAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded: an earlier build of the gateway may already have added these columns.
            migrationBuilder.Sql("ALTER TABLE line_channels ADD COLUMN IF NOT EXISTS \"JobsKeyEncrypted\" text NULL;");
            migrationBuilder.Sql("ALTER TABLE line_channels ADD COLUMN IF NOT EXISTS \"LiffId\" character varying(64) NULL;");
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
