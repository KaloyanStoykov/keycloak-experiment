# 3. Tokens Explained

Everything here is real output from this lab, not invented examples. Reproduce
any of it with `scripts/get-token.sh`.

## The three tokens

A successful login hands you three tokens with three different jobs. Mixing them
up is the most common OIDC bug.

| Token | Audience (`aud`) | Sent to | Lifetime here | Job |
|---|---|---|---|---|
| `access_token` | `dotnet-api` | the **API**, every call | 5 min | "let me do this" |
| `id_token` | `dotnet-webapp` | **nobody** — consumed by the app | 5 min | "this is who logged in" |
| `refresh_token` | the realm itself | **Keycloak's token endpoint only** | 30 min | "give me a new access token" |

> **The rule:** `id_token` logs you in. `access_token` calls APIs.
> `refresh_token` goes to Keycloak and nowhere else.

## What the token endpoint actually returns

```jsonc
{
  "access_token":       "eyJhbGciOiJSUzI1NiIsInR5c...",
  "expires_in":          300,      // access token: 5 minutes
  "refresh_token":      "eyJhbGciOiJIUzUxMiIsInR5c...",
  "refresh_expires_in":  1800,     // refresh token: 30 minutes
  "id_token":           "eyJhbGciOiJSUzI1NiIsInR5c...",
  "token_type":         "Bearer",  // always "Bearer" in practice
  "scope":              "openid profile email",
  "session_state":      "9610d59e-8ebc-45fe-b7c7-3b72d565e79e",
  "not-before-policy":   0         // Keycloak-specific revocation timestamp
}
```

`expires_in` is **seconds from now**, not a timestamp. The lab stores a computed
absolute `expires_at` in the cookie instead, because a relative number is
useless once you have written it down.

## JWT anatomy

A JWT is three base64url blobs joined by dots:

```
eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6IlQyUjEz...  . eyJleHAiOjE3OTExMzg2...  . mQZ8rN4kHs...
└────────────── header ──────────────┘   └── payload ──┘   └─ signature ─┘
```

### Header — how it was signed

```jsonc
{
  "alg": "RS256",   // RSA signature with SHA-256
  "typ": "JWT",
  "kid": "T2R13ekvZbmmYF0scgtfurEk5TW6u2y9lvcnrmo4E4M"   // WHICH key signed it
}
```

`kid` ("key id") is what makes key rotation painless. Keycloak can publish
several public keys at once; the API reads `kid`, picks the matching key from the
JWKS document, and verifies. Keycloak can add a new key and retire the old one
while tokens signed by both remain verifiable, and nobody redeploys anything.

### Payload — the claims

Real access token from this lab, annotated:

```jsonc
{
  // --- registered claims (RFC 7519) - these are the ones you validate ---
  "exp": 1791138639,                                  // expires at (unix seconds)
  "iat": 1791138339,                                  // issued at  -> 300s lifetime
  "jti": "05e03405-2427-4ce1-866b-387c46cdd76a",      // unique id, for replay detection
  "iss": "http://localhost:8080/realms/dotnet-lab",   // issuer; MUST match exactly
  "aud": ["dotnet-api", "account"],                   // intended recipients
  "sub": "467ed239-1595-4282-ba92-77600dc2cb33",      // the user. Your DB key.

  // --- Keycloak specifics ---
  "typ": "Bearer",
  "azp": "playground-cli",   // "authorized party": WHICH CLIENT asked for this.
                             // Different from `aud` (who it is FOR). Useful in
                             // audit logs: same user, different app.
  "sid": "9610d59e-...",     // Keycloak SSO session id
  "acr": "1",                // authentication context: 1 = password.
                             // Step-up auth raises this.

  // --- roles: note they are NESTED, which matters in .NET ---
  "realm_access": {
    "roles": ["offline_access", "default-roles-dotnet-lab",
              "uma_authorization", "lab-admin", "lab-user"]
  },
  "resource_access": {
    "dotnet-api": { "roles": ["reports.read"] },
    "account":    { "roles": ["manage-account", "view-profile"] }
  },

  // --- from the requested scopes ---
  "scope": "openid profile email",
  "preferred_username": "alice",   // mutable! do not use as a key
  "email": "alice@example.com",
  "email_verified": true,
  "name": "Alice Admin",
  "given_name": "Alice",
  "family_name": "Admin"
}
```

Two things to notice:

- `aud` contains `dotnet-api` **because the realm configures an audience
  mapper.** Keycloak does not add useful audiences on its own — see
  [05-keycloak-concepts.md](05-keycloak-concepts.md#the-audience-trap).
- Roles are **nested objects**, not flat strings. ASP.NET Core's
  `[Authorize(Roles = "lab-admin")]` looks for flat role claims and will find
  nothing until you flatten them. Both apps do that in `KeycloakRoles.cs`.

### Signature — the only part that matters for trust

> **A JWT is signed, not encrypted.**

Anyone can read any JWT. Paste one into <https://jwt.io> and every claim is
visible. Two consequences:

1. **Never put secrets in a token.** No internal ids you care about, no PII you
   would not put in a log.
2. **Never trust a token you have not verified.** Decoding is trivial base64.
   *Verifying* is the cryptographic step that proves Keycloak minted it and
   nobody altered a byte since.

`src/WebApp/Jwt.cs` only decodes, for display, and says so loudly. `src/Api`
verifies — that is the real check.

### Asymmetric vs symmetric, in one observation

Look at the `alg` of each token from this lab:

- `access_token` and `id_token` → **`RS256`** (asymmetric). Keycloak signs with a
  private key; *anyone* can verify with the public key from `jwks_uri`. This is
  precisely why your API can validate tokens offline, with zero calls to
  Keycloak.
- `refresh_token` → **`HS512`** (symmetric). Signed with a secret only Keycloak
  holds. Nobody else can validate one — which is fine, because nobody else is
  ever supposed to. It is for Keycloak's eyes only.

That difference is not an accident; it is the architecture.

## How the API validates a token

Five checks, in this order. All of them are local except the one-time key fetch.

```
1. Parse            three dot-separated parts, read the header
2. Find the key     match header `kid` against the cached JWKS
                    (fetched once from {authority}/protocol/openid-connect/certs)
3. Verify signature does the RS256 signature match the payload? ← the real check
4. Check claims     iss == our authority?      (wrong realm → reject)
                    aud contains "dotnet-api"? (someone else's token → reject)
                    exp in the future?         (expired → reject)
5. Build identity   claims → ClaimsPrincipal, flatten the nested roles
```

Step 3 is the one that makes the rest meaningful: if the signature holds, the
claims are exactly what Keycloak wrote.

### The clock skew surprise

`TokenValidationParameters.ClockSkew` defaults to **5 minutes**. With 5-minute
access tokens that silently doubles their effective life, which is baffling when
you are deliberately trying to watch one expire.

The lab sets it to 30 seconds:

```csharp
ClockSkew = TimeSpan.FromSeconds(30),
```

Some allowance is genuinely needed — servers' clocks drift. But five minutes is
a lot, and if you need that much, fix NTP instead.

## Why refresh tokens exist

Here is the tension. An access token is a **bearer** token: whoever holds it can
use it. And the API validates it **offline**, so an issued token cannot be
un-issued. If one leaks, it works until it expires.

The only real mitigation is to make expiry fast — 5 minutes here.

But you obviously cannot prompt the user for a password every 5 minutes. So the
Authorization Server issues a second credential with opposite properties:

| | access token | refresh token |
|---|---|---|
| Lifetime | minutes | hours to days |
| Sent to | every API | only Keycloak's token endpoint |
| Validated | offline, by signature | online, by Keycloak |
| Revocable? | **no** — valid until `exp` | **yes** — Keycloak can refuse it |
| Used | constantly | rarely |

The trade is the whole design: **a short-lived credential used everywhere, plus a
long-lived credential used rarely against exactly one endpoint that is allowed to
say no.**

Revocability is the payoff. Disable a user in Keycloak and their access token
still works for up to 5 more minutes — but the next refresh fails, and they are
out. Five minutes of exposure, with no per-request cost.

### The refresh request

Nothing exotic — a plain form POST:

```bash
curl -X POST http://localhost:8080/realms/dotnet-lab/protocol/openid-connect/token \
  -d grant_type=refresh_token \
  -d refresh_token="$REFRESH_TOKEN" \
  -d client_id=dotnet-webapp \
  -d client_secret=webapp-secret-do-not-use-in-prod
```

The response has the same shape as the original login response — including a
**new refresh token**. Always store the one you just received.

`src/WebApp/TokenRefresher.cs` does this, and critically also writes the result
back into the auth cookie. Forgetting that step is a real bug: you refresh
successfully and then keep sending the old expired token.

### Refresh token rotation

This realm has **Revoke Refresh Token off** (`revokeRefreshToken: false`), so a
new refresh token is issued but the old one keeps working until it expires.

Turn it on (`Realm settings → Tokens → Revoke Refresh Token`) and the old token
is burned the moment it is used. Replaying it fails — and Keycloak reads reuse as
evidence of theft and can terminate the whole session. That is the secure
setting; it just needs a client that reliably stores each new token.

### `offline_access`

A refresh token is normally bound to your SSO session: close the browser, log
out, and it dies with the session.

The `offline_access` scope requests something stronger — a refresh token that
*outlives* the session, so a background job can keep acting for you after you
have gone home. Keycloak reports `refresh_expires_in: 0` for these, meaning "not
tied to session expiry".

It requires the user to hold the `offline_access` realm role. Our users do, so
you can try it: uncomment the `offline_access` scope in
`src/WebApp/Program.cs` and compare `refresh_expires_in` on `/tokens`.

## Seeing expiry for yourself

1. Log in at <http://localhost:5002>, open `/tokens`, note the countdown.
2. Wait 5 minutes (go make tea).
3. Click `/api/whoami` → **401**, and the `X-Auth-Failure-Reason` header names
   `SecurityTokenExpiredException`.
4. Go to `/tokens`, press **Exchange refresh token**.
5. `/api/whoami` → **200** again.

That loop is what every OAuth client library does for you automatically. It is a
button here so you can watch it.

## Next

→ [04-authorization-code-pkce.md](04-authorization-code-pkce.md) — the login
flow, parameter by parameter.
