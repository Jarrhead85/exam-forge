using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VariantsAndPrinting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VariantId",
                table: "attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "variants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TestVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantNumber = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    QuestionOrder = table.Column<string>(type: "jsonb", nullable: false),
                    OptionOrders = table.Column<IReadOnlyDictionary<string, List<string>>>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_variants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_variants_test_versions_TestVersionId",
                        column: x => x.TestVersionId,
                        principalTable: "test_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attempts_VariantId",
                table: "attempts",
                column: "VariantId");

            migrationBuilder.CreateIndex(
                name: "IX_variants_TestVersionId_VariantNumber",
                table: "variants",
                columns: new[] { "TestVersionId", "VariantNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_attempts_variants_VariantId",
                table: "attempts",
                column: "VariantId",
                principalTable: "variants",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_attempts_variants_VariantId",
                table: "attempts");

            migrationBuilder.DropTable(
                name: "variants");

            migrationBuilder.DropIndex(
                name: "IX_attempts_VariantId",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "VariantId",
                table: "attempts");
        }
    }
}
