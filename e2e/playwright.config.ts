import { defineConfig } from "@playwright/test";

/**
 * E2E-система Exam Forge (P0-10).
 *
 * Требуется работающий сайт + PostgreSQL. Таблица сочетаний:
 *  - `E2E_WEB_SERVER=managed` — Playwright сам поднимает сайт (dotnet run) и останавливает его
 *    после прогона; БД указывается переменной E2E_DB (подключение Npgsql для examforge_e2e).
 *  - по умолчанию — сайт уже запущен на E2E_BASE_URL (например, docker compose на VDS),
 *    Playwright только его проверяет.
 * Сессия сидера: global-setup вызывает `dotnet run -- --seed-e2e .state/seed.json`;
 * E2E_SKIP_SEED=1 пропускает засев, если артефакт уже лежит на диске.
 */
const port = Number(process.env.E2E_PORT ?? 5080);
const baseURL = process.env.E2E_BASE_URL ?? `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: "./tests",
  globalSetup: "./global-setup.ts",
  timeout: 120_000,
  expect: { timeout: 20_000 },
  // Общая БД и лимит на обмен кодом (15/мин по IP): один воркер, последовательные тесты.
  fullyParallel: false,
  workers: 1,
  reporter: process.env.CI ? [["github"], ["list", { printSteps: true }]] : "list",
  use: {
    baseURL,
    trace: "retain-on-failure",
    locale: "ru-RU",
  },
  webServer:
    process.env.E2E_WEB_SERVER === "managed"
      ? {
          command: `dotnet run --project ../src/ExamForge.Web --no-build --urls ${baseURL}`,
          url: baseURL,
          reuseExistingServer: true,
          timeout: 300_000,
          env: {
            // БД и ключ — только через переменные окружения; ключ HMAC нужен приложению
            // всё равно (может быть dev-fallback в Development).
            ConnectionStrings__Default:
              process.env.E2E_DB ??
              "Host=localhost;Port=5432;Database=examforge_e2e;Username=postgres",
            ASPNETCORE_ENVIRONMENT: "Development",
          },
        }
      : undefined,
});
