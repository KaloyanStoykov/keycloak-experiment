# 8. Hands-On Walkthrough

Twelve exercises, roughly 45 minutes. Each has a **prediction** step — commit to
an answer before you run it. Being wrong is the part that teaches you something.

## Setup

Three terminals:

```bash
# Terminal 1 - Keycloak
docker compose up -d
docker compose logs -f keycloak        # wait for "started in ..."

# Terminal 2 - the API
dotnet run --project src/Api

# Terminal 3 - the web app
dotnet run --project src/WebApp
```

| | URL |
|---|---|
| Web app | <http://localhost:5002> |
| API | <http://localhost:5003> |
| Keycloak admin | <http://localhost:8080/admin> — `admin` / `admin` |

Users: `alice` / `password` (has `lab-admin`), `bob` / `password` (does not).

---

## Exercise 1 — Read the discovery document

```bash
curl -s http://localhost:8080/realms/dotnet-lab/.well-known/openid-configuration | jq .
```

**Find:** `issuer`, `authorization_endpoint`, `token_endpoint`, `jwks_uri`,
`grant_types_supported`.

**The point:** this one document is why both apps configure a single `Authority`
string instead of six URLs. Everything else is discovered.

Now look at the keys your API trusts:

```bash
curl -s http://localhost:8080/realms/dotnet-lab/protocol/openid-connect/certs | jq '.keys[0] | {kid, alg, use}'
```

Keep that `kid`. You will see it again in Exercise 3.

---

## Exercise 2 — Watch the login flow

**Predict first:** how many HTTP requests between clicking "Log in" and seeing
your profile?

1. Open devtools → Network. Tick **Preserve log** (essential — you are about to
   navigate across origins).
2. Go to <http://localhost:5002>, click **Log in**.
3. Before typing anything, **read the URL bar.** Find `response_type`,
   `scope`, `state`, `nonce`, `code_challenge`, `code_challenge_method`.
4. Log in as `alice`.
5. Read the request list top to bottom.

**Find:** the request to `/signin-oidc` and its `Set-Cookie` headers. Notice
there are several (`WebApp.Session`, `...C1`, `...C2`) — the cookie is chunked
because three JWTs exceed the ~4KB browser limit.

**Confirm:** no token appears anywhere in the browser. Only cookies and a code.
Tokens were fetched on the back channel, which devtools cannot see.

Prefer it narrated? `./scripts/walk-the-flow.sh` does the same flow in the shell
and prints each step.

---

## Exercise 3 — Read your own tokens

Go to <http://localhost:5002/tokens>.

**Find in the access token:**

- `iss` — matches the `issuer` from Exercise 1?
- `aud` — does it contain `dotnet-api`? (If it did not, your API would 401
  everything — see [the audience trap](05-keycloak-concepts.md#the-audience-trap))
- `sub` — the opaque user id. **This is your database key**, not `preferred_username`.
- `exp` − `iat` = 300 seconds
- `realm_access.roles` and `resource_access` — note they are **nested**
- `kid` in the header — the same key from Exercise 1

**Compare the three tokens:**

| Look at | `access_token` | `id_token` | `refresh_token` |
|---|---|---|---|
| `aud` | `dotnet-api` | `dotnet-webapp` | the realm itself |
| `typ` | `Bearer` | `ID` | `Refresh` |
| `alg` | `RS256` | `RS256` | **`HS512`** |

**The question to sit with:** why is the refresh token signed with a *symmetric*
algorithm while the others are asymmetric?

<details><summary>Answer</summary>

Asymmetric (`RS256`) means anyone with the public key can verify — which is
exactly what you want for access tokens, because every API must validate them
independently, offline, with no call to Keycloak.

Symmetric (`HS512`) means only the holder of the shared secret can verify. The
only party that ever validates a refresh token is Keycloak itself, so there is
nothing to publish. The algorithm choice encodes who the intended verifier is.
</details>

---

## Exercise 4 — 401 vs 403

**Predict:** which status does each produce?

```bash
./scripts/call-api.sh alice /api/admin      # ?
./scripts/call-api.sh bob   /api/admin      # ?
./scripts/call-api.sh --none /api/admin     # ?
./scripts/call-api.sh --bad  /api/admin     # ?
```

<details><summary>Answers</summary>

- alice → **200**. Valid token, has `lab-admin`.
- bob → **403**. Valid token, no `lab-admin`. *Authenticated but not permitted.*
- no token → **401**. Nobody to identify.
- bad token → **401**, plus `X-Auth-Failure-Reason` naming the exact exception.
</details>

**The takeaway you will use at 2am:** the status code tells you where to look.

> **401 = your token was rejected. 403 = your policy rejected you.**
>
> So if you expected 403 and got 401, *stop debugging your authorization rules*
> — the token never got that far.

---

## Exercise 5 — Watch an access token expire

**Predict:** after 5 minutes, does `/api/whoami` still work?

1. Log in, open `/tokens`, note the countdown.
2. Wait 5 minutes.
3. Click `/api/whoami`.

**Expect:** 401, and `X-Auth-Failure-Reason: SecurityTokenExpiredException`.

4. Back to `/tokens` → **Exchange refresh token for a new access token**.
5. `/api/whoami` again → 200.

**The point:** that loop is what every OAuth library does for you silently. It is
a button here only so you can watch it.

Impatient? Drop **Realm settings → Tokens → Access Token Lifespan** to 1 minute.

---

## Exercise 6 — The refresh grant from the shell

```bash
./scripts/refresh-token.sh
```

**Find:** the access token's `iat`/`exp` move forward, the `jti` is new, and a
new refresh token comes back.

**Note:** no password anywhere in step 2. That is the entire purpose of a refresh
token.

Then try rotation:

1. Admin console → **Realm settings → Tokens → Revoke Refresh Token: On**, save.
2. Re-run the script.

**Expect:** step 4 now reports the old refresh token is dead. Keycloak treats
reuse as evidence of theft.

(Turn it back off for the remaining exercises, or leave it on — it is the more
secure setting.)

---

## Exercise 7 — Break the audience check

This reproduces the most common Keycloak+API failure on purpose, so you recognise
it instantly later.

1. Admin console → **Clients → playground-cli → Client scopes →
   playground-cli-dedicated**
2. Delete the `dotnet-api-audience` mapper.
3. Get a fresh token and look at it:

```bash
./scripts/get-token.sh alice | grep -A3 '"aud"'
./scripts/call-api.sh alice /api/whoami
```

**Expect:** `aud` no longer contains `dotnet-api`, and the API returns **401**
with `IDX10214: Audience validation failed`.

**Now the important part — the tempting wrong fix.** Open `src/Api/Program.cs`
and set `ValidateAudience = false`. It works again!

And you have just accepted any token from this realm, including one minted for a
less trusted client. That is a real privilege escalation path.

**Undo both changes:** restore `ValidateAudience = true`, and re-add the mapper
(**Add mapper → By configuration → Audience**, Included Client Audience =
`dotnet-api`), or just `docker compose down -v && docker compose up -d`.

---

## Exercise 8 — Wrong issuer

**Predict:** what happens if the API trusts a different realm?

In `src/Api/appsettings.json`, change the authority to a realm that does not
exist:

```jsonc
"Authority": "http://localhost:8080/realms/some-other-realm"
```

Restart the API and call it.

**Expect:** 401. The handler cannot even fetch the discovery document, so it has
no keys to verify with.

Now a subtler version — set it to `http://127.0.0.1:8080/realms/dotnet-lab`
(same server, different spelling).

**Expect:** still 401, `IDX10205: Issuer validation failed`. `127.0.0.1` and
`localhost` are the same machine but not the same *string*, and issuer validation
is an exact string comparison.

**This is the hostname trap from
[doc 6](06-containerizing-keycloak.md#the-hostname-and-issuer-trap-read-this-one)**, and it is
why `KC_HOSTNAME` matters so much in Docker.

Revert when done.

---

## Exercise 9 — The two logouts

1. Log in as alice.
2. Click **Logout (local only)**.
3. Click **Log in** again.

**What happened?** No password prompt. You were logged straight back in.

Your cookie was cleared, but Keycloak's SSO session was not, so Keycloak
re-authenticated you silently. **That is single sign-on working correctly** — and
it is also why local-only logout is usually the wrong logout. A user who clicks
"log out" on a shared machine expects to be logged out.

4. Now click **Logout (full)**. You are bounced to Keycloak's
   `end_session_endpoint`.
5. Click **Log in**.

**Expect:** the password prompt is back.

Watch it from the other side: admin console → **Sessions** shows the live
session, and it disappears after the full logout.

---

## Exercise 10 — Revoke a user mid-session

This is the exercise that shows you *why* access tokens are short-lived.

1. Log in as alice, confirm `/api/whoami` works.
2. Admin console → **Users → alice → Enabled: off**, save.
3. Immediately call `/api/whoami`.

**Expect: still 200.** The token is valid until `exp`, and the API validates it
**offline** — it has no idea anything changed.

4. Now click **Exchange refresh token**.

**Expect: failure.** Refreshing requires a live call to Keycloak, and Keycloak
says no.

**The design in one observation:** disabling a user does not take effect
instantly — it takes effect within one access token lifetime. Five minutes of
exposure, in exchange for zero per-request cost. That trade is the whole reason
tokens are short-lived.

Need instant revocation? That is what
[introspection](05-keycloak-concepts.md#introspection--for-when-offline-validation-is-not-enough)
is for, and it costs you a network call per request.

Re-enable alice.

---

## Exercise 11 — Add your own claim

1. **Realm settings → User profile → Create attribute**: name `department`,
   display name `Department`. Save.
2. **Users → alice → Attributes**: `department` = `engineering`. Save.
3. **Clients → playground-cli → Client scopes → playground-cli-dedicated →
   Add mapper → By configuration → User Attribute**
   - User Attribute: `department`
   - Token Claim Name: `department`
   - tick **Add to ID token** and **Add to access token**
4. Save, then:

```bash
./scripts/get-token.sh alice | grep department
```

**Expect:** `"department": "engineering"` in the payload.

Then read it in code — it is already visible via `/api/claims`:

```csharp
var dept = User.FindFirst("department")?.Value;
```

**Before you reach for this in production:** every claim you add makes every
token bigger, on every request. Claims are for data you need on *most* requests.
Everything else belongs in your own database, keyed by `sub`.

---

## Exercise 12 — Prove your realm is reproducible

The real test of configuration-as-code.

```bash
docker compose down -v        # -v wipes the volume: realm, users, sessions
docker compose up -d
docker compose logs -f keycloak | grep -i import
```

**Expect:** `Realm 'dotnet-lab' imported`, and everything works again with no
clicking.

**The trap this exercise exists to teach:** import only runs **if the realm does
not already exist**. Edit `keycloak/realm-dotnet-lab.json`, restart without
`-v`, and your change is silently ignored. Hours have been lost to this.

Captured something by clicking? Export it:

```bash
./scripts/export-realm.sh
diff <(jq -S . keycloak/exported/dotnet-lab-realm.json) \
     <(jq -S . keycloak/realm-dotnet-lab.json) | head -40
```

Note the export is ~2200 lines against ~200 hand-written. Exports include every
default, which makes them good for backup and poor for review.

---

## Where to go next

- **Add a role and a policy.** New realm role → assign to bob → new endpoint with
  `RequireRole`. The full loop in five minutes.
- **Try client credentials.** `dotnet-api` has `serviceAccountsEnabled`:
  ```bash
  curl -s -X POST http://localhost:8080/realms/dotnet-lab/protocol/openid-connect/token \
    -d grant_type=client_credentials -d client_id=dotnet-api \
    -d client_secret=api-secret-do-not-use-in-prod | jq -r .access_token \
    | ./scripts/decode-token.sh
  ```
  Note `preferred_username` is `service-account-dotnet-api`: the `sub` identifies
  a **service account**, not a human. Note too that `aud` does *not* include
  `dotnet-api` — the audience mapper lives on the *calling* clients, so a
  machine token needs its own mapper before an API will accept it.
- **Turn PAR back on.** Set `PushedAuthorizationBehavior.UseIfAvailable` in
  `src/WebApp/Program.cs` and watch the authorization URL collapse to a
  `request_uri` handle.
- **Make refresh automatic.** Implement the `DelegatingHandler` described in
  [doc 7](07-dotnet-integration.md#doing-refresh-properly-in-production).
- **Add a second client** and watch `azp` change while `sub` stays the same.

## Next

→ [09-troubleshooting.md](09-troubleshooting.md) — when something breaks.
