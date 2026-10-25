using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaperWorksAndMaterials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorTeacherId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "materials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TopicId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AuthorTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    SourceNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_materials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_materials_AspNetUsers_AuthorTeacherId",
                        column: x => x.AuthorTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_materials_file_assets_FileAssetId",
                        column: x => x.FileAssetId,
                        principalTable: "file_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_materials_subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_materials_topics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "topics",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "paper_works",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    StudentNameSnapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StudentGroupSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EnteredByTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TotalEarned = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    TotalMax = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_works", x => x.Id);
                    table.ForeignKey(
                        name: "FK_paper_works_AspNetUsers_EnteredByTeacherId",
                        column: x => x.EnteredByTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_paper_works_assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "material_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    StudentProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedByTeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_material_grants_AspNetUsers_GrantedByTeacherId",
                        column: x => x.GrantedByTeacherId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_material_grants_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_material_grants_materials_MaterialId",
                        column: x => x.MaterialId,
                        principalTable: "materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_material_grants_student_profiles_StudentProfileId",
                        column: x => x.StudentProfileId,
                        principalTable: "student_profiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "paper_scans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaperWorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_scans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_paper_scans_file_assets_FileAssetId",
                        column: x => x.FileAssetId,
                        principalTable: "file_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_paper_scans_paper_works_PaperWorkId",
                        column: x => x.PaperWorkId,
                        principalTable: "paper_works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "paper_scores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaperWorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Earned = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    Max = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_scores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_paper_scores_paper_works_PaperWorkId",
                        column: x => x.PaperWorkId,
                        principalTable: "paper_works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CreatedAtUtc",
                table: "audit_events",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_TargetType_TargetId",
                table: "audit_events",
                columns: new[] { "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_grants_GrantedByTeacherId",
                table: "material_grants",
                column: "GrantedByTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_material_grants_GroupId",
                table: "material_grants",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_material_grants_MaterialId_GroupId",
                table: "material_grants",
                columns: new[] { "MaterialId", "GroupId" },
                unique: true,
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_material_grants_MaterialId_StudentProfileId",
                table: "material_grants",
                columns: new[] { "MaterialId", "StudentProfileId" },
                unique: true,
                filter: "\"StudentProfileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_material_grants_StudentProfileId",
                table: "material_grants",
                column: "StudentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_materials_AuthorTeacherId",
                table: "materials",
                column: "AuthorTeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_materials_FileAssetId",
                table: "materials",
                column: "FileAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_materials_Status",
                table: "materials",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_materials_SubjectId",
                table: "materials",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_materials_TopicId",
                table: "materials",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_scans_FileAssetId",
                table: "paper_scans",
                column: "FileAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_scans_PaperWorkId",
                table: "paper_scans",
                column: "PaperWorkId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_scores_PaperWorkId",
                table: "paper_scores",
                column: "PaperWorkId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_works_AssignmentId",
                table: "paper_works",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_works_EnteredByTeacherId",
                table: "paper_works",
                column: "EnteredByTeacherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "material_grants");

            migrationBuilder.DropTable(
                name: "paper_scans");

            migrationBuilder.DropTable(
                name: "paper_scores");

            migrationBuilder.DropTable(
                name: "materials");

            migrationBuilder.DropTable(
                name: "paper_works");
        }
    }
}
