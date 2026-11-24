#!/usr/bin/env bash
# Запускать из checkout проекта. Резервные копии содержат учебные данные.
set -euo pipefail
cd "$(dirname "$0")"
umask 077

[[ -f .env ]] || { echo "Скопируйте env.example в .env и заполните значения."; exit 1; }
if grep -q 'CHANGE_ME' .env; then
  echo "Заполните значения CHANGE_ME в .env."
  exit 1
fi

host=$(sed -n 's/^EXAMFORGE_ALLOWED_HOSTS=//p' .env | tail -n 1 | cut -d ';' -f 1)
[[ -n "$host" ]] || { echo "Укажите EXAMFORGE_ALLOWED_HOSTS в .env."; exit 1; }

docker compose build app
docker compose up -d --wait db
current_app=$(docker compose ps -a -q app)
docker compose stop app
migration_started=false
restore_service_on_backup_error() {
  if [[ "$migration_started" == false && -n "$current_app" ]]; then
    docker compose start app || true
  fi
}
trap restore_service_on_backup_error ERR

backup_dir="backups/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$backup_dir"
docker compose exec -T db pg_dump -Fc -U examforge examforge > "$backup_dir/database.dump"
docker compose exec -T db pg_restore --list < "$backup_dir/database.dump" > /dev/null

if [[ -n "$current_app" ]]; then
  docker compose run --rm --no-deps -T --entrypoint tar app -C /data/storage -czf - . > "$backup_dir/storage.tar.gz"
  tar -tzf "$backup_dir/storage.tar.gz" > /dev/null
fi

migration_started=true
docker compose run --rm --no-deps app --migrate
docker compose up -d --no-deps app

for (( attempt=1; attempt<=30; attempt++ )); do
  status=$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 3 \
    --header "Host: $host" --header 'X-Forwarded-Proto: https' http://127.0.0.1:8080/health/ready || true)
  if [[ "$status" == 200 ]]; then
    echo "Приложение готово. Резервная копия: $backup_dir"
    exit 0
  fi
  sleep 2
done

echo "Приложение не прошло проверку готовности. Проверьте docker compose logs app."
exit 1
