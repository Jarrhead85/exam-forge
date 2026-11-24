import { test, expect } from "@playwright/test";
import { seed } from "./seed";

/**
 * P0-10: кабинет преподавателя — форма логина (SSR), ошибка неверного пароля,
 * попадание в /cabinet. Лок-аут выключаем осторожно: ровно одна неудачная попытка.
 */
test("преподаватель: неверный пароль — ошибка, верный — кабинет", async ({ page }) => {
  const data = await seed();

  await page.goto("/account/login");
  await expect(page.locator("h1")).toContainText("Вход преподавателя");

  // Неверный пароль: alert-сообщение, редиректа в кабинет нет.
  await page.locator("#login").fill(data.teacherLogin);
  await page.locator("#password").fill("WrongPassword#2026");
  await page.getByRole("button", { name: "Войти" }).click();
  await expect(page.locator(".ef-form-error")).toContainText("Не удалось войти");
  await expect(page).not.toHaveURL(/\/cabinet/);

  // Верные данные.
  await page.locator("#password").fill(data.teacherPassword);
  await page.getByRole("button", { name: "Войти" }).click();
  await expect(page).toHaveURL(/\/cabinet/);
  await expect(page.locator("h1").first()).toBeVisible();
});
