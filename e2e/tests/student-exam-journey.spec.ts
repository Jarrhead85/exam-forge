import { test, expect, type Page } from "@playwright/test";
import { seed } from "./seed";

async function start(page: Page, code: string) {
  await page.goto("/exam");
  await page.getByLabel("Код приглашения", { exact: true }).fill(code);
  await page.getByRole("button", { name: "Продолжить", exact: true }).click();
  await page.getByRole("button", { name: "Начать работу", exact: true }).click();
  await page.getByRole("button", { name: "Открыть вопрос" }).click();
}

test("студент сдаёт последний вопрос и видит результат", async ({ page }) => {
  const data = await seed();
  await start(page, data.invitationCode);

  for (let question = 1; question <= 3; question++) {
    await expect(page.locator(".ef-exam-statusbar")).toContainText(`Вопрос ${question} из 3`);
    await page.getByRole("radio").first().check();
    const action = question === 3 ? "Сдать работу" : "Далее";
    await page.getByRole("button", { name: action, exact: true }).click();
  }

  await expect(page.locator("#result-title")).toHaveText("Работа сдана");
  await expect(page.locator(".ef-exam-score")).toContainText("Баллы: 3 из 3");
  await page.reload();
  await expect(page.locator("#result-title")).toBeVisible();
});

test("неверный код показывает понятную ошибку без ожидания WebAssembly", async ({ page }) => {
  await page.route("**/_framework/*.wasm", route => route.abort());
  await page.goto("/exam");
  await page.getByLabel("Код приглашения", { exact: true }).fill("XXXX-XXXX-XXXX");
  await page.getByRole("button", { name: "Продолжить", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Неверный код");
  await expect(page.locator("#code-title")).toBeVisible();
});

test("результат остаётся после повторного входа в новый браузерный контекст", async ({ page, browser }) => {
  const data = await seed();
  await start(page, data.revisitCode);
  await page.getByRole("button", { name: "Сдать досрочно" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("button", { name: "Продолжить работу" })).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("button", { name: "Сдать досрочно" })).toBeFocused();

  await page.getByRole("button", { name: "Сдать досрочно" }).click();
  await dialog.getByRole("button", { name: "Сдать работу" }).click();
  await expect(page.locator("#result-title")).toBeVisible();

  const revisit = await browser.newPage({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: process.env.E2E_IGNORE_HTTPS_ERRORS === "1",
  });
  try {
    await revisit.goto("/exam");
    await revisit.getByLabel("Код приглашения", { exact: true }).fill(data.revisitCode);
    await revisit.getByRole("button", { name: "Продолжить", exact: true }).click();
    await expect(revisit.getByRole("heading", { name: data.assignmentTitle })).toBeVisible();
    await expect(revisit.getByText("Попыток использовано: 1 из 2")).toBeVisible();
    await expect(revisit.locator(".ef-table")).toContainText("Оценено");
  } finally {
    await revisit.close();
  }
});

test("после разрыва связи ответ сохраняется и восстанавливается при перезагрузке", async ({ page }) => {
  const data = await seed();
  await start(page, data.offlineCode);
  const save = "**/api/attempts/*/answers/*";
  await page.route(save, route => route.abort("internetdisconnected"));
  await page.getByRole("radio").first().check();
  await expect(page.getByRole("button", { name: "Повторить сохранение" })).toBeVisible();
  await page.unroute(save);
  await expect(page.getByRole("status").filter({ hasText: "Ответ сохранён" })).toBeVisible();

  await page.reload();
  await expect(page.getByRole("radio").first()).toBeChecked();
});

test("серверные формы аккаунта и общей ссылки работают после выхода", async ({ page }) => {
  const data = await seed();
  await page.goto("/exam?mode=account");
  await page.getByLabel("Логин", { exact: true }).fill(data.accountLogin);
  await page.getByLabel("Пароль", { exact: true }).fill(data.accountPassword);
  await page.getByRole("button", { name: "Войти", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Доступные работы" })).toBeVisible();
  await page.getByRole("button", { name: "Выйти", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Вход на работу" })).toBeVisible();

  await page.goto("/exam?link=" + data.commonLinkCode);
  await expect(page.getByRole("heading", { name: data.assignmentTitle })).toBeVisible();
  await page.getByLabel("Фамилия", { exact: true }).fill("Проверочная");
  await page.getByLabel("Имя", { exact: true }).fill("Анна");
  await page.getByLabel("Группа", { exact: true }).fill("МЛ-103");
  await page.getByRole("button", { name: "Продолжить", exact: true }).click();
  await expect(page.getByRole("button", { name: "Начать работу", exact: true })).toBeVisible();
});
