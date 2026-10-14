using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionsAndUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Uploads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ClientPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    QuestionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Uploads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Questions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StableKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceFileId = table.Column<long>(type: "INTEGER", nullable: true),
                    UploadId = table.Column<long>(type: "INTEGER", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OriginalOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalCode = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OriginalText = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalOptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalCorrectIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    AnswerDetector = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Confidence = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IssuesJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReaderName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReaderVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ExtractedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questions", x => x.Id);
                    table.CheckConstraint("CK_Questions_OneSource", "(SourceFileId IS NULL) <> (UploadId IS NULL)");
                    table.ForeignKey(
                        name: "FK_Questions_SourceFiles_SourceFileId",
                        column: x => x.SourceFileId,
                        principalTable: "SourceFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Questions_Uploads_UploadId",
                        column: x => x.UploadId,
                        principalTable: "Uploads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Questions_ContentHash",
                table: "Questions",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_ReviewStatus",
                table: "Questions",
                column: "ReviewStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_SourceFileId",
                table: "Questions",
                column: "SourceFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_StableKey",
                table: "Questions",
                column: "StableKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Questions_UploadId",
                table: "Questions",
                column: "UploadId");

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_Sha256",
                table: "Uploads",
                column: "Sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Questions");

            migrationBuilder.DropTable(
                name: "Uploads");
        }
    }
}
