"""
Walk the Authorization Code + PKCE flow step by step, printing what happens.

This is what your browser does when you log in at http://localhost:5002.
Doing it in a script makes the sequence visible without reading devtools.

Standard library only - no pip install needed.
"""

import http.cookiejar
import urllib.request
import urllib.parse
import urllib.error
import re
import sys

WEB = "http://localhost:5002"
KC = "http://localhost:8080"

jar = http.cookiejar.CookieJar()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Stop at every 302 so we can look at it, like a very slow browser."""

    def redirect_request(self, *a, **k):
        return None


opener = urllib.request.build_opener(
    urllib.request.HTTPCookieProcessor(jar), NoRedirect
)


def fetch(url, data=None):
    req = urllib.request.Request(url, data=data)
    try:
        r = opener.open(req)
        out = r.status, r.headers, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        out = e.code, e.headers, e.read().decode("utf-8", "replace")
    # Keycloak marks its session cookies Secure. Real browsers DO send those
    # over http://localhost (localhost counts as a trusted origin), but urllib
    # refuses to, so we clear the flag to behave like a browser.
    for c in jar:
        c.secure = False
    return out


def banner(n, title):
    print(f"\n{'─' * 72}\nSTEP {n}: {title}\n{'─' * 72}")


# ---------------------------------------------------------------------------
banner(1, "Request a protected page with no session")

status, headers, _ = fetch(f"{WEB}/profile")
print(f"GET {WEB}/profile  ->  HTTP {status}")
if status != 302:
    sys.exit(f"expected a 302 challenge, got {status}. Is the WebApp running?")
authorize_url = headers["Location"]
print("The app has no cookie for us, so it 'challenges' the request:")
print("a redirect to Keycloak, plus two temporary cookies it will need later.")
print("  cookies set:", [c.name.split(".")[2] for c in jar if c.name.startswith(".AspNetCore")])

# ---------------------------------------------------------------------------
banner(2, "The authorization request (front channel)")

parsed = urllib.parse.urlparse(authorize_url)
print(f"{parsed.scheme}://{parsed.netloc}{parsed.path}")
for k, v in urllib.parse.parse_qsl(parsed.query):
    note = {
        "response_type": "<- 'code', not 'token': this IS the Authorization Code flow",
        "code_challenge": "<- PKCE: SHA256 of a secret we kept to ourselves",
        "code_challenge_method": "<- S256. 'plain' would defend nothing.",
        "state": "<- CSRF guard for the round trip",
        "nonce": "<- replay guard, must come back inside the id_token",
        "redirect_uri": "<- exact-matched by Keycloak",
    }.get(k, "")
    print(f"  {k:22} = {v[:58]}{'...' if len(v) > 58 else ''}  {note}")

status, headers, body = fetch(authorize_url)
print(f"\n-> HTTP {status}: Keycloak serves ITS OWN login page.")
print("   The password is typed on Keycloak's origin. Our app never sees it.")

# ---------------------------------------------------------------------------
banner(3, "Authenticate")

m = re.search(r'id="kc-form-login".*?action="([^"]+)"', body, re.S)
if not m:
    sys.exit("could not find the login form")
action = m.group(1).replace("&amp;", "&")

status, headers, body = fetch(
    action,
    urllib.parse.urlencode(
        {"username": "alice", "password": "password", "credentialId": ""}
    ).encode(),
)
print("POST username=alice&password=password  ->  HTTP", status)

# ---------------------------------------------------------------------------
banner(4, "The authorization code comes back")

fields = dict(re.findall(r'name="(\w+)"\s+value="([^"]*)"', body, re.I))
if "code" not in fields:
    sys.exit(f"no code returned. Response began:\n{body[:600]}")

print("Because response_mode=form_post, Keycloak returns a self-submitting")
print("HTML form instead of a redirect - so the code stays out of the URL,")
print("out of browser history, and out of server access logs.")
print()
for k, v in fields.items():
    print(f"  {k:16} = {v[:56]}{'...' if len(v) > 56 else ''}")
print()
print("This code is single-use, short-lived, and bound to our client_id,")
print("redirect_uri and PKCE challenge. On its own it is worthless.")

# ---------------------------------------------------------------------------
banner(5, "The app exchanges the code for tokens (BACK CHANNEL)")

print("The browser plays no part in this. Server to server:")
print("  POST /protocol/openid-connect/token")
print("       grant_type=authorization_code")
print("       code=<the code above>")
print("       client_secret=<our secret>")
print("       code_verifier=<the PKCE secret we never sent through the browser>")

status, headers, body = fetch(
    f"{WEB}/signin-oidc", urllib.parse.urlencode(fields).encode()
)
print(f"\nPOST {WEB}/signin-oidc  ->  HTTP {status}")

location = headers.get("Location", "")
if "/error" in location:
    msg = urllib.parse.unquote(location.split("message=", 1)[-1])
    sys.exit(f"\nFLOW FAILED: {msg}\n\nSee docs/09-troubleshooting.md")

session_cookies = sorted(c.name for c in jar if c.name.startswith("WebApp"))
print("\nKeycloak validated the code, the secret and the PKCE verifier, and")
print("returned access + id + refresh tokens. The app validated the id_token")
print("and wrote everything into an encrypted cookie:")
for c in session_cookies:
    print(f"  Set-Cookie: {c}")
if len(session_cookies) > 1:
    print("\n  (chunked: three JWTs exceed the browser's ~4KB cookie limit,")
    print("   so ASP.NET Core splits the payload across C1, C2, ...)")

# ---------------------------------------------------------------------------
banner(6, "We are logged in - the cookie carries every request now")

status, headers, body = fetch(f"{WEB}/profile")
print(f"GET {WEB}/profile  ->  HTTP {status}")
user = re.search(r"Signed in as <strong>([^<]+)</strong>", body)
if not user:
    user = re.search(r"preferred_username</code></td><td>([^<]+)<", body)
print("Signed in as:", user.group(1) if user else "(not found)")
print("\nNote what the browser holds: a cookie. Not a token.")
print("That is the BFF pattern - tokens stay server-side, out of reach of XSS.")

# ---------------------------------------------------------------------------
banner(7, "Use the access token against the API")

for path in ("/api/public", "/api/whoami", "/api/admin", "/api/reports"):
    _, _, body = fetch(f"{WEB}/call-api?path={urllib.parse.quote(path)}")
    m = re.search(r"<strong>(\d{3}) ([^<]*)</strong>", body)
    code = m.group(1) if m else "???"
    print(f"  {path:16} -> {code}")
print("\nAll 200 for alice. Re-run as bob and /api/admin becomes 403:")
print("  authenticated, but not permitted.")

# ---------------------------------------------------------------------------
banner(8, "Refresh the access token")

_, _, body = fetch(f"{WEB}/refresh", b"")
ok = "<strong>Success</strong>" in body
print("POST /refresh  ->", "success" if ok else "FAILED")
m = re.search(r"expires_in(?:&quot;|\")?:\s*(\d+)", body)
if m:
    print(f"A new access token was issued, valid for {m.group(1)}s.")
print("No password was involved. That is the whole point of a refresh token.")

print(f"\n{'─' * 72}")
print("DONE. Now do it in a browser at http://localhost:5002 with devtools")
print("open (Network tab, 'Preserve log' checked) and watch the same sequence.")
print(f"{'─' * 72}")
