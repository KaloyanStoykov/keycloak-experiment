#!/usr/bin/env bash
# Export the live realm from the running container into ./keycloak/exported/
#
# Use this after clicking around the admin console, to capture what you changed.
#
# NOTE: client secrets are NOT exported - they come out as "**********".
# Put real values back in from environment variables before importing.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="keycloak/exported"
mkdir -p "$OUT"

echo "Exporting realm 'dotnet-lab' from the running container..."

# kc.sh export boots a partial Keycloak, which tries to bind the management
# port (9000) that the live server already holds. Point it elsewhere so the
# export does not fail on "Address already in use".
docker compose exec -T keycloak /opt/keycloak/bin/kc.sh export \
  --dir /tmp/kc-export \
  --realm dotnet-lab \
  --users realm_file \
  --http-management-port 9001 2>&1 | grep -E "Export|export" || true

if ! docker compose exec -T keycloak test -f /tmp/kc-export/dotnet-lab-realm.json; then
  echo "error: export did not produce a file. Is the stack running? (docker compose ps)" >&2
  exit 1
fi

docker compose cp keycloak:/tmp/kc-export/dotnet-lab-realm.json "$OUT/" >/dev/null
echo "Written to $OUT/dotnet-lab-realm.json ($(wc -l < "$OUT/dotnet-lab-realm.json") lines)"
echo
echo "Note how verbose it is compared with the hand-written"
echo "keycloak/realm-dotnet-lab.json - exports include every default."
echo
echo "Suggested workflow:"
echo "  1. diff this against keycloak/realm-dotnet-lab.json"
echo "  2. port over ONLY what you meant to change"
echo "  3. prove it reproduces:  docker compose down -v && docker compose up -d"
echo "  4. commit"
