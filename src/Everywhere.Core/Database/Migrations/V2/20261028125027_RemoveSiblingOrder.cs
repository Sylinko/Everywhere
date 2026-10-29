using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Everywhere.Database.Migrations.V2
{
    /// <inheritdoc />
    public partial class RemoveSiblingOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Nodes_ChatContextId_ParentId_SiblingOrder",
                table: "Nodes");

            // Avoid an EF table rebuild, which would lose the deferred self-referencing keys.
            migrationBuilder.Sql("ALTER TABLE Nodes DROP COLUMN SiblingOrder;");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_ParentId",
                table: "Nodes",
                columns: new[] { "ChatContextId", "ParentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Nodes_ChatContextId_ParentId",
                table: "Nodes");

            migrationBuilder.AddColumn<long>(
                name: "SiblingOrder",
                table: "Nodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Reconstruct a unique position for old readers without changing any chosen-child ID.
            migrationBuilder.Sql(
                """
                WITH Ordered AS (
                    SELECT ChatContextId, Id,
                        ROW_NUMBER() OVER (PARTITION BY ChatContextId, ParentId ORDER BY CreatedAt, Id) - 1 AS Position
                    FROM Nodes
                )
                UPDATE Nodes SET SiblingOrder = (
                    SELECT Position FROM Ordered
                    WHERE Ordered.ChatContextId = Nodes.ChatContextId AND Ordered.Id = Nodes.Id
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ChatContextId_ParentId_SiblingOrder",
                table: "Nodes",
                columns: new[] { "ChatContextId", "ParentId", "SiblingOrder" },
                unique: true,
                filter: "\"ParentId\" IS NOT NULL");
        }
    }
}