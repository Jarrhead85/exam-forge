import { test, expect } from "@playwright/test";
import { seed } from "./seed";

/**
 * P0-10, ключевой UI-путь: обмен кода → список работ → начало → три вопроса
 * (радиокнопки, «Далее») → подтверждение «Завершить» → экран результата (ТЗ §10).
 * Сид всей цепочки делает E2ESeeder (global-setup), первый вариант — правильный.
 */
test("студент: обмен кода → все вопросы → завершение → результат", async ({ page }) => {
  const data = await seed();

  await page.goto("/exam");
  await expect(page.locator("#code-title")).toContainText("Вход на работу");

  // Обмен кода приглашения.
  await page.locator("#exam-code").fill(data.invitationCode);
  await page.getByRole("button", { name: "Продолжить" }).click();

  // Список работ с назначением и кнопкой старта.
  await expect(page.getByRole("heading", { name: data.assignmentTitle })).toBeVisible();
  await page.getByRole("button", { name: /Начать/ }).click();

  // Экран попытки: сначала «Открыть вопрос» (старт по клику — ТЗ §10.1).
  await expect(page.getByRole("button", { name: "Открыть вопрос" })).toBeVisible();
  await page.getByRole("button", { name: "Открыть вопрос" }).click();

  // Три вопроса единственного типа: выбрать первую радиокнопку (верную) и «Далее».
  for (let question = 1; question <= 3; question++) {
    await expect(page.locator(".ef-exam-statusbar")).toContainText(`Вопрос ${question} из 3`);
    await expect(page.locator(".ef-exam-question")).toBeVisible();
    await page.locator(".ef-choice-option input[type=radio]").first().check();
    await page.getByRole("button", { name: "Далее", exact: true }).click();
  }

  // Последний «Далее» само завершает попытку → экран результата.
  await expect(page.locator("#result-title")).toHaveText("Работа сдана");
  await expect(page.locator(".ef-exam-score")).toContainText("из 3");
});

test("студент: неверный код — понятная ошибка без входа", async ({ page }) => {
  await page.goto("/exam");

  await page.locator("#exam-code").fill("XXXX-XXXX-XXXX");
  await page.getByRole("button", { name: "Продолжить" }).click();

  await expect(page.locator(".ef-form-error")).toContainText("Неверный код");
  await expect(page.locator("#code-title")).toBeVisible();
});

test("студент: повторный визит после сдачи — результат сохранён в списке работ", async ({ page }) => {
  const data = await seed();

  // Сессия студента живёт в cookie ef.student (Strict) — контекст уже «сдал работу»
  // после первого теста файла; «Открыть» из истории попыток тоже ведёт на результат.
  await page.goto("/exam");
  await expect(page.getByRole("heading", { name: data.assignmentTitle })).toBeVisible();
  await expect(page.getByText("Попыток использовано: 1 из 2")).toBeVisible();
  await expect(page.locator(".ef-table")).toContainText("Finalized");
});
