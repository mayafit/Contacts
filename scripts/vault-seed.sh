#!/bin/sh
# ===================================
# Vault Auto-Seed Script
# ===================================
# Runs as an init container to seed Vault with secrets from environment variables.
# Vault dev mode stores data in-memory, so this runs on every docker compose up.

set -e

VAULT_ADDR="${VAULT_ADDR:-http://vault:8200}"
VAULT_TOKEN="${VAULT_TOKEN:-dev-root-token}"
SECRET_PATH="secret/data/contacts"
MAX_RETRIES=30
RETRY_INTERVAL=2

echo "=== Vault Auto-Seed ==="

# Wait for Vault to be ready
echo "Waiting for Vault at ${VAULT_ADDR}..."
RETRIES=0
until wget -q --spider "${VAULT_ADDR}/v1/sys/health" 2>/dev/null; do
  RETRIES=$((RETRIES + 1))
  if [ "$RETRIES" -ge "$MAX_RETRIES" ]; then
    echo "ERROR: Vault not ready after ${MAX_RETRIES} retries"
    exit 1
  fi
  sleep "$RETRY_INTERVAL"
done
echo "Vault is ready."

# Validate required secrets
if [ -z "$GOOGLE_CLIENT_SECRET" ]; then
  echo "ERROR: GOOGLE_CLIENT_SECRET is not set in .env"
  exit 1
fi

# Seed secrets into Vault KV v2
echo "Seeding secrets into Vault at ${SECRET_PATH}..."
wget -q -O /dev/null \
  --header="X-Vault-Token: ${VAULT_TOKEN}" \
  --header="Content-Type: application/json" \
  --post-data="{\"data\":{\"google_client_secret\":\"${GOOGLE_CLIENT_SECRET}\"}}" \
  "${VAULT_ADDR}/v1/${SECRET_PATH}"

echo "Secrets seeded successfully."

# Verify
RESPONSE=$(wget -q -O - \
  --header="X-Vault-Token: ${VAULT_TOKEN}" \
  "${VAULT_ADDR}/v1/${SECRET_PATH}" 2>/dev/null || true)

if echo "$RESPONSE" | grep -q "google_client_secret"; then
  echo "Verified: google_client_secret is in Vault."
else
  echo "WARNING: Could not verify secret in Vault."
  exit 1
fi

echo "=== Vault seed complete ==="
