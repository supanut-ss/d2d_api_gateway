using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D2D.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddLineChannelLiffAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded (works on MySQL and MariaDB): an earlier build of the gateway may already have added these columns.
            foreach (var (column, type) in new[] { ("JobsKeyEncrypted", "longtext"), ("LiffId", "varchar(64)") })
            {
                migrationBuilder.Sql(
                    "SET @d2d_sql = (SELECT IF(COUNT(*) = 0, 'ALTER TABLE `line_channels` ADD COLUMN `" + column + "` " + type + " NULL', 'SELECT 1') " +
                    "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'line_channels' AND COLUMN_NAME = '" + column + "');");
                migrationBuilder.Sql("PREPARE d2d_stmt FROM @d2d_sql;");
                migrationBuilder.Sql("EXECUTE d2d_stmt;");
                migrationBuilder.Sql("DEALLOCATE PREPARE d2d_stmt;");
            }
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
