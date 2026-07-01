using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Everywhere.Database.Migrations
{
    /// <inheritdoc />
    public partial class CloudSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LocalSyncVersion",
                table: "Nodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LocalSyncVersion",
                table: "Chats",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "SyncMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LocalVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPushedVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPulledVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSyncAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMetadata", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_LocalSyncVersion",
                table: "Nodes",
                column: "LocalSyncVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_LocalSyncVersion",
                table: "Chats",
                column: "LocalSyncVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncMetadata");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_LocalSyncVersion",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Chats_LocalSyncVersion",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "LocalSyncVersion",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "LocalSyncVersion",
                table: "Chats");
        }
    }
}
