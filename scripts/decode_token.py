"""
Decode a JWT and explain the claims that decide whether it is accepted.

DECODING IS NOT VERIFYING. This reads the token; it does not check the
signature. A JWT is signed, not encrypted - anyone can read any JWT. Only a
signature check proves Keycloak minted it and nobody altered it.
"""

import sys
import json
import base64
import datetime


def decode_part(part: str) -> dict:
    """A JWT part is base64url with the '=' padding stripped off."""
    p = part.replace("-", "+").replace("_", "/")
    return json.loads(base64.b64decode(p + "=" * (-len(p) % 4)))


token = (sys.argv[1] if len(sys.argv) > 1 else sys.stdin.read()).strip()
parts = token.split(".")
if len(parts) < 2:
    sys.exit("not a JWT (expected header.payload.signature)")

header = decode_part(parts[0])
payload = decode_part(parts[1])

print("--- header (how it was signed) ---")
print(json.dumps(header, indent=2))
print()
print("--- payload (the claims) ---")
print(json.dumps(payload, indent=2))
print()

print("--- the claims that decide acceptance ---")
for key, label in [
    ("iss", "iss (issuer)"),
    ("aud", "aud (audience)"),
    ("sub", "sub (user id)"),
    ("azp", "azp (which app asked)"),
    ("typ", "typ"),
    ("scope", "scope"),
]:
    print(f"  {label:24} {payload.get(key)}")

now = datetime.datetime.now(datetime.timezone.utc)
if "iat" in payload and "exp" in payload:
    iat = datetime.datetime.fromtimestamp(payload["iat"], datetime.timezone.utc)
    exp = datetime.datetime.fromtimestamp(payload["exp"], datetime.timezone.utc)
    remaining = (exp - now).total_seconds()
    lifetime = int((exp - iat).total_seconds())
    state = (
        f"VALID, {int(remaining)}s remaining"
        if remaining > 0
        else f"EXPIRED {int(-remaining)}s ago"
    )
    print(f"  {'iat (issued at)':24} {iat:%Y-%m-%d %H:%M:%S} UTC")
    print(f"  {'exp (expires)':24} {exp:%Y-%m-%d %H:%M:%S} UTC  (lifetime {lifetime}s)")
    print(f"  {'status':24} {state}")
print()

print("--- roles ---")
realm_roles = payload.get("realm_access", {}).get("roles", [])
print(f"  realm_access.roles       {realm_roles}")
for client, value in (payload.get("resource_access") or {}).items():
    print(f"  resource_access.{client:9} {value.get('roles', [])}")
print()
print("  Note they are NESTED objects, not flat claims. ASP.NET Core's")
print("  [Authorize(Roles = ...)] finds nothing until you flatten them;")
print("  that is what KeycloakRoles.cs is for.")
print()

alg = str(header.get("alg", "?"))
if alg.startswith("RS") or alg.startswith("ES"):
    meaning = (
        "asymmetric - Keycloak signs with a private key and ANYONE can verify\n"
        "       with the public key from jwks_uri. This is exactly why your API\n"
        "       validates tokens offline, with no call to Keycloak per request."
    )
else:
    meaning = (
        "symmetric - signed with a secret only Keycloak holds, so only Keycloak\n"
        "       can verify it. Refresh tokens use this: nobody else should ever\n"
        "       be validating one."
    )
print(f"--- alg = {alg} ---")
print(f"  {meaning}")
