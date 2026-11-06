#!/usr/bin/env bash
# Creates (or updates) the two K8s Secrets the Bitnami subcharts read, from Vault
# (AD-52 rule 2: Vault is the source; these Secrets are never committed).
#
#   contacts-postgresql-auth  postgres-password  <- secret/contacts-pg-admin  postgres-password
#                             password           <- secret/contacts           Database__Password
#     (contactsapi's password; the same one Database__ConnectionString carries. Bitnami
#     uses both only to initialise an empty data directory, e.g. after a restore)
#   contacts-redis-auth       redis-password     <- secret/contacts           Redis__Password
#
# Needs: vault (logged in, read on both paths), kubectl (create/patch Secrets in the
# namespace). No value is printed or passed on a command line: each one goes through
# a file in a private temporary directory that is removed on exit.
#
# Usage: helm/scripts/secrets-from-vault.sh [namespace]   (default: contacts)
set -euo pipefail

NS="${1:-contacts}"
PG_SECRET="${PG_SECRET:-contacts-postgresql-auth}"
REDIS_SECRET="${REDIS_SECRET:-contacts-redis-auth}"

umask 077
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

field() { # <kv path> <field> <out file>
  vault kv get -field="$2" "$1" > "$3"
  [ -s "$3" ] || { echo "empty or missing: $1 $2" >&2; exit 1; }
}

field secret/contacts-pg-admin postgres-password "$TMP/postgres-password"
field secret/contacts Database__Password "$TMP/password"
field secret/contacts Redis__Password "$TMP/redis-password"

# StackExchange.Redis parses the backend's connection string, and the Vault Agent
# writes it into a shell file: refuse characters that would break either
if LC_ALL=C grep -q '[,="$`\\[:space:]]' "$TMP/redis-password"; then
  echo "Redis__Password contains , = \" \$ \\ \` or whitespace; regenerate it as hex" >&2
  exit 1
fi

kubectl -n "$NS" create secret generic "$PG_SECRET" \
  --from-file=postgres-password="$TMP/postgres-password" \
  --from-file=password="$TMP/password" \
  --dry-run=client -o yaml | kubectl apply -f -
kubectl -n "$NS" create secret generic "$REDIS_SECRET" \
  --from-file=redis-password="$TMP/redis-password" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "Secrets $PG_SECRET and $REDIS_SECRET applied in namespace $NS (values not shown)"
