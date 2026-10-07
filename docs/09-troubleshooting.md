# 9. Troubleshooting

Real error strings, with the cause rather than a guess. Mostly ordered by how
often they happen.

## First, three questions that localise almost everything

**1. Is it a token problem or a permission problem?**

```
401  →  the token was rejected. Stop looking at your policies.
403  →  the token was accepted. Look only at roles and policies.
```

**2. What does the token actually contain?**

```bash
export TOKEN=$(./scripts/get-token.sh alice raw)
./scripts/decode-token.sh "$TOKEN"
```

Three-quarters of "auth is broken" turns out to be a claim that is not there.

**3. Do these three strings match, exactly?**

```bash
# what Keycloak advertises
curl -s http://localhost:8080/realms/dotnet-lab/.well-known/openid-configuration | jq -r .issuer
# what is in the token
./scripts/decode-token.sh "$TOKEN" | grep 'iss (issuer)'
# what the API trusts
grep Authority src/Api/appsettings.json
```

Trailing slashes count. `http` vs `https` counts. `localhost` vs `127.0.0.1`
counts.

---

## `IDX10214: Audience validation failed`

> **Cause:** the token's `aud` does not contain your API's identifier.

**The single most common Keycloak + API failure.** Keycloak does not put your API
into `aud` by default.

Check it:

```bash
./scripts/decode-token.sh "$TOKEN" | grep 'aud'
```

**Fix:** add an audience mapper (**Clients → <client> → Client scopes →
<client>-dedicated → Add mapper → By configuration → Audience**, Included Client
Audience = `dotnet-api`).

**Do not fix it with `ValidateAudience = false`.** That makes any token from the
realm work against your API, including tokens minted for less trusted clients.
Full explanation: [the audience trap](05-keycloak-concepts.md#the-audience-trap).

Note the mapper belongs on the **calling** client, not on the API. A token issued
*to* `dotnet-api` (client credentials) needs its own mapper.

---

## `IDX10205: Issuer validation failed`

> **Cause:** the `iss` string in the token is not byte-identical to the
> `Authority`/`ValidIssuer` the API trusts.

Common variants:

| Token says | API expects | Why |
|---|---|---|
| `http://localhost:8080/...` | `http://keycloak:8080/...` | containerised app using the Docker hostname |
| `http://127.0.0.1:8080/...` | `http://localhost:8080/...` | same machine, different string |
| `http://.../realms/dotnet-lab` | `http://.../realms/dotnet-lab/` | trailing slash |
| `http://...` | `https://...` | proxy terminating TLS without `KC_PROXY_HEADERS` |

**Fix:** make `KC_HOSTNAME`, the browser's URL, and every app's `Authority`
agree on one string. See
[the hostname trap](06-containerizing-keycloak.md#the-hostname-and-issuer-trap-read-this-one).

---

## `Correlation failed.` / `.AspNetCore.Correlation.* cookie not found`

> **Cause:** the correlation cookie set before the redirect did not come back.

Candidates, most likely first:

1. **`SameSite = Strict` on the cookie.** The return from Keycloak is a
   cross-site navigation, so `Strict` withholds the cookie. Use `Lax`.
2. **`Secure` cookies over plain HTTP** on a non-localhost hostname. Either use
   HTTPS or `CookieSecurePolicy.SameAsRequest` in dev.
3. **You took too long.** The correlation cookie expires in ~15 minutes.
4. **The host changed mid-flow** — started on `localhost`, came back on
   `127.0.0.1`. Cookies are per-host.
5. **Stale cookies from an earlier broken attempt.** Clear site data and retry
   — this genuinely fixes it more often than it should.

---

## `Invalid parameter: redirect_uri`

Shown by **Keycloak**, before any token exists.

> **Cause:** the `redirect_uri` your app sent is not in the client's registered
> list. Keycloak exact-matches it, deliberately — this is what stops an attacker
> redirecting an authorization code to their own server.

```bash
# what the app sends
curl -s -o /dev/null -D- http://localhost:5002/profile | grep -i location
# what Keycloak allows: Clients -> dotnet-webapp -> Valid redirect URIs
```

Usual culprits: a port change (5002 vs 5000), `http` vs `https`, a missing
`/signin-oidc` path, or a trailing slash.

Wildcards (`http://localhost:5002/*`) work for local development. **Never in
production** — be exact.

---

## Login works, but every API call returns 401

> **Cause:** almost always `SaveTokens`.

Without `options.SaveTokens = true`, ASP.NET Core validates the `id_token`,
builds the cookie, and **discards the tokens**. You are logged in and have no
access token to send.

```csharp
options.SaveTokens = true;
```

Confirm by checking whether you have one at all:

```csharp
var token = await HttpContext.GetTokenAsync("access_token");  // null → this is your bug
```

Second possibility: you are sending the **`id_token`** instead of the
`access_token`. Its `aud` is your client id, so a correctly configured API
rejects it (and if yours accepts it, your API is not validating `aud`).

---

## A claim I can see in the token is `null` in C#

> **Cause:** .NET renamed it.

By default .NET maps inbound claims to legacy WS-Federation URIs:

| JWT claim | becomes |
|---|---|
| `sub` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier` |
| `email` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` |
| `given_name` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname` |

So `User.FindFirst("sub")` returns `null` and nothing explains why.

**Fix:**

```csharp
options.MapInboundClaims = false;
```

Both apps in this lab set it. Visit `/api/claims` to see the real names.

---

## `[Authorize(Roles = "lab-admin")]` always 403s, even for alice

> **Cause:** Keycloak nests roles; ASP.NET Core wants them flat.

```jsonc
"realm_access": { "roles": ["lab-admin"] }   // nested object, not a role claim
```

ASP.NET Core looks for a flat claim of type `ClaimTypes.Role` and finds nothing.

**Fix:** flatten them in `OnTokenValidated` — `KeycloakRoles.cs` in both projects
does exactly this, for realm roles and client roles.

Check what arrived:

```bash
./scripts/call-api.sh alice /api/claims | jq 'keys'
```

---

## `SecurityTokenExpiredException`

> **Cause:** `exp` has passed. Working as designed.

Access tokens live 5 minutes here. Refresh instead of re-authenticating.

If it happens *immediately* after issuing a token, your clocks disagree. Check
the container's clock:

```bash
date -u; docker compose exec -T keycloak date -u
```

`ClockSkew` (default 5 minutes, 30s in this lab) absorbs small drift. If you need
more than a few seconds, fix NTP rather than widening the window.

---

## `invalid_grant` on refresh

Keycloak's catch-all for "that refresh token is no good". Causes:

| Cause | Check |
|---|---|
| Refresh token expired | `refresh_expires_in` at issue; bounded by **SSO Session Idle** |
| SSO session idled out (30 min here) | admin console → **Sessions** |
| Already used, and rotation is on | **Realm settings → Tokens → Revoke Refresh Token** |
| User disabled or logged out | **Users → alice → Enabled** |
| Wrong `client_id`/`client_secret` | they must match the client that got the token |

**All of these mean the same thing to your app: the session is over.** There is
nothing to retry — send the user back through login.

---

## `Offline tokens not allowed for the user or client`

> **Cause:** you requested the `offline_access` scope but the user lacks the
> `offline_access` realm role.

**Fix:** grant the role (it comes with `default-roles-<realm>`), or stop
requesting the scope.

Worth knowing: **you do not need `offline_access` to get a refresh token.** The
Authorization Code flow returns one anyway. `offline_access` asks for a refresh
token that *outlives the session* — a different, stronger thing.

---

## My realm JSON edits do nothing

> **Cause:** `--import-realm` only imports **if the realm does not already
> exist.**

```bash
docker compose down -v        # the -v is the whole point: it drops the volume
docker compose up -d
docker compose logs keycloak | grep -i import   # expect: Realm 'dotnet-lab' imported
```

Without `-v`, Postgres still holds the old realm and your file is ignored
silently. This is worth half an hour of anyone's life, once.

---

## Keycloak will not start

```bash
docker compose logs keycloak | tail -40
```

| Message | Cause |
|---|---|
| `Unrecognized field "xyz"` | your realm JSON has a key this Keycloak version does not know. The error names the field and lists valid ones. |
| `Connection refused` to postgres | Postgres was not ready. The compose healthcheck should prevent this — check `docker compose ps`. |
| `Address already in use` | something else holds 8080: `ss -ltnp \| grep 8080` |
| `Failed to run import` | malformed realm JSON. Validate it: `jq . keycloak/realm-dotnet-lab.json` |
| Hangs ~40s on first boot | normal. Schema migration. `start --optimized` fixes it in production. |

---

## The authorization URL has no `scope`, `state` or `code_challenge`

```
?client_id=dotnet-webapp&request_uri=urn:ietf:params:oauth:request_uri:5c64...
```

> **Not a bug.** This is **PAR** (Pushed Authorization Requests), which .NET 9
> enables by default when the server supports it — and Keycloak 26 does.

The parameters went to Keycloak over the back channel; the browser carries only a
handle. More secure, less observable.

This lab disables it so you can see the parameters:

```csharp
options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
```

---

## Port already in use

```bash
ss -ltnp | grep -E ':(5002|5003|8080) '     # find it
kill <pid>                                   # stop it
```

The .NET ports are set in `src/*/Properties/launchSettings.json`; Keycloak's is
in `docker-compose.yml`.

---

## Useful commands, collected

```bash
# --- Keycloak ---
docker compose logs -f keycloak
docker compose ps                                  # health status
docker compose down -v && docker compose up -d      # full reset
curl -s localhost:8080/realms/dotnet-lab/.well-known/openid-configuration | jq .
curl -s localhost:8080/realms/dotnet-lab/protocol/openid-connect/certs | jq '.keys[].kid'

# --- tokens ---
export TOKEN=$(./scripts/get-token.sh alice raw)
./scripts/decode-token.sh "$TOKEN"
./scripts/call-api.sh bob /api/admin
./scripts/refresh-token.sh
./scripts/walk-the-flow.sh

# is this token still live, according to Keycloak?
curl -s -X POST localhost:8080/realms/dotnet-lab/protocol/openid-connect/token/introspect \
  -u dotnet-api:api-secret-do-not-use-in-prod -d token="$TOKEN" | jq .

# --- verbose .NET auth logging ---
# src/*/appsettings.Development.json:
#   "Microsoft.AspNetCore.Authentication": "Debug"
```

## Next

→ [10-production-checklist.md](10-production-checklist.md) — what this lab does
that you must not ship.
