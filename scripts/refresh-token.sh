#!/usr/bin/env bash
# Demonstrate the refresh grant end to end, from the command line.
#
#   ./refresh-token.sh [user]
set -euo pipefail
cd "$(dirname "$0")" && source ./_common.sh
require_keycloak

USER="${1:-alice}"

echo "1. Log in to get an initial access + refresh token pair"
FIRST=$(curl -fsS -X POST "$TOKEN_ENDPOINT" \
  -d grant_type=password -d client_id=playground-cli \
  -d "username=$USER" -d password=password -d scope="openid profile email")

read -r A1 R1 E1 RE1 <<<"$(echo "$FIRST" | python3 -c '
import sys,json; d=json.load(sys.stdin)
print(d["access_token"], d["refresh_token"], d["expires_in"], d["refresh_expires_in"])')"

echo "   access token  expires in ${E1}s"
echo "   refresh token expires in ${RE1}s   <- note it lives much longer"
echo

# Pause so the new token's `iat` differs visibly from the old one.
echo "   (waiting 2s so the timestamps differ)"
sleep 2
echo
echo "2. Exchange the refresh token for a NEW access token"
echo "   POST $TOKEN_ENDPOINT"
echo "        grant_type=refresh_token  (no password involved)"
SECOND=$(curl -fsS -X POST "$TOKEN_ENDPOINT" \
  -d grant_type=refresh_token -d client_id=playground-cli \
  -d "refresh_token=$R1")

read -r A2 R2 <<<"$(echo "$SECOND" | python3 -c '
import sys,json; d=json.load(sys.stdin)
print(d["access_token"], d["refresh_token"])')"
echo

echo "3. Compare"
python3 - "$A1" "$A2" "$R1" "$R2" <<'PY'
import sys, json, base64
a1, a2, r1, r2 = sys.argv[1:5]
def pl(t):
    p = t.split(".")[1].replace("-","+").replace("_","/")
    return json.loads(base64.b64decode(p + "="*(-len(p)%4)))
p1, p2 = pl(a1), pl(a2)
print(f"   old access token  iat={p1['iat']}  exp={p1['exp']}  jti={p1['jti'][:8]}...")
print(f"   new access token  iat={p2['iat']}  exp={p2['exp']}  jti={p2['jti'][:8]}...")
moved = p2["iat"] - p1["iat"]
print(f"   -> a different token: {a1 != a2}; issued {moved}s later, so it now")
print(f"      expires {moved}s later too. The clock was pushed forward.")
print()
print(f"   refresh token changed: {r1 != r2}")
print("   Keycloak always issues a new refresh token. Whether the OLD one still")
print("   works depends on the realm's 'Revoke Refresh Token' setting (off here).")
PY
echo

echo "4. Is the old refresh token still usable?"
if curl -fsS -o /dev/null -X POST "$TOKEN_ENDPOINT" \
     -d grant_type=refresh_token -d client_id=playground-cli -d "refresh_token=$R1" 2>/dev/null; then
  echo "   YES - 'Revoke Refresh Token' is OFF in this realm."
  echo "   Turn it on (Realm settings -> Tokens) and re-run: this becomes an error,"
  echo "   because Keycloak reads reuse as a sign the token was stolen."
else
  echo "   NO - rotation is enforced. The old token is burned."
fi
