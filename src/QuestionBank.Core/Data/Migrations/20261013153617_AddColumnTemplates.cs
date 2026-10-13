using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColumnTemplates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    HeaderKey = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    HeadersJson = table.Column<string>(type: "TEXT", nullable: false),
                    StemHeader = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OptionHeadersJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnswerHeader = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CodeHeader = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnTemplates_HeaderKey",
                table: "ColumnTemplates",
                column: "HeaderKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnTemplates");
        }
    }
}
