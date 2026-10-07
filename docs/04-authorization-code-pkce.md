# 4. The Authorization Code Flow (+ PKCE)

This is the flow you should use for anything with a human in front of it. This
document walks the exact request sequence the lab produces, parameter by
parameter.

## The whole flow at a glance

```
     Browser                  WebApp :5002              Keycloak :8080
        │                          │                           │
   1.   │─ GET /profile ──────────►│                           │
        │◄── 302 to Keycloak ──────│  (no cookie → challenge)  │
        │                          │                           │
   2.   │─ GET /auth?client_id=...&code_challenge=... ─────────►│
        │◄──────────────── login page ──────────────────────────│
   3.   │─ POST username+password ─────────────────────────────►│
        │                          │        (password only ever seen here)
        │◄── 200: self-submitting form carrying CODE ───────────│
   4.   │─ POST /signin-oidc (code, state) ──►│                 │
        │                          │                           │
   5.   │                          │─ POST /token ────────────►│  BACK CHANNEL
        │                          │   code + secret + verifier│  (browser blind)
        │                          │◄── access + id + refresh ─│
        │                          │                           │
   6.   │◄── 302 /profile + Set-Cookie: WebApp.Session ────────│
        │                          │                           │
   7.   │─ GET /profile (cookie) ─►│  200, "Hello alice"       │
```

Steps 1-4 and 6-7 are **front channel** — through the browser, visible, loggable.
Step 5 is **back channel** — server to server. The tokens only ever exist on the
back channel and inside the encrypted cookie. The browser never holds one.

## Step 1 — the challenge

You request a protected page with no cookie:

```csharp
app.MapGet("/profile", ...).RequireAuthorization();
```

ASP.NET Core "challenges" the request: HTTP 302 to Keycloak, plus two temporary
cookies (`.AspNetCore.Correlation.*` and `.AspNetCore.OpenIdConnect.Nonce.*`)
that it will need to verify the response in step 4.

## Step 2 — the authorization request

Here is the real redirect this lab produces:

```
http://localhost:8080/realms/dotnet-lab/protocol/openid-connect/auth
  ?client_id=dotnet-webapp
  &redirect_uri=http%3A%2F%2Flocalhost%3A5002%2Fsignin-oidc
  &response_type=code
  &scope=openid%20profile%20email
  &code_challenge=hkEIvgzsGwmoM3OkgqZaM7LCPePEuVXYY1owaRtTJSw
  &code_challenge_method=S256
  &response_mode=form_post
  &nonce=639267348089164174.MzZjYTIyZjctOWIyMi00NjEy...
  &state=CfDJ8EsoK96Yi1dBuZPdKiRCSMOLkfe2H3b4OLYCEZqZI-Ee...
```

Every parameter earns its place:

| Parameter | Why it is there |
|---|---|
| `client_id` | which app is asking |
| `redirect_uri` | where to send the user back. **Exact-matched** against the client config — this is what stops an attacker redirecting the code to their own server |
| `response_type=code` | "give me a code, not a token" — this is what makes it the Authorization Code flow |
| `scope` | `openid` makes it OIDC; the rest request claims |
| `code_challenge` + `method` | PKCE. See below. |
| `response_mode=form_post` | return the code via POST body instead of a query string, keeping it out of access logs and `Referer` headers |
| `nonce` | random value Keycloak must echo into the `id_token`, proving it answers *this* request |
| `state` | CSRF protection. See below. |

### `state` vs `nonce` — they are not the same thing

Both are random, both come back, and they defend different attacks:

- **`state`** protects **the authorization request**. It is tied to the
  `.AspNetCore.Correlation.*` cookie. If a code arrives with a `state` we never
  issued, somebody is trying to feed us *their* login — a **login CSRF**, where
  an attacker silently signs you into their account. We reject it.
- **`nonce`** protects **the id_token**. It is embedded in the token itself. If
  the returned `id_token` carries a `nonce` we did not send, the token is a
  replay from another session. We reject it.

One guards the round trip; the other guards the credential that comes back.

## PKCE — the important part

**P**roof **K**ey for **C**ode **E**xchange, RFC 7636, pronounced "pixy".

### The attack it prevents

The authorization code travels through the browser, so it is exposed to anything
that can observe a redirect: a malicious app registered for the same custom URL
scheme on a phone, a browser extension, a proxy, a shoulder-surfed log.

If an attacker steals the code, can they redeem it? For a **public** client
(SPA, mobile app) there is no secret to stop them — the "secret" would be
shipped inside the app, so it is not a secret. Historically, a stolen code was
game over.

### How PKCE closes it

```
1. Before redirecting, the client generates a random secret:
       code_verifier  = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"   (kept locally)

2. It sends only the hash in the authorization request:
       code_challenge = BASE64URL(SHA256(code_verifier))
                      = "hkEIvgzsGwmoM3OkgqZaM7LCPePEuVXYY1owaRtTJSw"

3. Keycloak stores that challenge next to the issued code.

4. Redeeming the code requires the ORIGINAL verifier:
       POST /token  ... &code_verifier=dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk

5. Keycloak checks SHA256(verifier) == stored challenge. No match → refused.
```

A stolen code is now worthless on its own. The verifier never travelled through
the browser, and SHA-256 cannot be run backwards to recover it from the
challenge.

`code_challenge_method=S256` means SHA-256. The spec also allows `plain` (send
the verifier as-is), which defends nothing — never use it. This realm **requires**
S256 via `"pkce.code.challenge.method": "S256"` on the client, so a downgrade
attempt is rejected by the server.

> **Use PKCE even for confidential clients.** The lab's WebApp has a secret *and*
> uses PKCE. It costs nothing and defends against code injection regardless. In
> .NET 9 `UsePkce` is already `true` by default; the lab sets it explicitly
> because it is worth seeing.

## Step 3 — authentication

The user types their password into **Keycloak's** page on **Keycloak's** origin.
Our app never sees it, cannot log it, and cannot leak it. This is the structural
benefit of the redirect: not convenience, but the elimination of an entire class
of credential exposure.

This is also where MFA, social login, "remember me", and password policy live —
all without a line of code in our app.

## Step 4 — the code comes back

Because `response_mode=form_post`, Keycloak returns a small self-submitting HTML
form rather than a redirect:

```html
<BODY Onload="document.forms[0].submit()">
  <FORM METHOD="POST" ACTION="http://localhost:5002/signin-oidc">
    <INPUT TYPE="HIDDEN" NAME="code"  VALUE="8442d10d-e8e8-4fa9-8c74-a172254f783f..." />
    <INPUT TYPE="HIDDEN" NAME="state" VALUE="CfDJ8EsoK96Yi1dBuZPdKiRCSMOLkfe2..." />
    <INPUT TYPE="HIDDEN" NAME="iss"   VALUE="http://localhost:8080/realms/dotnet-lab" />
    <INPUT TYPE="HIDDEN" NAME="session_state" VALUE="..." />
  </FORM>
</BODY>
```

The code is:

- **single use** — redeem it twice and Keycloak refuses (and treats it as suspicious)
- **short lived** — about a minute
- **bound** to this `client_id`, this `redirect_uri`, and this PKCE challenge
- **worthless alone** — it is not a credential for the API, only a ticket to
  collect one

## Step 5 — the token exchange (back channel)

This is the request the browser never sees:

```http
POST /realms/dotnet-lab/protocol/openid-connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code
&code=8442d10d-e8e8-4fa9-8c74-a172254f783f...
&redirect_uri=http://localhost:5002/signin-oidc
&client_id=dotnet-webapp
&client_secret=webapp-secret-do-not-use-in-prod
&code_verifier=dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk
```

Keycloak verifies all of it — code valid and unused, client secret correct,
`redirect_uri` identical to the one in step 2, PKCE verifier hashes to the stored
challenge — and only then returns the tokens.

ASP.NET Core then validates the `id_token` (signature, `iss`, `aud`, `exp`,
`nonce`), builds a `ClaimsPrincipal`, and — because `SaveTokens = true` — tucks
the tokens into the auth cookie.

## Step 6 — the cookie

```
Set-Cookie: WebApp.Session=chunks-2; path=/; httponly
Set-Cookie: WebApp.SessionC1=CfDJ8EsoK96Yi1dBuZPdKiRCSM...; path=/; httponly
Set-Cookie: WebApp.SessionC2=...; path=/; httponly
```

Note the **chunking**. Browsers cap a cookie at ~4KB, and three JWTs plus claims
blow past that, so ASP.NET Core splits the payload across `C1`, `C2`, … with an
index cookie. It is transparent, but worth recognising when you see it in
devtools, and it is a reason to consider a server-side ticket store in production
([10-production-checklist.md](10-production-checklist.md)).

The cookie is **encrypted and signed** with the app's Data Protection keys. The
browser cannot read the tokens inside it, and cannot forge one.

From here on, the browser sends only this cookie. **The browser never holds a
token.** That is the BFF (Backend For Frontend) pattern, and it is why tokens are
out of reach of XSS.

## Watch it happen yourself

```bash
# The authorization redirect, decoded parameter by parameter
curl -s -o /dev/null -D- http://localhost:5002/profile | grep -i location
```

Or run the scripted walkthrough of all seven steps:

```bash
./scripts/walk-the-flow.sh
```

Or do it with devtools open (Network tab, "Preserve log" checked) and read the
real sequence. The redirects will be there in order.

## One .NET 9 surprise: PAR

If your authorization redirect looks like this instead:

```
?client_id=dotnet-webapp&request_uri=urn:ietf:params:oauth:request_uri:5c6410a8-...
```

…you are seeing **Pushed Authorization Requests** (RFC 9126). .NET 9 changed the
default to use PAR when the server supports it, and Keycloak 26 does.

PAR POSTs the parameters to Keycloak over the back channel first; the browser
then carries only a `request_uri` handle. It is genuinely better — parameters
cannot be tampered with or leak through logs and `Referer` headers.

But it also hides `scope`, `state`, `nonce` and `code_challenge` from the URL
bar, which is exactly what you came to look at. So the lab turns it off:

```csharp
options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
```

**Off while learning, on in production.** Flip it back to `UseIfAvailable` and
reload `/profile` to watch the URL collapse.

## Next

→ [05-keycloak-concepts.md](05-keycloak-concepts.md) — realms, clients, mappers,
and the audience trap.
