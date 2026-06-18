using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Everywhere.Statistics.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceGuid = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Metadata",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Metadata", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ModelInvocationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TurnEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssistantChatNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IsSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCanceled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    InputTokenCount = table.Column<long>(type: "INTEGER", nullable: false),
                    CachedInputTokenCount = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokenCount = table.Column<long>(type: "INTEGER", nullable: false),
                    ReasoningTokenCount = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTokenCount = table.Column<long>(type: "INTEGER", nullable: false),
                    GenerationSeconds = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelInvocationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelInvocationEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ToolInvocationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TurnEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelInvocationEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PluginKey = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    FunctionName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolInvocationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolInvocationEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TopicEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Topic = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopicEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TopicEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TurnEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserChatNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssistantChatNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TurnEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TurnEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VisualContextEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TurnEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChatContextId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ElementCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ScreenshotCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ByteCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisualContextEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisualContextEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceGuid",
                table: "Devices",
                column: "DeviceGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelInvocationEvents_ChatContextId",
                table: "ModelInvocationEvents",
                column: "ChatContextId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInvocationEvents_DeviceId",
                table: "ModelInvocationEvents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInvocationEvents_StartedAt",
                table: "ModelInvocationEvents",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInvocationEvents_TurnEventId",
                table: "ModelInvocationEvents",
                column: "TurnEventId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolInvocationEvents_ChatContextId",
                table: "ToolInvocationEvents",
                column: "ChatContextId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolInvocationEvents_DeviceId",
                table: "ToolInvocationEvents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolInvocationEvents_ModelInvocationEventId",
                table: "ToolInvocationEvents",
                column: "ModelInvocationEventId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolInvocationEvents_StartedAt",
                table: "ToolInvocationEvents",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ToolInvocationEvents_TurnEventId",
                table: "ToolInvocationEvents",
                column: "TurnEventId");

            migrationBuilder.CreateIndex(
                name: "IX_TopicEvents_CreatedAt",
                table: "TopicEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TopicEvents_DeviceId_ChatContextId",
                table: "TopicEvents",
                columns: new[] { "DeviceId", "ChatContextId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TurnEvents_ChatContextId",
                table: "TurnEvents",
                column: "ChatContextId");

            migrationBuilder.CreateIndex(
                name: "IX_TurnEvents_CreatedAt",
                table: "TurnEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TurnEvents_DeviceId",
                table: "TurnEvents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_VisualContextEvents_ChatContextId",
                table: "VisualContextEvents",
                column: "ChatContextId");

            migrationBuilder.CreateIndex(
                name: "IX_VisualContextEvents_CreatedAt",
                table: "VisualContextEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VisualContextEvents_DeviceId",
                table: "VisualContextEvents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_VisualContextEvents_TurnEventId",
                table: "VisualContextEvents",
                column: "TurnEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Metadata");

            migrationBuilder.DropTable(
                name: "ModelInvocationEvents");

            migrationBuilder.DropTable(
                name: "ToolInvocationEvents");

            migrationBuilder.DropTable(
                name: "TopicEvents");

            migrationBuilder.DropTable(
                name: "TurnEvents");

            migrationBuilder.DropTable(
                name: "VisualContextEvents");

            migrationBuilder.DropTable(
                name: "Devices");
        }
    }
}
