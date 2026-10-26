using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Server.Auth;
using Server.Data;

namespace Server.Api;

// Sign-in/out, "who am I", profiles, API tokens and the editor's device login.
public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        // ---- Sign in / out -------------------------------------------------------------

        app.MapGet("/api/auth/providers", (IConfiguration config, IWebHostEnvironment env) =>
            new { github = AuthSetup.GitHubConfigured(config), dev = env.IsDevelopment() });

        // The browser navigates here (a normal link, not fetch). We bounce it
        // to GitHub, GitHub bounces it back to /signin-github, the OAuth
        // handler signs the cookie in and finally redirects to returnUrl.
        app.MapGet("/auth/github", (string? returnUrl, IConfiguration config) =>
            AuthSetup.GitHubConfigured(config)
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [AuthSetup.GitHubScheme])
                : Results.Problem("GitHub login isn't configured on this server. See Website/README.md.", statusCode: 503));

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // Development only: sign in as any username without GitHub, so the
        // site can be tried before an OAuth app is registered.
        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/auth/dev-login", async (DevLoginRequest body, HttpContext ctx, AppDb db) =>
            {
                string wanted = (body.Username ?? "").Trim();
                if (wanted.Length is 0 or > 39) return GameEndpoints.Problem("Pick a username (1-39 characters).");
                var user = await db.Users.FirstOrDefaultAsync(u => u.Username == wanted && u.GitHubId == null);
                if (user == null)
                {
                    user = new User { Username = await AuthSetup.UniqueUsername(db, wanted), DisplayName = wanted };
                    db.Users.Add(user);
                    await db.SaveChangesAsync();
                }
                var identity = new ClaimsIdentity(UserClaims.For(user), CookieAuthenticationDefaults.AuthenticationScheme);
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Ok(ToMe(user));
            });
        }

        // ---- Me ---------------------------------------------------------------------------

        var me = app.MapGroup("/api/me").RequireAuthorization();

        me.MapGet("/", async (ClaimsPrincipal user, AppDb db) =>
            await db.Users.FindAsync(user.UserId()) is { } u ? Results.Ok(ToMe(u)) : Results.Unauthorized());

        me.MapPatch("/", async (UpdateMeRequest body, ClaimsPrincipal user, AppDb db) =>
        {
            var u = await db.Users.FindAsync(user.UserId());
            if (u == null) return Results.Unauthorized();
            if (body.DisplayName is { } name)
            {
                name = name.Trim();
                if (name.Length > 50) return GameEndpoints.Problem("Display names can be at most 50 characters.");
                u.DisplayName = name;
            }
            if (body.Bio is { } bio)
            {
                if (bio.Length > 500) return GameEndpoints.Problem("Bios can be at most 500 characters.");
                u.Bio = bio.Trim();
            }
            await db.SaveChangesAsync();
            return Results.Ok(ToMe(u));
        });

        // The editor's Publish dialog lists these to offer "upload as a new version of…".
        me.MapGet("/games", async (ClaimsPrincipal user, AppDb db) =>
        {
            int id = user.UserId();
            return await db.Games.Where(g => g.OwnerId == id).OrderByDescending(g => g.UpdatedAt).ToCards().ToListAsync();
        });

        me.MapGet("/tokens", async (ClaimsPrincipal user, AppDb db) =>
        {
            int id = user.UserId();
            return await db.ApiTokens.Where(t => t.UserId == id).OrderByDescending(t => t.CreatedAt)
                .Select(t => new TokenDto(t.Id, t.Name, t.CreatedAt, t.LastUsedAt)).ToListAsync();
        });

        me.MapDelete("/tokens/{id:int}", async (int id, ClaimsPrincipal user, AppDb db) =>
        {
            int userId = user.UserId();
            int deleted = await db.ApiTokens.Where(t => t.Id == id && t.UserId == userId).ExecuteDeleteAsync();
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        // ---- Public profiles ------------------------------------------------------------

        app.MapGet("/api/users/{username}", async (string username, AppDb db) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Username == username);
            if (u == null) return Results.NotFound();
            var games = await db.Games.Where(g => g.OwnerId == u.Id).OrderByDescending(g => g.UpdatedAt).ToCards().ToArrayAsync();
            return Results.Ok(new ProfileDto(Map.Summary(u), u.Bio, u.CreatedAt, games,
                games.Sum(g => g.Downloads), games.Sum(g => g.Likes)));
        });

        // ---- Editor device login (see DeviceLogin.cs) -----------------------------------------

        app.MapPost("/api/device/start", (DeviceLogin device, IConfiguration config, HttpContext ctx) =>
        {
            var r = device.Start();
            string site = (config["Site:PublicUrl"] ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
            return Results.Ok(new
            {
                deviceCode = r.DeviceCode,
                userCode = r.UserCode,
                verificationUrl = $"{site}/device?code={r.UserCode}",
                expiresIn = (int)DeviceLogin.Lifetime.TotalSeconds,
                interval = 2,
            });
        }).RequireRateLimiting("write");

        app.MapGet("/api/device/{userCode}", (string userCode, DeviceLogin device) =>
            device.FindByUserCode(userCode) is { } r && r.ApprovedUserId == null && !r.Denied
                ? Results.Ok(new { userCode = r.UserCode, expiresAt = r.ExpiresAt })
                : Results.NotFound()).RequireAuthorization();

        app.MapPost("/api/device/approve", (DeviceApproveRequest body, DeviceLogin device, ClaimsPrincipal user) =>
        {
            var r = device.FindByUserCode(body.UserCode);
            if (r == null || r.ApprovedUserId != null || r.Denied) return Results.NotFound();
            if (body.Approve) r.ApprovedUserId = user.UserId(); else r.Denied = true;
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapPost("/api/device/token", async (DeviceTokenRequest body, DeviceLogin device, AppDb db) =>
        {
            var r = device.FindByDeviceCode(body.DeviceCode ?? "");
            if (r == null) return Results.Ok(new { status = "expired" });
            if (r.Denied) { device.Complete(r); return Results.Ok(new { status = "denied" }); }
            if (r.ApprovedUserId is not { } userId) return Results.Ok(new { status = "pending" });

            device.Complete(r);
            string token = ApiTokenHandler.NewToken();
            db.ApiTokens.Add(new ApiToken { UserId = userId, TokenHash = ApiTokenHandler.Hash(token) });
            await db.SaveChangesAsync();
            var u = await db.Users.FindAsync(userId);
            return Results.Ok(new { status = "approved", token, username = u!.Username });
        });
    }

    private static MeDto ToMe(User u) =>
        new(u.Username, string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName, u.AvatarUrl, u.Bio, u.GitHubId != null);

    // Only allow redirects back into our own site ("/games/x"), never to
    // "https://evil.example" or protocol-relative "//evil.example".
    private static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}
