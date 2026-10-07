# 10. Production Checklist

This lab is built to be *readable*, which repeatedly meant choosing the less
secure option. This document is the honest list of what to undo.

## Things in this repo you must not ship

| What | Where | Why it is here | What to do instead |
|---|---|---|---|
| Tokens rendered on a web page | `/tokens` | so you can read them | delete the page |
| `X-Auth-Failure-Reason` header | `src/Api/Program.cs` | so 401s explain themselves | remove it — it tells an attacker which check failed |
| Hardcoded client secrets | `appsettings.json`, realm JSON | so it runs with no setup | secret manager / env vars |
| `admin` / `admin` | `docker-compose.yml` | so you can log straight in | named admin + MFA, bootstrap account deleted |
| Plain HTTP everywhere | all | no certs to manage | HTTPS, HSTS |
| `start-dev` | `docker-compose.yml` | relaxed and fast | `start --optimized` |
| Password grant enabled | `playground-cli` client | shortest path to a token in `curl` | delete the client |
| PAR disabled | `src/WebApp/Program.cs` | so the authorize URL is readable | `UseIfAvailable` |
| `RequireHttpsMetadata = false` | both apps | Keycloak is on HTTP | `true` (the default) |
| `sslRequired: none` | realm JSON | plain HTTP | `all` |
| Revoke Refresh Token off | realm JSON | so reuse demos work | **on** |
| 5-minute access tokens | realm JSON | observable expiry | 1–5 min is actually fine |

## Keycloak

### Deployment

- [ ] `start --optimized`, with config baked in via `kc.sh build` — see
      [doc 6](06-containerizing-keycloak.md#1-use-start-and-build-an-optimized-image)
- [ ] pin the image to an exact tag (`26.0.8`, not `26.0` or `latest`)
- [ ] managed Postgres, or Postgres with backups and TLS, verified by a restore test
- [ ] `KC_HOSTNAME` set to the real public URL
- [ ] `KC_HOSTNAME_STRICT=true`
- [ ] TLS terminated at Keycloak, or `KC_PROXY_HEADERS=xforwarded` with a proxy
      you control
- [ ] `KC_HEALTH_ENABLED` + `KC_METRICS_ENABLED`, port 9000 kept internal
- [ ] liveness → `/health/live`, readiness → `/health/ready`
- [ ] 2+ replicas with an Infinispan cache stack
- [ ] resource limits, and a JVM heap sized for them

### Realm

- [ ] `sslRequired: all`
- [ ] **Revoke Refresh Token: on**
- [ ] access token lifespan 1–5 minutes
- [ ] SSO session idle/max matched to your risk appetite
- [ ] brute-force detection on (**Realm settings → Security defenses**)
- [ ] a password policy
- [ ] `registrationAllowed: false` unless you mean it
- [ ] email configured (verification, password reset)
- [ ] realm JSON in version control, secrets externalised as `${ENV_VAR}`
- [ ] admin events and user events enabled, with a retention period

### Clients

- [ ] exact redirect URIs, **no wildcards**
- [ ] `webOrigins` as narrow as CORS allows
- [ ] PKCE S256 required on every client
- [ ] `directAccessGrantsEnabled: false` everywhere
- [ ] `implicitFlowEnabled: false` everywhere
- [ ] prefer `client_jwt` (private key JWT) over a shared secret
- [ ] secret rotation has a procedure, and someone has run it
- [ ] one client per application — never share a client id across apps

### Admin

- [ ] bootstrap admin **deleted** after creating a real one
- [ ] admin accounts have MFA
- [ ] no application lives in the `master` realm
- [ ] admin console not reachable from the public internet

## The .NET apps

### API

- [ ] `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime`,
      `ValidateIssuerSigningKey` — **all true**
- [ ] `RequireHttpsMetadata = true`
- [ ] `ClockSkew` ≤ 1 minute, with NTP actually working
- [ ] `MapInboundClaims = false` (or deliberately handle the renaming)
- [ ] `X-Auth-Failure-Reason` removed
- [ ] `UseAuthentication()` before `UseAuthorization()`
- [ ] policies, not scattered inline role strings
- [ ] no PII in logs — and remember tokens *are* PII
- [ ] rate limiting

### Web app

- [ ] `Cookie.SecurePolicy = Always`
- [ ] `Cookie.HttpOnly = true`
- [ ] `Cookie.SameSite = Lax` (**not `Strict`** — it breaks the OIDC return)
- [ ] `UsePkce = true`
- [ ] `PushedAuthorizationBehavior = UseIfAvailable`
- [ ] full RP-initiated logout, not local-only
- [ ] refresh handled automatically (see below)
- [ ] a failed refresh redirects to login rather than throwing
- [ ] **Data Protection keys persisted and shared across instances** ← see below
- [ ] consider a server-side ticket store ← see below

## Four things that bite in production specifically

### 1. Data Protection keys

ASP.NET Core encrypts the auth cookie with Data Protection keys. By default those
are generated **per instance** and stored on local disk.

Scale to two instances and users get logged out at random — instance B cannot
decrypt a cookie written by instance A. Restart a container and every session
dies.

```csharp
builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(redis, "DataProtection-Keys")
    .SetApplicationName("MyApp");          // must be identical across instances
```

Or Azure Blob / AWS S3 / a mounted volume. `SetApplicationName` matters — keys
are isolated by it.

This is not an OAuth problem, but it presents as "auth randomly breaks in
production", so it belongs here.

### 2. Cookie size

`SaveTokens = true` puts three JWTs in the cookie. In this lab that already
chunks across `WebApp.SessionC1`, `C2`. Add a few claims and you approach the
browser's hard limit, and every request carries those kilobytes.

Options:

- store the ticket **server-side** (`ITicketStore`) and keep only a session id
  in the cookie — Redis-backed is the standard answer
- trim claims (`options.ClaimActions.Remove(...)`)
- do not request scopes you are not using

### 3. Automatic token refresh

The lab's manual button is a teaching device. In production, pick one:

| Approach | Notes |
|---|---|
| `DelegatingHandler` retrying once on 401 | simple, self-contained, easy to reason about |
| Proactive refresh in `OnValidatePrincipal` | always-fresh cookie; watch for concurrent refreshes |
| [Duende.AccessTokenManagement](https://github.com/DuendeSoftware/foss) | handles refresh, caching and concurrency properly — **prefer this** |

Whatever you choose: a failed refresh is not a retryable error. It means the
session is over.

### 4. Key rotation will happen

Keycloak rotates signing keys. ASP.NET Core re-fetches JWKS periodically and
matches on `kid`, so this normally works with no action — but:

- do not pin a signing key manually
- allow outbound access from your API to Keycloak's JWKS endpoint (a firewall
  rule that blocks it causes a total outage at the next rotation, not at deploy
  time, which makes it horrible to diagnose)
- `options.RefreshInterval` controls how often; the default is fine
- `AutomaticRefreshInterval` has a 12-hour floor — do not expect instant pickup

## Design decisions worth revisiting

### Do you need JWT validation, or introspection?

| | JWT (offline) | Introspection |
|---|---|---|
| Per-request cost | none | a network call |
| Revocation visible | after ≤ token lifetime | immediately |
| Keycloak availability | needed only for refresh | needed for **every request** |

Default to JWT with short lifetimes. Add introspection for genuinely high-value
operations. Do not add it everywhere "to be safe" — you have just made Keycloak a
single point of failure on your hot path.

### Where do authorization decisions live?

Roles in tokens are convenient and they go stale — a token issued before a
permission change still carries the old one, for up to its lifetime.

- **coarse, slow-changing** (`admin`, `employee`) → roles in the token, fine
- **fine-grained, fast-changing** (per-document ACLs) → your own data, queried at
  request time

Resist putting everything in the token. Every claim is paid for on every request,
and you cannot retract one.

### One realm or many?

One realm per **security boundary**: separate user populations, separate admins.
Not per application — applications are clients within a realm.

Multi-tenant SaaS: realm-per-tenant gives hard isolation and gets unwieldy past a
few hundred. A single realm with a tenant claim scales further and makes
isolation your code's problem. Decide early; migrating is painful.

## Further reading

Specifications (shorter and clearer than their reputation):

- [RFC 6749](https://datatracker.ietf.org/doc/html/rfc6749) — OAuth 2.0 core
- [RFC 7636](https://datatracker.ietf.org/doc/html/rfc7636) — PKCE
- [RFC 7519](https://datatracker.ietf.org/doc/html/rfc7519) — JWT
- [RFC 9126](https://datatracker.ietf.org/doc/html/rfc9126) — PAR
- [OpenID Connect Core](https://openid.net/specs/openid-connect-core-1_0.html)

Guidance worth reading in full:

- [OAuth 2.0 Security Best Current Practice](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-security-topics)
  — the single most useful document on this list
- [OAuth 2.1 draft](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-v2-1)
  — consolidates the above; shows where things are heading
- [Keycloak documentation](https://www.keycloak.org/documentation)
- [Keycloak hardening guide](https://www.keycloak.org/server/configuration-production)
