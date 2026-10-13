using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuestionBank.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FullHash",
                table: "Questions",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StemHash",
                table: "Questions",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Questions_FullHash",
                table: "Questions",
                column: "FullHash");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_StemHash",
                table: "Questions",
                column: "StemHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Questions_FullHash",
                table: "Questions");

            migrationBuilder.DropIndex(
                name: "IX_Questions_StemHash",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "FullHash",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "StemHash",
                table: "Questions");
        }
    }
}
