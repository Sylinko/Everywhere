using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Everywhere.Database.Migrations.V2
{
    /// <inheritdoc />
    public partial class AddChatPinning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chats_ArchivedAt_Id",
                table: "Chats");

            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "Chats",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Chats_ActiveHistory",
                table: "Chats",
                columns: new[] { "IsPinned", "UpdatedAt", "Id" },
                descending: new bool[0],
                filter: "\"ArchivedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_ArchivedHistory",
                table: "Chats",
                columns: new[] { "IsPinned", "UpdatedAt", "Id" },
                descending: new bool[0],
                filter: "\"ArchivedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chats_ActiveHistory",
                table: "Chats");

            migrationBuilder.DropIndex(
                name: "IX_Chats_ArchivedHistory",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "Chats");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_ArchivedAt_Id",
                table: "Chats",
                columns: new[] { "ArchivedAt", "Id" },
                filter: "\"ArchivedAt\" IS NOT NULL");
        }
    }
}