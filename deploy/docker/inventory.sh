#!/usr/bin/env bash
# Read-only инвентаризация VDS перед установкой Docker (ТЗ §21.1–21.2).
# Ничего не меняет: только чтение. Запуск: bash inventory.sh [sudo]
set -uo pipefail

SUDO="${1:-}"
run() { echo; echo "=== $1 ==="; shift; "$@" 2>&1 | head -60; }

run "ОС и ядро" $SUDO uname -a
run "Релиз" $SUDO sh -c 'cat /etc/os-release | head -4'
run "CPU/RAM" $SUDO sh -c 'nproc; free -h'
run "Диск" $SUDO df -h / /var
run "Слушающие порты" $SUDO ss -tulpn
run "Запущенные сервисы" $Sudo sh -c 'systemctl list-units --type=service --state=running --no-pager' 2>/dev/null || systemctl list-units --type=service --state=running --no-pager
run "Топ по памяти" $SUDO sh -c 'ps aux --sort=-%mem | head -15'
run "Docker установлен?" $SUDO sh -c 'command -v docker && docker --version || echo "docker: НЕ установлен"'
run "Контейнеры/тома существующие" $SUDO sh -c 'docker ps -a 2>/dev/null; docker volume ls 2>/dev/null' || true
run "Nginx" $SUDO sh -c 'command -v nginx && nginx -v 2>&1 || echo "nginx: не найден"'
run "Nginx конфиг (server_name/listen)" $SUDO sh -c 'grep -RE "^\s*(server_name|listen)" /etc/nginx/ 2>/dev/null | head -30' || true
run "Firewall ufw" $SUDO ufw status || echo "ufw: неактивен/не установлен"
run "iptables (кратко)" $SUDO sh -c 'iptables -L -n | head -20'
run "Crontab root (backups?)" $SUDO sh -c 'crontab -l 2>/dev/null | head -20'
run "SugarGuard процессы" $SUDO sh -c 'ps aux | grep -iE "sugar|guard" | grep -v grep | head -10'

echo
echo "=== Инвентаризация завершена (только чтение, ничего не изменено) ==="
