using System.Text;
using System.Text.Json;

namespace WebApp;

/// <summary>
/// Decodes a JWT for DISPLAY ONLY. It does not verify the signature.
/// </summary>
/// <remarks>
/// This is the single most important caveat about JWTs: a JWT is not encrypted,
/// it is only signed. Anyone can read one - paste any access token into
/// jwt.io and you will see every claim. So:
///
///   - never put secrets in a token
///   - never trust a token you have not verified the signature of
///
/// Decoding (what this class does) is trivial base64url. VERIFYING is the
/// cryptographic part, and that is what the API does with Keycloak's public
/// keys. Do not confuse the two.
/// </remarks>
public static class Jwt
{
    /// <summary>A JWT is three base64url chunks joined by dots: header.payload.signature</summary>
    public static (string Header, string Payload, string Signature)? Split(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        // Refresh tokens from Keycloak are JWTs too, but opaque tokens in
        // general are not - handle both.
        return parts.Length < 2 ? null : (parts[0], parts[1], parts.Length > 2 ? parts[2] : "");
    }

    public static string? DecodePart(string? base64UrlPart)
    {
        if (string.IsNullOrWhiteSpace(base64UrlPart)) return null;
        try
        {
            var s = base64UrlPart.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            // Pretty-print so nested claims like resource_access are readable.
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return null;   // not a JWT part - fine, we just show it raw
        }
    }

    /// <summary>Human-readable "expires in 4m 12s" / "EXPIRED 30s ago" for the exp claim.</summary>
    public static string Countdown(string? payloadJson)
    {
        if (payloadJson is null) return "unknown";
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return "no exp claim";

            var expiry = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
            var left = expiry - DateTimeOffset.UtcNow;

            return left.TotalSeconds > 0
                ? $"valid for another {(int)left.TotalMinutes}m {left.Seconds}s (until {expiry:HH:mm:ss} UTC)"
                : $"EXPIRED {(int)-left.TotalSeconds}s ago (at {expiry:HH:mm:ss} UTC)";
        }
        catch { return "unparseable"; }
    }
}
