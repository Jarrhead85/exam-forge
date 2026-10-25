using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DictionaryOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "dictionaries",
                type: "integer",
                nullable: false,
                // Private = 1: существующие legacy-словари получают валидное значение;
                // их доступность для всех обеспечивает OwnerTeacherId = Guid.Empty (P0-08).
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerTeacherId",
                table: "dictionaries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "dictionary_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DictionaryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedByTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dictionary_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dictionary_grants_dictionaries_DictionaryId",
                        column: x => x.DictionaryId,
                        principalTable: "dictionaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dictionaries_Mode",
                table: "dictionaries",
                column: "Mode");

            migrationBuilder.CreateIndex(
                name: "IX_dictionaries_OwnerTeacherId",
                table: "dictionaries",
                column: "OwnerTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_dictionary_grants_DictionaryId_TeacherId",
                table: "dictionary_grants",
                columns: new[] { "DictionaryId", "TeacherId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dictionary_grants");

            migrationBuilder.DropIndex(
                name: "IX_dictionaries_Mode",
                table: "dictionaries");

            migrationBuilder.DropIndex(
                name: "IX_dictionaries_OwnerTeacherId",
                table: "dictionaries");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "dictionaries");

            migrationBuilder.DropColumn(
                name: "OwnerTeacherId",
                table: "dictionaries");
        }
    }
}
