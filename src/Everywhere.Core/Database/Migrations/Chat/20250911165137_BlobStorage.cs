#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Everywhere.Database.Migrations.Chat
{
    /// <inheritdoc />
    public partial class BlobStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalPath",
                table: "Blobs",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalPath",
                table: "Blobs");
        }
    }
}
