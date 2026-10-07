# 5. Keycloak Concepts

Docs 1–4 were about the standards. This one is about Keycloak's particular
vocabulary, and the handful of places where its defaults will bite you.

## Realms

A **realm** is an isolated universe of users, clients, roles, and keys. Users in
one realm cannot log into another. Each realm has its own signing keys, so
tokens from realm A are rejected by services trusting realm B — that is exactly
what `ValidateIssuer` enforces.

```
Keycloak instance
├── master realm        ← administers Keycloak itself. Do not put apps here.
├── dotnet-lab realm    ← this lab
│   ├── users: alice, bob
│   ├── clients: dotnet-webapp, dotnet-api, playground-cli
│   ├── roles: lab-user, lab-admin
│   └── signing keys (its own)
└── (other realms)
```

**Rule: never put applications in `master`.** It exists to administer Keycloak.
One realm per security boundary — usually one per environment or tenant.

The realm name is in every URL and every `iss` claim:

```
http://localhost:8080/realms/dotnet-lab/.well-known/openid-configuration
                              ^^^^^^^^^^
```

## Clients

A **client** is an application that talks to Keycloak. The lab has three, chosen
to illustrate the three shapes you will meet.

### `dotnet-webapp` — confidential client

```jsonc
{
  "clientId": "dotnet-webapp",
  "publicClient": false,                  // has a secret → "confidential"
  "secret": "webapp-secret-do-not-use-in-prod",
  "standardFlowEnabled": true,            // Authorization Code flow: ON
  "directAccessGrantsEnabled": false,     // password grant: OFF (good)
  "redirectUris": ["http://localhost:5002/signin-oidc"],
  "attributes": { "pkce.code.challenge.method": "S256" }
}
```

Server-side app, so it can keep a secret. `standardFlowEnabled` is Keycloak's
name for "Authorization Code flow allowed".

### `dotnet-api` — the resource server

```jsonc
{
  "clientId": "dotnet-api",
  "standardFlowEnabled": false,        // never runs a browser flow
  "directAccessGrantsEnabled": false,
  "serviceAccountsEnabled": true,      // CAN get its own token (client credentials)
  "redirectUris": []                   // nothing to redirect to
}
```

The API never logs anyone in, so every interactive flow is disabled. It exists as
a client for two reasons: to be a valid **audience** for tokens, and to own
**client roles** like `reports.read`.

### `playground-cli` — public client

```jsonc
{
  "clientId": "playground-cli",
  "publicClient": true,                 // no secret
  "directAccessGrantsEnabled": true     // password grant: ON (teaching only!)
}
```

Exists only so `curl` can fetch a token in one command. **Password grant is
deprecated** — see [doc 1](01-oauth2-fundamentals.md#resource-owner-password-credentials-aka-the-password-grant-deprecated).

### Confidential vs public

| | Confidential | Public |
|---|---|---|
| Can keep a secret? | yes | no |
| Examples | ASP.NET Core server app, backend service | SPA, mobile, desktop, CLI |
| Authenticates with | client secret (or mTLS / signed JWT) | PKCE only |
| PKCE | recommended | **mandatory** |

The test is simple: **could an attacker extract the secret from a copy of the
app?** A JavaScript bundle or a mobile binary ships to the user, so anything in
it is public. Prefer `client_jwt` (private key JWT) over a shared secret for
confidential clients in production.

## Roles

Keycloak has two kinds, and the distinction shows up directly in the token.

### Realm roles — global to the realm

```jsonc
"realm_access": { "roles": ["lab-admin", "lab-user"] }
```

Use for organisation-wide facts: `employee`, `contractor`, `admin`.

### Client roles — scoped to one client

```jsonc
"resource_access": {
  "dotnet-api": { "roles": ["reports.read"] }
}
```

Use for permissions that only mean something inside one application.
`reports.read` on `dotnet-api` is unrelated to a `reports.read` on some other
client, which is the point — two teams can both define `admin` without colliding.

### Composite roles

A role that contains other roles. `default-roles-dotnet-lab` is one: it bundles
`offline_access`, `uma_authorization`, and some `account` client roles, and is
granted to every new user. The lab's users include it explicitly, which is why
they can request `offline_access`.

### In the lab

| | alice | bob |
|---|---|---|
| `lab-user` (realm) | ✅ | ✅ |
| `lab-admin` (realm) | ✅ | ❌ |
| `dotnet-api:reports.read` (client) | ✅ | ❌ |

Log in as each and call `/api/admin`. Alice gets 200, bob gets **403** — not 401.
He is perfectly authenticated; he is just not permitted. That distinction is the
whole lesson of [doc 7](07-dotnet-integration.md#401-vs-403).

## Client scopes and protocol mappers

This is the machinery that decides **what ends up in the token**, and where
Keycloak's defaults most often surprise people.

- A **protocol mapper** is a rule: "put X into the token." Map a user attribute
  to a claim, add an audience, include roles, flatten something.
- A **client scope** is a reusable bundle of mappers. The built-in `email` scope
  contains mappers for `email` and `email_verified`.
- **Default client scopes** are always applied. **Optional** ones apply only when
  the client requests them by name.

### The audience trap

**This is the number one Keycloak + API integration failure, so read this twice.**

By default, a Keycloak access token's `aud` does **not** contain your API. You
get something like:

```jsonc
"aud": ["account"]           // your API is nowhere to be seen
```

So your API, correctly configured with `ValidateAudience = true`, rejects every
token with a 401 — and the message (`IDX10214: Audience validation failed`) does
not obviously point at Keycloak.

At this point there are two paths, and people usually take the wrong one:

- ❌ **Set `ValidateAudience = false`.** Now *any* token from this realm works
  against your API, including one minted for a completely different, less trusted
  client. You have removed a real defence to silence a config error.
- ✅ **Add an audience mapper**, so Keycloak names your API as an intended
  recipient.

The lab does the latter. Each client that needs to call the API carries:

```jsonc
"protocolMappers": [{
  "name": "dotnet-api-audience",
  "protocolMapper": "oidc-audience-mapper",
  "config": {
    "included.client.audience": "dotnet-api",
    "access.token.claim": "true",
    "id.token.claim": "false"        // audiences belong in ACCESS tokens
  }
}]
```

Result — verify it yourself with `./scripts/get-token.sh alice`:

```jsonc
"aud": ["dotnet-api", "account"]     // now the API accepts it
```

In the admin console this lives at **Clients → <client> → Client scopes →
<client>-dedicated → Add mapper → By configuration → Audience**.

### Adding your own claim

To put a user attribute (say `department`) into tokens:

1. **Realm settings → User profile** — declare the `department` attribute
2. **Users → alice → Attributes** — set a value
3. **Clients → dotnet-webapp → Client scopes → dotnet-webapp-dedicated →
   Add mapper → By configuration → User Attribute**
   - User Attribute: `department`, Token Claim Name: `department`
   - tick **Add to access token** and **Add to ID token**

The claim then appears in the token, and in .NET as
`User.FindFirst("department")?.Value`. Visible immediately on `/api/claims`.

## Token lifetimes

Set at **Realm settings → Tokens** (and overridable per client). The lab's values:

| Setting | Value | Meaning |
|---|---|---|
| Access Token Lifespan | **5 min** | how long an access token is accepted |
| SSO Session Idle | **30 min** | log out after this much inactivity |
| SSO Session Max | **10 h** | hard cap regardless of activity |
| Refresh token lifetime | = SSO Session Idle | refresh resets the idle clock |
| Revoke Refresh Token | **off** | old refresh tokens keep working |

Short access tokens are the lever that makes revocation meaningful. The cost is
more refresh calls; Keycloak handles that volume easily.

**Watch out:** a refresh token's practical life is governed by *SSO Session Idle*,
not by any "refresh token lifespan" field. Idle out and refresh fails even though
the token has not formally expired.

## Keycloak's URLs

Everything hangs off `/realms/{realm}/protocol/openid-connect/`:

| Endpoint | Purpose |
|---|---|
| `/auth` | start the browser flow (front channel) |
| `/token` | exchange code/refresh for tokens (back channel) |
| `/userinfo` | fetch user claims with an access token |
| `/certs` | **JWKS** — public keys. Your API fetches this. |
| `/logout` | end the SSO session |
| `/token/introspect` | ask Keycloak whether a token is still valid |

Plus `/realms/{realm}/.well-known/openid-configuration`, which lists all of the
above. Never hardcode these; let discovery find them.

### Introspection — for when offline validation is not enough

Local JWT validation cannot know about a revocation that happened 30 seconds ago.
Introspection asks Keycloak directly:

```bash
curl -s -X POST \
  http://localhost:8080/realms/dotnet-lab/protocol/openid-connect/token/introspect \
  -u dotnet-api:api-secret-do-not-use-in-prod \
  -d token="$ACCESS_TOKEN" | jq .
```

`{"active": true, ...}` or just `{"active": false}`.

This is the **opposite trade-off** to JWT validation: perfectly current, but a
network call on every request, and it makes Keycloak a hard dependency of every
API call. Use it for high-value operations (a payment, a permission change), not
for every request. Short token lifetimes are the cheaper answer most of the time.

## Admin console tour

<http://localhost:8080/admin> — `admin` / `admin`. Switch to **dotnet-lab**
(realm selector, top left) and look at:

- **Clients → dotnet-webapp → Settings** — the flows, redirect URIs, PKCE
- **Clients → dotnet-webapp → Client scopes → Evaluate** — ⭐ the best tool in
  Keycloak. Pick a user, see exactly which claims their token would contain and
  why. Use this *before* debugging your app.
- **Realm settings → Keys** — the RSA keypair; `kid` matches your token headers
- **Realm settings → Sessions / Tokens** — all the lifetimes
- **Sessions** — who is logged in; sign them out and watch refresh fail

## Next

→ [06-containerizing-keycloak.md](06-containerizing-keycloak.md) — the compose
file, and getting from `start-dev` to production.
