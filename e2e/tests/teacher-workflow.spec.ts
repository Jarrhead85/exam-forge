import { expect, request, test, type Page } from "@playwright/test";
import { seed } from "./seed";

async function openCabinet(page: Page, route: string) {
  const data = await seed();
  await page.goto("/account/login");
  await page.getByLabel("Логин", { exact: true }).fill(data.teacherLogin);
  await page.getByLabel("Пароль", { exact: true }).fill(data.teacherPassword);
  await page.getByRole("button", { name: "Войти", exact: true }).click();
  await expect(page).toHaveURL(/\/cabinet$/);
  await page.goto(route);
}

test("преподаватель импортирует и приглашает группу из 30 студентов", async ({ page }) => {
  const data = await seed();
  const size = 30;
  const groupName = "Пилотная группа " + data.assignmentId.slice(0, 8);
  await page.setViewportSize({ width: 320, height: 850 });
  await openCabinet(page, "/cabinet/groups");
  const groupNameInput = page.locator("#g-name");
  const create = page.getByRole("button", { name: "Создать", exact: true });
  await expect(create).toBeEnabled();
  await groupNameInput.fill(groupName);
  await create.click();

  const group = page.getByRole("region", { name: groupName, exact: true });
  await group.getByText("Импортировать студентов из CSV", { exact: true }).click();
  const rows = Array.from({ length: size }, (_, index) => ["Тестов", "Студент " + (index + 1), ""].join(";"));
  await group.getByLabel("Файл CSV").setInputFiles({
    name: "test-group.csv",
    mimeType: "text/csv",
    buffer: Buffer.from(["Фамилия;Имя;Отчество", ...rows].join("\n"), "utf8"),
  });
  await group.getByRole("button", { name: "Добавить выбранных (30)", exact: true }).click();
  await expect(group.getByRole("status")).toHaveText("Добавлено студентов: 30.");

  await page.goto("/cabinet/assignments/" + data.assignmentId);
  const lookup = page.getByLabel("Выдать коды группе");
  const choice = page.getByRole("option", { name: groupName + " (30)", exact: true });
  await lookup.selectOption({ label: (await choice.textContent())! });
  const issue = page.getByRole("button", { name: "Выдать коды группе", exact: true });
  await issue.click();
  const result = page.getByRole("region", { name: "Коды группы", exact: true });
  await expect(result.getByRole("status")).toHaveText("Выдано 30 · Уже приглашены 0");
  const codes = await result.locator("code").allTextContents();
  await issue.click();
  await expect(result.getByRole("status")).toHaveText("Выдано 0 · Уже приглашены 30");

  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(320);
  const baseURL = new URL(page.url()).origin;
  // Все клиенты используют один внешний IP и отдельные студенческие сессии.
  const scores = await Promise.all(codes.map(async code => {
    const student = await request.newContext({
      baseURL,
      ignoreHTTPSErrors: process.env.E2E_IGNORE_HTTPS_ERRORS === "1",
    });
    try {
      await expect(await student.post("/api/access/exchange", { data: { code } })).toBeOK();
      const csrf = await student.get("/api/access/antiforgery");
      await expect(csrf).toBeOK();
      const headers = { "X-XSRF-TOKEN": (await csrf.json()).requestToken as string };
      const started = await student.post("/api/assignments/" + data.assignmentId + "/attempts", { headers });
      await expect(started).toBeOK();
      const attemptUrl = "/api/attempts/" + (await started.json()).attemptId;
      const next = await student.post(attemptUrl + "/open-next", { headers });
      await expect(next).toBeOK();
      const question = (await next.json()).currentQuestion;
      const submitted = await student.post(attemptUrl + "/finish", {
        headers,
        data: { questionSessionId: question.questionSessionId, expectedRevision: 0, answer: { selectedOptionId: "a" } },
      });
      await expect(submitted).toBeOK();
      return await submitted.json();
    } finally {
      await student.dispose();
    }
  }));
  expect(scores).toHaveLength(size);
  expect(scores.every(score => score.status === "Finalized" && score.earnedPoints === 1)).toBeTruthy();
});

test("пробная работа не меняет ведомость, разбор создаёт черновик тренировки", async ({ page }) => {
  const data = await seed();
  await openCabinet(page, "/cabinet/assignments/" + data.assignmentId);
  await page.getByRole("link", { name: "Повторить работу" }).click();
  await expect(page.getByText("Параметры заполнены из предыдущей работы.", { exact: false })).toBeVisible();
  await expect(page.locator("#a-attempts")).toHaveValue("2");
  await expect(page.locator("#a-perq")).toHaveValue("120");
  await expect(page.locator("#a-opens")).toHaveValue("");
  await page.goto("/cabinet/assignments/" + data.assignmentId);

  const statsUrl = "/api/assignments/" + data.assignmentId + "/stats";
  const before = (await (await page.request.get(statsUrl)).json()).attemptsTotal;

  await page.getByRole("link", { name: "Пройти пробно" }).click();
  await page.getByRole("button", { name: "Открыть первый вопрос" }).click();
  for (let index = 0; index < 3; index++) {
    await page.getByRole("radio").first().check();
    const action = index === 2 ? "Завершить пробную работу" : "Далее";
    await page.getByRole("button", { name: action, exact: true }).click();
  }
  await expect(page.getByRole("heading", { name: "Пробная работа завершена" })).toBeVisible();
  expect((await (await page.request.get(statsUrl)).json()).attemptsTotal).toBe(before);

  await page.goto("/cabinet/results?assignment=" + data.assignmentId);
  await page.getByRole("button", { name: "Показать результаты по заданиям" }).click();
  const analysis = page.getByRole("region", { name: "Разбор по заданиям", exact: true });
  await expect(analysis.getByRole("table")).toBeVisible();
  await analysis.getByRole("checkbox").first().check();
  await page.getByRole("button", { name: "Создать черновик тренировки" }).click();
  await expect(page).toHaveURL(/\/cabinet\/tests#t-/);
  const heading = new URL(page.url()).hash;
  await expect(page.locator(heading)).toHaveText("Тренировка — " + data.testTitle);
});
