using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalAI.Api.Migrations
{
    /// <inheritdoc />
    public partial class AdvancedRag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChunkLevel",
                table: "Documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentChunkId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Conversations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MessageFeedbacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Helpful = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageFeedbacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageFeedbacks_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_document_level",
                table: "Documents",
                column: "ChunkLevel");

            migrationBuilder.CreateIndex(
                name: "idx_document_parent",
                table: "Documents",
                column: "ParentChunkId");

            migrationBuilder.CreateIndex(
                name: "idx_feedback_message",
                table: "MessageFeedbacks",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageFeedbacks");

            migrationBuilder.DropIndex(
                name: "idx_document_level",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "idx_document_parent",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ChunkLevel",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ParentChunkId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Conversations");
        }
    }
}
