-- Синтетические строки в схеме до обновления. Вход и сдачу проверяет Playwright.
BEGIN;

INSERT INTO student_profiles ("Id", "LastName", "FirstName", "CreatedAtUtc", "CreatedByTeacherId", "IsIdentified")
SELECT gen_random_uuid(), 'Upgrade', 'Student', now(), "Id", true
FROM "AspNetUsers" WHERE "UserName" = 'admin1';

INSERT INTO student_accounts ("Id", "StudentProfileId", "Login", "PasswordHash", "IsDisabled", "FailedAttempts",
                              "CreatedByTeacherId", "CreatedAtUtc")
SELECT gen_random_uuid(), "Id", 'upgrade-student', 'disabled-test-fixture', true, 0, "CreatedByTeacherId", now()
FROM student_profiles WHERE "LastName" = 'Upgrade';

INSERT INTO assignments ("Id", "TestVersionId", "Mode", "Feedback", "OpensAtUtc", "MaxAttempts",
                         "NoBacktracking", "AllowCommonLink", "CreatedByTeacherId", "CreatedAtUtc")
SELECT gen_random_uuid(), v."Id", 1, 1, now(), 2, true, false, t."AuthorTeacherId", now()
FROM test_versions AS v JOIN tests AS t ON t."Id" = v."TestId";

INSERT INTO invitations ("Id", "AssignmentId", "StudentProfileId", "CodeHash", "Status", "FailedAttempts",
                         "CreatedByTeacherId", "CreatedAtUtc")
SELECT gen_random_uuid(), a."Id", p."Id", repeat('0', 64), 1, 0, a."CreatedByTeacherId", now()
FROM assignments AS a JOIN student_profiles AS p ON p."CreatedByTeacherId" = a."CreatedByTeacherId";

COMMIT;
