using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ManualReviewAndGradebook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "grade_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CriterionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OldEarnedPoints = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    NewEarnedPoints = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    OldAttemptEarned = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    NewAttemptEarned = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grade_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_grade_revisions_AspNetUsers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_grade_revisions_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "attempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_grades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CriterionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MaxPoints = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    EarnedPoints = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    TeacherComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    GradedByTeacherId = table.Column<Guid>(type: "uuid", nullable: true),
                    GradedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question_grades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_question_grades_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "attempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_question_grades_question_sessions_QuestionSessionId",
                        column: x => x.QuestionSessionId,
                        principalTable: "question_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_grade_revisions_AttemptId",
                table: "grade_revisions",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_grade_revisions_TeacherId",
                table: "grade_revisions",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_question_grades_AttemptId",
                table: "question_grades",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_question_grades_QuestionSessionId_CriterionId",
                table: "question_grades",
                columns: new[] { "QuestionSessionId", "CriterionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_question_grades_State",
                table: "question_grades",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "grade_revisions");

            migrationBuilder.DropTable(
                name: "question_grades");
        }
    }
}
