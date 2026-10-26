using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Editor.Publishing;

// Talks to the MyEngine Games website (Website/Server) — the editor side of
// the Publish button. Requests are authenticated with the API token from the
// device login (see Server/Auth/DeviceLogin.cs for the whole dance).
public sealed class SiteClient
{
    public record DeviceStart(string DeviceCode, string UserCode, string VerificationUrl, int ExpiresIn, int Interval);
    public record DevicePoll(string Status, string? Token, string? Username);
    public record MyGame(string Slug, string Title, string Tagline, string? LatestVersion, int Downloads, int Likes);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public string BaseUrl { get; }

    public SiteClient(string baseUrl, string? token)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        // Builds can take minutes to upload on a slow connection.
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add("X-MyEngine", "1");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyEngine-Editor/0.1");
        if (token != null) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public string GameUrl(string slug) => $"{BaseUrl}/games/{slug}";

    // ---- Sign in ----------------------------------------------------------------

    public Task<DeviceStart> StartDeviceLoginAsync(CancellationToken ct = default) =>
        SendAsync<DeviceStart>(new HttpRequestMessage(HttpMethod.Post, "api/device/start"), ct);

    public Task<DevicePoll> PollDeviceLoginAsync(string deviceCode, CancellationToken ct = default) =>
        SendAsync<DevicePoll>(new HttpRequestMessage(HttpMethod.Post, "api/device/token") { Content = JsonContent.Create(new { deviceCode }) }, ct);

    // ---- Games --------------------------------------------------------------------

    public Task<List<MyGame>> GetMyGamesAsync(CancellationToken ct = default) =>
        SendAsync<List<MyGame>>(new HttpRequestMessage(HttpMethod.Get, "api/me/games"), ct);

    public async Task<string> CreateGameAsync(string title, string tagline, string description, string tags,
        string version, string changelog, string buildZip, IReadOnlyList<string> screenshots, Action<float>? progress, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(title), "title");
        form.Add(new StringContent(tagline), "tagline");
        form.Add(new StringContent(description), "description");
        form.Add(new StringContent(tags), "tags");
        form.Add(new StringContent(version), "version");
        form.Add(new StringContent(changelog), "changelog");
        foreach (var shot in screenshots) form.Add(FileContent(shot, "image/png"), "screenshots", Path.GetFileName(shot));
        form.Add(new ProgressFileContent(buildZip, progress), "build", Path.GetFileName(buildZip));

        var result = await SendAsync<JsonElement>(new HttpRequestMessage(HttpMethod.Post, "api/games") { Content = form }, ct);
        return result.GetProperty("slug").GetString()!;
    }

    public async Task AddVersionAsync(string slug, string version, string changelog, string buildZip, Action<float>? progress, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(version), "version");
        form.Add(new StringContent(changelog), "changelog");
        form.Add(new ProgressFileContent(buildZip, progress), "build", Path.GetFileName(buildZip));
        await SendAsync<JsonElement?>(new HttpRequestMessage(HttpMethod.Post, $"api/games/{slug}/versions") { Content = form }, ct);
    }

    public async Task UpdateDetailsAsync(string slug, string tagline, string description, string tags, CancellationToken ct)
    {
        var body = new { tagline, description, tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
        await SendAsync<JsonElement?>(new HttpRequestMessage(HttpMethod.Patch, $"api/games/{slug}") { Content = JsonContent.Create(body) }, ct);
    }

    public async Task AddScreenshotsAsync(string slug, IReadOnlyList<string> screenshots, CancellationToken ct)
    {
        if (screenshots.Count == 0) return;
        using var form = new MultipartFormDataContent();
        foreach (var shot in screenshots) form.Add(FileContent(shot, "image/png"), "screenshots", Path.GetFileName(shot));
        await SendAsync<JsonElement?>(new HttpRequestMessage(HttpMethod.Post, $"api/games/{slug}/screenshots") { Content = form }, ct);
    }

    // ---- Plumbing ------------------------------------------------------------------

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new SiteException($"Can't reach {BaseUrl} — is the website running? ({e.Message})");
        }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new SiteException("Not signed in (or the sign-in was revoked on the website). Sign in again.", unauthorized: true);
            if (!response.IsSuccessStatusCode)
            {
                string message = $"The website answered {(int)response.StatusCode} {response.ReasonPhrase}.";
                try { if (JsonDocument.Parse(text).RootElement.TryGetProperty("error", out var err)) message = err.GetString() ?? message; }
                catch (JsonException) { }
                throw new SiteException(message);
            }
            if (string.IsNullOrWhiteSpace(text)) return default!;
            return JsonSerializer.Deserialize<T>(text, Json)!;
        }
    }

    private static ByteArrayContent FileContent(string path, string type)
    {
        var content = new ByteArrayContent(File.ReadAllBytes(path));
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return content;
    }

    // Streams a file into the request body and reports how much has gone out —
    // HttpClient has no built-in upload progress.
    private sealed class ProgressFileContent : HttpContent
    {
        private readonly string _path;
        private readonly Action<float>? _progress;

        public ProgressFileContent(string path, Action<float>? progress)
        {
            _path = path;
            _progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var file = File.OpenRead(_path);
            var buffer = new byte[81920];
            long sent = 0, total = file.Length;
            int read;
            while ((read = await file.ReadAsync(buffer)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read));
                sent += read;
                _progress?.Invoke(total == 0 ? 1 : (float)sent / total);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = new FileInfo(_path).Length;
            return true;
        }
    }
}

public sealed class SiteException(string message, bool unauthorized = false) : Exception(message)
{
    public bool Unauthorized { get; } = unauthorized;
}
