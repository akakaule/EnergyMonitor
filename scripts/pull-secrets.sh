#!/bin/sh
# Writes the Eloverblik and Telegram tokens and the Sensibo API key from Key Vault into deploy/local/.env (mode 600).
# The Sensibo key is optional: without the secret, .env keeps whatever Sensibo__ApiKey it has.
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
SENSIBO_KEY=$(az keyvault secret show --vault-name "$vault" --name Sensibo--ApiKey --query value -o tsv 2>/dev/null | tr -d '\r\n') || SENSIBO_KEY=
export ELOVERBLIK_TOKEN TELEGRAM_TOKEN SENSIBO_KEY

if [ -f "$target" ]; then source=$target; else source=$local_dir/.env.example; fi

umask 077
tmp=$(mktemp "$(dirname "$target")/.env.XXXXXX")
trap 'rm -f "$tmp"' EXIT

awk '
  BEGIN { e = 0; t = 0; s = ENVIRON["SENSIBO_KEY"] == "" }
  /^Eloverblik__RefreshToken=/ { print "Eloverblik__RefreshToken=" ENVIRON["ELOVERBLIK_TOKEN"]; e = 1; next }
  /^Telegram__BotToken=/       { print "Telegram__BotToken=" ENVIRON["TELEGRAM_TOKEN"]; t = 1; next }
  /^Sensibo__ApiKey=/ && ENVIRON["SENSIBO_KEY"] != "" { print "Sensibo__ApiKey=" ENVIRON["SENSIBO_KEY"]; s = 1; next }
  { print }
  END {
    if (!e) print "Eloverblik__RefreshToken=" ENVIRON["ELOVERBLIK_TOKEN"]
    if (!t) print "Telegram__BotToken=" ENVIRON["TELEGRAM_TOKEN"]
    if (!s) print "Sensibo__ApiKey=" ENVIRON["SENSIBO_KEY"]
  }
' "$source" > "$tmp"

chmod 600 "$tmp"
mv "$tmp" "$target"
trap - EXIT
if [ -n "$SENSIBO_KEY" ]; then
  echo "Wrote Eloverblik__RefreshToken, Telegram__BotToken and Sensibo__ApiKey from '$vault' to $target."
else
  echo "Wrote Eloverblik__RefreshToken and Telegram__BotToken from '$vault' to $target (no Sensibo--ApiKey in the vault)."
fi
