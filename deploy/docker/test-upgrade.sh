#!/usr/bin/env bash
# Выполняется на одноразовом GitHub-hosted runner на синтетических данных.
set -euo pipefail
cd "$(dirname "$0")/../.."
repo=$PWD
[[ ${GITHUB_ACTIONS:-} == true && ${RUNNER_ENVIRONMENT:-} == github-hosted ]] || {
  echo "Запустите проверку через Docker upgrade and rollback на GitHub Actions."
  exit 1
}

scratch=$(mktemp -d)
baseline=$scratch/baseline
env_file=$repo/deploy/docker/.env
compose_file=$repo/deploy/docker/docker-compose.yml
baseline_compose=$baseline/deploy/docker/docker-compose.yml
compose=(docker compose --env-file "$env_file" -f "$compose_file")
previous=(docker compose --project-directory "$baseline" --env-file "$env_file" -f "$baseline_compose")
cleanup() {
  local status=$?
  if (( status != 0 )); then
    "${compose[@]}" logs --tail 120 app db || true
    docker logs examforge-upgrade-proxy || true
    docker logs examforge-upgrade-rollback || true
  fi
  docker rm -f examforge-upgrade-proxy examforge-upgrade-rollback >/dev/null 2>&1 || true
  "${compose[@]}" down --volumes --remove-orphans || true
  git worktree remove "$baseline" 2>/dev/null || true
}
trap cleanup EXIT

umask 077
{
  printf 'EXAMFORGE_DB_PASSWORD=%s\n' "$(openssl rand -hex 24)"
  printf 'EXAMFORGE_HMAC_KEY=%s\n' "$(openssl rand -hex 32)"
  printf 'EXAMFORGE_ALLOWED_HOSTS=localhost;127.0.0.1\n'
} > "$env_file"

wait_http() {
  curl --insecure --fail --silent --show-error --max-time 3 \
    --retry 30 --retry-delay 2 --retry-all-errors --output /dev/null "$1"
}

sql() {
  local database=$1
  shift
  "${compose[@]}" exec -T db psql -XqAt -v ON_ERROR_STOP=1 -U examforge -d "$database" "$@"
}

echo '::group::Build and seed the previous release'
git worktree add --detach "$baseline" "$1"
"${previous[@]}" build app
"${previous[@]}" up -d --wait db
"${previous[@]}" run --rm --no-deps -T app --migrate
teacher_password="Aa1!$(openssl rand -hex 12)"
echo "::add-mask::$teacher_password"
"${previous[@]}" run --rm --no-deps -T app --seed-admin admin1 "$teacher_password" 'Upgrade Teacher'
"${previous[@]}" run --rm --no-deps -T app --seed-demo
sql examforge < deploy/docker/test-upgrade-fixtures.sql
"${previous[@]}" up -d --no-build app
previous_image=$(docker inspect --format '{{.Image}}' "$("${previous[@]}" ps -q app)")
wait_http http://127.0.0.1:8080/account/login
"${previous[@]}" exec -T app sh -c 'mkdir -p /data/storage && printf "Exam Forge upgrade fixture\n" > /data/storage/upgrade-fixture.txt'
legacy_account=$(sql examforge -c 'SELECT "Id" FROM student_accounts')
legacy_invitation=$(sql examforge -c 'SELECT "Id" FROM invitations')
previous_migrations=$(sql examforge -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1')
[[ -n "$legacy_account" && -n "$legacy_invitation" && -n "$previous_migrations" ]]
echo '::endgroup::'

echo '::group::Upgrade the running stack'
bash deploy/docker/deploy.sh
backup=$(find deploy/docker/backups -mindepth 1 -maxdepth 1 -type d -print -quit)
[[ -n "$backup" && -s "$backup/database.dump" && -s "$backup/storage.tar.gz" ]]
[[ $(sql examforge -c "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" IN ('20261123183610_StudentSessionVersion', '20261124183522_InvitationGroupSnapshot')") == 2 ]]
[[ $(sql examforge -c "SELECT \"Id\" FROM student_accounts WHERE \"SessionVersion\" = '00000000-0000-0000-0000-000000000000'") == "$legacy_account" ]]
[[ $(sql examforge -c 'SELECT "Id" FROM invitations WHERE "GroupNameSnapshot" IS NULL') == "$legacy_invitation" ]]
echo '::endgroup::'

echo '::group::HTTPS browser checks'
docker run -d --name examforge-upgrade-proxy --network host caddy:2-alpine \
  caddy reverse-proxy --from https://localhost:5443 --to http://127.0.0.1:8080 --internal-certs
wait_http https://localhost:5443/health/ready
"${compose[@]}" run --rm --no-deps -T -e ASPNETCORE_ENVIRONMENT=Development app --seed-e2e /data/storage/upgrade-seed.json
mkdir -p e2e/.state
"${compose[@]}" exec -T app cat /data/storage/upgrade-seed.json > e2e/.state/seed.json
(cd e2e && E2E_BASE_URL=https://localhost:5443 E2E_SKIP_SEED=1 E2E_IGNORE_HTTPS_ERRORS=1 npm test)
echo '::endgroup::'

echo '::group::Restore the backup and start the previous image'
"${compose[@]}" exec -T db createdb -U examforge examforge_rollback
"${compose[@]}" exec -T db pg_restore --exit-on-error -U examforge -d examforge_rollback < "$backup/database.dump"
[[ $(sql examforge_rollback -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1') == "$previous_migrations" ]]
[[ $(sql examforge_rollback -c 'SELECT "Id" FROM student_accounts') == "$legacy_account" ]]
[[ $(sql examforge_rollback -c 'SELECT "Id" FROM invitations') == "$legacy_invitation" ]]
mkdir "$scratch/storage"
tar -xzf "$backup/storage.tar.gz" -C "$scratch/storage"
printf 'Exam Forge upgrade fixture\n' | cmp - "$scratch/storage/upgrade-fixture.txt"

"${compose[@]}" stop app
docker run -d --name examforge-upgrade-rollback --network examforge \
  -p 127.0.0.1:8080:8080 \
  --mount "type=bind,source=$scratch/storage,target=/data/storage" \
  --mount type=volume,source=examforge_examforge-dataprotection,target=/root/.aspnet/DataProtection-Keys \
  -e ASPNETCORE_ENVIRONMENT=Production -e ExamForge__HttpsRedirection=false \
  -e ExamForge__Cookies__SecurePolicy=SameAsRequest \
  -e "ConnectionStrings__Default=Host=db;Database=examforge_rollback;Username=examforge;Password=$(sed -n 's/^EXAMFORGE_DB_PASSWORD=//p' "$env_file")" \
  -e "ExamForge__InvitationHmacKey=$(sed -n 's/^EXAMFORGE_HMAC_KEY=//p' "$env_file")" \
  "$previous_image"
wait_http http://127.0.0.1:8080/account/login
(
  cd e2e
  UPGRADE_TEACHER_PASSWORD="$teacher_password" node -e '
    const { writeFileSync } = require("node:fs");
    writeFileSync(".state/seed.json", JSON.stringify({
      teacherLogin: "admin1",
      teacherPassword: process.env.UPGRADE_TEACHER_PASSWORD,
    }));'
  E2E_BASE_URL=http://127.0.0.1:8080 E2E_SKIP_SEED=1 npm test -- teacher-login.spec.ts
)
echo '::endgroup::'
