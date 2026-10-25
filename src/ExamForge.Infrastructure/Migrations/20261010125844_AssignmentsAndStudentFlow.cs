using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentsAndStudentFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "question_sessions",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadlineUtc",
                table: "attempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "attempts",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "answer_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AnswerJson = table.Column<string>(type: "jsonb", nullable: false),
                    SavedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_answer_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_answer_revisions_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "attempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_answer_revisions_question_sessions_QuestionSessionId",
                        column: x => x.QuestionSessionId,
                        principalTable: "question_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tests_AspNetUsers_AuthorTeacherId",
                        column: x => x.AuthorTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tests_subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "test_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TestId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    QuestionVersionIds = table.Column<string>(type: "jsonb", nullable: false),
                    MaxPoints = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_versions_tests_TestId",
                        column: x => x.TestId,
                        principalTable: "tests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TestVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Feedback = table.Column<int>(type: "integer", nullable: false),
                    OpensAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosesAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    PerQuestionTimeLimitSeconds = table.Column<int>(type: "integer", nullable: true),
                    AttemptTotalTimeLimitSeconds = table.Column<int>(type: "integer", nullable: true),
                    NoBacktracking = table.Column<bool>(type: "boolean", nullable: false),
                    AllowCommonLink = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assignments_AspNetUsers_CreatedByTeacherId",
                        column: x => x.CreatedByTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assignments_test_versions_TestVersionId",
                        column: x => x.TestVersionId,
                        principalTable: "test_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "extra_attempt_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    GrantedByTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedByAttemptId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extra_attempt_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_extra_attempt_grants_AspNetUsers_GrantedByTeacherId",
                        column: x => x.GrantedByTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_extra_attempt_grants_assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_extra_attempt_grants_attempts_UsedByAttemptId",
                        column: x => x.UsedByAttemptId,
                        principalTable: "attempts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_extra_attempt_grants_student_profiles_StudentProfileId",
                        column: x => x.StudentProfileId,
                        principalTable: "student_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CodeHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReplacedByInvitationId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invitations_AspNetUsers_CreatedByTeacherId",
                        column: x => x.CreatedByTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invitations_assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invitations_student_profiles_StudentProfileId",
                        column: x => x.StudentProfileId,
                        principalTable: "student_profiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_answer_revisions_AttemptId",
                table: "answer_revisions",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_answer_revisions_QuestionSessionId_Revision",
                table: "answer_revisions",
                columns: new[] { "QuestionSessionId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assignments_CreatedByTeacherId",
                table: "assignments",
                column: "CreatedByTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_OpensAtUtc",
                table: "assignments",
                column: "OpensAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_TestVersionId",
                table: "assignments",
                column: "TestVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_extra_attempt_grants_AssignmentId_StudentProfileId",
                table: "extra_attempt_grants",
                columns: new[] { "AssignmentId", "StudentProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_extra_attempt_grants_GrantedByTeacherId",
                table: "extra_attempt_grants",
                column: "GrantedByTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_extra_attempt_grants_StudentProfileId",
                table: "extra_attempt_grants",
                column: "StudentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_extra_attempt_grants_UsedByAttemptId",
                table: "extra_attempt_grants",
                column: "UsedByAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_AssignmentId_StudentProfileId",
                table: "invitations",
                columns: new[] { "AssignmentId", "StudentProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_CodeHash",
                table: "invitations",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invitations_CreatedByTeacherId",
                table: "invitations",
                column: "CreatedByTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_StudentProfileId",
                table: "invitations",
                column: "StudentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_test_versions_PublishedAtUtc",
                table: "test_versions",
                column: "PublishedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_test_versions_TestId_VersionNumber",
                table: "test_versions",
                columns: new[] { "TestId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tests_AuthorTeacherId",
                table: "tests",
                column: "AuthorTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_tests_SubjectId",
                table: "tests",
                column: "SubjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answer_revisions");

            migrationBuilder.DropTable(
                name: "extra_attempt_grants");

            migrationBuilder.DropTable(
                name: "invitations");

            migrationBuilder.DropTable(
                name: "assignments");

            migrationBuilder.DropTable(
                name: "test_versions");

            migrationBuilder.DropTable(
                name: "tests");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "question_sessions");

            migrationBuilder.DropColumn(
                name: "DeadlineUtc",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "attempts");
        }
    }
}
