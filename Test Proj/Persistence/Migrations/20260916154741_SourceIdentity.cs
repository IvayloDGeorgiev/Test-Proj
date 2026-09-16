using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Test_Proj.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SourceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "PoliceRecords",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PoliceRecords_Dataset_Scope_SourceId",
                table: "PoliceRecords",
                columns: new[] { "Dataset", "Scope", "SourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PoliceRecords_Dataset_Scope_SourceId",
                table: "PoliceRecords");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "PoliceRecords");
        }
    }
}
