namespace Server.Data;

// The database tables. EF Core turns each class into a table and each
// navigation property (e.g. Game.Owner) into a foreign key.

public class User
{
    public int Id { get; set; }
    // Null for accounts made with the Development-only "dev login".
    public long? GitHubId { get; set; }
    public required string Username { get; set; }
    public string DisplayName { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string Bio { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Game> Games { get; set; } = new();
}

public class Game
{
    public int Id { get; set; }
    // URL-friendly unique name: "/games/my-cool-game".
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string Tagline { get; set; } = "";
    public string Description { get; set; } = "";
    // Path relative to the uploads folder, e.g. "images/3f2a....png".
    public string? CoverPath { get; set; }
    public int DownloadCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public List<GameTag> Tags { get; set; } = new();
    public List<Screenshot> Screenshots { get; set; } = new();
    public List<GameVersion> Versions { get; set; } = new();
    public List<Like> Likes { get; set; } = new();
    public List<Comment> Comments { get; set; } = new();
}

public class GameTag
{
    public int GameId { get; set; }
    public required string Name { get; set; }
}

public class Screenshot
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public required string Path { get; set; }
    public int Order { get; set; }
}

// One uploaded build. A game's newest version is what the big Download
// button gives you; older ones stay downloadable from the version list.
public class GameVersion
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public required string Version { get; set; }
    public string Changelog { get; set; } = "";
    public required string FilePath { get; set; }
    public long SizeBytes { get; set; }
    public int Downloads { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Like
{
    public int UserId { get; set; }
    public int GameId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Comment
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A long-lived login for the editor's Publish button. Only a SHA-256 hash
// of the token is stored, so a leaked database doesn't leak working tokens.
public class ApiToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public required string TokenHash { get; set; }
    public string Name { get; set; } = "MyEngine Editor";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
