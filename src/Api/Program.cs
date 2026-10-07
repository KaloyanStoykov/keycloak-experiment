// ---------------------------------------------------------------------------
// THE RESOURCE SERVER
// ---------------------------------------------------------------------------
// In OAuth 2.0 terms this project is a "Resource Server": it owns data and
// only hands it over when presented with a valid access token.
//
// What it does NOT do, on purpose:
//   - it has no login page
//   - it never sees a password
//   - it never talks to Keycloak during a request
//
// That last point surprises people. Validating a JWT is an offline, local,
// signature-check operation. The API downloads Keycloak's public signing keys
// ONCE (from the JWKS endpoint), caches them, and from then on verifies tokens
// with pure maths. No network call per request.
//
// Reading order: docs/03-tokens-explained.md then docs/07-dotnet-integration.md
// ---------------------------------------------------------------------------

using Api;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var keycloak = builder.Configuration.GetSection("Keycloak");
var authority = keycloak["Authority"]!;   // e.g. http://localhost:8080/realms/dotnet-lab
var audience = keycloak["Audience"]!;     // e.g. dotnet-api

// ---------------------------------------------------------------------------
// 1. AUTHENTICATION: who is the caller?
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Authority is the token ISSUER's base URL. On startup (lazily, on the
        // first request) the handler fetches:
        //     {Authority}/.well-known/openid-configuration
        // and from that document the `jwks_uri`, which holds the public keys.
        options.Authority = authority;
        options.Audience = audience;

        // Dev only. Keycloak is on plain HTTP here, so we must allow it.
        // In production this stays true (the default) and you use HTTPS.
        options.RequireHttpsMetadata = false;

        // THE CLAIM-RENAMING GOTCHA.
        // By default .NET "maps inbound claims": it rewrites well-known JWT
        // claim names into old WS-Federation URIs, so `sub` silently becomes
        // `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`
        // and `email` becomes a similar monster. Your lookup for "sub" then
        // returns null and you lose an hour.
        // Turning it off keeps claims named exactly as Keycloak sent them,
        // which is what you want when learning (and arguably always).
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Signature: was this token really minted by Keycloak and not
            // altered since? This is the one check you can never skip.
            ValidateIssuerSigningKey = true,

            // Issuer: does `iss` equal our authority? Stops a token from a
            // DIFFERENT realm (or a different Keycloak entirely) being replayed
            // here. Must match the string in the token exactly, character for
            // character - this is why KC_HOSTNAME matters so much.
            ValidateIssuer = true,
            ValidIssuer = authority,

            // Audience: does `aud` contain "dotnet-api"? Stops a token that was
            // legitimately issued for some OTHER API being replayed against us.
            // Keycloak does not add a useful `aud` by itself; the realm JSON
            // configures an "audience mapper" to inject it. See
            // docs/05-keycloak-concepts.md.
            ValidateAudience = true,
            ValidAudience = audience,

            // Expiry: is `exp` in the future? Access tokens live 5 minutes in
            // this realm, which is why refresh tokens exist.
            ValidateLifetime = true,

            // Allowance for clock drift between Keycloak and this API.
            // The default is 5 minutes, which quietly extends every token's
            // life by 5 minutes - a surprise when you are trying to OBSERVE
            // expiry. We tighten it so the lab behaves predictably.
            ClockSkew = TimeSpan.FromSeconds(30),

            // Keycloak puts roles in nested JSON, not in a flat "role" claim,
            // so [Authorize(Roles = ...)] will not work until we flatten them
            // (we do that in OnTokenValidated below). Telling ASP.NET which
            // claim type means "role" is still required for the flattened ones.
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = "preferred_username",
        };

        options.Events = new JwtBearerEvents
        {
            // Runs after the signature and all the checks above passed.
            // Keycloak shape:
            //   "realm_access":    { "roles": ["lab-user", "lab-admin"] }
            //   "resource_access": { "dotnet-api": { "roles": ["reports.read"] } }
            // We copy both into standard role claims so the rest of ASP.NET
            // Core (policies, [Authorize(Roles=..)], User.IsInRole) works.
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is not ClaimsIdentity identity)
                    return Task.CompletedTask;

                foreach (var role in KeycloakRoles.Extract(context.Principal, audience))
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));

                return Task.CompletedTask;
            },

            // Without this, a rejected token just produces a bare 401 and you
            // are left guessing. The reason is gold while learning, so we echo
            // it in a response header. Do not ship this: it tells an attacker
            // exactly which check they failed.
            OnAuthenticationFailed = context =>
            {
                context.Response.Headers["X-Auth-Failure-Reason"] =
                    context.Exception.GetType().Name + ": " + context.Exception.Message
                        .Replace('\r', ' ').Replace('\n', ' ');
                return Task.CompletedTask;
            },
        };
    });

// ---------------------------------------------------------------------------
// 2. AUTHORIZATION: is this caller allowed to do THIS?
// ---------------------------------------------------------------------------
// Authentication and authorization are different questions. A valid token
// proves who you are; a policy decides what that identity may do.
builder.Services.AddAuthorization(options =>
{
    // Realm role - a property of the user across the whole realm.
    options.AddPolicy("RequireLabAdmin", policy => policy.RequireRole("lab-admin"));

    // Client role - a property of the user *with respect to this API*.
    options.AddPolicy("RequireReportsRead", policy => policy.RequireRole("reports.read"));

    // Scope check. Scopes describe what the CLIENT APP asked permission for,
    // which is a different axis from what the USER is allowed to do. Keycloak
    // returns them as one space-delimited string in the `scope` claim.
    options.AddPolicy("RequireProfileScope", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.FindFirst("scope")?.Value.Split(' ').Contains("profile") == true));
});

var app = builder.Build();

// Order matters and is a classic source of silent bugs:
// Authentication (read the token) MUST come before Authorization (judge it).
app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// 3. THE ENDPOINTS - a ladder from "no token" to "specific role"
// ---------------------------------------------------------------------------

// Rung 0: anonymous. Proves the API is up without involving auth at all.
app.MapGet("/api/public", () => new
{
    message = "No token needed. Everyone can read this.",
    at = DateTimeOffset.UtcNow,
});

// Rung 1: any valid token. This is "authentication only".
app.MapGet("/api/whoami", (ClaimsPrincipal user) => new
{
    message = "Your token passed signature, issuer, audience and expiry checks.",
    subject = user.FindFirst("sub")?.Value,          // stable user id - use THIS as a key
    username = user.FindFirst("preferred_username")?.Value,  // mutable - do not key on it
    email = user.FindFirst("email")?.Value,
    scopes = user.FindFirst("scope")?.Value.Split(' '),
    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).OrderBy(r => r),
    tokenExpiresAt = ToTime(user.FindFirst("exp")?.Value),
    tokenIssuedAt = ToTime(user.FindFirst("iat")?.Value),
}).RequireAuthorization();

// Rung 2: every claim, raw. The most useful endpoint in the whole lab -
// compare this against docs/03-tokens-explained.md while poking at it.
app.MapGet("/api/claims", (ClaimsPrincipal user) =>
        user.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray()))
    .RequireAuthorization();

// Rung 3: a realm role. alice passes, bob gets 403 (not 401 - he IS
// authenticated, he is just not permitted).
app.MapGet("/api/admin", (ClaimsPrincipal user) => new
{
    message = $"Hello {user.Identity?.Name}, you hold the realm role 'lab-admin'.",
}).RequireAuthorization("RequireLabAdmin");

// Rung 4: a client role, scoped to this API specifically.
app.MapGet("/api/reports", (ClaimsPrincipal user) => new
{
    message = "You hold the client role 'dotnet-api:reports.read'.",
    rows = new[] { "Q1: 42", "Q2: 58", "Q3: 71" },
}).RequireAuthorization("RequireReportsRead");

app.Run();

// 401 vs 403, the distinction people get wrong:
//   401 Unauthorized -> "I do not know who you are." Missing/bad/expired token.
//   403 Forbidden    -> "I know who you are, and the answer is still no."
// ASP.NET Core picks the right one for you based on whether authentication
// succeeded. If you see 401 when you expected 403, your token was rejected -
// check the X-Auth-Failure-Reason header.

static string? ToTime(string? unixSeconds) =>
    long.TryParse(unixSeconds, out var s)
        ? DateTimeOffset.FromUnixTimeSeconds(s).ToString("u")
        : null;
