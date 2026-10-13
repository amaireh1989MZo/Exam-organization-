using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Exports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Format = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QuestionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RejectedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exports", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Exports");
        }
    }
}
