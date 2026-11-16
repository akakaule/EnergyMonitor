#!/bin/sh
# Online backup of deploy/local/data/energymonitor.db, safe while the app is running.
#
# Opens the database read-only (the app owns the files), copies it with SQLite's backup API, checks the
# copy's integrity and keeps one file per day. Backups older than BACKUP_KEEP_DAYS (default 30) are removed.
# If the backup fails, a Telegram message is sent with the bot token and the Telegram__ChatIds__n
# recipients from deploy/local/.env (or, without those, the recipients set in the running app).
#
#   scripts/backup-db.sh [backup-dir]      # default $BACKUP_DIR or ~/backups/energymonitor
#
# Restore: docker compose down, copy a backup over data/energymonitor.db (delete the -wal and -shm files),
# chown it to 1654, docker compose up -d.
set -eu

local_dir=$(cd "$(dirname "$0")/../deploy/local" && pwd)
db=$local_dir/data/energymonitor.db
env_file=$local_dir/.env
dir=${1:-${BACKUP_DIR:-$HOME/backups/energymonitor}}
keep=${BACKUP_KEEP_DAYS:-30}

errors=$(mktemp)
tmp=
# The real stderr, for the exit handler: it also runs while stderr points at $errors.
exec 3>&2

# Best effort: a failing notification must not hide the backup's own error.
notify() {
  [ -r "$env_file" ] || return 0
  token=$(sed -n 's/^Telegram__BotToken=//p' "$env_file" | tail -n 1)
  [ -n "$token" ] || return 0
  chats=$(sed -n 's/^Telegram__ChatIds__[0-9]*=//p' "$env_file")
  if [ -z "$chats" ]; then
    chats=$(curl -fsS -m 10 http://127.0.0.1:8080/api/admin/settings/notifications 2>/dev/null \
      | sed -n 's/.*"recipients":\[\([^]]*\)\].*/\1/p' | tr -d '"' | tr ',' '\n') || chats=
  fi
  for chat in $chats; do
    # The token is passed to curl on stdin, never on its command line.
    printf 'url = "https://api.telegram.org/bot%s/sendMessage"\n' "$token" \
      | curl -fsS -m 20 -K - -o /dev/null -d "chat_id=$chat" --data-urlencode "text=$1" \
      && echo "Sent a Telegram notification to $chat." >&3 \
      || echo "Could not send the Telegram notification to $chat." >&3
  done
}

on_exit() {
  status=$?
  [ -n "$tmp" ] && rm -f "$tmp"
  if [ "$status" -ne 0 ]; then
    cat "$errors" >&3
    detail=$(tail -n 5 "$errors")
    notify "⚠️ Backup af Energy Monitor-databasen fejlede på $(hostname) ($(date '+%F %H:%M')).
${detail:-Ukendt fejl (exit $status).}
Se: journalctl --user -u energymonitor-backup" 2>&3
  fi
  rm -f "$errors"
  exit "$status"
}
trap on_exit EXIT

{
  [ -f "$db" ] || { echo "No database at $db." >&2; exit 1; }
  mkdir -p "$dir"

  target="$dir/energymonitor-$(date +%F).db"
  tmp="$target.tmp"
  rm -f "$tmp"

  sqlite3 -readonly "$db" ".backup '$tmp'"
  result=$(sqlite3 "$tmp" 'pragma integrity_check;')
  if [ "$result" != ok ]; then
    echo "Integrity check of the backup failed: $result" >&2
    exit 1
  fi

  mv "$tmp" "$target"
  tmp=
  find "$dir" -maxdepth 1 -type f -name 'energymonitor-*.db' -mtime +"$keep" -delete
} 2>"$errors"

echo "Backed up $db to $target ($(du -h "$target" | cut -f1))."
