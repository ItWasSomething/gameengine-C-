using Server.Data;

namespace Server.Api;

// What the API sends back. Database entities are never returned directly:
// that would leak fields (GitHub ids, file paths on disk) and tie the JSON
// shape to the table layout.

public record UserSummary(string Username, string DisplayName, string AvatarUrl);

public record MeDto(string Username, string DisplayName, string AvatarUrl, string Bio, bool HasGitHub);

public record GameCard(
    string Slug, string Title, string Tagline, string? CoverUrl, UserSummary Author,
    string[] Tags, int Likes, int Downloads, string? LatestVersion, DateTime UpdatedAt);

public record ScreenshotDto(int Id, string Url);

public record VersionDto(int Id, string Version, string Changelog, long SizeBytes, int Downloads, DateTime CreatedAt);

public record GameDetail(
    string Slug, string Title, string Tagline, string Description, string? CoverUrl, UserSummary Author,
    string[] Tags, int Likes, int Downloads, bool LikedByMe, bool IsOwner, int CommentCount,
    ScreenshotDto[] Screenshots, VersionDto[] Versions, DateTime CreatedAt, DateTime UpdatedAt);

public record CommentDto(int Id, UserSummary Author, string Body, DateTime CreatedAt, bool CanDelete);

public record PagedGames(GameCard[] Items, int Page, int PageSize, int Total);

public record ProfileDto(UserSummary User, string Bio, DateTime JoinedAt, GameCard[] Games, int TotalDownloads, int TotalLikes);

public record TagCount(string Name, int Count);

public record TokenDto(int Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt);

// ---- Requests ----

public record UpdateGameRequest(string? Title, string? Tagline, string? Description, string[]? Tags);
public record UpdateMeRequest(string? DisplayName, string? Bio);
public record CommentRequest(string Body);
public record DevLoginRequest(string Username);
public record DeviceApproveRequest(string UserCode, bool Approve);
public record DeviceTokenRequest(string DeviceCode);
public record ReorderScreenshotsRequest(int[] Ids);

public static class Map
{
    public static string? Media(string? path) => path == null ? null : "/media/" + path;

    public static UserSummary Summary(User u) => new(u.Username, string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName, u.AvatarUrl);
}
