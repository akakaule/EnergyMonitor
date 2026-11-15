#!/bin/sh
# Writes the Eloverblik and Telegram tokens from Key Vault into deploy/local/.env (mode 600).
#
# For the Linux machine. Requires `az login` (`az login --use-device-code` without a browser) with read
# access to the vault's secrets. An existing .env keeps its other settings; otherwise it is created from
# .env.example. The tokens are passed through the environment, never on a command line.
#
#   scripts/pull-secrets.sh [path/to/.env]      # KEY_VAULT=<name> overrides the vault
#
# Restart afterwards: cd deploy/local && docker compose up -d
set -eu

vault=${KEY_VAULT:-kv-em-dev-skljlmd6ey75m}
local_dir=$(cd "$(dirname "$0")/../deploy/local" && pwd)
target=${1:-$local_dir/.env}

get() {
  value=$(az keyvault secret show --vault-name "$vault" --name "$1" --query value -o tsv) || value=
  value=$(printf '%s' "$value" | tr -d '\r\n')
  if [ -z "$value" ]; then
    echo "Could not read '$1' from Key Vault '$vault'. Run 'az login' with an account that can read its secrets." >&2
    exit 1
  fi
  printf '%s' "$value"
}

ELOVERBLIK_TOKEN=$(get Eloverblik--RefreshToken)
TELEGRAM_TOKEN=$(get Telegram--BotToken)
export ELOVERBLIK_TOKEN TELEGRAM_TOKEN

if [ -f "$target" ]; then source=$target; else source=$local_dir/.env.example; fi

umask 077
tmp=$(mktemp "$(dirname "$target")/.env.XXXXXX")
trap 'rm -f "$tmp"' EXIT

awk '
  BEGIN { e = 0; t = 0 }
  /^Eloverblik__RefreshToken=/ { print "Eloverblik__RefreshToken=" ENVIRON["ELOVERBLIK_TOKEN"]; e = 1; next }
  /^Telegram__BotToken=/       { print "Telegram__BotToken=" ENVIRON["TELEGRAM_TOKEN"]; t = 1; next }
  { print }
  END {
    if (!e) print "Eloverblik__RefreshToken=" ENVIRON["ELOVERBLIK_TOKEN"]
    if (!t) print "Telegram__BotToken=" ENVIRON["TELEGRAM_TOKEN"]
  }
' "$source" > "$tmp"

chmod 600 "$tmp"
mv "$tmp" "$target"
trap - EXIT
echo "Wrote Eloverblik__RefreshToken and Telegram__BotToken from '$vault' to $target."
