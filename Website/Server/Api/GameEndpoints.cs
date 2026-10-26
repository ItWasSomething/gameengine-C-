using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Server.Auth;
using Server.Data;
using Server.Storage;

namespace Server.Api;

// Everything under /api/games: browse, post, edit, versions, screenshots,
// downloads, likes and comments.
public static partial class GameEndpoints
{
    public const int MaxScreenshots = 8;
    public const int MaxTags = 6;

    public static void MapGameEndpoints(this WebApplication app)
    {
        var games = app.MapGroup("/api/games");

        games.MapGet("/", Browse);
        games.MapGet("/{slug}", Get);
        games.MapPost("/", Create).RequireAuthorization().RequireRateLimiting("upload");
        games.MapPatch("/{slug}", Update).RequireAuthorization();
        games.MapDelete("/{slug}", Delete).RequireAuthorization();

        games.MapPut("/{slug}/cover", SetCover).RequireAuthorization().RequireRateLimiting("upload");
        games.MapPost("/{slug}/screenshots", AddScreenshots).RequireAuthorization().RequireRateLimiting("upload");
        games.MapDelete("/{slug}/screenshots/{id:int}", DeleteScreenshot).RequireAuthorization();
        games.MapPut("/{slug}/screenshots/order", ReorderScreenshots).RequireAuthorization();

        games.MapPost("/{slug}/versions", AddVersion).RequireAuthorization().RequireRateLimiting("upload");
        games.MapDelete("/{slug}/versions/{id:int}", DeleteVersion).RequireAuthorization();
        games.MapGet("/{slug}/download", (string slug, AppDb db, FileStore files) => Download(slug, null, db, files));
        games.MapGet("/{slug}/download/{versionId:int}", (string slug, int versionId, AppDb db, FileStore files) => Download(slug, versionId, db, files));

        games.MapPost("/{slug}/like", (string slug, ClaimsPrincipal user, AppDb db) => SetLike(slug, true, user, db)).RequireAuthorization();
        games.MapDelete("/{slug}/like", (string slug, ClaimsPrincipal user, AppDb db) => SetLike(slug, false, user, db)).RequireAuthorization();

        games.MapGet("/{slug}/comments", ListComments);
        games.MapPost("/{slug}/comments", AddComment).RequireAuthorization().RequireRateLimiting("write");
        app.MapDelete("/api/comments/{id:int}", DeleteComment).RequireAuthorization();

        app.MapGet("/api/tags", async (AppDb db) =>
            (await db.Tags.GroupBy(t => t.Name)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(t => t.Count).ThenBy(t => t.Name)
                .Take(40).ToListAsync())
            .Select(t => new TagCount(t.Name, t.Count)));
    }

    // ---- Browse ---------------------------------------------------------------------

    public static IQueryable<GameCard> ToCards(this IQueryable<Game> q) => q.Select(g => new GameCard(
        g.Slug, g.Title, g.Tagline,
        g.CoverPath == null ? null : "/media/" + g.CoverPath,
        new UserSummary(g.Owner.Username, g.Owner.DisplayName == "" ? g.Owner.Username : g.Owner.DisplayName, g.Owner.AvatarUrl),
        g.Tags.Select(t => t.Name).ToArray(),
        g.Likes.Count,
        g.DownloadCount,
        g.Versions.OrderByDescending(v => v.CreatedAt).Select(v => v.Version).FirstOrDefault(),
        g.UpdatedAt));

    private static async Task<PagedGames> Browse(AppDb db, string? q, string? tag, string? sort, int page = 1, int pageSize = 24)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 60);

        IQueryable<Game> query = db.Games;
        if (!string.IsNullOrWhiteSpace(q))
        {
            // SQLite's LIKE is already case-insensitive for ASCII.
            string pattern = $"%{q.Trim().Replace("%", "").Replace("_", "")}%";
            query = query.Where(g => EF.Functions.Like(g.Title, pattern) || EF.Functions.Like(g.Tagline, pattern)
                                     || g.Tags.Any(t => EF.Functions.Like(t.Name, pattern)));
        }
        if (!string.IsNullOrWhiteSpace(tag))
        {
            string t = tag.Trim().ToLowerInvariant();
            query = query.Where(g => g.Tags.Any(x => x.Name == t));
        }

        query = sort switch
        {
            "popular" => query.OrderByDescending(g => g.Likes.Count).ThenByDescending(g => g.DownloadCount),
            "downloads" => query.OrderByDescending(g => g.DownloadCount),
            "updated" => query.OrderByDescending(g => g.UpdatedAt),
            _ => query.OrderByDescending(g => g.CreatedAt),
        };

        int total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToCards().ToArrayAsync();
        return new PagedGames(items, page, pageSize, total);
    }

    private static async Task<IResult> Get(string slug, AppDb db, ClaimsPrincipal user)
    {
        int? me = user.UserIdOrNull();
        var g = await db.Games.AsSplitQuery()
            .Include(g => g.Owner).Include(g => g.Tags).Include(g => g.Screenshots).Include(g => g.Versions)
            .FirstOrDefaultAsync(g => g.Slug == slug);
        if (g == null) return Results.NotFound();

        int likes = await db.Likes.CountAsync(l => l.GameId == g.Id);
        bool liked = me != null && await db.Likes.AnyAsync(l => l.GameId == g.Id && l.UserId == me);
        int comments = await db.Comments.CountAsync(c => c.GameId == g.Id);

        return Results.Ok(new GameDetail(
            g.Slug, g.Title, g.Tagline, g.Description, Map.Media(g.CoverPath), Map.Summary(g.Owner),
            g.Tags.Select(t => t.Name).Order().ToArray(), likes, g.DownloadCount, liked, g.OwnerId == me, comments,
            g.Screenshots.OrderBy(s => s.Order).Select(s => new ScreenshotDto(s.Id, Map.Media(s.Path)!)).ToArray(),
            g.Versions.OrderByDescending(v => v.CreatedAt)
                .Select(v => new VersionDto(v.Id, v.Version, v.Changelog, v.SizeBytes, v.Downloads, v.CreatedAt)).ToArray(),
            g.CreatedAt, g.UpdatedAt));
    }

    // ---- Create / edit ------------------------------------------------------------------

    // multipart/form-data: title, tagline, description, tags ("a, b"),
    // version, changelog, build (.zip, required), cover (image), screenshots (images).
    private static async Task<IResult> Create(HttpContext ctx, AppDb db, FileStore files)
    {
        AllowLargeBody(ctx);
        var form = await ctx.Request.ReadFormAsync();

        string title = form["title"].ToString().Trim();
        string? error = ValidateText(title, form["tagline"], form["description"]);
        if (error != null) return Problem(error);
        var tags = ParseTags(form["tags"]);
        if (tags.Error != null) return Problem(tags.Error);
        string version = CleanVersion(form["version"]);
        if (form.Files["build"] is not { } buildFile) return Problem("Attach your game build as a .zip file.");
        var shots = form.Files.GetFiles("screenshots");
        if (shots.Count > MaxScreenshots) return Problem($"At most {MaxScreenshots} screenshots.");

        // Validate + save every file first; if anything fails, delete what was
        // already written so no orphans are left on disk.
        var saved = new List<string>();
        IResult Fail(string message) { foreach (var p in saved) files.Delete(p); return Problem(message); }

        var build = await files.SaveBuildAsync(buildFile);
        if (build.Error != null) return Fail(build.Error);
        saved.Add(build.Path!);

        string? coverPath = null;
        if (form.Files["cover"] is { } coverFile)
        {
            var cover = await files.SaveImageAsync(coverFile);
            if (cover.Error != null) return Fail("Cover: " + cover.Error);
            saved.Add(coverPath = cover.Path!);
        }

        var shotPaths = new List<string>();
        foreach (var shot in shots)
        {
            var s = await files.SaveImageAsync(shot);
            if (s.Error != null) return Fail($"Screenshot \"{shot.FileName}\": {s.Error}");
            saved.Add(s.Path!);
            shotPaths.Add(s.Path!);
        }
        // No cover given: the first screenshot doubles as one.
        coverPath ??= shotPaths.FirstOrDefault();

        var game = new Game
        {
            Slug = await UniqueSlug(db, title),
            Title = title,
            Tagline = form["tagline"].ToString().Trim(),
            Description = form["description"].ToString().Trim(),
            CoverPath = coverPath,
            OwnerId = ctx.User.UserId(),
            Tags = tags.Tags.Select(t => new GameTag { Name = t }).ToList(),
            Screenshots = shotPaths.Select((p, i) => new Screenshot { Path = p, Order = i }).ToList(),
            Versions = { new GameVersion { Version = version, Changelog = form["changelog"].ToString().Trim(), FilePath = build.Path!, SizeBytes = buildFile.Length } },
        };
        db.Games.Add(game);
        await db.SaveChangesAsync();
        return Results.Created($"/api/games/{game.Slug}", new { slug = game.Slug });
    }

    private static async Task<IResult> Update(string slug, UpdateGameRequest body, AppDb db, ClaimsPrincipal user)
    {
        var (game, denied) = await OwnedGame(db, slug, user, includeTags: true);
        if (game == null) return denied!;

        string title = body.Title?.Trim() ?? game.Title;
        string? error = ValidateText(title, body.Tagline ?? game.Tagline, body.Description ?? game.Description);
        if (error != null) return Problem(error);

        game.Title = title;
        if (body.Tagline != null) game.Tagline = body.Tagline.Trim();
        if (body.Description != null) game.Description = body.Description.Trim();
        if (body.Tags != null)
        {
            var tags = ParseTags(string.Join(',', body.Tags));
            if (tags.Error != null) return Problem(tags.Error);
            // Diff instead of replacing the whole list: removing and re-adding
            // the same (GameId, Name) key in one save confuses EF's tracking.
            game.Tags.RemoveAll(t => !tags.Tags.Contains(t.Name));
            foreach (var name in tags.Tags.Where(n => game.Tags.All(t => t.Name != n)))
                game.Tags.Add(new GameTag { Name = name });
        }
        game.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Delete(string slug, AppDb db, FileStore files, ClaimsPrincipal user)
    {
        var (game, denied) = await OwnedGame(db, slug, user);
        if (game == null) return denied!;
        await db.Entry(game).Collection(g => g.Screenshots).LoadAsync();
        await db.Entry(game).Collection(g => g.Versions).LoadAsync();

        var paths = game.Screenshots.Select(s => s.Path).Concat(game.Versions.Select(v => v.FilePath)).Append(game.CoverPath).ToList();
        db.Games.Remove(game);   // tags, screenshots, versions, likes, comments cascade
        await db.SaveChangesAsync();
        foreach (var p in paths.Distinct()) files.Delete(p);
        return Results.NoContent();
    }

    // ---- Images ---------------------------------------------------------------------------

    private static async Task<IResult> SetCover(string slug, HttpContext ctx, AppDb db, FileStore files)
    {
        var (game, denied) = await OwnedGame(db, slug, ctx.User);
        if (game == null) return denied!;
        var form = await ctx.Request.ReadFormAsync();
        if (form.Files["cover"] is not { } file) return Problem("No image attached.");

        var saved = await files.SaveImageAsync(file);
        if (saved.Error != null) return Problem(saved.Error);

        string? old = game.CoverPath;
        game.CoverPath = saved.Path;
        game.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        // The old cover might also be a screenshot (the "first screenshot" fallback).
        if (old != null && !await db.Screenshots.AnyAsync(s => s.Path == old)) files.Delete(old);
        return Results.Ok(new { coverUrl = Map.Media(game.CoverPath) });
    }

    private static async Task<IResult> AddScreenshots(string slug, HttpContext ctx, AppDb db, FileStore files)
    {
        var (game, denied) = await OwnedGame(db, slug, ctx.User);
        if (game == null) return denied!;
        var form = await ctx.Request.ReadFormAsync();
        var uploads = form.Files.GetFiles("screenshots");
        if (uploads.Count == 0) return Problem("No images attached.");

        int existing = await db.Screenshots.CountAsync(s => s.GameId == game.Id);
        if (existing + uploads.Count > MaxScreenshots) return Problem($"A game can have at most {MaxScreenshots} screenshots.");
        int order = existing == 0 ? 0 : await db.Screenshots.Where(s => s.GameId == game.Id).MaxAsync(s => s.Order) + 1;

        var added = new List<Screenshot>();
        foreach (var upload in uploads)
        {
            var saved = await files.SaveImageAsync(upload);
            if (saved.Error != null)
            {
                foreach (var a in added) files.Delete(a.Path);
                return Problem($"\"{upload.FileName}\": {saved.Error}");
            }
            added.Add(new Screenshot { GameId = game.Id, Path = saved.Path!, Order = order++ });
        }
        db.Screenshots.AddRange(added);
        game.CoverPath ??= added[0].Path;
        game.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(added.Select(s => new ScreenshotDto(s.Id, Map.Media(s.Path)!)));
    }

    private static async Task<IResult> DeleteScreenshot(string slug, int id, AppDb db, FileStore files, ClaimsPrincipal user)
    {
        var (game, denied) = await OwnedGame(db, slug, user);
        if (game == null) return denied!;
        var shot = await db.Screenshots.FirstOrDefaultAsync(s => s.Id == id && s.GameId == game.Id);
        if (shot == null) return Results.NotFound();

        db.Screenshots.Remove(shot);
        bool usedAsCover = game.CoverPath == shot.Path;
        if (usedAsCover)
            game.CoverPath = await db.Screenshots.Where(s => s.GameId == game.Id && s.Id != id)
                .OrderBy(s => s.Order).Select(s => s.Path).FirstOrDefaultAsync();
        await db.SaveChangesAsync();
        files.Delete(shot.Path);
        return Results.NoContent();
    }

    private static async Task<IResult> ReorderScreenshots(string slug, ReorderScreenshotsRequest body, AppDb db, ClaimsPrincipal user)
    {
        var (game, denied) = await OwnedGame(db, slug, user);
        if (game == null) return denied!;
        var shots = await db.Screenshots.Where(s => s.GameId == game.Id).ToListAsync();
        for (int i = 0; i < body.Ids.Length; i++)
            if (shots.FirstOrDefault(s => s.Id == body.Ids[i]) is { } s) s.Order = i;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // ---- Versions + downloads ---------------------------------------------------------------

    private static async Task<IResult> AddVersion(string slug, HttpContext ctx, AppDb db, FileStore files)
    {
        AllowLargeBody(ctx);
        var (game, denied) = await OwnedGame(db, slug, ctx.User);
        if (game == null) return denied!;
        var form = await ctx.Request.ReadFormAsync();
        if (form.Files["build"] is not { } file) return Problem("Attach the new build as a .zip file.");

        string version = CleanVersion(form["version"]);
        if (await db.Versions.AnyAsync(v => v.GameId == game.Id && v.Version == version))
            return Problem($"Version {version} already exists — pick a new version number.");

        var saved = await files.SaveBuildAsync(file);
        if (saved.Error != null) return Problem(saved.Error);

        var v = new GameVersion { GameId = game.Id, Version = version, Changelog = form["changelog"].ToString().Trim(), FilePath = saved.Path!, SizeBytes = file.Length };
        db.Versions.Add(v);
        game.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new VersionDto(v.Id, v.Version, v.Changelog, v.SizeBytes, 0, v.CreatedAt));
    }

    private static async Task<IResult> DeleteVersion(string slug, int id, AppDb db, FileStore files, ClaimsPrincipal user)
    {
        var (game, denied) = await OwnedGame(db, slug, user);
        if (game == null) return denied!;
        var versions = await db.Versions.Where(v => v.GameId == game.Id).ToListAsync();
        var target = versions.FirstOrDefault(v => v.Id == id);
        if (target == null) return Results.NotFound();
        if (versions.Count == 1) return Problem("A game needs at least one version. Upload a new one first, or delete the game.");

        db.Versions.Remove(target);
        await db.SaveChangesAsync();
        files.Delete(target.FilePath);
        return Results.NoContent();
    }

    private static async Task<IResult> Download(string slug, int? versionId, AppDb db, FileStore files)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Slug == slug);
        if (game == null) return Results.NotFound();
        var versions = db.Versions.Where(v => v.GameId == game.Id);
        var version = versionId is { } id
            ? await versions.FirstOrDefaultAsync(v => v.Id == id)
            : await versions.OrderByDescending(v => v.CreatedAt).FirstOrDefaultAsync();
        if (version == null) return Results.NotFound();

        // Count with a single UPDATE statement, so two downloads at the same
        // moment can't overwrite each other's +1.
        await db.Games.Where(g => g.Id == game.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.DownloadCount, g => g.DownloadCount + 1));
        await db.Versions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Downloads, v => v.Downloads + 1));

        string fileName = $"{game.Slug}-{SafeFilePart(version.Version)}.zip";
        return Results.File(files.FullPath(version.FilePath), "application/zip", fileName, enableRangeProcessing: true);
    }

    // ---- Likes + comments --------------------------------------------------------------------

    private static async Task<IResult> SetLike(string slug, bool like, ClaimsPrincipal user, AppDb db)
    {
        int gameId = await db.Games.Where(g => g.Slug == slug).Select(g => g.Id).FirstOrDefaultAsync();
        if (gameId == 0) return Results.NotFound();
        int me = user.UserId();
        var existing = await db.Likes.FindAsync(me, gameId);
        if (like && existing == null) db.Likes.Add(new Like { UserId = me, GameId = gameId });
        if (!like && existing != null) db.Likes.Remove(existing);
        await db.SaveChangesAsync();
        return Results.Ok(new { liked = like, likes = await db.Likes.CountAsync(l => l.GameId == gameId) });
    }

    private static async Task<IResult> ListComments(string slug, AppDb db, ClaimsPrincipal user)
    {
        var game = await db.Games.Where(g => g.Slug == slug).Select(g => new { g.Id, g.OwnerId }).FirstOrDefaultAsync();
        if (game == null) return Results.NotFound();
        int? me = user.UserIdOrNull();
        var comments = await db.Comments.Where(c => c.GameId == game.Id).OrderByDescending(c => c.CreatedAt).Take(200)
            .Select(c => new CommentDto(c.Id,
                new UserSummary(c.User.Username, c.User.DisplayName == "" ? c.User.Username : c.User.DisplayName, c.User.AvatarUrl),
                c.Body, c.CreatedAt, me != null && (c.UserId == me || game.OwnerId == me)))
            .ToListAsync();
        return Results.Ok(comments);
    }

    private static async Task<IResult> AddComment(string slug, CommentRequest body, AppDb db, ClaimsPrincipal user)
    {
        string text = body.Body?.Trim() ?? "";
        if (text.Length == 0) return Problem("Comment is empty.");
        if (text.Length > 2000) return Problem("Comments can be at most 2000 characters.");
        int gameId = await db.Games.Where(g => g.Slug == slug).Select(g => g.Id).FirstOrDefaultAsync();
        if (gameId == 0) return Results.NotFound();

        var comment = new Comment { GameId = gameId, UserId = user.UserId(), Body = text };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        var author = await db.Users.FindAsync(comment.UserId);
        return Results.Ok(new CommentDto(comment.Id, Map.Summary(author!), comment.Body, comment.CreatedAt, true));
    }

    // Your own comments, or any comment on your game (moderation).
    private static async Task<IResult> DeleteComment(int id, AppDb db, ClaimsPrincipal user)
    {
        int me = user.UserId();
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id);
        if (comment == null) return Results.NotFound();
        int owner = await db.Games.Where(g => g.Id == comment.GameId).Select(g => g.OwnerId).FirstAsync();
        if (comment.UserId != me && owner != me) return Results.Forbid();
        db.Comments.Remove(comment);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static async Task<(Game? Game, IResult? Denied)> OwnedGame(AppDb db, string slug, ClaimsPrincipal user, bool includeTags = false)
    {
        IQueryable<Game> q = db.Games;
        if (includeTags) q = q.Include(g => g.Tags);
        var game = await q.FirstOrDefaultAsync(g => g.Slug == slug);
        if (game == null) return (null, Results.NotFound());
        if (game.OwnerId != user.UserId()) return (null, Results.Forbid());
        return (game, null);
    }

    public static IResult Problem(string message) => Results.BadRequest(new { error = message });

    // Kestrel caps request bodies at ~30 MB by default; builds can be bigger.
    private static void AllowLargeBody(HttpContext ctx)
    {
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = FileStore.MaxBuildBytes + 64 * 1024 * 1024;
    }

    private static string? ValidateText(string title, string? tagline, string? description)
    {
        if (title.Length == 0) return "Give your game a title.";
        if (title.Length > 80) return "Titles can be at most 80 characters.";
        if ((tagline ?? "").Trim().Length > 140) return "Taglines can be at most 140 characters.";
        if ((description ?? "").Length > 20000) return "Descriptions can be at most 20,000 characters.";
        return null;
    }

    private static (string[] Tags, string? Error) ParseTags(string? raw)
    {
        var tags = (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => TagChars().Replace(t.ToLowerInvariant().Replace(' ', '-'), ""))
            .Where(t => t.Length > 0).Distinct().ToArray();
        if (tags.Length > MaxTags) return (tags, $"At most {MaxTags} tags.");
        if (tags.Any(t => t.Length > 24)) return (tags, "Tags can be at most 24 characters.");
        return (tags, null);
    }

    private static string CleanVersion(string? raw)
    {
        string v = (raw ?? "").Trim();
        if (v.Length == 0) return "1.0";
        return v.Length > 32 ? v[..32] : v;
    }

    private static string SafeFilePart(string s) => string.Concat(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'));

    private static async Task<string> UniqueSlug(AppDb db, string title)
    {
        string slug = SlugChars().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0) slug = "game";
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        string candidate = slug;
        for (int i = 2; await db.Games.AnyAsync(g => g.Slug == candidate); i++) candidate = $"{slug}-{i}";
        return candidate;
    }

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex SlugChars();
    [GeneratedRegex("[^a-z0-9-]")] private static partial Regex TagChars();
}
