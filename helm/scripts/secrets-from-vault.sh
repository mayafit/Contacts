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
#
# First upgrade of a live release to chart 0.6.0, in this order:
#  1. Before anything else, save the `postgres` superuser password. Today it exists only
#     in the Bitnami-generated Secret `contacts-postgresql`, which the upgrade DELETES
#     (the chart stops rendering it once auth.existingSecret is set):
#       kubectl -n contacts get secret contacts-postgresql -o jsonpath='{.data.postgres-password}' \
#         | base64 -d > "$f" && vault kv put secret/contacts-pg-admin postgres-password=@"$f"
#     (with f a file in a private directory; `@file` keeps the value off the command line)
#  2. Add to secret/contacts (vault kv patch ... KEY=@file): Redis__Password (new, hex)
#     and Database__Password (the password inside Database__ConnectionString). Without
#     Redis__Password in Vault BEFORE the upgrade, the backend gets the password-less
#     connection string while Redis now requires one, and sessions fail.
#  3. Run this script.
#  4. In the release (raspberry_deployment apps/contacts/release.yaml), in one commit:
#     drop postgresql.auth.password, set postgresql.auth.existingSecret and
#     redis.auth.{enabled: true, existingSecret}. The chart refuses to render otherwise,
#     so a chart that reaches `main` first only leaves the HelmRelease failed, unchanged.
#  5. After the new pods are Ready, set `audience=vault` on the Vault roles `contacts`
#     and `contacts-routes` (story 21.21). Not before: a role with an audience rejects
#     the old pods' default tokens. Login works in between because a role without an
#     audience has Vault ask the API server for the token's own audience.
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
