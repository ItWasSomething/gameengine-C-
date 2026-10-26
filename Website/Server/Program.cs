using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Server.Api;
using Server.Auth;
using Server.Data;
using Server.Storage;

// MyEngine Games — the website + API where people post games made with the
// engine. The React site (Website/Client) talks to this over /api; the
// editor's Publish button does too, with an API token.

var builder = WebApplication.CreateBuilder(args);

// Everything the site writes (the SQLite file and uploads) lives in one
// folder, so backing up or moving the site = copying that folder.
string dataPath = Path.GetFullPath(builder.Configuration["Site:DataPath"] ?? "App_Data", builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataPath);

builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataPath, "site.db")}"));
builder.Services.AddSingleton(new FileStore(Path.Combine(dataPath, "uploads")));
builder.AddSiteAuth();

// Multipart uploads: ASP.NET's default cap is 128 MB; builds can be up to 1 GB.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = FileStore.MaxBuildBytes + 64 * 1024 * 1024);

// Rate limits, per signed-in user (or per IP for anonymous requests), so one
// script can't flood the site with comments or uploads.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    static string Key(HttpContext c) => c.User.Identity?.Name ?? c.Connection.RemoteIpAddress?.ToString() ?? "anon";
    o.AddPolicy("write", c => RateLimitPartition.GetFixedWindowLimiter(Key(c), _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("upload", c => RateLimitPartition.GetFixedWindowLimiter(Key(c), _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));
});

// Behind a reverse proxy (nginx, Caddy, a cloud load balancer) the real
// scheme/IP arrive in X-Forwarded-* headers; OAuth needs the real https URL.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// No migrations yet: create the database on first run. When the schema
// starts changing on a live site, switch to EF Core migrations.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler(e => e.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new { error = "Something went wrong on the server." });
}));

// Uploaded images at /media/images/<name>. They never change (new upload =
// new random name), so browsers may cache them for a long time.
var store = app.Services.GetRequiredService<FileStore>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(store.ImagesFolder),
    RequestPath = "/media/images",
    OnPrepareResponse = c =>
    {
        c.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        c.Context.Response.Headers.XContentTypeOptions = "nosniff";
    },
});

// The built React site (npm run build writes it to wwwroot/).
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseCsrfHeaderCheck();

app.MapGameEndpoints();
app.MapUserEndpoints();
app.MapGet("/api/health", () => new { ok = true });

// Unknown /api routes are real 404s; any other path is a page of the React
// app (e.g. /games/foo), so hand it index.html and let the client router decide.
app.Map("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();
