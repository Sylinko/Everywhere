#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Everywhere.Database.Migrations.Chat
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Blobs",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAccessAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blobs", x => x.Sha256);
                });

            migrationBuilder.CreateTable(
                name: "Chats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Topic = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChoiceChildId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => new { x.ChatContextId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "NodeBlobs",
                columns: table => new
                {
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    BlobSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeBlobs", x => new { x.ChatContextId, x.ChatNodeId, x.Index });
                    table.ForeignKey(
                        name: "FK_NodeBlobs_Nodes_ChatContextId_ChatNodeId",
                        columns: x => new { x.ChatContextId, x.ChatNodeId },
                        principalTable: "Nodes",
                        principalColumns: new[] { "ChatContextId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_LastAccessAt",
                table: "Blobs",
                column: "LastAccessAt");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_UpdatedAt",
                table: "Chats",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NodeBlobs_BlobSha256",
                table: "NodeBlobs",
                column: "BlobSha256");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_ChoiceChildId",
                table: "Nodes",
                columns: new[] { "ChatContextId", "ChoiceChildId" });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_IsDeleted",
                table: "Nodes",
                columns: new[] { "ChatContextId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_ParentId_Id",
                table: "Nodes",
                columns: new[] { "ChatContextId", "ParentId", "Id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Blobs");

            migrationBuilder.DropTable(
                name: "Chats");

            migrationBuilder.DropTable(
                name: "NodeBlobs");

            migrationBuilder.DropTable(
                name: "Nodes");
        }
    }
}
