using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace WebApp;

/// <summary>
/// Exchanges a refresh token for a new access token and writes the result back
/// into the auth cookie.
/// </summary>
/// <remarks>
/// WHY REFRESH TOKENS EXIST
///
/// Access tokens are bearer tokens: anyone holding one can use it, and the API
/// validates them offline, so a leaked one cannot be un-issued. The mitigation
/// is to make them expire fast - 5 minutes in this realm.
///
/// But you cannot send the user back through a login screen every 5 minutes.
/// So the authorization server also issues a refresh token, which:
///   - lives much longer (hours or days)
///   - is only ever sent to the token endpoint, never to an API
///   - can be revoked server-side, because redeeming it requires a live call
///     to Keycloak, which gets to say no
///
/// That is the trade: a short-lived credential used constantly, and a
/// long-lived credential used rarely against exactly one endpoint that can
/// refuse it.
/// </remarks>
public sealed class TokenRefresher(IHttpClientFactory factory, IConfiguration config)
{
    public async Task<RefreshOutcome> RefreshAsync(HttpContext ctx)
    {
        var kc = config.GetSection("Keycloak");

        var oldAccess = await ctx.GetTokenAsync("access_token");
        var refreshToken = await ctx.GetTokenAsync("refresh_token");

        if (string.IsNullOrEmpty(refreshToken))
            return new RefreshOutcome(false,
                "No refresh token in the cookie. Either SaveTokens is off, or the " +
                "client was not granted one.", null, null, null, null);

        // The token endpoint. Normally discovery finds this for us; spelled out
        // here so the raw HTTP is visible.
        var tokenEndpoint = $"{kc["Authority"]}/protocol/openid-connect/token";

        // RFC 6749 section 6: the refresh grant. Note it is a plain
        // application/x-www-form-urlencoded POST - no JSON, no bearer header.
        // Because our client is confidential, we authenticate with the client
        // secret. A public client (SPA, mobile) omits it and relies on PKCE.
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = kc["ClientId"]!,
            ["client_secret"] = kc["ClientSecret"]!,
        };

        var http = factory.CreateClient();
        var response = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
        var raw = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            // The usual causes: the refresh token itself expired, it was already
            // used once and rotation is on, the user's SSO session ended, or an
            // admin revoked the session. All of them mean the same thing to you:
            // the session is over, send the user back through login.
            return new RefreshOutcome(false,
                $"Keycloak refused the refresh ({(int)response.StatusCode}). " +
                "The session is over - the user must log in again.", raw, null, null, null);

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>()
                     ?? throw new InvalidOperationException("Unparseable token response");

        // -------------------------------------------------------------------
        // WRITE THE NEW TOKENS BACK INTO THE COOKIE
        // -------------------------------------------------------------------
        // Easy to get wrong: refreshing and then forgetting to persist means the
        // next request happily reuses the old, expired token.
        //
        // Also note Keycloak returns a NEW refresh token in the response. Always
        // store the one you just received. If the realm has "Revoke Refresh
        // Token" enabled (it is off in this lab) the old one is burned on use,
        // and replaying it fails - Keycloak reads reuse as a sign the token was
        // stolen and can terminate the whole session.
        var auth = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (auth.Properties is null || auth.Principal is null)
            return new RefreshOutcome(false, "Could not re-read the auth cookie.", raw, null, null, null);

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn);

        auth.Properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = tokens.AccessToken },
            new AuthenticationToken { Name = "refresh_token", Value = tokens.RefreshToken ?? refreshToken },
            new AuthenticationToken { Name = "id_token", Value = tokens.IdToken ?? (await ctx.GetTokenAsync("id_token") ?? "") },
            // Round-trip format matters: the handler parses this back out.
            new AuthenticationToken { Name = "expires_at", Value = expiresAt.ToString("o") },
        ]);

        // Re-issue the cookie with the updated tokens inside it.
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            auth.Principal, auth.Properties);

        return new RefreshOutcome(
            true,
            $"New access token issued, valid for {tokens.ExpiresIn}s (until {expiresAt:u}).",
            raw,
            oldAccess,
            tokens.AccessToken,
            tokens.RefreshToken is not null && tokens.RefreshToken != refreshToken
                ? "A NEW refresh token came back. Keycloak always issues one. Whether the " +
                  "OLD one still works depends on the realm's 'Revoke Refresh Token' setting - " +
                  "it is off here, so the old one stays valid until it expires. Turn it on and " +
                  "reusing an old token fails (and Keycloak treats reuse as a theft signal)."
                : "The same refresh token came back unchanged.");
    }

    /// <summary>Shape of a successful token endpoint response (RFC 6749 s5.1).</summary>
    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }   // always "Bearer"
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }       // seconds, access token
        [JsonPropertyName("refresh_expires_in")] public int RefreshExpiresIn { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }
}

public record RefreshOutcome(
    bool Success,
    string Message,
    string? RawResponse,
    string? OldAccessToken,
    string? NewAccessToken,
    string? RotationNote);
