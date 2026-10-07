#!/usr/bin/env bash
# Fetch real tokens for a user and show them decoded.
#
#   ./get-token.sh            -> alice
#   ./get-token.sh bob
#   ./get-token.sh alice raw  -> print just the access token (for piping)
#
# Uses the Direct Access Grant (password grant) on the `playground-cli` client.
# That grant is DEPRECATED for real apps - it exists here only because it is the
# shortest path to a token on the command line. See docs/01-oauth2-fundamentals.md
set -euo pipefail
cd "$(dirname "$0")" && source ./_common.sh
require_keycloak

USER="${1:-alice}"
MODE="${2:-pretty}"

RESPONSE=$(curl -fsS -X POST "$TOKEN_ENDPOINT" \
  -d grant_type=password \
  -d client_id=playground-cli \
  -d "username=$USER" \
  -d password=password \
  -d scope="openid profile email") || die "token request failed for '$USER'"

ACCESS=$(echo "$RESPONSE" | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')

if [ "$MODE" = "raw" ]; then echo "$ACCESS"; exit 0; fi

echo "=== token endpoint response ==================================="
echo "$RESPONSE" | python3 -c '
import sys, json
d = json.load(sys.stdin)
for k, v in d.items():
    print(f"  {k:20} = {(v[:40] + chr(8230)) if isinstance(v, str) and len(v) > 40 else v}")
'
echo
echo "=== access_token, decoded ====================================="
./decode-token.sh "$ACCESS"
echo
echo "Tip: export it for the other scripts:"
echo "  export TOKEN=\$(./scripts/get-token.sh $USER raw)"
