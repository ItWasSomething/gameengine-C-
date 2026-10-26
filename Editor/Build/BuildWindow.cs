using System.Diagnostics;
using System.Numerics;
using Engine.Core;
using ImGuiNET;

namespace Editor.Build;

// File → Build Settings… : product name, version, start scene, and the Build
// button with a progress bar + log. The Publish window reuses the same
// GameBuilder, so "Build & Publish" there shows up here too.
public sealed class BuildWindow
{
    private readonly EditorContext _ctx;
    public GameBuilder Builder { get; } = new();
    public GameSettings Settings { get; private set; }
    public bool Open;
    public Action? OpenPublish;

    private readonly List<string> _logLines = new();
    private bool _showLog;

    public BuildWindow(EditorContext ctx)
    {
        _ctx = ctx;
        Settings = GameSettings.Load(ctx.ProjectDirectory);
    }

    // Saves the open scene first (the build copies files from disk), then
    // starts a build. Returns null if it can't start right now.
    public Task<string?>? StartBuild()
    {
        if (Builder.IsRunning) return null;
        if (_ctx.IsPlaying) { Engine.Log.Warn("Stop Play mode before building."); return null; }
        if (_ctx.Scripts?.HasErrors == true) { Engine.Log.Error("Fix the script compile errors before building (see the Console)."); return null; }
        if (_ctx.IsDirty && _ctx.ScenePath != null) _ctx.SaveScene();
        Settings.Save(_ctx.ProjectDirectory);
        _logLines.Clear();
        Engine.Log.Info($"Building {Settings.ProductName} {Settings.Version}...");
        var task = Builder.BuildAsync(_ctx.ProjectDirectory, Settings);
        task.ContinueWith(t =>
        {
            if (t.Result is { } zip) Engine.Log.Info($"Build succeeded: {zip}");
            else Engine.Log.Error($"Build failed: {Builder.Error}");
        });
        return task;
    }

    public void Draw()
    {
        while (Builder.Log.TryDequeue(out var line)) _logLines.Add(line);
        if (_logLines.Count > 400) _logLines.RemoveRange(0, _logLines.Count - 400);
        if (!Open) return;

        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.Begin("Build Settings###BuildSettings", ref Open, ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        bool busy = Builder.IsRunning;
        ImGui.BeginDisabled(busy);
        EditorTheme.Label("Game");
        string name = Settings.ProductName, version = Settings.Version;
        ImGui.SetNextItemWidth(360);
        if (ImGui.InputText("Product name", ref name, 80)) Settings.ProductName = name;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("Version", ref version, 32)) Settings.Version = version;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Bump this every time you publish an update, e.g. 1.0 → 1.1.");

        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("Start scene", Settings.StartScene))
        {
            foreach (var scene in Directory.EnumerateFiles(AssetDatabase.assetsFolder, "*" + SceneSerializer.SceneExtension, SearchOption.AllDirectories)
                         .Where(f => !f.EndsWith(".recovery" + SceneSerializer.SceneExtension)))
            {
                string rel = AssetDatabase.ToAssetPath(scene);
                if (ImGui.Selectable(rel, rel == Settings.StartScene)) Settings.StartScene = rel;
            }
            ImGui.EndCombo();
        }
        bool selfContained = Settings.SelfContained;
        if (ImGui.Checkbox("Include .NET runtime (players don't need to install anything)", ref selfContained)) Settings.SelfContained = selfContained;
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        EditorTheme.Label("Output");
        ImGui.TextDisabled($"Builds/{Settings.SafeName}-{Settings.Version}-win64.zip   (Windows 64-bit)");
        ImGui.Spacing();

        if (busy)
        {
            ImGui.ProgressBar(Builder.Progress, new Vector2(-1, 6), "");
            ImGui.TextUnformatted(Builder.Step + "...");
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel")) Builder.Cancel();
        }
        else
        {
            if (EditorTheme.PrimaryButton("BUILD", new Vector2(160, 34))) StartBuild();
            ImGui.SameLine();
            if (ImGui.Button("Build & Publish...", new Vector2(160, 34))) OpenPublish?.Invoke();

            if (Builder.Status == GameBuilder.BuildStatus.Succeeded && Builder.ZipPath is { } zip)
            {
                ImGui.Spacing();
                EditorTheme.StatusDot(EditorTheme.Text);
                ImGui.SameLine(0, 6);
                ImGui.TextUnformatted($"Built {Path.GetFileName(zip)}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Show in Explorer")) Process.Start("explorer.exe", $"/select,\"{zip}\"");
            }
            else if (Builder.Status == GameBuilder.BuildStatus.Failed)
            {
                ImGui.Spacing();
                ImGui.PushTextWrapPos(540);
                ImGui.TextColored(EditorTheme.Error, Builder.Error ?? "Build failed.");
                ImGui.PopTextWrapPos();
            }
        }

        ImGui.Spacing();
        if (ImGui.Checkbox("Show build log", ref _showLog) || _showLog)
        {
            if (_showLog)
            {
                ImGui.BeginChild("buildlog", new Vector2(540, 200), ImGuiChildFlags.Border);
                foreach (var line in _logLines) ImGui.TextUnformatted(line);
                if (busy) ImGui.SetScrollHereY(1);
                ImGui.EndChild();
            }
        }
        ImGui.End();
    }

    public void ReloadSettings() => Settings = GameSettings.Load(_ctx.ProjectDirectory);
}
