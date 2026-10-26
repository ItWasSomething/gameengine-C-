using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Server.Data;

namespace Server.Auth;

// Three ways a request can be "signed in":
//   • Cookie   — the browser, after "Sign in with GitHub" (or dev login).
//   • ApiToken — the editor, with "Authorization: Bearer mye_...".
//   • neither  — anonymous; can still browse and download.
// A tiny "policy scheme" looks at each request and forwards to the right one,
// so endpoints just say RequireAuthorization() and don't care which it was.
public static class AuthSetup
{
    public const string GitHubScheme = "GitHub";
    private const string SmartScheme = "Smart";

    public static bool GitHubConfigured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["GitHub:ClientId"]) && !string.IsNullOrWhiteSpace(config["GitHub:ClientSecret"]);

    public static void AddSiteAuth(this WebApplicationBuilder builder)
    {
        var auth = builder.Services.AddAuthentication(o =>
            {
                o.DefaultScheme = SmartScheme;
                o.DefaultChallengeScheme = SmartScheme;
            })
            .AddPolicyScheme(SmartScheme, "Cookie or API token", o =>
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? ApiTokenHandler.SchemeName
                        : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "mye_session";
                o.Cookie.HttpOnly = true;                     // JavaScript can't read it
                o.Cookie.SameSite = SameSiteMode.Lax;         // other sites can't POST with it
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                // This is an API: answer 401/403 instead of redirecting to a login page.
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(ApiTokenHandler.SchemeName, null);

        if (GitHubConfigured(builder.Configuration))
        {
            auth.AddOAuth(GitHubScheme, o =>
            {
                o.ClientId = builder.Configuration["GitHub:ClientId"]!;
                o.ClientSecret = builder.Configuration["GitHub:ClientSecret"]!;
                o.CallbackPath = "/signin-github";
                o.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
                o.TokenEndpoint = "https://github.com/login/oauth/access_token";
                o.UserInformationEndpoint = "https://api.github.com/user";
                o.Scope.Add("read:user");
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.Events.OnCreatingTicket = OnGitHubSignedIn;
            });
        }

        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<DeviceLogin>();
    }

    // GitHub has said "yes, this is user X". Fetch their profile, create or
    // update our own User row, and put *our* user id in the cookie — the rest
    // of the site never deals with GitHub ids or tokens.
    private static async Task OnGitHubSignedIn(Microsoft.AspNetCore.Authentication.OAuth.OAuthCreatingTicketContext ctx)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("MyEngine-Games");   // GitHub rejects requests without one
        using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var gh = json.RootElement;

        long githubId = gh.GetProperty("id").GetInt64();
        string login = gh.GetProperty("login").GetString()!;
        string? Str(string name) => gh.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.GitHubId == githubId);
        if (user == null)
        {
            user = new User
            {
                GitHubId = githubId,
                Username = await UniqueUsername(db, login),
                DisplayName = Str("name") ?? login,
                Bio = Str("bio") ?? "",
            };
            db.Users.Add(user);
        }
        user.AvatarUrl = Str("avatar_url") ?? user.AvatarUrl;
        await db.SaveChangesAsync();

        ctx.Principal = new ClaimsPrincipal(new ClaimsIdentity(UserClaims.For(user), GitHubScheme));
    }

    public static async Task<string> UniqueUsername(AppDb db, string wanted)
    {
        string name = new string(wanted.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (name.Length == 0) name = "user";
        string candidate = name;
        for (int i = 2; await db.Users.AnyAsync(u => u.Username == candidate); i++) candidate = $"{name}{i}";
        return candidate;
    }

    // Cookie-authenticated POST/PUT/DELETE must carry "X-MyEngine: 1". A
    // malicious page on another site can make your browser *send* a form to
    // us, but it can't add custom headers without a CORS preflight, which we
    // never approve — so this blocks cross-site request forgery. Editor
    // requests use a bearer token, which a browser never attaches by itself.
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var method = ctx.Request.Method;
        bool unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
        bool bearer = ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        bool ours = ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/auth");
        if (unsafeMethod && ours && !bearer && ctx.Request.Headers["X-MyEngine"] != "1")
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-MyEngine header." });
            return;
        }
        await next();
    });
}
