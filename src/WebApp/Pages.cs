using System.Net;
using System.Security.Claims;
using System.Text;

namespace WebApp;

/// <summary>
/// Plain HTML built in C#. No Razor and no client-side JavaScript, so that
/// nothing sits between you and the auth mechanics you are trying to observe.
/// </summary>
public static class Pages
{
    public static IResult Home(ClaimsPrincipal user)
    {
        var body = new StringBuilder();

        if (user.Identity?.IsAuthenticated != true)
        {
            body.Append("""
                <p class="muted">You are <strong>anonymous</strong>. The app has no cookie for you.</p>
                <p>Click below. Watch the URL bar: you will be bounced to
                <code>localhost:8080</code> (Keycloak), log in there, and land back here.
                That round trip is the Authorization Code flow.</p>
                <p><a class="btn" href="/profile">Log in &rarr;</a></p>
                <p class="muted">Users: <code>alice / password</code> (admin) or
                <code>bob / password</code> (plain user).</p>
                """);
        }
        else
        {
            var roles = string.Join(", ", user.FindAll(ClaimTypes.Role).Select(c => c.Value).OrderBy(r => r));
            body.Append($"""
                <p>Signed in as <strong>{Esc(user.Identity.Name)}</strong></p>
                <p class="muted">Roles: <code>{Esc(roles)}</code></p>
                <h3>Explore</h3>
                <ul>
                  <li><a href="/profile">/profile</a> &mdash; the claims in your cookie</li>
                  <li><a href="/tokens">/tokens</a> &mdash; the raw tokens, decoded</li>
                  <li><a href="/call-api?path=/api/whoami">/api/whoami</a> &mdash; call the API with your token</li>
                  <li><a href="/call-api?path=/api/claims">/api/claims</a> &mdash; what the API sees</li>
                  <li><a href="/call-api?path=/api/admin">/api/admin</a> &mdash; needs realm role <code>lab-admin</code></li>
                  <li><a href="/call-api?path=/api/reports">/api/reports</a> &mdash; needs client role <code>reports.read</code></li>
                  <li><a href="/call-api?path=/api/public">/api/public</a> &mdash; no token needed</li>
                </ul>
                <h3>Logout</h3>
                <ul>
                  <li><a href="/logout-local">Local only</a> &mdash; clears our cookie; Keycloak session survives,
                      so logging back in asks for nothing. That is SSO.</li>
                  <li><a href="/logout">Full (RP-initiated)</a> &mdash; ends the Keycloak session too.</li>
                </ul>
                """);
        }

        return Html("Keycloak + .NET Lab", body.ToString());
    }

    public static IResult Profile(ClaimsPrincipal user)
    {
        var rows = string.Join("", user.Claims
            .OrderBy(c => c.Type)
            .Select(c => $"<tr><td><code>{Esc(c.Type)}</code></td><td>{Esc(c.Value)}</td></tr>"));

        return Html("Your claims", $"""
            <p>These claims live in your <strong>cookie</strong>, not in a token. They were
            copied out of the <code>id_token</code> (and the <code>/userinfo</code>
            response) when you logged in.</p>
            <p class="muted">These names match what Keycloak actually sent, because we set
            <code>MapInboundClaims = false</code>. Leave that at its default and .NET
            rewrites <code>sub</code> into a long
            <code>http://schemas.xmlsoap.org/...</code> URI.</p>
            <table><tr><th>Claim type</th><th>Value</th></tr>{rows}</table>
            <p><a href="/">&larr; back</a></p>
            """);
    }

    public static IResult Tokens(string? access, string? refresh, string? id, string? expiresAt)
    {
        var accessParts = Jwt.Split(access);
        var accessPayload = Jwt.DecodePart(accessParts?.Payload);
        var idPayload = Jwt.DecodePart(Jwt.Split(id)?.Payload);
        var refreshPayload = Jwt.DecodePart(Jwt.Split(refresh)?.Payload);

        return Html("Your tokens", $"""
            <p class="warn"><strong>These are live credentials.</strong> Printing them to a
            web page is a teaching device, never a real pattern.</p>

            <h2>access_token</h2>
            <p>Sent to the API as <code>Authorization: Bearer ...</code>. The API cares only
            about this one. <strong>{Esc(Jwt.Countdown(accessPayload))}</strong></p>
            <p class="muted">Stored <code>expires_at</code>: <code>{Esc(expiresAt)}</code></p>
            <details><summary>Raw (three dot-separated base64url chunks)</summary>
              <pre class="tok">{Esc(access)}</pre>
              <p class="muted">header: <code>{Esc(accessParts?.Header)}</code><br>
              signature: <code>{Esc(Truncate(accessParts?.Signature, 40))}</code> &mdash; the part
              that proves Keycloak minted it, and the only part you cannot read.</p>
            </details>
            <pre>{Esc(Jwt.DecodePart(accessParts?.Header))}</pre>
            <pre>{Esc(accessPayload)}</pre>
            <p class="muted">Look for: <code>iss</code> (who issued it), <code>aud</code>
            (who it is FOR &mdash; must say <code>dotnet-api</code>), <code>sub</code>
            (the stable user id), <code>exp</code>, <code>realm_access.roles</code>,
            <code>resource_access</code>, and <code>azp</code> (which client asked for it).</p>

            <h2>id_token</h2>
            <p>Proof of <em>authentication</em>, for this app only. Never send it to an API &mdash;
            its <code>aud</code> is the client id, so a correctly configured API will reject it.
            This is the token that makes OIDC more than OAuth 2.0.</p>
            <pre>{Esc(idPayload)}</pre>

            <h2>refresh_token</h2>
            <p>Only ever sent to Keycloak's token endpoint, to obtain new access tokens.
            Note <code>"typ": "Refresh"</code> and that it carries almost no claims &mdash;
            it is a handle, not a description.</p>
            <pre>{Esc(refreshPayload ?? "(opaque - not a readable JWT)")}</pre>

            <h2>Try the refresh grant</h2>
            <form method="post" action="/refresh">
              <button class="btn" type="submit">Exchange refresh token for a new access token</button>
            </form>
            <p class="muted">Then come back here and compare <code>iat</code>/<code>exp</code>.
            To watch a real expiry, wait 5 minutes and call
            <a href="/call-api?path=/api/whoami">/api/whoami</a> &mdash; it will 401.</p>
            <p><a href="/">&larr; back</a></p>
            """);
    }

    public static IResult RefreshResult(RefreshOutcome r)
    {
        var cls = r.Success ? "ok" : "warn";
        var headline = r.Success ? "Success" : "Failed";
        var rotation = r.RotationNote is null
            ? ""
            : $"<p>Refresh token rotated? {Esc(r.RotationNote)}</p>";

        return Html("Refresh result", $"""
            <p class="{cls}"><strong>{headline}</strong>
            &mdash; {Esc(r.Message)}</p>
            {rotation}
            <h3>Old access token (first 60 chars)</h3>
            <pre class="tok">{Esc(Truncate(r.OldAccessToken, 60))}</pre>
            <h3>New access token (first 60 chars)</h3>
            <pre class="tok">{Esc(Truncate(r.NewAccessToken, 60))}</pre>
            <p class="muted">The header chunk is identical (same algorithm, same signing key);
            the payload differs because <code>iat</code> and <code>exp</code> moved.</p>
            <h3>Raw response from Keycloak's token endpoint</h3>
            <pre>{Esc(r.RawResponse)}</pre>
            <p><a href="/tokens">&larr; back to tokens</a></p>
            """);
    }

    public static IResult ApiResult(string path, int status, string? reason, string body, string? failureReason)
    {
        // Explaining the status code is the lesson here. 401 and 403 answer
        // two different questions, and conflating them is a common bug.
        var verdict = status switch
        {
            200 => """
                <p class="ok"><strong>200 OK</strong> &mdash; the token was valid and the
                policy allowed it.</p>
                """,
            401 => """
                <p class="warn"><strong>401 Unauthorized</strong> &mdash; the API does not accept
                your token at all: missing, malformed, expired, wrong issuer, or wrong
                audience. It is a statement about the <em>token</em>.</p>
                """,
            403 => """
                <p class="warn"><strong>403 Forbidden</strong> &mdash; your token is perfectly
                valid and the API knows exactly who you are. You simply lack the required
                role. It is a statement about your <em>permissions</em>. Try logging in as
                <code>alice</code> instead of <code>bob</code>.</p>
                """,
            _ => "",
        };

        var why = failureReason is null
            ? ""
            : $"""
                <h3>Why the token was rejected</h3>
                <pre class="warn">{Esc(failureReason)}</pre>
                <p class="muted">The API echoes this in a debug header. Never do that in production.</p>
                """;

        return Html($"API: {path}", $"""
            <p>GET <code>{Esc(path)}</code> &rarr; <strong>{status} {Esc(reason)}</strong></p>
            {verdict}
            {why}
            <h3>Response body</h3>
            <pre>{Esc(string.IsNullOrWhiteSpace(body) ? "(empty)" : body)}</pre>
            <p><a href="/">&larr; back</a></p>
            """);
    }

    public static IResult Error(string? message) =>
        Html("Something went wrong", $"""
            <p class="warn">{Esc(message ?? "unknown error")}</p>
            <p>Check <code>docs/09-troubleshooting.md</code> &mdash; it lists this
            error and the usual cause.</p>
            <p><a href="/">&larr; back</a></p>
            """);

    // -- plumbing ----------------------------------------------------------

    private static IResult Html(string title, string body) => Results.Content($$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{Esc(title)}}</title>
        <style>
          :root { color-scheme: light dark; }
          body { font: 15px/1.6 ui-sans-serif, system-ui, sans-serif; max-width: 48rem;
                 margin: 0 auto; padding: 1rem 16px 4rem; }
          h1 { font-size: 1.4rem; border-bottom: 2px solid currentColor; padding-bottom: .4rem; }
          h2 { font-size: 1.1rem; margin-top: 2rem; }
          code { font-size: .88em; background: rgba(127,127,127,.18); padding: .1em .35em;
                 border-radius: 3px; }
          pre { background: rgba(127,127,127,.12); padding: .75rem; border-radius: 6px;
                overflow-x: auto; font-size: .82rem; }
          pre.tok { word-break: break-all; white-space: pre-wrap; }
          table { border-collapse: collapse; width: 100%; font-size: .85rem; }
          td, th { border: 1px solid rgba(127,127,127,.35); padding: .35rem .5rem;
                   text-align: left; vertical-align: top; word-break: break-word; }
          .btn { display: inline-block; padding: .5rem .9rem; border: 1px solid currentColor;
                 border-radius: 6px; text-decoration: none; font-weight: 600; cursor: pointer;
                 background: transparent; color: inherit; font-size: 1rem; }
          .muted { opacity: .72; font-size: .9rem; }
          .warn { color: #b3261e; }
          .ok { color: #1b6e34; }
          @media (prefers-color-scheme: dark) { .warn { color: #ff8a80; } .ok { color: #7ee2a8; } }
          nav { font-size: .85rem; margin-bottom: 1rem; }
        </style></head><body>
        <nav><a href="/">home</a> · <a href="/tokens">tokens</a> ·
             <a href="http://localhost:8080/admin" target="_blank">Keycloak admin</a></nav>
        <h1>{{Esc(title)}}</h1>
        {{body}}
        </body></html>
        """, "text/html", Encoding.UTF8);

    private static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string? Truncate(string? s, int n) =>
        s is null ? null : s.Length <= n ? s : s[..n] + "…";
}
