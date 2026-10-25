import { readFile } from "node:fs/promises";
import path from "node:path";

export interface E2ESeed {
  teacherLogin: string;
  teacherPassword: string;
  invitationCode: string;
  assignmentTitle: string;
  testTitle: string;
  subjectName: string;
}

let cached: E2ESeed | null = null;

/** Читает артефакт сидера E2E (global-setup → .state/seed.json). */
export async function seed(): Promise<E2ESeed> {
  cached ??= JSON.parse(
    await readFile(path.resolve(__dirname, "..", ".state", "seed.json"), "utf8"),
  ) as E2ESeed;
  return cached;
}
