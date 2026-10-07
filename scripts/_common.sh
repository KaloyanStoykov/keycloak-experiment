#!/usr/bin/env bash
# Shared settings for the helper scripts.
KC_URL="${KC_URL:-http://localhost:8080}"
REALM="${REALM:-dotnet-lab}"
API_URL="${API_URL:-http://localhost:5003}"
WEB_URL="${WEB_URL:-http://localhost:5002}"

ISSUER="$KC_URL/realms/$REALM"
TOKEN_ENDPOINT="$ISSUER/protocol/openid-connect/token"

# Pretty-print JSON with jq if present, otherwise fall back to python.
pp() {
  if command -v jq >/dev/null 2>&1; then jq .; else python3 -m json.tool; fi
}

# Decode a base64url segment (JWTs omit the padding).
b64url() {
  python3 -c "
import sys,base64
s=sys.stdin.read().strip().replace('-','+').replace('_','/')
sys.stdout.write(base64.b64decode(s+'='*(-len(s)%4)).decode('utf-8','replace'))
"
}

die() { echo "error: $*" >&2; exit 1; }

require_keycloak() {
  curl -fsS -o /dev/null "$ISSUER/.well-known/openid-configuration" \
    || die "Keycloak is not reachable at $ISSUER. Run: docker compose up -d"
}
