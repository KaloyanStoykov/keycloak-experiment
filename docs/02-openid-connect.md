# 2. OpenID Connect (OIDC)

> **The one-sentence version:** OIDC is a thin, standardised identity layer on
> top of OAuth 2.0. OAuth answers *"may this app do that?"*; OIDC adds
> *"and who is the user?"*

## Why it had to exist

[Doc 1](01-oauth2-fundamentals.md) ended on the problem: an access token is a
hotel key card. It grants access but asserts nothing about identity, and if you
build a login button on one you are vulnerable to the confused deputy problem —
App A's token replayed to App B as "proof" of who you are.

Before 2014, every provider invented its own fix. Integrating four "Login with"
buttons meant learning four proprietary identity APIs.

OIDC standardised three things, and that is essentially the whole specification:

1. **The `id_token`** — a signed, structured statement about *who* logged in
2. **The `/userinfo` endpoint** — a standard place to fetch more user attributes
3. **Discovery** — a standard place to find out everything else automatically

## 1. The id_token

The heart of OIDC. Request the `openid` scope and the token endpoint returns an
extra token alongside the access token.

```jsonc
{
  "iss": "http://localhost:8080/realms/dotnet-lab",  // who is asserting this
  "sub": "9940970c-03d1-4319-9b84-4c9955496b89",     // WHICH user (stable id)
  "aud": "dotnet-webapp",                            // who this claim is FOR
  "exp": 1759601234,                                 // don't trust after this
  "iat": 1759600934,                                 // issued at
  "auth_time": 1759600934,                           // when they actually typed the password
  "nonce": "639267348089164174...",                  // ties it to MY login request
  "preferred_username": "alice",
  "email": "alice@example.com",
  "email_verified": true,
  "name": "Alice Admin"
}
```

### The two differences that matter

Both of these are what make an `id_token` safe for login where an access token
is not:

**`aud` is the client, not an API.** The `id_token`'s audience is
`dotnet-webapp` — *this app*. If App A hands its `id_token` to App B, App B
checks `aud`, sees the wrong client id, and rejects it. The confused deputy
problem is closed by construction.

**`nonce` ties it to one specific login attempt.** Our app generated a random
`nonce`, sent it in the authorization request, and Keycloak copied it into the
`id_token`. If the `nonce` coming back is not the one we sent, this token is a
replay of somebody else's login and we discard it. ASP.NET Core does this check
for you — that is what the `.AspNetCore.OpenIdConnect.Nonce.*` cookie is for.

### The rule that follows

> **Use the `id_token` to log the user in. Use the `access_token` to call APIs.
> Never swap them.**

Sending an `id_token` to an API is a classic bug. A correctly configured API
rejects it, because `aud` names a client rather than the API. (If your API
*accepts* it, your API is not validating `aud` — fix that first.)

### `sub` is the user's primary key

`sub` ("subject") is opaque, unique within the issuer, and **immutable**. Alice
can change her username, email, and name; `sub` never changes.

So: store `sub` in your database as the foreign key to the user. Never key on
email or username — people change them, and they get reassigned. This is a
migration you only want to do once.

## 2. The /userinfo endpoint

`GET /userinfo` with an access token returns the user's claims as JSON.

Why have it if the `id_token` already carries claims? Because tokens travel in
headers and cookies, and size matters. The pattern is a small `id_token` with
just enough to identify the user, then `/userinfo` on demand for the rest.

In this lab `GetClaimsFromUserInfoEndpoint = true`, so you can watch the extra
request happen in the logs. For most apps it is unnecessary — the `id_token`
already has what you need.

## 3. Discovery — the best part

Every OIDC provider publishes its configuration at a well-known URL:

```
{issuer}/.well-known/openid-configuration
```

Try it right now:

```bash
curl -s http://localhost:8080/realms/dotnet-lab/.well-known/openid-configuration | jq .
```

Abbreviated output from this lab:

```jsonc
{
  "issuer":                 "http://localhost:8080/realms/dotnet-lab",
  "authorization_endpoint": ".../protocol/openid-connect/auth",
  "token_endpoint":         ".../protocol/openid-connect/token",
  "userinfo_endpoint":      ".../protocol/openid-connect/userinfo",
  "jwks_uri":               ".../protocol/openid-connect/certs",
  "end_session_endpoint":   ".../protocol/openid-connect/logout",
  "introspection_endpoint": ".../protocol/openid-connect/token/introspect",
  "grant_types_supported":  ["authorization_code", "refresh_token", "client_credentials", ...],
  "id_token_signing_alg_values_supported": ["RS256", ...]
}
```

**This is why you configure one `Authority` string in .NET instead of six URLs.**
Both apps in this lab set only:

```csharp
options.Authority = "http://localhost:8080/realms/dotnet-lab";
```

and ASP.NET Core fetches this document to learn the rest — including `jwks_uri`,
where it downloads the public keys used to verify signatures. It caches the
result and re-fetches periodically, which is how **key rotation** works without
anybody redeploying anything.

## The scopes OIDC defines

| Scope | Gets you |
|---|---|
| `openid` | **required** — this is what makes the request OIDC. No `openid`, no `id_token`. |
| `profile` | `name`, `family_name`, `given_name`, `preferred_username`, `picture`, … |
| `email` | `email`, `email_verified` |
| `address` | the address claim |
| `phone` | `phone_number`, `phone_number_verified` |
| `offline_access` | a refresh token that outlives the browser session |

## OAuth 2.0 vs OIDC, side by side

| | OAuth 2.0 | OpenID Connect |
|---|---|---|
| Question answered | may this app do X? | who is this user? |
| Called | authorization | authentication |
| Tokens | `access_token`, `refresh_token` | adds `id_token` |
| Token format | unspecified (often opaque) | `id_token` is always a JWT |
| Claims | not standardised | standardised |
| Discovery | none | `/.well-known/openid-configuration` |
| Relationship | the foundation | a strict superset |

OIDC is not an alternative to OAuth 2.0 — it *is* OAuth 2.0, plus an `id_token`,
plus naming conventions. Every OIDC flow is a valid OAuth 2.0 flow.

## Terminology note

OIDC renames two OAuth roles. Specs and vendor docs mix them freely:

| OAuth 2.0 | OIDC | Here |
|---|---|---|
| Client | **Relying Party (RP)** | `src/WebApp` |
| Authorization Server | **OpenID Provider (OP)** | Keycloak |

## Next

→ [03-tokens-explained.md](03-tokens-explained.md) — JWT anatomy, and why
refresh tokens exist.
