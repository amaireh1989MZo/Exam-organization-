using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNearDuplicateBands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuestionBands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QuestionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Band = table.Column<byte>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionBands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestionBands_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuestionBands_Band_Key",
                table: "QuestionBands",
                columns: new[] { "Band", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_QuestionBands_QuestionId",
                table: "QuestionBands",
                column: "QuestionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuestionBands");
        }
    }
}
