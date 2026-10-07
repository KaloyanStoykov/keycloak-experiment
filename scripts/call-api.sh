#!/usr/bin/env bash
# Call the API as a given user and explain the status code.
#
#   ./call-api.sh                      -> alice, /api/whoami
#   ./call-api.sh bob /api/admin       -> expect 403
#   ./call-api.sh --none /api/whoami   -> no token at all, expect 401
#   ./call-api.sh --bad  /api/whoami   -> garbage token, expect 401
set -euo pipefail
cd "$(dirname "$0")" && source ./_common.sh

USER="${1:-alice}"
PATH_="${2:-/api/whoami}"

case "$USER" in
  --none) TOKEN=""; LABEL="no token" ;;
  --bad)  TOKEN="not.a.real.token"; LABEL="malformed token" ;;
  *)      require_keycloak; TOKEN=$(./get-token.sh "$USER" raw); LABEL="$USER" ;;
esac

echo "GET $API_URL$PATH_   (as: $LABEL)"
echo

if [ -n "$TOKEN" ]; then
  RESP=$(curl -s -D /tmp/.kc-headers -H "Authorization: Bearer $TOKEN" "$API_URL$PATH_")
else
  RESP=$(curl -s -D /tmp/.kc-headers "$API_URL$PATH_")
fi

STATUS=$(head -1 /tmp/.kc-headers | awk '{print $2}')
echo "HTTP $STATUS"

case "$STATUS" in
  200) echo "  -> token accepted AND the policy allowed it." ;;
  401) echo "  -> UNAUTHORIZED: the token itself was not accepted."
       echo "     (missing, malformed, expired, wrong issuer, or wrong audience)"
       echo "     This is a statement about the TOKEN." ;;
  403) echo "  -> FORBIDDEN: the token is valid and the API knows who you are."
       echo "     You just lack the required role."
       echo "     This is a statement about PERMISSIONS." ;;
  *)   echo "  -> unexpected status" ;;
esac

grep -i '^x-auth-failure-reason' /tmp/.kc-headers | sed 's/^/     /' || true
echo
echo "--- body ---"
echo "$RESP" | pp 2>/dev/null || echo "$RESP"
rm -f /tmp/.kc-headers
