using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFrameworksAndClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FrameworkId",
                table: "Questions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IndicatorId",
                table: "Questions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchMethod",
                table: "Questions",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MatchScore",
                table: "Questions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchStatus",
                table: "Questions",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MatchedAtUtc",
                table: "Questions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchedBy",
                table: "Questions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Frameworks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ImportedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Frameworks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Indicators",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FrameworkId = table.Column<long>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    MainDomain = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SubDomain = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    SourceRow = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Indicators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Indicators_Frameworks_FrameworkId",
                        column: x => x.FrameworkId,
                        principalTable: "Frameworks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Questions_IndicatorId",
                table: "Questions",
                column: "IndicatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Frameworks_Name_Version",
                table: "Frameworks",
                columns: new[] { "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Indicators_FrameworkId_Order",
                table: "Indicators",
                columns: new[] { "FrameworkId", "Order" });

            migrationBuilder.AddForeignKey(
                name: "FK_Questions_Indicators_IndicatorId",
                table: "Questions",
                column: "IndicatorId",
                principalTable: "Indicators",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Questions_Indicators_IndicatorId",
                table: "Questions");

            migrationBuilder.DropTable(
                name: "Indicators");

            migrationBuilder.DropTable(
                name: "Frameworks");

            migrationBuilder.DropIndex(
                name: "IX_Questions_IndicatorId",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "FrameworkId",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "IndicatorId",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MatchMethod",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MatchScore",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MatchStatus",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MatchedAtUtc",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MatchedBy",
                table: "Questions");
        }
    }
}
