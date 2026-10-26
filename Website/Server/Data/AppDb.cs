using Microsoft.EntityFrameworkCore;

namespace Server.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameTag> Tags => Set<GameTag>();
    public DbSet<Screenshot> Screenshots => Set<Screenshot>();
    public DbSet<GameVersion> Versions => Set<GameVersion>();
    public DbSet<Like> Likes => Set<Like>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    // SQLite has no date type, so EF stores DateTimes as text and reads them
    // back with Kind = Unspecified — which JSON then writes without a "Z", and
    // browsers misread as local time. We only ever store UTC, so say so.
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTime>().HaveConversion<UtcConverter>();
        b.Properties<DateTime?>().HaveConversion<UtcConverter>();
    }

    private sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<User>().HasIndex(u => u.GitHubId).IsUnique();

        b.Entity<Game>().HasIndex(g => g.Slug).IsUnique();
        b.Entity<Game>().HasOne(g => g.Owner).WithMany(u => u.Games).HasForeignKey(g => g.OwnerId);

        // Composite keys: a game has each tag once, a user likes a game once.
        b.Entity<GameTag>().HasKey(t => new { t.GameId, t.Name });
        b.Entity<GameTag>().HasIndex(t => t.Name);
        b.Entity<Like>().HasKey(l => new { l.UserId, l.GameId });

        b.Entity<ApiToken>().HasIndex(t => t.TokenHash).IsUnique();
    }
}
