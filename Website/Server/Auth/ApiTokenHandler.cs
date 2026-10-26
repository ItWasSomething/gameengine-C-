using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Data;

namespace Server.Auth;

// Authenticates "Authorization: Bearer <token>" requests — that's how the
// editor's Publish button talks to the site. The browser uses a cookie
// instead (see Program.cs); both end up as the same kind of user identity.
public sealed class ApiTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        string hash = Hash(header["Bearer ".Length..].Trim());
        var token = await db.ApiTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token == null) return AuthenticateResult.Fail("Unknown or revoked token.");

        token.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var identity = new ClaimsIdentity(UserClaims.For(token.User), SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    public static string NewToken() => "mye_" + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class UserClaims
{
    public static IEnumerable<Claim> For(User user) =>
    [
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
    ];

    // The signed-in user's database id (only call on authorized endpoints).
    public static int UserId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static int? UserIdOrNull(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : null;
}
