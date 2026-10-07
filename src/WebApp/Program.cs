// ---------------------------------------------------------------------------
// THE CLIENT (OAuth2) / RELYING PARTY (OIDC)
// ---------------------------------------------------------------------------
// This app logs a human in through Keycloak and then calls the API on their
// behalf. It is a "confidential client": it runs on a server and can keep a
// secret, so it uses the Authorization Code flow.
//
// THE MENTAL MODEL - two completely separate security contexts:
//
//   Browser  <--cookie-->  WebApp  <--Bearer token-->  API
//
//   The cookie authenticates the user TO THIS APP. The browser never sees a
//   token. Tokens are tucked inside the (encrypted) cookie, server-decrypted
//   on each request, and only ever leave over the back channel.
//
//   This is the "BFF" (Backend For Frontend) pattern and it is the recommended
//   shape for server-rendered apps - tokens in JavaScript are a liability.
//
// Reading order: docs/04-authorization-code-pkce.md, then docs/07-dotnet-integration.md
// ---------------------------------------------------------------------------

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using WebApp;

var builder = WebApplication.CreateBuilder(args);

var kc = builder.Configuration.GetSection("Keycloak");
var apiBaseUrl = builder.Configuration["ApiBaseUrl"]!;

// Used for the manual refresh endpoint and for calling the API.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TokenRefresher>();

builder.Services.AddAuthentication(options =>
    {
        // Two schemes with two different jobs:

        // "Cookies" is the PRIMARY scheme. After login completes, every
        // subsequent request is authenticated by reading the cookie. Keycloak
        // is not involved again until the session expires.
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;

        // OIDC is the CHALLENGE scheme. When an unauthenticated user hits a
        // protected page, "challenge" means "start the login redirect dance".
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "WebApp.Session";
        options.Cookie.HttpOnly = true;       // JavaScript cannot read it (XSS defence)
        options.Cookie.SameSite = SameSiteMode.Lax;  // Lax, not Strict: the OIDC
                                                     // redirect back from Keycloak is a
                                                     // cross-site navigation and Strict
                                                     // would drop the correlation cookie.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // dev only; Always in prod

        // How long the local session lasts, independent of token lifetimes.
        options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
        options.SlidingExpiration = true;
    })
    .AddOpenIdConnect(options =>
    {
        // ------------------------------------------------------------------
        // DISCOVERY
        // ------------------------------------------------------------------
        // Authority is the realm's base URL. ASP.NET Core appends
        // /.well-known/openid-configuration and learns every other URL
        // (authorize, token, jwks, userinfo, end_session) from it. This is why
        // you configure one string instead of six.
        options.Authority = kc["Authority"];
        options.RequireHttpsMetadata = false;   // dev only - Keycloak is on plain HTTP

        options.ClientId = kc["ClientId"];
        options.ClientSecret = kc["ClientSecret"];

        // Keep claims named as Keycloak sent them instead of letting .NET
        // rewrite them into legacy WS-Federation URIs. See the same note in
        // src/Api/Program.cs - it is the most common "why is this claim null?"
        options.MapInboundClaims = false;

        // ------------------------------------------------------------------
        // THE FLOW
        // ------------------------------------------------------------------
        // "code" = Authorization Code flow. The browser carries back a short
        // one-time code, NOT a token. This app then swaps that code for tokens
        // over a direct server-to-server call. Tokens therefore never touch the
        // URL bar, browser history, or Referer headers.
        options.ResponseType = OpenIdConnectResponseType.Code;

        // PKCE (Proof Key for Code Exchange). The app invents a random
        // `code_verifier`, sends only its SHA-256 hash up front, then reveals
        // the original when redeeming the code. An attacker who steals the code
        // cannot use it without the verifier. Default true in .NET 9 - set
        // explicitly because it matters and you should see it.
        options.UsePkce = true;

        // Post the code back as a form POST rather than a query string, so it
        // stays out of server access logs.
        options.ResponseMode = OpenIdConnectResponseMode.FormPost;

        // PAR (Pushed Authorization Requests, RFC 9126) - DISABLED ON PURPOSE.
        //
        // .NET 9 changed the default to "use it if the server supports it", and
        // Keycloak 26 does. With PAR on, the app POSTs all the request
        // parameters to Keycloak first and the browser redirect carries only a
        // short `request_uri` handle:
        //
        //   ...?client_id=dotnet-webapp&request_uri=urn:ietf:params:oauth:request_uri:5c64...
        //
        // That is genuinely more secure - parameters cannot be tampered with or
        // leaked through logs and Referer headers. But it also means you cannot
        // SEE scope, state, nonce or code_challenge in the URL bar, which is
        // exactly what you are here to look at.
        //
        // So: off while learning, on in production. Flip this line back to
        // UseIfAvailable and reload /profile to watch the URL collapse.
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;

        // ------------------------------------------------------------------
        // SCOPES - what we are asking Keycloak for
        // ------------------------------------------------------------------
        options.Scope.Clear();
        options.Scope.Add("openid");          // REQUIRED. This is what makes it OIDC
                                              // rather than plain OAuth2. Without it
                                              // you get no id_token.
        options.Scope.Add("profile");         // name, preferred_username, ...
        options.Scope.Add("email");           // email, email_verified

        // NOT requested: "offline_access".
        // A common misconception is that you need it to get a refresh token.
        // You do not - the Authorization Code flow returns one anyway, tied to
        // your SSO session. offline_access asks for something stronger: a
        // refresh token that OUTLIVES the session, so a background job can keep
        // acting for you after you close the browser.
        //
        // It also requires the user to hold the `offline_access` realm role.
        // Our users do (via default-roles-dotnet-lab), so you can uncomment the
        // line below and compare `refresh_expires_in` on /tokens - an offline
        // token reports 0, meaning "does not expire with the session".
        //
        // options.Scope.Add("offline_access");

        // ------------------------------------------------------------------
        // THE PIECE EVERYONE FORGETS
        // ------------------------------------------------------------------
        // Without SaveTokens, ASP.NET Core validates the id_token, builds the
        // cookie, and then THROWS THE TOKENS AWAY. You would be logged in but
        // unable to call any API. With it, access/refresh/id tokens are stored
        // inside the encrypted auth cookie.
        //
        // Trade-off: it makes the cookie noticeably bigger (often >4KB, so it
        // gets split across chunks). For production, consider a server-side
        // ticket store instead - see docs/10-production-checklist.md.
        options.SaveTokens = true;

        // Call /userinfo after login and merge the result into the identity.
        // Usually unnecessary (the id_token already has the claims) - on here
        // so you can watch the extra request happen.
        options.GetClaimsFromUserInfoEndpoint = true;

        // These paths are OURS, not Keycloak's. They must match the redirect
        // URIs registered on the client in Keycloak, or you get
        // "Invalid parameter: redirect_uri".
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";

        // Map JWT claims onto what ASP.NET Core considers name/role.
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "preferred_username",
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // Keycloak's logout endpoint. Hitting our own /logout only clears the
        // local cookie; the Keycloak SSO session would still be live and the
        // next login would silently succeed with no prompt. Signing out of both
        // is what "single logout" means.
        options.SignedOutRedirectUri = "/";

        options.Events = new OpenIdConnectEvents
        {
            // Flatten Keycloak's nested roles, same as the API does.
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal?.Identity is ClaimsIdentity id)
                    foreach (var role in KeycloakRoles.Extract(ctx.Principal, kc["ClientId"]!))
                        id.AddClaim(new Claim(ClaimTypes.Role, role));
                return Task.CompletedTask;
            },

            // Surface the real reason instead of a generic 500 page.
            OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/error?message=" +
                    Uri.EscapeDataString(ctx.Failure?.Message ?? "unknown OIDC failure"));
                ctx.HandleResponse();
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// PAGES
// ---------------------------------------------------------------------------
// Deliberately plain HTML built in C#. No Razor, no JavaScript framework -
// nothing between you and the auth mechanics.

app.MapGet("/", (HttpContext ctx) => Pages.Home(ctx.User));

// Hitting a page with RequireAuthorization while anonymous triggers the
// challenge: a 302 to Keycloak's /authorize endpoint. Open devtools and watch
// it - that redirect is step 1 of the whole flow.
app.MapGet("/profile", (HttpContext ctx) => Pages.Profile(ctx.User))
   .RequireAuthorization();

// Shows the actual access/refresh/id tokens and decodes them in place.
app.MapGet("/tokens", async (HttpContext ctx) =>
{
    var access = await ctx.GetTokenAsync("access_token");
    var refresh = await ctx.GetTokenAsync("refresh_token");
    var id = await ctx.GetTokenAsync("id_token");
    var expiresAt = await ctx.GetTokenAsync("expires_at");
    return Pages.Tokens(access, refresh, id, expiresAt);
}).RequireAuthorization();

// Manually exchange the refresh token for a fresh access token. Normally you
// would do this transparently, on demand, when a call fails with 401. It is a
// button here so you can watch `iat`/`exp` move and see the refresh token
// itself get rotated.
app.MapPost("/refresh", async (HttpContext ctx, TokenRefresher refresher) =>
{
    var result = await refresher.RefreshAsync(ctx);
    return Pages.RefreshResult(result);
}).RequireAuthorization();

// Call the downstream API with our access token - the whole point of OAuth 2.0.
app.MapGet("/call-api", async (HttpContext ctx, IHttpClientFactory factory, string path = "/api/whoami") =>
{
    var accessToken = await ctx.GetTokenAsync("access_token");

    var http = factory.CreateClient();
    var request = new HttpRequestMessage(HttpMethod.Get, apiBaseUrl + path);

    // THE ONE LINE THAT MATTERS: "Authorization: Bearer <jwt>".
    // "Bearer" means possession is sufficient - whoever holds it can use it.
    // That is exactly why access tokens are short-lived.
    request.Headers.Authorization = new("Bearer", accessToken);

    var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();

    return Pages.ApiResult(
        path,
        (int)response.StatusCode,
        response.ReasonPhrase,
        body,
        response.Headers.TryGetValues("X-Auth-Failure-Reason", out var r) ? string.Join("; ", r) : null);
}).RequireAuthorization();

// Local logout only: clears our cookie, leaves the Keycloak SSO session alone.
// Try it, then log in again - no password prompt. That is SSO, and it is also
// why this is usually the WRONG logout.
app.MapGet("/logout-local", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// Full logout: clears our cookie AND redirects to Keycloak's end_session
// endpoint to kill the SSO session. The id_token is sent along as
// `id_token_hint` so Keycloak knows which session to end.
app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
});

app.MapGet("/error", (string? message) => Pages.Error(message));

app.Run();
