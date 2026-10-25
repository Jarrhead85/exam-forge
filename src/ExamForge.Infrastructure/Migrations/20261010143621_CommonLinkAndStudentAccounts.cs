using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CommonLinkAndStudentAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommonLinkCode",
                table: "assignments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "student_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Login = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsDisabled = table.Column<bool>(type: "boolean", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_student_accounts_AspNetUsers_CreatedByTeacherId",
                        column: x => x.CreatedByTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_student_accounts_student_profiles_StudentProfileId",
                        column: x => x.StudentProfileId,
                        principalTable: "student_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_CommonLinkCode",
                table: "assignments",
                column: "CommonLinkCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_student_accounts_CreatedByTeacherId",
                table: "student_accounts",
                column: "CreatedByTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_student_accounts_Login",
                table: "student_accounts",
                column: "Login",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_student_accounts_StudentProfileId",
                table: "student_accounts",
                column: "StudentProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "student_accounts");

            migrationBuilder.DropIndex(
                name: "IX_assignments_CommonLinkCode",
                table: "assignments");

            migrationBuilder.DropColumn(
                name: "CommonLinkCode",
                table: "assignments");
        }
    }
}
