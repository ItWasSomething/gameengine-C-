using System.Diagnostics;
using System.Numerics;
using Editor.Build;
using ImGuiNET;

namespace Editor.Publishing;

// File → Publish to MyEngine Games… : sign in, fill in the store page, then
// Build & Publish = build the zip and upload it, as a new game or as a new
// version of one you posted before.
//
// Everything slow (network, building) runs on background tasks that only set
// fields; Draw() reads those fields every frame. No ImGui calls off the main thread.
public sealed class PublishWindow
{
    private readonly EditorContext _ctx;
    private readonly BuildWindow _build;
    private readonly Func<string, bool> _captureGameView;   // saves the Game view to a PNG
    public bool Open;

    private EditorPrefs Prefs => EditorPrefs.Instance;
    private GameSettings Settings => _build.Settings;
    private SiteClient Client => new(Prefs.SiteUrl, Prefs.ApiToken);

    // Sign-in state
    private SiteClient.DeviceStart? _device;
    private CancellationTokenSource? _signInCancel;
    private string? _signInError;

    // Your games on the site (for "update existing")
    private List<SiteClient.MyGame>? _myGames;
    private bool _loadingGames;
    private string? _gamesError;
    private int _target = -1;   // -1 = new game, otherwise index into _myGames

    // Form fields that aren't stored in GameSettings
    private string _changelog = "";
    private readonly HashSet<string> _selectedShots = new();
    private bool _uploadShotsOnUpdate;
    private string _siteUrlDraft = "";

    // Publish progress
    private enum Phase { Idle, Building, Uploading, Done, Failed }
    private Phase _phase = Phase.Idle;
    private float _uploadProgress;
    private string? _publishError;
    private string? _publishedUrl;
    private CancellationTokenSource? _publishCancel;

    public PublishWindow(EditorContext ctx, BuildWindow build, Func<string, bool> captureGameView)
    {
        _ctx = ctx;
        _build = build;
        _captureGameView = captureGameView;
    }

    private string ScreenshotFolder => Path.Combine(_ctx.ProjectDirectory, "Screenshots");

    public void Show()
    {
        Open = true;
        _siteUrlDraft = Prefs.SiteUrl;
        if (_phase is Phase.Done or Phase.Failed) _phase = Phase.Idle;
        if (Prefs.ApiToken != null) RefreshMyGames();
        // Preselect every screenshot on first open.
        if (_selectedShots.Count == 0) foreach (var s in Screenshots()) _selectedShots.Add(s);
    }

    private IEnumerable<string> Screenshots() =>
        Directory.Exists(ScreenshotFolder)
            ? Directory.EnumerateFiles(ScreenshotFolder, "*.png").OrderBy(f => File.GetCreationTimeUtc(f))
            : [];

    // ---- Background work --------------------------------------------------------------

    private void StartSignIn()
    {
        _signInError = null;
        _signInCancel = new CancellationTokenSource();
        var ct = _signInCancel.Token;
        var client = Client;
        Task.Run(async () =>
        {
            try
            {
                var start = await client.StartDeviceLoginAsync(ct);
                _device = start;
                OpenUrl(start.VerificationUrl);
                var deadline = DateTime.UtcNow.AddSeconds(start.ExpiresIn);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, start.Interval)), ct);
                    var poll = await client.PollDeviceLoginAsync(start.DeviceCode, ct);
                    if (poll.Status == "approved")
                    {
                        Prefs.ApiToken = poll.Token;
                        Prefs.SiteUsername = poll.Username;
                        Prefs.Save();
                        Engine.Log.Info($"Signed in to MyEngine Games as {poll.Username}.");
                        _device = null;
                        RefreshMyGames();
                        return;
                    }
                    if (poll.Status is "denied" or "expired")
                    {
                        _signInError = poll.Status == "denied" ? "Sign-in was denied in the browser." : "The sign-in code expired. Try again.";
                        _device = null;
                        return;
                    }
                }
                _signInError = "The sign-in code expired. Try again.";
                _device = null;
            }
            catch (OperationCanceledException) { _device = null; }
            catch (Exception e) { _signInError = e.Message; _device = null; }
        }, ct);
    }

    private void SignOut()
    {
        Prefs.ApiToken = null;
        Prefs.SiteUsername = null;
        Prefs.Save();
        _myGames = null;
        _target = -1;
    }

    private void RefreshMyGames()
    {
        if (_loadingGames) return;
        _loadingGames = true;
        _gamesError = null;
        var client = Client;
        Task.Run(async () =>
        {
            try
            {
                var games = await client.GetMyGamesAsync();
                _myGames = games;
                // Default target: the game this project was published as before.
                _target = Settings.PublishedSlug is { } slug ? games.FindIndex(g => g.Slug == slug) : -1;
            }
            catch (SiteException e)
            {
                _gamesError = e.Message;
                if (e.Unauthorized) SignOut();
            }
            finally { _loadingGames = false; }
        });
    }

    private void StartPublish()
    {
        var buildTask = _build.StartBuild();
        if (buildTask == null) { _publishError = "Couldn't start the build — see the Console."; _phase = Phase.Failed; return; }

        _phase = Phase.Building;
        _publishError = null;
        _publishedUrl = null;
        _uploadProgress = 0;
        _publishCancel = new CancellationTokenSource();
        var ct = _publishCancel.Token;
        var client = Client;
        var s = Settings;
        var target = _target >= 0 && _myGames != null && _target < _myGames.Count ? _myGames[_target] : null;
        var shots = Screenshots().Where(_selectedShots.Contains).Take(8).ToList();
        string changelog = _changelog;
        bool uploadShots = _uploadShotsOnUpdate;

        Task.Run(async () =>
        {
            try
            {
                string? zip = await buildTask;
                if (zip == null) throw new Exception(_build.Builder.Error ?? "Build failed.");
                ct.ThrowIfCancellationRequested();

                _phase = Phase.Uploading;
                string slug;
                if (target == null)
                {
                    slug = await client.CreateGameAsync(s.ProductName, s.Tagline, s.Description, s.Tags, s.Version, changelog,
                        zip, shots, p => _uploadProgress = p, ct);
                }
                else
                {
                    slug = target.Slug;
                    await client.AddVersionAsync(slug, s.Version, changelog, zip, p => _uploadProgress = p, ct);
                    await client.UpdateDetailsAsync(slug, s.Tagline, s.Description, s.Tags, ct);
                    if (uploadShots) await client.AddScreenshotsAsync(slug, shots, ct);
                }

                s.PublishedSlug = slug;
                s.Save(_ctx.ProjectDirectory);
                _publishedUrl = client.GameUrl(slug);
                _phase = Phase.Done;
                _changelog = "";
                Engine.Log.Info($"Published {s.ProductName} {s.Version}: {_publishedUrl}");
                RefreshMyGames();
            }
            catch (OperationCanceledException) { _publishError = "Cancelled."; _phase = Phase.Failed; }
            catch (Exception e)
            {
                _publishError = e.Message;
                _phase = Phase.Failed;
                if (e is SiteException { Unauthorized: true }) SignOut();
                Engine.Log.Error($"Publish failed: {e.Message}");
            }
        });
    }

    // ---- UI --------------------------------------------------------------------------

    public void Draw()
    {
        if (!Open) return;
        ImGui.SetNextWindowSize(new Vector2(640, 720), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.Begin("Publish to MyEngine Games###Publish", ref Open, ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        DrawAccount();
        if (Prefs.ApiToken != null && _device == null)
        {
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
            bool busy = _phase is Phase.Building or Phase.Uploading;
            ImGui.BeginDisabled(busy);
            DrawTarget();
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
            DrawStorePage();
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
            DrawScreenshots();
            ImGui.EndDisabled();
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
            DrawPublishBar();
        }
        ImGui.End();
    }

    private void DrawAccount()
    {
        EditorTheme.Label("Account");
        if (Prefs.ApiToken != null)
        {
            ImGui.TextUnformatted("Signed in as ");
            ImGui.SameLine(0, 0);
            ImGui.PushFont(EditorTheme.BoldFont);
            ImGui.TextUnformatted("@" + (Prefs.SiteUsername ?? "?"));
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextDisabled($"on {Prefs.SiteUrl}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Sign out")) SignOut();
            return;
        }

        if (_device is { } d)
        {
            ImGui.PushTextWrapPos(600);
            ImGui.TextUnformatted("Your browser opened the MyEngine Games website. Sign in there (with GitHub) and approve this code:");
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            ImGui.PushFont(EditorTheme.HeadingFont);
            ImGui.TextUnformatted(d.UserCode);
            ImGui.PopFont();
            ImGui.TextDisabled("Waiting for approval...");
            if (ImGui.Button("Open browser again")) OpenUrl(d.VerificationUrl);
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) _signInCancel?.Cancel();
            return;
        }

        ImGui.PushTextWrapPos(600);
        ImGui.TextUnformatted("Sign in to post your game on the MyEngine Games website. The editor opens your browser; you approve it there — your GitHub password never touches the editor.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (EditorTheme.PrimaryButton("SIGN IN IN BROWSER", new Vector2(220, 34))) { Prefs.SiteUrl = _siteUrlDraft.TrimEnd('/'); Prefs.Save(); StartSignIn(); }
        if (_signInError != null) ImGui.TextColored(EditorTheme.Error, _signInError);

        ImGui.Spacing();
        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Website URL", ref _siteUrlDraft, 200);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Where the MyEngine Games server runs.\nLocal dev server: http://localhost:5080");
    }

    private void DrawTarget()
    {
        EditorTheme.Label("Publish as");
        if (EditorTheme.ToggleButton("A new game", _target < 0)) _target = -1;
        ImGui.SameLine(0, 0);
        bool hasGames = _myGames is { Count: > 0 };
        ImGui.BeginDisabled(!hasGames);
        if (EditorTheme.ToggleButton("A new version of...", _target >= 0) && hasGames) _target = Math.Max(0, _target);
        ImGui.EndDisabled();
        if (!hasGames && !_loadingGames && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("You haven't published a game yet.");
        if (_target >= 0 && _myGames != null)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(260);
            var current = _myGames[Math.Min(_target, _myGames.Count - 1)];
            if (ImGui.BeginCombo("##game", $"{current.Title}  (v{current.LatestVersion})"))
            {
                for (int i = 0; i < _myGames.Count; i++)
                    if (ImGui.Selectable($"{_myGames[i].Title}  (v{_myGames[i].LatestVersion})##{i}", i == _target)) _target = i;
                ImGui.EndCombo();
            }
        }
        if (_loadingGames) ImGui.TextDisabled("Loading your games...");
        if (_gamesError != null) ImGui.TextColored(EditorTheme.Error, _gamesError);
        if (_target >= 0 && _myGames != null && _myGames[_target].LatestVersion == Settings.Version)
            ImGui.TextColored(EditorTheme.Warning, $"Version {Settings.Version} is already published — bump the version below.");
    }

    private void DrawStorePage()
    {
        EditorTheme.Label("Store page");
        float w = ImGui.GetContentRegionAvail().X - 110;
        bool newGame = _target < 0;

        string name = Settings.ProductName, version = Settings.Version, tagline = Settings.Tagline, tags = Settings.Tags, description = Settings.Description;
        ImGui.BeginDisabled(!newGame);
        ImGui.SetNextItemWidth(w);
        if (ImGui.InputText("Title", ref name, 80)) Settings.ProductName = name;
        ImGui.EndDisabled();
        if (!newGame && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Rename the game on the website's Edit page.");
        ImGui.SetNextItemWidth(w);
        if (ImGui.InputTextWithHint("Tagline", "One line that sells it", ref tagline, 140)) Settings.Tagline = tagline;
        ImGui.SetNextItemWidth(w);
        if (ImGui.InputTextWithHint("Tags", "platformer, pixel-art (max 6)", ref tags, 160)) Settings.Tags = tags;
        if (ImGui.InputTextMultiline("Description", ref description, 20000, new Vector2(w, 110))) Settings.Description = description;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputText("Version", ref version, 32)) Settings.Version = version;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(w - 120 - 70);
        ImGui.InputTextWithHint("What's new", newGame ? "First release!" : "Fixed the double jump...", ref _changelog, 2000);
    }

    private void DrawScreenshots()
    {
        var shots = Screenshots().ToList();
        EditorTheme.Label($"Screenshots ({shots.Count(_selectedShots.Contains)} selected, max 8)");
        if (shots.Count == 0)
            ImGui.TextDisabled("No screenshots yet. Press Play, set up a nice moment, then capture the Game view.");
        foreach (var shot in shots)
        {
            bool on = _selectedShots.Contains(shot);
            if (ImGui.Checkbox(Path.GetFileName(shot), ref on))
            {
                if (on) _selectedShots.Add(shot); else _selectedShots.Remove(shot);
            }
        }
        if (ImGui.Button("Capture Game view"))
        {
            Directory.CreateDirectory(ScreenshotFolder);
            string file = Path.Combine(ScreenshotFolder, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            if (_captureGameView(file)) _selectedShots.Add(file);
            else Engine.Log.Warn("Couldn't capture: the Game view needs to be visible with a camera in the scene.");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Saves exactly what the Game view shows to <project>/Screenshots/.\nWorks during Play mode too.");
        ImGui.SameLine();
        if (ImGui.Button("Open folder"))
        {
            Directory.CreateDirectory(ScreenshotFolder);
            Process.Start("explorer.exe", $"\"{ScreenshotFolder}\"");
        }
        if (_target >= 0) { ImGui.SameLine(); ImGui.Checkbox("Also add these to the existing game", ref _uploadShotsOnUpdate); }
        else if (shots.Count > 0) ImGui.TextDisabled("The first selected screenshot becomes the cover image.");
    }

    private void DrawPublishBar()
    {
        switch (_phase)
        {
            case Phase.Building:
                ImGui.TextUnformatted($"Building: {_build.Builder.Step}...");
                ImGui.ProgressBar(_build.Builder.Progress * 0.6f, new Vector2(-1, 6), "");
                if (ImGui.SmallButton("Cancel")) { _build.Builder.Cancel(); _publishCancel?.Cancel(); }
                return;
            case Phase.Uploading:
                ImGui.TextUnformatted($"Uploading... {_uploadProgress * 100:0}%");
                ImGui.ProgressBar(0.6f + _uploadProgress * 0.4f, new Vector2(-1, 6), "");
                if (ImGui.SmallButton("Cancel")) _publishCancel?.Cancel();
                return;
            case Phase.Done:
                EditorTheme.StatusDot(EditorTheme.Text);
                ImGui.SameLine(0, 6);
                ImGui.TextUnformatted($"Published {Settings.ProductName} {Settings.Version}.");
                if (EditorTheme.PrimaryButton("OPEN GAME PAGE", new Vector2(200, 34)) && _publishedUrl != null) OpenUrl(_publishedUrl);
                ImGui.SameLine();
                if (ImGui.Button("Close", new Vector2(100, 34))) { Open = false; _phase = Phase.Idle; }
                return;
        }

        if (_phase == Phase.Failed && _publishError != null)
        {
            ImGui.PushTextWrapPos(600);
            ImGui.TextColored(EditorTheme.Error, _publishError);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
        }

        string? problem = string.IsNullOrWhiteSpace(Settings.ProductName) ? "Give the game a title."
            : string.IsNullOrWhiteSpace(Settings.Version) ? "Set a version."
            : _ctx.IsPlaying ? "Stop Play mode first."
            : null;
        ImGui.BeginDisabled(problem != null);
        string label = _target < 0 ? "BUILD & PUBLISH" : "BUILD & PUBLISH UPDATE";
        if (EditorTheme.PrimaryButton(label, new Vector2(240, 38))) { Settings.Save(_ctx.ProjectDirectory); StartPublish(); }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(problem ?? $"Builds {Settings.SafeName} {Settings.Version} for Windows, then uploads it.");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Engine.Log.Warn($"Couldn't open the browser ({e.Message}). Open this yourself: {url}"); }
    }
}
