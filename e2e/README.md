# E2E-пакет Exam Forge (Playwright)

Сквозные UI-сценарии против работающего сайта (P0-10). Содержание прогоняет:
полный студенческий путь (обмен кода → вопросы → завершение → результат),
негативный вход по коду и вход/кабинет преподавателя.

## Структура

| Файл | Назначение |
| --- | --- |
| `playwright.config.ts` | baseURL/порт, режим `managed` (Playwright сам поднимает сайт), 1 воркер (лимит обмена кода 15/мин по IP) |
| `global-setup.ts` | `dotnet run -- --migrate` + `dotnet run -- --seed-e2e .state/seed.json` |
| `tests/seed.ts` | чтение артефакта сидера (коды/логины) |
| `tests/student-exam-journey.spec.ts` | полный студенческий путь (3 SingleChoice, автооценка, результат) |
| `tests/teacher-login.spec.ts` | вход преподавателя: ошибка пароля → кабинет |

## Окружение

| Переменная | Значение |
| --- | --- |
| `E2E_DB` | подключение Npgsql к отдельной тестовой БД (создаётся заранее, например `CREATE DATABASE examforge_e2e;`) |
| `E2E_WEB_SERVER` | `managed` = Playwright запускает `dotnet run`; иначе сайт должен уже работать |
| `E2E_BASE_URL` | адрес работающего сайта (по умолчанию `http://127.0.0.1:5080`) |
| `E2E_PORT` | порт `managed`-режима (по умолчанию 5080) |
| `E2E_SKIP_SEED` | `1` = не пересеивать (использовать существующий `.state/seed.json`) |
| `E2E_TEACHER_LOGIN` / `E2E_TEACHER_PASSWORD` | пароль/логин сидируемого преподавателя (есть безопасные дефолты) |

## Локальный запуск (нужен PostgreSQL)

```sh
dotnet build ExamForge.sln -c Release                # сайт и сидер собираются один раз
cd e2e
npm ci
npx playwright install chromium
set E2E_WEB_SERVER=managed                           # powershell: $env:E2E_WEB_SERVER="managed"
set E2E_DB="Host=localhost;Port=5432;Database=examforge_e2e;Username=postgres"
npx playwright test
```

## CI (docker compose Postgres)

```sh
docker compose -f deploy/docker/docker-compose.yml up -d db   # сервис PostgreSQL
export E2E_DB="Host=127.0.0.1;Port=5432;Database=examforge_e2e;Username=postgres;Password=$PGPASSWORD"
export E2E_WEB_SERVER=managed
npm ci && npx playwright install --with-deps chromium
npx playwright test
```

Снит используется `reuseExistingServer: true`: если на URL уже живёт сайт (например,
после деплоя по `deploy/docker/deploy.sh`), Playwright не перезапускает его.

## Замечания

- Сидер всегда создаёт **новое** назначение/приглашение; БД теста стоит пересоздавать
  между прогонами, чтобы не копить мусор (или оставить — данные независимы).
- Мониторинг ошибки: `.state/seed.json` содержит рабочий код приглашения — в Git не коммитится.
- Пути всего: `test --project student-exam-journey`, отдельные спеки — `npx playwright test tests/teacher-login.spec.ts`.
