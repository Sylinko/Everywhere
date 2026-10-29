using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Everywhere.Database.Migrations.V2
{
    /// <inheritdoc />
    public partial class PersistTemporaryChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTemporary",
                table: "Chats",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Chats_TemporaryHistory",
                table: "Chats",
                columns: new[] { "IsPinned", "UpdatedAt", "Id" },
                descending: new bool[0],
                filter: "\"IsTemporary\" = 1 AND \"ArchivedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chats_TemporaryHistory",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "IsTemporary",
                table: "Chats");
        }
    }
}