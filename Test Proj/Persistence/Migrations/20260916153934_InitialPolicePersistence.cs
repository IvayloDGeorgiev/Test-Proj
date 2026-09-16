using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Test_Proj.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPolicePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PoliceRecords",
                columns: table => new
                {
                    Dataset = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Scope = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Data = table.Column<string>(type: "text", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoliceRecords", x => new { x.Dataset, x.Scope, x.Key });
                    table.CheckConstraint("CK_PoliceRecords_Dataset", "\"Dataset\" IN ('forces','crimes','stop-searches')");
                    table.CheckConstraint("CK_PoliceRecords_Times", "\"UpdatedAtUtc\" >= \"FirstSeenUtc\" AND \"LastSeenUtc\" >= \"UpdatedAtUtc\"");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PoliceRecords");
        }
    }
}
