using System.Security.Claims;
using System.Text.Json;

namespace WebApp;

/// <summary>
/// Keycloak nests roles inside JSON objects, but ASP.NET Core expects flat role
/// claims. This pulls the former out so the latter works.
/// </summary>
/// <remarks>
/// A Keycloak access token payload contains:
/// <code>
/// "realm_access":    { "roles": ["lab-user", "lab-admin"] },
/// "resource_access": { "dotnet-api": { "roles": ["reports.read"] } }
/// </code>
/// The JWT handler stores each of those whole objects as a single claim whose
/// value is a JSON *string*, so we parse them back out.
/// </remarks>
public static class KeycloakRoles
{
    public static IEnumerable<string> Extract(ClaimsPrincipal principal, string clientId)
    {
        // Realm roles: the user's roles across the entire realm.
        foreach (var role in RolesFrom(principal.FindFirst("realm_access")?.Value, "roles"))
            yield return role;

        // Client roles: the user's roles for one specific client. We only care
        // about our own - another API's roles are none of our business.
        var resourceAccess = principal.FindFirst("resource_access")?.Value;
        if (resourceAccess is null) yield break;

        using var doc = JsonDocument.Parse(resourceAccess);
        if (doc.RootElement.TryGetProperty(clientId, out var mine) &&
            mine.TryGetProperty("roles", out var roles) &&
            roles.ValueKind == JsonValueKind.Array)
        {
            foreach (var role in roles.EnumerateArray())
                if (role.GetString() is { } name) yield return name;
        }
    }

    private static IEnumerable<string> RolesFrom(string? json, string property)
    {
        if (string.IsNullOrEmpty(json)) return [];

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(property, out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray()
            .Select(e => e.GetString())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();
    }
}
