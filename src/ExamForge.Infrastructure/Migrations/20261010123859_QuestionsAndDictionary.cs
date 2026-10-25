using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QuestionsAndDictionary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dictionaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dictionaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dictionaries_subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TopicId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_questions_AspNetUsers_AuthorTeacherId",
                        column: x => x.AuthorTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_questions_subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_questions_topics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "topics",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "dictionary_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DictionaryId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Lemma = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Translations = table.Column<string>(type: "jsonb", nullable: false),
                    Features_FormKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Features_Gender = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Features_Declension = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Features_GrammaticalCase = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Topics = table.Column<string>(type: "jsonb", nullable: false),
                    SourceRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dictionary_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dictionary_entries_dictionaries_DictionaryId",
                        column: x => x.DictionaryId,
                        principalTable: "dictionaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DictionaryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedByTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_jobs_dictionaries_DictionaryId",
                        column: x => x.DictionaryId,
                        principalTable: "dictionaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_import_jobs_file_assets_FileAssetId",
                        column: x => x.FileAssetId,
                        principalTable: "file_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    MaxPoints = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    ProposedTimeLimitSeconds = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_question_versions_questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dictionaries_SubjectId",
                table: "dictionaries",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_dictionary_entries_DictionaryId_Lemma",
                table: "dictionary_entries",
                columns: new[] { "DictionaryId", "Lemma" });

            migrationBuilder.CreateIndex(
                name: "IX_dictionary_entries_Status",
                table: "dictionary_entries",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_DictionaryId",
                table: "import_jobs",
                column: "DictionaryId");

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_FileAssetId",
                table: "import_jobs",
                column: "FileAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_Status",
                table: "import_jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_question_versions_PublishedAtUtc",
                table: "question_versions",
                column: "PublishedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_question_versions_QuestionId_VersionNumber",
                table: "question_versions",
                columns: new[] { "QuestionId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_questions_AuthorTeacherId",
                table: "questions",
                column: "AuthorTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_questions_SubjectId",
                table: "questions",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_questions_TopicId",
                table: "questions",
                column: "TopicId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dictionary_entries");

            migrationBuilder.DropTable(
                name: "import_jobs");

            migrationBuilder.DropTable(
                name: "question_versions");

            migrationBuilder.DropTable(
                name: "dictionaries");

            migrationBuilder.DropTable(
                name: "questions");
        }
    }
}
