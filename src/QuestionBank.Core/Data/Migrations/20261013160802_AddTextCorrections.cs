using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTextCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrectedOptionsJson",
                table: "Questions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectedText",
                table: "Questions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorrectedOptionsJson",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "CorrectedText",
                table: "Questions");
        }
    }
}
