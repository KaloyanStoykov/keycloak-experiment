# OAuth 2.0 / OpenID Connect + Keycloak + .NET 9 — a learning lab

A deliberately small, heavily documented system for understanding how OAuth 2.0,
OpenID Connect and Keycloak actually fit together — and for containerizing
Keycloak so deployments are reproducible.

Everything here has been run and verified. The token payloads, error messages and
URLs quoted in the docs are real output from this lab, not illustrations.

## Start here

```bash
# 1. Keycloak + Postgres, with the realm, clients, roles and users auto-imported
docker compose up -d
docker compose logs -f keycloak        # wait for "started in ..." (~40s first time)

# 2. The API (resource server) - new terminal
dotnet run --project src/Api

# 3. The web app (client) - new terminal
dotnet run --project src/WebApp

# 4. Open it
#    http://localhost:5002      log in as alice / password
```

Then either read [`docs/01-oauth2-fundamentals.md`](docs/01-oauth2-fundamentals.md)
or jump straight to the exercises in
[`docs/08-hands-on-walkthrough.md`](docs/08-hands-on-walkthrough.md).

Prefer to see the whole login flow narrated in your terminal first?

```bash
./scripts/walk-the-flow.sh
```

| | URL | Credentials |
|---|---|---|
| Web app | <http://localhost:5002> | `alice` / `password`, `bob` / `password` |
| API | <http://localhost:5003> | needs a bearer token |
| Keycloak admin | <http://localhost:8080/admin> | `admin` / `admin` |

## The reading path

Written to be read in order; each one assumes the last.

| | Doc | What you get |
|---|---|---|
| 1 | [OAuth 2.0 Fundamentals](docs/01-oauth2-fundamentals.md) | the four roles, every grant type with a verdict, why OAuth is *not* authentication |
| 2 | [OpenID Connect](docs/02-openid-connect.md) | the `id_token`, discovery, and the attack OIDC exists to prevent |
| 3 | [Tokens Explained](docs/03-tokens-explained.md) | JWT anatomy from real tokens, the five validation checks, why refresh tokens exist |
| 4 | [Authorization Code + PKCE](docs/04-authorization-code-pkce.md) | the login flow parameter by parameter; `state` vs `nonce`; how PKCE works |
| 5 | [Keycloak Concepts](docs/05-keycloak-concepts.md) | realms, clients, roles, mappers — and **the audience trap** |
| 6 | [Containerizing Keycloak](docs/06-containerizing-keycloak.md) | the compose file explained, **the hostname/issuer trap**, realm-as-code, production |
| 7 | [The .NET Integration](docs/07-dotnet-integration.md) | a guided reading of the code, and the gotchas built into .NET's defaults |
| 8 | [Hands-On Walkthrough](docs/08-hands-on-walkthrough.md) | 12 exercises with predictions — including breaking things on purpose |
| 9 | [Troubleshooting](docs/09-troubleshooting.md) | real error strings → actual causes |
| 10 | [Production Checklist](docs/10-production-checklist.md) | everything this lab does that you must not ship |

**Short on time?** Docs 3, 5 and 6 carry most of the practical value. Doc 5's
"audience trap" and doc 6's "hostname trap" are the two problems that cost people
the most hours.

## What is in here

```
├── docker-compose.yml           Keycloak 26 + Postgres, realm auto-imported
├── docker-compose.full.yml      everything containerized (shows the issuer problem)
├── keycloak/
│   └── realm-dotnet-lab.json    the whole realm as code, hand-written to be readable
├── src/
│   ├── Api/                     RESOURCE SERVER - validates JWT access tokens
│   │   ├── Program.cs             the five validation checks, policies, 401 vs 403
│   │   └── KeycloakRoles.cs       flattening Keycloak's nested roles for ASP.NET
│   └── WebApp/                  CLIENT - Authorization Code + PKCE, cookie session
│       ├── Program.cs             OIDC + cookie wiring, endpoints
│       ├── TokenRefresher.cs      the refresh grant, done by hand so you can see it
│       ├── Jwt.cs                 decode-for-display (NOT verification)
│       └── Pages.cs               plain HTML, no framework in the way
├── scripts/                     see below
└── docs/                        the point of the repo
```

### The two apps

```
Browser  ──cookie──►  WebApp :5002  ──Bearer token──►  Api :5003
                           │
                           └── OIDC login ──►  Keycloak :8080
```

Two separate security contexts. The cookie authenticates the user *to the
WebApp*; the token authenticates the WebApp *acting for the user* to the API.
**The browser never holds a token** — that is the BFF pattern, and it is what
keeps tokens out of reach of XSS.

### Scripts

All verified working. Run them from the repo root.

| Script | Does |
|---|---|
| `./scripts/walk-the-flow.sh` | drives the whole login flow, printing all 8 steps |
| `./scripts/get-token.sh [user]` | fetches real tokens and decodes them |
| `./scripts/decode-token.sh "$T"` | decodes any JWT and explains the claims |
| `./scripts/call-api.sh [user] [path]` | calls the API and explains the status code |
| `./scripts/refresh-token.sh` | the refresh grant, with a before/after comparison |
| `./scripts/export-realm.sh` | exports the live realm back to a file |

```bash
./scripts/call-api.sh alice /api/admin     # 200
./scripts/call-api.sh bob   /api/admin     # 403 - authenticated, not permitted
./scripts/call-api.sh --bad /api/admin     # 401 - token rejected outright
```

### The realm

Everything is pre-configured in `keycloak/realm-dotnet-lab.json`, so there is no
console clicking before anything works.

**Clients** — one of each shape you will meet:

| Client | Type | Purpose |
|---|---|---|
| `dotnet-webapp` | confidential | the browser app; Authorization Code + PKCE |
| `dotnet-api` | resource server | all interactive flows disabled |
| `playground-cli` | public | password grant, so `curl` can get a token (deprecated for real use) |

**Users:**

| | realm roles | client role | so |
|---|---|---|---|
| `alice` | `lab-user`, `lab-admin` | `dotnet-api:reports.read` | everything returns 200 |
| `bob` | `lab-user` | — | `/api/admin` returns **403** |

### API endpoints — a ladder from no auth to a specific role

| Endpoint | Requires |
|---|---|
| `/api/public` | nothing |
| `/api/whoami` | any valid token |
| `/api/claims` | any valid token — **the most useful endpoint here** |
| `/api/admin` | realm role `lab-admin` |
| `/api/reports` | client role `dotnet-api:reports.read` |

## Two traps worth knowing before you start

These cause more lost hours than everything else combined, so they are flagged
up front.

**1. The audience trap.** A Keycloak access token's `aud` does **not** contain
your API by default, so a correctly configured API rejects every token with a
401. The fix is an audience mapper, not `ValidateAudience = false`. →
[doc 5](docs/05-keycloak-concepts.md#the-audience-trap)

**2. The hostname trap.** Every token's `iss` claim is built from Keycloak's
configured hostname, and your API compares it by exact string match. In Docker,
the browser says `localhost:8080` and containers say `keycloak:8080` — and one of
them has to lose. → [doc 6](docs/06-containerizing-keycloak.md#the-hostname-and-issuer-trap-read-this-one)

## Common operations

```bash
docker compose logs -f keycloak        # startup errors live here
docker compose ps                      # health status
docker compose down                    # stop, keep data
docker compose down -v                 # stop and WIPE

# Edited the realm JSON? Import only runs if the realm does not exist yet,
# so you MUST wipe first or your change is silently ignored:
docker compose down -v && docker compose up -d

dotnet build                           # both projects
```

Everything containerized instead (and the issuer problem it creates):

```bash
echo "127.0.0.1 keycloak" | sudo tee -a /etc/hosts     # one-time, required
docker compose -f docker-compose.full.yml up --build -d
# then browse to http://keycloak:5002  (NOT localhost:5002)
```

## ⚠ This is a lab, not a template

Several things here are deliberately insecure so that the mechanics are visible:
tokens are rendered on a web page, the API echoes *why* it rejected a token,
secrets are hardcoded, everything is plain HTTP, and the password grant is
enabled.

Each of those is called out where it appears, and
[doc 10](docs/10-production-checklist.md) is the full list of what to undo.

**Do not copy this into a production service.** Read it, then write the real
thing with the checklist open.

## Requirements

- Docker + Docker Compose
- .NET 9 SDK
- `jq` optional (scripts fall back to Python)

Verified on .NET 9.0.315, Docker 29.8.1, Keycloak 26.0.8.
