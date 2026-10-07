# 1. OAuth 2.0 Fundamentals

> **The one-sentence version:** OAuth 2.0 is a *delegation* protocol. It lets an
> application act on your behalf against some service, without that application
> ever learning your password.

## The problem it solves

Imagine a photo-printing site that wants to fetch your photos from a cloud drive.

**The bad old way** — you give the printing site your cloud password. Now:

- the printing site can read your email too, and delete your files
- revoking access means changing your password, which breaks every other app
- the printing site now stores your password, and will eventually be breached
- two-factor authentication is impossible

The password is a *universal* credential. You needed a *narrow* one.

OAuth 2.0 introduces exactly that: a token that says **"the bearer may read
Kiko's photos, for the next hour, and nothing else."**

## The four roles

Every OAuth 2.0 conversation has these four parties. Internalise them, because
every spec and error message assumes you know which one you are.

| Role | Who | In this lab |
|---|---|---|
| **Resource Owner** | the human who owns the data | you, logging in as `alice` |
| **Client** | the app that wants access | `src/WebApp` |
| **Authorization Server** | issues tokens; the only party that sees the password | Keycloak |
| **Resource Server** | holds the data, accepts tokens | `src/Api` |

The crucial separation: **the Client never sees the password.** The user types it
into the Authorization Server's own page, on the Authorization Server's own
domain. That is why login redirects you to `localhost:8080` and not a form
inside our app.

```
   ┌──────────┐                                   ┌────────────────────┐
   │  Browser │◄──── 2. login page ──────────────►│ Authorization Srv  │
   │  (Alice) │      (password typed HERE)        │     (Keycloak)     │
   └────┬─────┘                                   └─────────┬──────────┘
        │ 1. "let me in"                                    │
        │ 3. redirect back with a one-time CODE             │
        ▼                                                   │
   ┌──────────┐                                             │
   │  Client  │──── 4. code + client secret ────────────────┘
   │ (WebApp) │◄─── 5. access token + refresh token
   └────┬─────┘
        │ 6. GET /api/whoami
        │    Authorization: Bearer eyJ...
        ▼
   ┌──────────────┐
   │ Resource Srv │  7. verifies the token's signature locally. No call to
   │    (Api)     │     Keycloak. Returns the data.
   └──────────────┘
```

## What OAuth 2.0 is *not*

This is the single most common misunderstanding, so it gets its own section.

**OAuth 2.0 is not an authentication protocol.** It was designed to answer
*"is this app allowed to do this?"*, not *"who is this person?"*

An access token is like a hotel key card. It opens room 412. It does **not**
tell the door who you are, and the door does not care — possession is the whole
argument. That is what "Bearer" means in `Authorization: Bearer ...`.

So if you build a login button on OAuth 2.0 alone, you hit real problems:

- there is no standard way to ask "who is the user?" — every provider differs
- there is no standard format for the answer
- worst of all, an access token proves nothing about *who obtained it*. An app
  can take a token it was given for another purpose and present it as proof of
  identity. This is the **confused deputy** problem, and it was a real
  vulnerability in early "Login with ..." buttons.

**OpenID Connect** is the layer that fixes this. See
[02-openid-connect.md](02-openid-connect.md).

## Grant types (how you get a token)

A "grant type" is a strategy for obtaining a token. Picking the wrong one is the
most consequential mistake in an OAuth integration, so here is the whole menu
with a verdict on each.

### Authorization Code (+ PKCE) — use this one

The browser redirect dance above. The user authenticates at the Authorization
Server, the app receives a short-lived single-use **code**, and exchanges that
code for tokens over a direct back-channel call.

Use it for: **web apps, SPAs, mobile apps, desktop apps.** Essentially
everything with a human in front of it.

Why it wins: tokens never travel through the browser's URL, history, or
`Referer` headers. Only the disposable code does, and PKCE makes a stolen code
useless. Covered in depth in
[04-authorization-code-pkce.md](04-authorization-code-pkce.md).

### Client Credentials — for machines

No user at all. A service authenticates as *itself* with its own id and secret.

```
POST /token
grant_type=client_credentials&client_id=batch-job&client_secret=...
```

Use it for: cron jobs, service-to-service calls, daemons. The token represents
the *application*, not a person: its `sub` points at a Keycloak **service
account** (`preferred_username: service-account-dotnet-api`), and there is no
human in the picture at all.

### Refresh Token — for continuation

Not a way to log in; a way to stay logged in. Trades a long-lived refresh token
for a fresh access token. See [03-tokens-explained.md](03-tokens-explained.md).

### Resource Owner Password Credentials, aka the password grant (deprecated)

The app collects the username and password itself and posts them to the token
endpoint.

```
POST /token
grant_type=password&username=alice&password=password&client_id=playground-cli
```

This throws away the entire point of OAuth: the client sees the password again.
It also cannot support MFA, social login, or a consent screen. The OAuth 2.1
draft **removes it**.

**It is enabled in this lab on the `playground-cli` client only**, because it is
the shortest way to get a real token into a `curl` command for teaching. Never
use it in an application.

### Implicit — dead

Returned the access token directly in the URL fragment, so the token landed in
browser history and server logs. Existed only because browsers once lacked CORS.
Fully superseded by Authorization Code + PKCE. **Do not use it.**

### Device Authorization — for input-constrained devices

The "go to example.com/device and type CODE-1234" flow you see on smart TVs. The
device polls the token endpoint while you authenticate on your phone.

### Summary table

| Grant | Use it? | For what |
|---|---|---|
| Authorization Code + PKCE | ✅ yes | anything with a user |
| Client Credentials | ✅ yes | machine-to-machine |
| Refresh Token | ✅ yes | staying logged in |
| Device Authorization | ✅ yes | TVs, CLIs, no browser |
| Password (ROPC) | ❌ no | legacy migration only |
| Implicit | ❌ never | nothing |

## Scopes: what the *app* asked for

A scope is a label on the token naming a category of access the **client**
requested — `openid`, `profile`, `email`, `photos.read`.

Scopes are about the *application's* permissions, not the user's. Keep the two
axes separate in your head:

- **Scope** — "this app may read photos." Chosen by the app, consented to by the user.
- **Role / permission** — "Alice is an administrator." A property of the user.

A token can carry the `photos.read` scope while its user has no photos. Both
checks matter, and they are different questions. The lab's API demonstrates both:
`RequireProfileScope` checks a scope, `RequireLabAdmin` checks a role.

## Key terms

| Term | Meaning |
|---|---|
| **Bearer token** | a credential where possession = authority. No proof of identity needed. |
| **Front channel** | via browser redirects. Visible to the user, loggable, untrusted. |
| **Back channel** | direct server-to-server HTTP. Not visible to the browser. Trusted. |
| **Confidential client** | can keep a secret (server-side app). |
| **Public client** | cannot keep a secret (SPA, mobile). Must use PKCE. |
| **Redirect URI** | where the Authorization Server may send the user back. Exact-matched, to stop code theft. |

## Next

→ [02-openid-connect.md](02-openid-connect.md) — the identity layer on top.
