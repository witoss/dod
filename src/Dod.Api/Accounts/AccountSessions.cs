using System.Globalization;
using System.Security.Claims;
using Dod.Api.Entries;
using Dod.Api.Motivation.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Dod.Api.Accounts;

public static class AccountSessions
{
    private const string VersionClaim = "dodo.session-version";
    public static ClaimsPrincipal Principal(string id, int version) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id), new Claim(VersionClaim, version.ToString(CultureInfo.InvariantCulture))],
        CookieAuthenticationDefaults.AuthenticationScheme));

    public static async Task Validate(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        // Cookies created before migration 7 represent version zero.
        var version = context.Principal?.FindFirstValue(VersionClaim) ?? "0";
        var store = context.HttpContext.RequestServices.GetRequiredService<EntryStore>();
        await using var db = await store.OpenAsync();
        using var cmd = WeeklyRules.Command(db, null, "SELECT SessionVersion FROM Users WHERE Id=$id AND PasswordHash IS NOT NULL", ("$id", id));
        var current = await cmd.ExecuteScalarAsync();
        if (current is not null && Convert.ToInt32(current).ToString(CultureInfo.InvariantCulture) == version) return;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
