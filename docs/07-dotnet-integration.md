# 7. The .NET Integration

A guided reading of the code. Each section names the file so you can read them
side by side.

## Two projects, two completely different jobs

| | `src/WebApp` | `src/Api` |
|---|---|---|
| OAuth role | Client / Relying Party | Resource Server |
| NuGet package | `Microsoft.AspNetCore.Authentication.OpenIdConnect` | `Microsoft.AspNetCore.Authentication.JwtBearer` |
| Authenticated by | a **cookie** | a **bearer token** |
| Runs a login flow | yes | never |
| Talks to Keycloak | at login and refresh | once, to fetch public keys |
| Holds a secret | yes | only for introspection |

The thing to internalise:

```
Browser ──cookie──► WebApp ──Bearer token──► Api
```

**Two separate security contexts.** The cookie authenticates the user *to the
WebApp*. The token authenticates the WebApp *acting for the user* to the API. The
browser never sees a token.

## The API: JWT bearer validation

`src/Api/Program.cs`

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "http://localhost:8080/realms/dotnet-lab";
        options.Audience  = "dotnet-api";
        options.RequireHttpsMetadata = false;   // dev only
        ...
    });
```

`Authority` is the only URL you configure. On the first request the handler
fetches `{Authority}/.well-known/openid-configuration`, reads `jwks_uri` from it,
downloads the public keys, and caches them. Everything after that is local.

**The API makes no network call per request.** Validating a JWT is a signature
check — pure arithmetic against a cached public key. This is the central
performance property of JWT-based auth, and the reason people choose it over
session lookups.

### The five validation parameters

```csharp
options.TokenValidationParameters = new TokenValidationParameters
{
    ValidateIssuerSigningKey = true,                    // 1
    ValidateIssuer   = true, ValidIssuer   = authority, // 2
    ValidateAudience = true, ValidAudience = audience,  // 3
    ValidateLifetime = true,                            // 4
    ClockSkew = TimeSpan.FromSeconds(30),               // 5
};
```

1. **Signature** — did Keycloak mint this, unaltered? The one check you can never
   turn off.
2. **Issuer** — is `iss` our realm? Blocks a valid token from a *different* realm
   being replayed here.
3. **Audience** — does `aud` include `dotnet-api`? Blocks a token legitimately
   issued for some *other* API. Requires Keycloak's audience mapper —
   [the audience trap](05-keycloak-concepts.md#the-audience-trap).
4. **Lifetime** — is `exp` in the future?
5. **Clock skew** — the default is **5 minutes**, which quietly extends every
   token's life. Tightened here so expiry is observable.

> **Never set `ValidateAudience = false` to make a 401 go away.** It is the
> tempting fix and it removes a real defence. Add the audience mapper instead.

### Flattening Keycloak's roles

`src/Api/KeycloakRoles.cs`

Keycloak nests roles; ASP.NET Core expects them flat. Without a translation step
`[Authorize(Roles = "lab-admin")]` silently matches nothing — no error, just 403
for everybody.

```jsonc
// what Keycloak sends
"realm_access":    { "roles": ["lab-admin", "lab-user"] }
"resource_access": { "dotnet-api": { "roles": ["reports.read"] } }
```

```csharp
// what we do about it, in OnTokenValidated (after validation has passed)
options.Events = new JwtBearerEvents
{
    OnTokenValidated = context =>
    {
        if (context.Principal?.Identity is ClaimsIdentity identity)
            foreach (var role in KeycloakRoles.Extract(context.Principal, audience))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        return Task.CompletedTask;
    },
};
```

`KeycloakRoles.Extract` parses both shapes. Note it only pulls client roles for
*our own* client id — another API's roles are none of our business.

### ⚠ The claim-renaming gotcha

```csharp
options.MapInboundClaims = false;
```

This one wastes an hour of everybody's life exactly once.

By default .NET "maps inbound claims": it rewrites well-known JWT claim names
into legacy WS-Federation URIs. So `sub` becomes
`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`, and
`email` becomes something similarly enormous. Your perfectly reasonable
`User.FindFirst("sub")` returns `null`, and nothing anywhere says why.

With mapping off, claims keep the names Keycloak gave them. Compare for yourself:
flip the line and watch `/api/claims` change.

### 401 vs 403

```
401 Unauthorized  →  "I do not know who you are."
                     Missing, malformed, expired, wrong issuer, wrong audience.
                     A statement about the TOKEN.

403 Forbidden     →  "I know exactly who you are, and the answer is no."
                     Valid token, insufficient permissions.
                     A statement about PERMISSIONS.
```

ASP.NET Core picks correctly based on whether authentication succeeded. So the
code is a diagnostic: **if you expected 403 and got 401, your token was rejected
— stop looking at your authorization policies.**

Prove it:

```bash
./scripts/call-api.sh bob /api/admin      # 403 - valid token, wrong role
./scripts/call-api.sh --bad /api/admin    # 401 - token rejected outright
```

### Surfacing the rejection reason

```csharp
OnAuthenticationFailed = context =>
{
    context.Response.Headers["X-Auth-Failure-Reason"] = ...;
    return Task.CompletedTask;
},
```

A bare 401 tells you nothing, and the real reason is otherwise buried in logs.
This echoes it in a response header, which is a gift while learning.

**Remove it before production** — it tells an attacker precisely which check they
failed.

### Authorization policies

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireLabAdmin",    p => p.RequireRole("lab-admin"));     // realm role
    options.AddPolicy("RequireReportsRead", p => p.RequireRole("reports.read"));  // client role
    options.AddPolicy("RequireProfileScope", p => p.RequireAssertion(ctx =>
        ctx.User.FindFirst("scope")?.Value.Split(' ').Contains("profile") == true));
});
```

The third shows why scopes need special handling: Keycloak returns them as **one
space-delimited string**, not as repeated claims. `RequireClaim("scope", "profile")`
would compare against the whole `"openid profile email"` string and fail. Split
it first.

### Middleware order

```csharp
app.UseAuthentication();   // read and validate the token → build the identity
app.UseAuthorization();    // judge that identity against the policy
```

Reversed, authorization runs against an empty identity and everything 403s. This
is a classic silent bug: no exception, just a mysteriously locked-out API.

## The WebApp: cookie + OIDC

`src/WebApp/Program.cs`

### Two schemes, two jobs

```csharp
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
```

- **`DefaultScheme = Cookies`** — how an already-logged-in user is recognised on
  every request. Keycloak is not involved.
- **`DefaultChallengeScheme = OpenIdConnect`** — what "please log in" *means*:
  start the redirect dance.

That split is the pattern. OIDC runs **once**, at login. The cookie carries every
request after it.

### The OIDC options that matter

```csharp
options.ResponseType = OpenIdConnectResponseType.Code;  // Authorization Code flow
options.UsePkce = true;                                 // (default in .NET 9)
options.ResponseMode = OpenIdConnectResponseMode.FormPost;
options.SaveTokens = true;                              // ← see below
options.MapInboundClaims = false;
options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
```

### `SaveTokens` — the piece everyone forgets

Without it, ASP.NET Core validates the `id_token`, builds the cookie, and
**throws the tokens away**. You are logged in and cannot call a single API,
because you have no access token. The symptom is bewildering: login works
perfectly, every API call is 401.

With it, the tokens live inside the encrypted cookie:

```csharp
var accessToken  = await ctx.GetTokenAsync("access_token");
var refreshToken = await ctx.GetTokenAsync("refresh_token");
var expiresAt    = await ctx.GetTokenAsync("expires_at");
```

The cost is cookie size. Three JWTs exceed the browser's ~4KB limit, so ASP.NET
Core chunks the cookie into `WebApp.SessionC1`, `C2`, … It is transparent, but it
is a reason to consider a server-side ticket store in production
([doc 10](10-production-checklist.md)).

### `SameSite=Lax`, not `Strict`

```csharp
options.Cookie.SameSite = SameSiteMode.Lax;
```

`Strict` looks more secure and **breaks the login flow**. The return from
Keycloak is a cross-site navigation, so `Strict` tells the browser to withhold
the correlation cookie, and ASP.NET Core then cannot match the `state` — you get
"Correlation failed". `Lax` sends cookies on top-level navigations, which is
exactly what an OIDC return is.

### Calling the API

```csharp
var accessToken = await ctx.GetTokenAsync("access_token");
request.Headers.Authorization = new("Bearer", accessToken);
```

Those two lines are the entire point of everything before them.

"Bearer" means possession is sufficient — whoever holds it can use it. Which is
exactly why access tokens expire in 5 minutes.

### Refreshing

`src/WebApp/TokenRefresher.cs`

The refresh grant is an unglamorous form POST:

```csharp
var form = new Dictionary<string, string>
{
    ["grant_type"]    = "refresh_token",
    ["refresh_token"] = refreshToken,
    ["client_id"]     = clientId,
    ["client_secret"] = clientSecret,   // confidential client; a SPA omits this
};
```

And then the step that is easy to miss — **write the new tokens back**:

```csharp
auth.Properties.StoreTokens([ /* new access, refresh, id, expires_at */ ]);
await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                      auth.Principal, auth.Properties);
```

Refresh without re-issuing the cookie and the next request cheerfully reuses the
old expired token. The refresh "worked" and nothing improved.

Three details worth noting:

- **Keycloak returns a new refresh token.** Store the one you just received.
- `expires_at` must round-trip in the format the handler expects (`"o"`).
- A failed refresh means **the session is over** — expired, revoked, idled out,
  or rotated. There is nothing to retry; send the user back through login.

In the lab this is a button on `/tokens` so you can watch it. In production it
should be automatic — see below.

### Logout: two kinds, and the first one surprises people

```csharp
// local only - clears OUR cookie
app.MapGet("/logout-local", async ctx => {
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// full - clears our cookie AND ends the Keycloak SSO session
app.MapGet("/logout", async ctx => {
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
});
```

Try `/logout-local`, then log in again: **no password prompt.** The Keycloak SSO
session is still alive, so it re-authenticates you silently.

That is single sign-on working correctly, and it is also why local-only logout is
usually the wrong logout. Users who click "log out" expect to be logged out.

The full version redirects to Keycloak's `end_session_endpoint` with the
`id_token` as `id_token_hint`, so Keycloak knows which session to end.

## Doing refresh properly in production

The lab's manual button is for observing the mechanism. Real apps refresh
transparently. Three options, roughly in order of how much you should like them:

**1. A `DelegatingHandler` that retries on 401** — refresh once, replay the
request. Self-contained and easy to reason about.

**2. Refresh proactively in `CookieAuthenticationEvents.OnValidatePrincipal`** —
check `expires_at` on each request and refresh when it is nearly due. Keeps the
cookie always-fresh; be careful with concurrent requests triggering parallel
refreshes.

**3. Use a library.** [Duende.AccessTokenManagement](https://github.com/DuendeSoftware/foss)
handles refresh, caching, concurrency and client credentials properly. For
production, prefer this over hand-rolling.

The lab hand-rolls it on purpose — you cannot learn the mechanism from a library
that hides it.

## Next

→ [08-hands-on-walkthrough.md](08-hands-on-walkthrough.md) — the exercises.
