# 6. Containerizing Keycloak

This is the operational half of the lab. It covers the compose file you are
running, the one gotcha that causes most Docker-Keycloak failures, how to get
config in and out of a container, and what has to change before production.

## What `docker-compose.yml` does

Two containers:

```
┌─────────────────┐        ┌──────────────────┐
│  kc-postgres    │◄───────│   kc-server      │
│  postgres:16    │  JDBC  │  keycloak:26.0   │
│                 │        │                  │
│ volume:         │        │ mounts:          │
│  postgres-data  │        │  realm JSON (ro) │
└─────────────────┘        └────────┬─────────┘
                                    │ 8080
                                    ▼  localhost:8080
```

### Why Postgres and not the default database

Keycloak's `start-dev` uses an embedded H2 database. It is fine for a five-minute
experiment and wrong for everything else:

- it is wiped on container restart — realms, users, sessions, all of it
- it cannot be shared, so you can never run two Keycloak replicas
- it is not supported in production

Adding Postgres is a 10-line change that removes a whole category of "where did
my realm go?" Make it the default for any environment you care about.

### Why the healthcheck on Postgres

Keycloak boots faster than Postgres accepts connections, so without gating
startup Keycloak crash-loops on a connection refused. `depends_on` with
`condition: service_healthy` makes compose wait:

```yaml
depends_on:
  postgres:
    condition: service_healthy
```

`depends_on` alone is not enough — plain `depends_on` waits for the container to
*start*, not for the service inside it to be *ready*.

### Why Keycloak's own healthcheck looks strange

```yaml
test: ["CMD-SHELL", "exec 3<>/dev/tcp/127.0.0.1/9000 && echo -e 'GET /health/ready ...' >&3 && ..."]
```

The Keycloak image ships no `curl` and no `wget` — deliberately, to keep the
attack surface small. So we open a raw TCP socket with a bash redirection and
speak HTTP by hand. Ugly, but dependency-free.

Note port **9000**: Keycloak 26 serves health and metrics on a separate
management port, which lets you expose `8080` publicly while keeping `/health`
and `/metrics` internal. Those endpoints exist because `KC_HEALTH_ENABLED=true`.

### The two commands

```yaml
command: ["start-dev", "--import-realm"]
```

`start-dev` means: HTTP allowed, hostname checks relaxed, caching local,
dev-friendly logging. **Never in production** — the production command is
`start`, covered below.

## The hostname and issuer trap (read this one)

**This causes more Keycloak-in-Docker failures than everything else combined.**

Every token Keycloak issues contains an `iss` (issuer) claim built from
Keycloak's configured hostname:

```jsonc
"iss": "http://localhost:8080/realms/dotnet-lab"
```

Your API validates that string **by exact character match**. So all three of
these must agree:

1. the URL the **browser** uses to reach Keycloak
2. the URL your **apps** use to reach Keycloak
3. `KC_HOSTNAME`

Inside Docker, (1) and (2) naturally disagree. The browser is on your host and
says `localhost:8080`. A container on the compose network says `keycloak:8080`.
If you set `Authority` to `http://keycloak:8080/...` in a containerised app, the
token's `iss` is `http://localhost:8080/...`, and validation fails with:

```
IDX10205: Issuer validation failed.
```

### Three ways out

**A. Keycloak in Docker, apps on the host** ← what this lab does by default

Everything says `localhost:8080`. Nothing to reconcile. Best for learning, and
genuinely good for day-to-day development.

**B. Everything in Docker, one shared hostname** ← `docker-compose.full.yml`

Make `keycloak:8080` work from both sides. Containers already resolve it; the
host needs one line:

```bash
echo "127.0.0.1 keycloak" | sudo tee -a /etc/hosts
```

Then `KC_HOSTNAME=http://keycloak:8080` and every app uses the same URL. One
issuer string, consistent everywhere.

**C. Split the URLs explicitly** (production shape)

In production a real DNS name resolves the same way everywhere, so the problem
disappears. Where it does not, Keycloak lets you separate the public URL from the
internal one:

```yaml
KC_HOSTNAME: https://auth.example.com          # goes into `iss`
KC_HOSTNAME_BACKCHANNEL_DYNAMIC: "true"        # allow internal URLs for back-channel
```

Discovery then advertises the public hostname (what `iss` must match) while
internal services may reach it directly.

### If you hit it anyway

```bash
# What issuer is Keycloak actually advertising?
curl -s http://localhost:8080/realms/dotnet-lab/.well-known/openid-configuration | jq -r .issuer

# What issuer is in the token?
./scripts/decode-token.sh "$TOKEN" | jq -r .iss
```

Those two strings and your `Authority` setting must be identical. Trailing
slashes count. `http` vs `https` counts.

## Realm config as code

Clicking through the admin console is fine for exploring and terrible for
repeatability. A realm you cannot recreate from a file is a realm you will
eventually lose.

### Import on startup

```yaml
command: ["start-dev", "--import-realm"]
volumes:
  - ./keycloak/realm-dotnet-lab.json:/opt/keycloak/data/import/realm-dotnet-lab.json:ro
```

Keycloak reads every `*.json` in `/opt/keycloak/data/import` at boot.

> **The gotcha that will cost you twenty minutes:** import only runs **if the
> realm does not already exist.** Editing the JSON and restarting does
> *nothing*. You must drop the database:
>
> ```bash
> docker compose down -v    # the -v removes the volume
> docker compose up -d
> ```
>
> If you edit the realm JSON and your change does not appear, this is why.

Mounted `:ro` on purpose — the container should never rewrite your source of
truth.

### Export what you clicked

Explored in the console and want to keep it? Export the live realm:

```bash
# --http-management-port 9001 matters: kc.sh export boots a partial Keycloak
# that would otherwise collide with the live server already holding port 9000.
docker compose exec -T keycloak /opt/keycloak/bin/kc.sh export \
  --dir /tmp/kc-export --realm dotnet-lab --users realm_file \
  --http-management-port 9001

docker compose cp keycloak:/tmp/kc-export/dotnet-lab-realm.json ./keycloak/
```

Or use the helper: `./scripts/export-realm.sh`

Two warnings about exports:

- **Client secrets are not exported** — you get `"secret": "**********"`. Put
  real values back in, from environment variables.
- Exports are verbose (thousands of lines of defaults). Good for backup, poor
  for review. The hand-written realm in this repo is deliberately minimal so you
  can read it.

### The workflow that actually works

1. Explore in the admin console until it behaves
2. Export
3. Diff against your committed realm file, port over just the parts you meant to change
4. `docker compose down -v && docker compose up -d` to prove it reproduces
5. Commit

Step 4 is the one people skip, and it is the one that catches "it only works
because of something I clicked three weeks ago."

### Secrets do not belong in the file

The lab hardcodes `webapp-secret-do-not-use-in-prod` because it is a lab. For
real use, parameterise:

```jsonc
"secret": "${WEBAPP_CLIENT_SECRET}"
```

Keycloak substitutes `${ENV_VAR}` during import. Supply the value from your
secret manager.

## Going to production

`start-dev` → `start` is not one change, it is several. Here is the checklist.

### 1. Use `start`, and build an optimized image

```dockerfile
# Dockerfile.keycloak
FROM quay.io/keycloak/keycloak:26.0 AS builder
ENV KC_DB=postgres
ENV KC_HEALTH_ENABLED=true
ENV KC_METRICS_ENABLED=true
RUN /opt/keycloak/bin/kc.sh build          # bake the config into the image

FROM quay.io/keycloak/keycloak:26.0
COPY --from=builder /opt/keycloak/ /opt/keycloak/
ENTRYPOINT ["/opt/keycloak/bin/kc.sh", "start", "--optimized"]
```

`kc.sh build` resolves provider configuration ahead of time. With `--optimized`,
startup drops from ~40s to a few seconds, and Keycloak will not silently
re-augment at boot.

### 2. TLS, not plaintext

```yaml
KC_HOSTNAME: https://auth.example.com
KC_HTTP_ENABLED: "false"
KC_HTTPS_CERTIFICATE_FILE: /etc/certs/tls.crt
KC_HTTPS_CERTIFICATE_KEY_FILE: /etc/certs/tls.key
```

Terminating TLS at a proxy instead? Then Keycloak must be told, or it will
generate `http://` URLs and set non-secure cookies:

```yaml
KC_PROXY_HEADERS: xforwarded      # trust X-Forwarded-* (only from a proxy you control)
KC_HTTP_ENABLED: "true"           # plaintext inside the trusted network
```

Also set the realm's `sslRequired` to `external` or `all`. This lab uses `none`.

### 3. Hostname strictness on

```yaml
KC_HOSTNAME_STRICT: "true"
```

The lab sets `false`. In production, leaving it off lets a `Host` header set the
issuer, which enables token-issuer confusion.

### 4. Real admin credentials

`KC_BOOTSTRAP_ADMIN_USERNAME` / `_PASSWORD` create a **temporary bootstrap**
admin. In production: start with them, log in once, create a named admin with
MFA, then delete the bootstrap account and remove the variables.

(These replaced `KEYCLOAK_ADMIN` / `KEYCLOAK_ADMIN_PASSWORD` in Keycloak 26. Old
tutorials still use the deprecated names.)

### 5. Database hygiene

Managed Postgres, or at minimum: real credentials from a secret store, automated
backups, and TLS on the connection (`KC_DB_URL_PROPERTIES=?sslmode=require`).
Keycloak's database holds every identity you have — treat it accordingly.

### 6. More than one replica

```yaml
KC_CACHE: ispn
KC_CACHE_STACK: kubernetes     # or tcp, or a custom Infinispan config
```

Replicas must form an Infinispan cluster to share sessions; otherwise a user's
session exists on one pod and the next request lands on another and they appear
logged out. Keycloak 26 persists sessions to the database by default, which makes
this far less fragile than it used to be.

### 7. Lock the realm down

- `sslRequired: all`
- **Revoke Refresh Token: on** (see [doc 3](03-tokens-explained.md#refresh-token-rotation))
- `registrationAllowed: false` unless you mean it
- brute-force detection on (**Realm settings → Security defenses**)
- a password policy
- shorter token lifetimes than the defaults

### 8. Watch it

`KC_HEALTH_ENABLED` and `KC_METRICS_ENABLED` give you:

- `:9000/health/ready` — ready for traffic (use for readiness probes)
- `:9000/health/live` — process alive (liveness probes)
- `:9000/metrics` — Prometheus format

Keep port 9000 internal.

### Dev vs prod, side by side

| | this lab | production |
|---|---|---|
| Command | `start-dev` | `start --optimized` |
| Image | stock | custom, pre-built |
| TLS | none | required |
| `KC_HOSTNAME_STRICT` | `false` | `true` |
| `sslRequired` | `none` | `all` |
| Admin | `admin`/`admin` | named account + MFA |
| Secrets | in files | secret manager |
| Replicas | 1 | 2+ with Infinispan |
| Revoke refresh token | off | on |

## Operating the container

```bash
docker compose up -d                  # start
docker compose logs -f keycloak       # watch (startup errors live here)
docker compose ps                     # health status
docker compose restart keycloak       # restart, keeping data
docker compose down                   # stop, keeping data
docker compose down -v                # stop and WIPE (needed to re-import a realm)
docker compose exec keycloak bash    # shell inside

# admin CLI from inside the container
docker compose exec keycloak /opt/keycloak/bin/kcadm.sh config credentials \
  --server http://localhost:8080 --realm master --user admin --password admin
docker compose exec keycloak /opt/keycloak/bin/kcadm.sh get realms/dotnet-lab
```

Startup takes ~40 seconds on a cold database (schema migration) and ~20 after.
`start --optimized` cuts that to a few seconds.

## Next

→ [07-dotnet-integration.md](07-dotnet-integration.md) — how the .NET code maps
onto all of this.
