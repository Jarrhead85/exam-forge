using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StudentSessionVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SessionVersion",
                table: "student_accounts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionVersion",
                table: "student_accounts");
        }
    }
}
