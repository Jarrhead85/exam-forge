import { spawnSync } from "node:child_process";
import { mkdirSync, existsSync } from "node:fs";
import path from "node:path";

/**
 * Готовит данные для E2E-прогона (P0-10):
 *  1) применяет EF-миграции к указанной БД (dotnet run -- --migrate);
 *  2) сидит предмет/тест/назначение/приглашение (dotnet run -- --seed-e2e .state/seed.json).
 * Тесты дальше читают .state/seed.json (tests/seed.ts).
 * E2E_SKIP_SEED=1 — пропустить оба шага (внешнее окружение уже подготовлено).
 */
export default function globalSetup(): void {
  if (process.env.E2E_SKIP_SEED === "1") {
    const artifact = path.resolve(__dirname, ".state/seed.json");
    if (!existsSync(artifact)) {
      throw new Error(
        `E2E_SKIP_SEED=1, но артефакт сидера не найден: ${artifact}. ЗапуститеSeeder или снимите E2E_SKIP_SEED.`,
      );
    }
    return;
  }

  const dotnet = process.env.DOTNET_EXE ?? "dotnet";
  const project = path.resolve(__dirname, "..", "src", "ExamForge.Web");
  const db = process.env.E2E_DB ?? "Host=localhost;Port=5432;Database=examforge_e2e;Username=postgres";
  const env = { ...process.env, ConnectionStrings__Default: db };

  mkdirSync(path.resolve(__dirname, ".state"), { recursive: true });

  const artifact = path.resolve(__dirname, ".state", "seed.json");
  const common = { stdio: "inherit" as const, env };

  const migrate = spawnSync(dotnet, ["run", "--project", project, "--no-build", "--", "--migrate"], common);
  if (migrate.status !== 0) {
    throw new Error("Миграции E2E-БД не применились (см. вывод выше). Сайт собран? (dotnet build перед прогоном)");
  }

  const seed = spawnSync(
    dotnet,
    ["run", "--project", project, "--no-build", "--", "--seed-e2e", artifact],
    common,
  );
  if (seed.status !== 0) {
    throw new Error("Сидер E2E-данных не отработал (см. вывод выше).");
  }
}
