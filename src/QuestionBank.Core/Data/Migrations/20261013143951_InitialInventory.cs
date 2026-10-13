using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScanRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DirectoriesCompleted = table.Column<long>(type: "INTEGER", nullable: false),
                    DirectoriesFailed = table.Column<long>(type: "INTEGER", nullable: false),
                    FilesSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    FilesNew = table.Column<long>(type: "INTEGER", nullable: false),
                    FilesChanged = table.Column<long>(type: "INTEGER", nullable: false),
                    FilesMissing = table.Column<long>(type: "INTEGER", nullable: false),
                    LinksSkipped = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryErrors = table.Column<long>(type: "INTEGER", nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceFiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastErrorAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanDirectories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanDirectories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanDirectories_ScanRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ScanRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScanDirectories_RunId_RelativePath",
                table: "ScanDirectories",
                columns: new[] { "RunId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScanDirectories_RunId_Status_NextAttemptUtc",
                table: "ScanDirectories",
                columns: new[] { "RunId", "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanRuns_Status",
                table: "ScanRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_RelativePath",
                table: "SourceFiles",
                column: "RelativePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_Status_Id",
                table: "SourceFiles",
                columns: new[] { "Status", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanDirectories");

            migrationBuilder.DropTable(
                name: "SourceFiles");

            migrationBuilder.DropTable(
                name: "ScanRuns");
        }
    }
}
