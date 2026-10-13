using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerCommands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerStates",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Busy = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProblemCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ProblemMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    HeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerStates", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerCommands_ProcessedAtUtc_Id",
                table: "WorkerCommands",
                columns: new[] { "ProcessedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerCommands");

            migrationBuilder.DropTable(
                name: "WorkerStates");
        }
    }
}
