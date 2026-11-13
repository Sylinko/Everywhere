using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using MessagePack;
using Microsoft.EntityFrameworkCore.Migrations;
using Serilog;

#nullable disable

namespace Everywhere.Database.Migrations.V2
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
                name: "ChatInputs",
                columns: table => new
                {
                    SlotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Payload = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatInputs", x => x.SlotId);
                });

            migrationBuilder.CreateTable(
                name: "Chats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Topic = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ArchivedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsTemporary = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    LocalAccountId = table.Column<string>(type: "TEXT", nullable: true),
                    IsSyncExcluded = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ExtensionData = table.Column<byte[]>(type: "BLOB", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "SyncAccounts",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<long>(type: "INTEGER", nullable: false),
                    ScanCursor = table.Column<long>(type: "INTEGER", nullable: false),
                    LocalRevision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAccounts", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "SyncChats",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DiscoveredRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    IsContentUnavailable = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Head = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncChats", x => new { x.AccountId, x.ChatId });
                });

            migrationBuilder.CreateTable(
                name: "SyncPending",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    LocalRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseRevision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncPending", x => new { x.AccountId, x.ChatId, x.Kind, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "SyncRemote",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRemote", x => new { x.AccountId, x.ChatId, x.Kind, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "SyncSubmissions",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Header = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CapturedChanges = table.Column<byte[]>(type: "BLOB", nullable: false),
                    QuotaRejection = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSubmissions", x => new { x.AccountId, x.ChatId });
                });

            migrationBuilder.Sql(
                """
                CREATE TABLE "Nodes" (
                    "ChatContextId" TEXT NOT NULL,
                    "Id" TEXT NOT NULL,
                    "ParentId" TEXT NULL,
                    "TurnId" TEXT NULL,
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
                name: "SyncParts",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Body = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncParts", x => new { x.AccountId, x.ChatId, x.Position });
                    table.ForeignKey(
                        name: "FK_SyncParts_SyncSubmissions_AccountId_ChatId",
                        columns: x => new { x.AccountId, x.ChatId },
                        principalTable: "SyncSubmissions",
                        principalColumns: new[] { "AccountId", "ChatId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeBlobs",
                columns: table => new
                {
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlobSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_Chats_TemporaryHistory",
                table: "Chats",
                columns: new[] { "IsPinned", "UpdatedAt", "Id" },
                descending: new bool[0],
                filter: "\"IsTemporary\" = 1 AND \"ArchivedAt\" IS NULL");

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
                name: "IX_Nodes_ChatContextId_ParentId",
                table: "Nodes",
                columns: new[] { "ChatContextId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_TurnId",
                table: "Nodes",
                columns: new[] { "ChatContextId", "TurnId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncSubmissions_AccountId_CommitId",
                table: "SyncSubmissions",
                columns: new[] { "AccountId", "CommitId" },
                unique: true);

            string text;
            try
            {
                // The desktop loads this store before database initialization. Design-time EF
                // commands have no application services; missing/unreadable old input is optional.
                text = ServiceLocator.Resolve<PersistentState>().Storage.Get<string>("ChatInputAreaText");
            }
            catch (Exception ex)
            {
                Log.ForContext<Initial>().Warning(ex, "Failed to read `ChatInputAreaText` for migration");
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var input = new ChatInputSnapshot(new ChatInputDraftSnapshot(text, [], null), null, null);
            migrationBuilder.InsertData(
                table: "ChatInputs",
                columns: ["SlotId", "ChatId", "Payload"],
                values: [Guid.Empty, Guid.CreateVersion7(), MessagePackSerializer.Serialize(input, cancellationToken: CancellationToken.None)]);
            // Up builds SQL operations; it has not committed them yet. Leave the unused old key
            // intact rather than deleting its only durable copy before this transaction succeeds.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatInputs");

            migrationBuilder.DropTable(
                name: "Blobs");

            migrationBuilder.DropTable(
                name: "NodeBlobs");

            migrationBuilder.DropTable(
                name: "StoreInfo");

            migrationBuilder.DropTable(
                name: "SyncAccounts");

            migrationBuilder.DropTable(
                name: "SyncChats");

            migrationBuilder.DropTable(
                name: "SyncParts");

            migrationBuilder.DropTable(
                name: "SyncPending");

            migrationBuilder.DropTable(
                name: "SyncRemote");

            migrationBuilder.DropTable(
                name: "Nodes");

            migrationBuilder.DropTable(
                name: "SyncSubmissions");

            migrationBuilder.DropTable(
                name: "Chats");
        }
    }
}
