namespace Server.Storage;

// Saves uploads to a folder on disk:
//   <uploads>/images/<random>.png   — covers + screenshots, served at /media/images/...
//   <uploads>/builds/<random>.zip   — game downloads, only served through the
//                                     download endpoint (so it can count them)
//
// Files get random names, never the uploader's, so nobody can overwrite
// someone else's file or sneak "../" into a path. The type is decided by the
// file's first bytes ("magic numbers"), not by its extension or the
// Content-Type the browser claims — both of those are trivially faked.
public sealed class FileStore
{
    public const long MaxImageBytes = 8 * 1024 * 1024;
    public const long MaxBuildBytes = 1024L * 1024 * 1024;

    public string Root { get; }
    public string ImagesFolder => Path.Combine(Root, "images");
    public string BuildsFolder => Path.Combine(Root, "builds");

    public FileStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(ImagesFolder);
        Directory.CreateDirectory(BuildsFolder);
    }

    // Returns the stored relative path ("images/abc.png"), or an error message.
    public async Task<(string? Path, string? Error)> SaveImageAsync(IFormFile file)
    {
        if (file.Length == 0) return (null, "Image is empty.");
        if (file.Length > MaxImageBytes) return (null, $"Images can be at most {MaxImageBytes / 1024 / 1024} MB.");

        var header = new byte[12];
        await using (var s = file.OpenReadStream()) _ = await s.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
        string? ext = ImageExtension(header);
        if (ext == null) return (null, "Only PNG, JPEG, GIF and WebP images are allowed.");

        return ("images/" + await WriteAsync(file, ImagesFolder, ext), null);
    }

    public async Task<(string? Path, string? Error)> SaveBuildAsync(IFormFile file)
    {
        if (file.Length == 0) return (null, "Build file is empty.");
        if (file.Length > MaxBuildBytes) return (null, "Builds can be at most 1 GB.");

        var header = new byte[4];
        await using (var s = file.OpenReadStream()) _ = await s.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
        // Every .zip starts with "PK\x03\x04".
        if (header is not [0x50, 0x4B, 0x03, 0x04]) return (null, "Builds must be uploaded as a .zip file.");

        return ("builds/" + await WriteAsync(file, BuildsFolder, ".zip"), null);
    }

    public string FullPath(string relative)
    {
        string full = Path.GetFullPath(Path.Combine(Root, relative));
        // Belt and braces: stored paths are always ours, but never let one
        // resolve outside the uploads folder.
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Path escapes the uploads folder.");
        return full;
    }

    public void Delete(string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return;
        try { File.Delete(FullPath(relative)); } catch (IOException) { /* already gone / in use: leave it */ }
    }

    private static async Task<string> WriteAsync(IFormFile file, string folder, string ext)
    {
        string name = Guid.NewGuid().ToString("N") + ext;
        await using var output = File.Create(Path.Combine(folder, name));
        await file.CopyToAsync(output);
        return name;
    }

    private static string? ImageExtension(byte[] h)
    {
        if (h is [0x89, 0x50, 0x4E, 0x47, ..]) return ".png";
        if (h is [0xFF, 0xD8, 0xFF, ..]) return ".jpg";
        if (h is [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..]) return ".gif";
        if (h is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P']) return ".webp";
        return null;
    }
}
