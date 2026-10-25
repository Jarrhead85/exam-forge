#!/usr/bin/env bash
# Развёртывание Exam Forge на VDS (§21). Запускать из каталога с docker-compose.yml.
# Перед этим: .env заполнен, исходники рядом (deploy/docker/Dockerfile в контексте ..).
set -euo pipefail
cd "$(dirname "$0")"

echo "== 1. Проверка .env =="
[ -f .env ] || { echo "Нет .env — скопируйте env.example и заполните."; exit 1; }
grep -q CHANGE_ME .env && { echo "В .env остались CHANGE_ME значения."; exit 1; }

echo "== 2. Backup БД (если уже существует) =="
if docker compose exec db pg_dump -U examforge examforge > "backup-$(date +%Y%m%d-%H%M%S).sql" 2>/dev/null; then
  echo "Backup сделан."
else
  echo "БД ещё нет — пропускаю backup (первый запуск)."
fi

echo "== 3. Сборка и запуск =="
docker compose up -d --build

echo "== 4. Миграции (однократно, вручную — §21.9) =="
docker compose run --rm app dotnet ExamForge.Web.dll --migrate

echo "== 5. Ждём готовности БД/приложения =="
for i in $(seq 1 30); do
  if curl -sf -o /dev/null http://127.0.0.1:8080/; then echo "Приложение отвечает."; break; fi
  sleep 2
  [ "$i" = 30 ] && { echo "Приложение не отвечает — смотрите docker compose logs app"; exit 1; }
done

echo "== Готово. Создайте администратора: =="
echo "  docker compose run --rm app dotnet ExamForge.Web.dll --seed-admin <логин> <пароль> '<ФИО>'"
