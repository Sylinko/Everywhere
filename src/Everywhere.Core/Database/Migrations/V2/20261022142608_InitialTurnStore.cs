using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Everywhere.Database.Migrations.V2
{
    /// <inheritdoc />
    public partial class InitialTurnStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Blobs",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
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
                    Topic = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ArchivedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoreInfo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    InitializedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LegacyImportedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpgradeNoticeAcknowledgedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreInfo", x => x.Id);
                    table.CheckConstraint("CK_StoreInfo_Singleton", "\"Id\" = 1");
                });

            // SQLite requires deferred graph references: inserting a child also changes its
            // parent's choice, and the first user node references itself as its turn boundary.
            migrationBuilder.Sql(
                """
                CREATE TABLE "Nodes" (
                    "ChatContextId" TEXT NOT NULL,
                    "Id" TEXT NOT NULL,
                    "ParentId" TEXT NULL,
                    "TurnId" TEXT NULL,
                    "SiblingOrder" INTEGER NOT NULL,
                    "ChoiceChildId" TEXT NULL,
                    "MessageKind" INTEGER NOT NULL,
                    "SummaryThroughNodeId" TEXT NULL,
                    "Payload" BLOB NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL,
                    CONSTRAINT "PK_Nodes" PRIMARY KEY ("ChatContextId", "Id"),
                    CONSTRAINT "FK_Nodes_Chats_ChatContextId" FOREIGN KEY ("ChatContextId")
                        REFERENCES "Chats" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_Nodes_Nodes_ChatContextId_ChoiceChildId" FOREIGN KEY ("ChatContextId", "ChoiceChildId")
                        REFERENCES "Nodes" ("ChatContextId", "Id") DEFERRABLE INITIALLY DEFERRED,
                    CONSTRAINT "FK_Nodes_Nodes_ChatContextId_ParentId" FOREIGN KEY ("ChatContextId", "ParentId")
                        REFERENCES "Nodes" ("ChatContextId", "Id") DEFERRABLE INITIALLY DEFERRED,
                    CONSTRAINT "FK_Nodes_Nodes_ChatContextId_TurnId" FOREIGN KEY ("ChatContextId", "TurnId")
                        REFERENCES "Nodes" ("ChatContextId", "Id") DEFERRABLE INITIALLY DEFERRED
                );
                """);

            migrationBuilder.CreateTable(
                name: "NodeBlobs",
                columns: table => new
                {
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlobSha256 = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeBlobs", x => new { x.ChatContextId, x.ChatNodeId, x.BlobSha256 });
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
                name: "IX_Chats_ArchivedAt_Id",
                table: "Chats",
                columns: new[] { "ArchivedAt", "Id" },
                filter: "\"ArchivedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_UpdatedAt_Id",
                table: "Chats",
                columns: new[] { "UpdatedAt", "Id" },
                filter: "\"ArchivedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NodeBlobs_BlobSha256",
                table: "NodeBlobs",
                column: "BlobSha256");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId",
                table: "Nodes",
                column: "ChatContextId",
                unique: true,
                filter: "\"ParentId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_ChoiceChildId",
                table: "Nodes",
                columns: new[] { "ChatContextId", "ChoiceChildId" });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_ParentId_SiblingOrder",
                table: "Nodes",
                columns: new[] { "ChatContextId", "ParentId", "SiblingOrder" },
                unique: true,
                filter: "\"ParentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_TurnId",
                table: "Nodes",
                columns: new[] { "ChatContextId", "TurnId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Blobs");

            migrationBuilder.DropTable(
                name: "NodeBlobs");

            migrationBuilder.DropTable(
                name: "StoreInfo");

            migrationBuilder.DropTable(
                name: "Nodes");

            migrationBuilder.DropTable(
                name: "Chats");
        }
    }
}