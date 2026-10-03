using Dod.Api.Motivation.Persistence;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dod.Api.Entries;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace Dod.Api.Accounts;

public record Credentials(string Nickname, string Password, string? OwnerPassword = null);
public record ProfileInput(string Nickname);
public static class AccountEndpoints
{
    internal static readonly PasswordHasher<string> Hasher = new();
    internal static bool ValidNickname(string? nick) => nick is not null && Regex.IsMatch(nick, "^[a-zA-Z0-9_]{3,24}$", RegexOptions.CultureInvariant);
    internal static string Normalize(string nick) => nick.ToUpperInvariant();
    internal static IResult Error(string message, int status = 400) => Results.Problem(message, statusCode: status);

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/account");
        group.MapGet("/csrf", (HttpContext ctx, IAntiforgery antiforgery) =>
            Results.Ok(new { token = antiforgery.GetAndStoreTokens(ctx).RequestToken }));
        group.MapGet("/me", async (HttpContext ctx, EntryStore store) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            await using var db = await store.OpenAsync();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Nickname, Avatar IS NOT NULL, IsAdmin FROM Users WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", store.UserId);
            using var row = await cmd.ExecuteReaderAsync();
            return await row.ReadAsync() ? Results.Ok(new { id = store.UserId, nickname = row.GetString(0), hasAvatar = row.GetBoolean(1), isAdmin = row.GetBoolean(2) }) : Results.Unauthorized();
        });
        group.MapPost("/register", (Credentials input, HttpContext ctx, EntryStore store) => Register(input, ctx, store, false, app.Configuration)).RequireRateLimiting("accounts");
        group.MapPost("/claim", (Credentials input, HttpContext ctx, EntryStore store) => Register(input, ctx, store, true, app.Configuration)).RequireRateLimiting("accounts");
        group.MapPost("/login", async (Credentials input, HttpContext ctx, EntryStore store) =>
        {
            if (!ValidNickname(input.Nickname) || input.Password is null || input.Password.Length > 128) return Error("Invalid nickname or password.", 401);
            await using var db = await store.OpenAsync();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Id, PasswordHash, SessionVersion FROM Users WHERE NormalizedNickname=$nick AND PasswordHash IS NOT NULL";
            cmd.Parameters.AddWithValue("$nick", Normalize(input.Nickname));
            using var row = await cmd.ExecuteReaderAsync();
            if (!await row.ReadAsync()) return Error("Invalid nickname or password.", 401);
            var id = row.GetString(0);
            if (Hasher.VerifyHashedPassword(id, row.GetString(1), input.Password) == PasswordVerificationResult.Failed) return Error("Invalid nickname or password.", 401);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccountSessions.Principal(id, row.GetInt32(2)),
                new AuthenticationProperties { IsPersistent = true });
            return Results.NoContent();
        }).RequireRateLimiting("accounts");
        group.MapPost("/forgot-password", async (PasswordResetRequest input, PasswordResetStore resets, HttpContext ctx) =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await resets.Request(input.Nickname);
            // Queue delivery asynchronously and apply the same minimum response time to all account states.
            var remaining = TimeSpan.FromMilliseconds(300) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ctx.RequestAborted);
            return Results.Ok(new { detail = PasswordResetStore.RequestMessage });
        }).RequireRateLimiting("accounts");
        group.MapPost("/reset-password", async (PasswordResetInput input, PasswordResetStore resets) =>
        {
            if (input.Password is null || input.Password.Length is < 12 or > 128) return Error("Choose a password with 12–128 characters.");
            return await resets.Reset(input.Token, input.Password) ? Results.NoContent()
                : Error("This password reset link is invalid, expired, or already used. Request a new one from the sign-in page.");
        }).RequireRateLimiting("accounts");
        group.MapPost("/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.NoContent(); }).RequireAuthorization();
        group.MapPut("/profile", async (ProfileInput input, EntryStore store) =>
        {
            if (!ValidNickname(input.Nickname)) return Error("Use 3–24 letters, numbers or underscores for your nickname.");
            await using var db = await store.OpenAsync();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE Users SET Nickname=$nick, NormalizedNickname=$normal WHERE Id=$id";
            cmd.Parameters.AddWithValue("$nick", input.Nickname); cmd.Parameters.AddWithValue("$normal", Normalize(input.Nickname)); cmd.Parameters.AddWithValue("$id", store.UserId);
            try { await cmd.ExecuteNonQueryAsync(); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { return Error("That nickname is already taken.", 409); }
            return Results.NoContent();
        }).RequireAuthorization();
        group.MapPut("/avatar", async (HttpContext ctx, EntryStore store) =>
        {
            // Raw image upload: bounded bytes, supported raster signatures, no supplied filename or SVG.
            const int limit = 1024 * 1024;
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await ctx.Request.Body.ReadAsync(buffer)) > 0)
            {
                if (output.Length + count > limit) return Error("Avatar must be at most 1 MB.", 413);
                output.Write(buffer, 0, count);
            }
            var bytes = output.ToArray();
            var type = AvatarFormat.Detect(bytes);
            if (type is null || ctx.Request.ContentType != type) return Error("Choose a PNG or JPEG image no larger than 2048 × 2048 pixels.");
            await using var db = await store.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE Users SET Avatar=$data, AvatarType=$type WHERE Id=$id";
            cmd.Parameters.AddWithValue("$data", bytes); cmd.Parameters.AddWithValue("$type", type); cmd.Parameters.AddWithValue("$id", store.UserId);
            await cmd.ExecuteNonQueryAsync(); return Results.NoContent();
        }).RequireAuthorization();
        group.MapDelete("/avatar", async (EntryStore store) =>
        {
            await using var db = await store.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE Users SET Avatar=NULL, AvatarType=NULL WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", store.UserId); await cmd.ExecuteNonQueryAsync(); return Results.NoContent();
        }).RequireAuthorization();
        app.MapGet("/api/avatars/{id}", async (string id, HttpContext ctx, EntryStore store) =>
        {
            await using var db = await store.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Avatar, AvatarType FROM Users WHERE Id=$id AND PasswordHash IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id); using var row = await cmd.ExecuteReaderAsync();
            if (!await row.ReadAsync() || row.IsDBNull(0)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File((byte[])row[0], row.GetString(1));
        }).RequireAuthorization();
    }
    private static async Task<IResult> Register(Credentials input, HttpContext ctx, EntryStore store, bool claim, IConfiguration config)
    {
        if (!ValidNickname(input.Nickname)) return Error("Use 3–24 letters, numbers or underscores for your nickname.");
        if (input.Password is null || input.Password.Length is < 12 or > 128) return Error("Choose a password with 12–128 characters.");
        if (claim)
        {
            var secret = config["Tracker:Password"];
            if (string.IsNullOrWhiteSpace(secret) || input.OwnerPassword is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(input.OwnerPassword)))) return Error("Owner claim could not be verified.", 403);
        }
        var id = claim ? "legacy" : Guid.NewGuid().ToString("N");
        var hash = Hasher.HashPassword(id, input.Password);
        await using var db = await store.OpenAsync(); using var cmd = db.CreateCommand();
        cmd.CommandText = claim
            ? "UPDATE Users SET Nickname=$nick, NormalizedNickname=$normal, PasswordHash=$hash WHERE Id=$id AND PasswordHash IS NULL"
            : "INSERT INTO Users (Id, Nickname, NormalizedNickname, PasswordHash, WeeklyEligibleFrom) VALUES ($id,$nick,$normal,$hash,$eligible)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$nick", input.Nickname); cmd.Parameters.AddWithValue("$normal", Normalize(input.Nickname)); cmd.Parameters.AddWithValue("$hash", hash);
        var today = DateOnly.FromDateTime(ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
        cmd.Parameters.AddWithValue("$eligible", Dod.Api.Motivation.Persistence.WeeklyRules.Day(Dod.Api.Motivation.Persistence.WeeklyRules.NextMonday(today)));
        try { if (await cmd.ExecuteNonQueryAsync() == 0) return Error("The original journal has already been claimed.", 409); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { return Error("That nickname is already taken.", 409); }
        await SignIn(ctx, id); return Results.NoContent();
    }
    private static Task SignIn(HttpContext ctx, string id) => ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        AccountSessions.Principal(id, 0),
        new AuthenticationProperties { IsPersistent = true });
}
